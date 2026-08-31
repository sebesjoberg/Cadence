# Cadence — design decisions

Why the shipped parts of Cadence are the way they are. **What is left to build is
[`plan-to-1.0.md`](plan-to-1.0.md)**; this file is the record that plan is built on.

**Section numbers here are stable and are cited from source comments and the README** (§13.2, §13.4,
§13.6, §14.5 …). Do not renumber them.

**What this file keeps.** Decisions and the reasons for them, and measured behaviour that contradicted
an assumption. What it does not keep: the story of arriving at a decision, or anything now readable
from the code, the tests or the README. When a section's subject ships, it is cut down to whatever
would otherwise have to be re-derived.

---

## 1. What it is, in one paragraph

A NuGet job scheduler for .NET Generic Host apps where **the schedule lives in a database and can be
changed at runtime**. Jobs are plain classes resolved from DI, one fresh scope per run. Multiple app
instances can run at once without executing the same scheduled slot twice. Everything beyond that —
persistence, clustering, dashboard, alerting — is a separate opt-in package.

**The actual product** is *DB-editable schedules + a dashboard + per-job alert rules*. Distributed
scheduling is table stakes that Hangfire and Quartz already have; it is the cost, not the value.

---

## 2. The mental model

```
                         ┌───────────────────┐
   DB / code / config  →  │  IScheduleSource  │  what should run, and when
                         └─────────┬─────────┘
                                   │  cached; invalidated by change token
                         ┌─────────▼─────────┐
   every second       →  │     Tick loop     │  which occurrences are due now
                         └─────────┬─────────┘
                                   │
                    ┌──────────────▼──────────────┐
   one winner    →   │   IOccurrenceCoordinator    │  may THIS instance run it
                    └──────────────┬──────────────┘
                                   │  Task.Run — never awaited inline
                         ┌─────────▼─────────┐
   fresh DI scope     → │   Run executor    │  IJob.ExecuteAsync
                         └─────────┬─────────┘
                                   │
                         ┌─────────▼─────────┐
                         │ IRunHistoryStore  │  what happened → UI, alerts
                         └───────────────────┘
```

Three storage interfaces, not one, because they differ in write volume, consistency need, and what
happens when they are unavailable. That split is what makes "no infrastructure at all" a real mode
instead of a degraded one.

---

## 3. The five things that actually matter

### 3.1 Claim the *occurrence*, not the job

The lock key is `{jobName}:{scheduledForUtc}`.

A lock held for the duration of a run needs a TTL longer than the longest run, which is unknowable,
and that road ends in lease renewal, GC pauses and fencing tokens — a distributed-systems project,
not a scheduler feature. Claiming the occurrence asks one question — *has anyone already started
this slot?* — and once answered it never needs re-answering. So the TTL only has to cover clock skew
plus tick jitter, and a fixed 60s is correct however long jobs run.

**The guarantee is: at most one instance *starts* a given occurrence** — not "at most one instance is
ever running this job".

### 3.2 In SQL, the claim *is* the run row

```sql
CREATE UNIQUE INDEX UX_CadenceJobRun_Occurrence
    ON CadenceJobRun (JobName, ScheduledForUtc)
    WHERE ScheduledForUtc IS NOT NULL;   -- API/manual runs exempt
```

`TryClaim` is an `INSERT`. A unique violation (SQL Server 2601/2627, PostgreSQL 23505) means someone
else won. No lock primitive, and no window where a slot is claimed but unrecorded.

Catch **only** those error codes. A blanket `catch` turns a dead connection into a silently skipped
run — the worst possible failure mode for a scheduler.

### 3.3 Never block the tick loop

No `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` anywhere in the tick path. One synchronous
wait on one slow job stalls every other schedule in the process. This is the most common way
schedulers of this shape die, so it gets an analyzer rule, not just good intentions.

### 3.4 Two cancellation sources, kept apart

The `MaxDuration` CTS and the host-shutdown CTS are linked for the job, but tracked separately so
history can distinguish `TimedOut` from `Aborted`. Collapse them and history cannot answer "is this
job slow, or is the host churning?".

The completion write uses `CancellationToken.None` — recording *why* a run ended must not be
cancelled by the shutdown that ended it.

### 3.5 The watchdog is the highest-value alert

A job that throws sends a failure alert. A scheduler that quietly died, or a job someone disabled six
weeks ago, sends **nothing**. `NotSucceededWithin` catches the failure mode nobody notices. Make it
prominent, and offer to create one automatically (3× the cron interval) whenever a job is enabled in
the dashboard.

Externally, alert on the `cadence.job.seconds_since_success` gauge. Absence of failure is not
evidence of success.

---

## 4. Alerting

Unbuilt, and therefore a plan rather than a decision record: see
[`plan-to-1.0.md`](plan-to-1.0.md) — *Milestone v0.5*. The two constraints settled before any of it
was designed are stated there as inherited: **channels in code, rules in the store**, and
**throttling is not optional**.

---
## 5. Fail closed, twice

- **At boot:** every registered job is resolved from a real scope before the first tick. If it cannot
  be constructed, the process dies at deploy time instead of at 02:00.
- **At the edge:** `MapCadenceApi()` / `MapCadenceDashboard()` refuse to map outside Development when
  nothing authenticates them. The refusal happens at *map* time, before the server listens, so a
  missing token fails the deploy rather than the night. §13.3 has the gate.

Be honest in the README about what *cannot* be checked: a Roslyn analyzer cannot validate a DI graph,
because registrations happen through arbitrary runtime code. The analyzer validates *registration
metadata* — duplicate names, bad cron literals, unparseable durations. The graph is validated at
boot, from a scope. Do not promise "build-time DI checking".

---

## 6. Build order

v0.1 Core, v0.2 persistence and clustering, v0.3 the control surface, v0.3.1 identity, v0.4 the
dashboard — all done. What remains, and in what order, is
[`plan-to-1.0.md`](plan-to-1.0.md).

**The decision point is resolved.** Whether to build on Quartz.NET's clustering instead, should our
own coordination layer overrun its budget: it did not. One filtered unique index on a table run
history needed anyway, a `TryClaimAsync` that is an `INSERT` plus a check for 2601/2627, and a
conformance suite both tiers are held to. Quartz would have traded that for a dependency, a second
scheduling model to reconcile with this one, and misfire semantics we do not control.

`IOccurrenceCoordinator` stays the only seam that knows how a claim is won — no longer because we
might swap it wholesale, but because a second tier has to slot in underneath it without Core
noticing. Redis then did.

---
## 7. The open questions, answered

Distributed pause and one-`MapCadence()`-or-two are settled and owned elsewhere: §12 and §13.1.

| # | Question | Answer |
|---|---|---|
| 1 | `QueueOne` semantics | **Cut from v1.** The only policy needing a per-job coalescing queue, and its `ScheduledFor` has no clean answer. `Skip` + `AllowConcurrent` cover the real cases. |
| 2 | Per-job concurrency caps | **Defer.** Global `MaxConcurrentRuns` + `Skip` is enough for v1. |
| 3 | Payload JSON Schema | **No.** Leave payloads opaque; the job validates. Saves a dependency and a UI surface. |
| 4 | Retry within run vs. reschedule | **Cut `MaxAttempts` from v1.** In-run retry makes duration and timeout ambiguous in history. Later, do it as a new run with `Trigger = Retry` and `ScheduledForUtc = null`, which sidesteps claim uniqueness entirely. |

---

## 8. Gaps

The gaps still open are tracked as debt in [`plan-to-1.0.md`](plan-to-1.0.md) — *Debt to clear before
1.0*: **#2** (a 1s tick that re-evaluates every job does not scale; needs an in-memory min-heap of
next occurrences, rebuilt when the change token fires) and **#7** (warn at boot if alerting is enabled
without a persistent store).

Numbered as first written, so the references from §13 still resolve. **Closed since:** **#1** (`Skip`
across instances — a run of a `Skip` job holds its job name as an exclusive key on the run row, so a
second instance is refused and records `Skipped`; the key is released by the outcome write or by the
reap, which is why the README documents a bounded block rather than a best-effort policy), **#4** (IANA
ids need ICU — `CronParser` detects it and names `InvariantGlobalization`), **#5**
(`IWritableScheduleSource` split out), **#6** (`JobContext.Report` batching — flush on 100 entries or
250 ms, drop rather than block, because back-pressure on `Report` would make a slow database into a
slow job), **#3** (see below), **#8** (`dotnet pack`'s `GenerateEmbeddedFilesManifest` warning — `CadenceEmbedSpa` hung off
`BeforeBuild` only, and pack's output-group pass reaches `PrepareResourceNames` without going through
it, so the item list was empty in that pass alone; the warning carries no code and could not be
suppressed, so the target now also hooks the manifest-inputs target that raises it).

**Gap 3 was mis-stated, and the wrong half was the documented one.** It read "boot versus tick: never
fail boot on a store blip", and boot was never the problem — `StartAsync` reads no schedules, the
change-token registration's poll is fire-and-forget behind a catch-all, and the first read happens on
the tick, where an exception was already caught. The real defect was one exception filter.
`ScheduleTicker.ReloadSchedulesAsync` caught a failed read only `when (_states.Count > 0)` —
deliberately, to keep running on the schedules already loaded — so a blip *after* loading was
survivable and a blip *before* it was not. On a cold start against an unreachable store the exception
escaped to the hosted service's catch-all, the tick was abandoned, and **nothing ran at all** for as
long as the store stayed down, including jobs whose code-declared cron needed no store row to be
correct.

Closed by resolving from the code defaults on that branch rather than giving up:
`ScheduleResolver.ResolveFromDefaults()` runs the same per-job loop with no rows, so a job with no
default cron is reported as a problem exactly as it would be if the store were readable and held
nothing for it. The fallback is a stopgap, not a latch — the store is retried on the next reload and
the first success replaces it silently. Event `1106` says the process is running on code-declared cron
and that store or dashboard edits are not in effect; storage health already reported `Degraded`
(§13.4), so the operator signal was never the missing part. Three tests pin it, and all three fail
without the fix.

---

## 9. Measured behaviour and deliberate deviations

Each was an assumption in the original spec that turned out to need correcting. All but §9.6, which
is a measurement rather than a behaviour, are pinned by tests.

### 9.1 Daylight saving, measured against Cronos 0.8.4 / Europe/Stockholm

| Case | Actual behaviour | Consequence |
|---|---|---|
| `30 2 * * *` on spring-forward day (2026-03-29, 02:30 local does not exist) | **Fires at 03:00 local / 01:00 UTC** — the instant the clock jumps | The job still runs that night, half an hour late. It is **not** skipped. |
| `30 2 * * *` on autumn-back day (2026-10-25, 02:30 local happens twice) | Fires once, at the first 02:30 (00:30 UTC, CEST) | No duplicate. |
| `*/15 * * * *` across autumn-back | Continues on wall clock; the repeated hour yields 02:00–02:45 twice in local terms | Distinct UTC instants, so occurrence keys never collide. |

The original note claimed the spring-forward occurrence was *skipped*. Anyone reading that would
conclude a nightly 02:30 job misses one night a year and might build a catch-up around it. It runs
late instead.

### 9.2 The run id is assigned before the claim, not after it

`TryClaimAsync` takes the run id from its caller. §3.2 requires that — a claim that generates its own
id is two rows colliding on one occurrence — but the property that made it worth an interface change
is that **the claim becomes idempotent.** A transient fault can drop the acknowledgement of an insert
that already committed; a blind retry then gets 2627 back, reports "someone else won", and silently
skips a run this instance owns, which §3.2 identifies as the worst failure mode available. With a
caller-assigned id the retry asks whether the existing row is its own and answers exactly.

Rejected, for the record: a separate `CadenceOccurrenceClaim` table, which needs no Core change but
reintroduces the claimed-but-unrecorded window and cannot be made retry-safe; and a blind `UPDATE`
from `StartAsync`, which leaves the claim ignorant of the run id and has the same hole.

### 9.3 A disabled job's occurrences are treated as never having existed

While a job is disabled, its evaluation point advances with the clock. Re-enabling starts from the
next occurrence rather than replaying the disabled period.

The spec's `MaxCatchUp` rationale implied the opposite — that a `*/5` job disabled for a month would
queue ~8,600 runs on re-enable, capped. Capping a footgun is worse than not having it: nobody who
ticks "enabled" in a dashboard means "and replay the last month". `MaxCatchUp` still guards the case
it should, which is host downtime.

### 9.4 Occurrence claiming elects a leader; it does not spread load

Measured in `samples/Cadence.Sample.AppHost`, three replicas against one SQL Server, twice: the
replica that started first — by ~40 ms — won **every** occurrence, and the winner changed between
runs exactly as start order did. Every replica ticks on its own one-second timer whose phase is fixed
by when the process started, and the claim is a race to an `INSERT`, so a tick firing 40 ms earlier
wins every race there is. Nothing is broken by that — §3.1's guarantee is *at most one*. Killing the
leader mid-run moved every subsequent claim to the next-earliest replica within one occurrence, and
the janitor marked the interrupted run `Lost` 21 seconds later.

The README says this next to the guarantee, because someone sizing a cluster on the assumption that
replicas share the load will size it wrong.

**The better of the two fixes is now being taken.** Pulling work from a queue instead of executing what
was claimed (§14.1) makes tick phase stop deciding who works at all; it is v0.5 in
[`plan-to-1.0.md`](plan-to-1.0.md). Tick jitter (§14.2) was the cheaper fix and is dropped, because
pulling removes the problem it addressed. **The measurements above are pre-v0.5 and must be retaken
once the pull loop lands** — they are the baseline that change is measured against, not a description
of the shipped system after it.

### 9.5 `Cadence.Core` takes the full health-checks package, not the abstractions

v0.3's constraint was that health checks enter Core through
`…HealthChecks.Abstractions` only. Core references `…HealthChecks` instead, because `AddHealthChecks()`
and `AddCheck` live in the larger package and there is no way to register a check without them. The
spirit holds — a test asserts `Cadence.Core` carries no reference to `Microsoft.AspNetCore.*`, even
transitively — but the cost is real: every Core consumer now gets `DefaultHealthCheckService` and the
health-check publisher hosted service, the latter inert with no publisher registered. Recorded so
nobody rediscovers it as a violation.

### 9.6 What the dashboard bundle weighs

| File | Raw | Gzipped |
|---|---|---|
| `index-*.js` | 601.4 kB | 185.2 kB |
| `index-*.css` | 202.3 kB | 29.4 kB |
| **Total** | **803.6 kB** | **214.6 kB** |

From `src/Cadence.Dashboard/wwwroot/assets` after a Release build, gzipped at the default level —
the comparison Vite's own build report makes. This replaces the 194.5 kB / 60.8 kB figure taken at
first build, which was React over a placeholder route, measured before Mantine, the three TanStack
packages and the screens landed: a baseline 3.5× out measures nothing, and this is the number the
next change has to be compared against. Cadence compresses nothing itself — the assets are embedded
and streamed by an endpoint — so what a browser receives is whatever the host has configured.

---

## 10. The end-to-end samples

`Cadence.Sample.Worker` consumes Cadence **as a package from a local feed**, not by project
reference, which is the point of it: that is how it caught `NU5039`, a declared `PackageReadmeFile`
that was never packed.

`Cadence.Sample.AppHost` runs three replicas against one SQL Server; what it measured is §9.4. **It
is a demonstration, not the proof** — clustering is proven by `ClusteredSchedulingTests`, five
instances sharing one `SqlOccurrenceCoordinator` against a real SQL Server inside one test process,
on every CI run. What only the Aspire host can show is real process boundaries, a replica killed
mid-run for the janitor to reap, and the `Skip` caveat happening in front of someone — better in a
sample we control than in someone's incident.

---

## 11. Two storage tiers, and what the second one proved

`Cadence.Storage.Redis` implements the same storage interfaces as the SQL tier and is held to every
one of the conformance suites — seven as of v0.4, and a tier is never granted an exemption from one.
It is an **alternative**, not a layer.

### 11.1 The claim is still the run

The obvious Redis coordinator is `SET key NX EX 60`, and it is wrong for this. A claim that expires
is a claim that can be won twice — not inside the tick's horizon, but by anything replaying an older
occurrence, which is exactly what catch-up after downtime does. §3.2 gets its property in SQL from
the claim being a permanent row; a tier whose claims quietly stop existing after a minute is a
different guarantee wearing the same interface. So the Redis claim is permanent too, written by the
same Lua script as the run's hash and its index entries, and removed by the janitor with the run it
belongs to.

### 11.2 The seam held, and one thing had to move

`IOccurrenceCoordinator` needed no change, which was the test §6 set for it. The janitor did: it
lived in `Cadence.Storage.Sql`, calling that store's internal maintenance methods, and Redis needed
the same four passes over completely different operations. Rather than a second copy of the policy —
reap before purge, batch, never escalate a failure into a scheduling problem — the policy moved to
`Cadence.Core` behind `IStorageMaintenance`, and each tier now supplies only the operations. **The
seam that mattered second was the one nobody had named.**

### 11.3 Where the tiers genuinely differ

Not in behaviour — the conformance suites are the point — but in operations, and the README's table
is the statement of that. Two entries in it need their reason recorded here:

**The schedule poll stays enabled on Redis** even though pub/sub delivers an edit in milliseconds,
because Redis pub/sub is fire-and-forget with no redelivery, and a scheduler that had silently
stopped noticing schedule edits would look perfectly healthy while ignoring the dashboard.

**An API token's expiry is enforced in `IApiTokenStore.FindAsync` itself**, not by the caller, which
is what makes it one place that can push the predicate into an index or a key's TTL. SQL folds it
into the lookup query and the janitor's token pass deletes expired rows in batches; Redis needs no
such pass, because a token key carries its expiry as its own TTL. Neither tier caches a resolved
token — revocation and expiry both take effect on the next request, on every instance, which is what
makes a store-backed token cheaper to reason about than a cached one.

---

## 12. Pause, and why it is two switches

`PauseScope` is a flags enum, and paused occurrences are treated as never having existed — §9.3's
rule reused rather than a second policy. The README states both. Three things it does not:

**The write rides the schedule version.** `SqlPauseStore` bumps `CadenceScheduleVersion` in the
transaction that writes the switches; `RedisPauseStore` INCRs the counter and publishes on the
schedule channel. Neither tier adds anything for an instance to poll: the ticker re-reads the
switches on the same reload as the schedules. The cost is one small read per instance per config
poll; the property bought is that a pause and a schedule edit can never be observed out of order.

**The trigger gate reads through, the tick loop reads cached.** A trigger is rare enough to afford a
round trip, and someone pausing during an incident should not watch a run start ten seconds later;
the tick loop runs every second and cannot. The asymmetry is deliberate.

**In-memory, pause is process-local, and the conformance suite says so** — `IsDistributed` is false
for that tier and the cross-instance test skips rather than being quietly dropped. Letting two
in-memory stores share static state to make the test pass would have made the suite lie about the
tier it was hardest to be honest about.

---

## 13. The control surface

v0.3 is the first package that is not a storage tier, and the first to reference ASP.NET Core. Core
stays on the `Extensions.*` abstractions.

### 13.1 Two trees, one options object

`MapCadenceApi()` mounts the machine-callable tree and authenticates it with a token.
`MapCadenceDashboard()` mounts the UI and the endpoints it needs, and authenticates those with an
operator session.

**Two trees rather than one** because the callable API has to be switchable off: on one tree "off" is
a flag that leaves every route mounted and answering to a session, while on two it is the absence of
a line of code, so the routes do not exist and a leaked token has nothing to reach. What the single
tree was protecting still holds — one `CadenceApiOptions`, one gate, one thing to secure.

**Paths stopped being configurable, and the dashboard is why.** `CadenceApiOptions` has no
`BasePath`; every route is fixed under `/cadence`. The bundle ships prebuilt inside the NuGet
package, so there is no build step left in the consuming application to bake a configured prefix
into — the application never compiles the SPA at all. A `BasePath` a host could still set would have
been a promise the bundle had no mechanism to keep: every fetch it makes would either hardcode
`/cadence` and ignore the setting, or the setting would silently do nothing. Fixing the path is what
lets the package work unpacked.

`MapCadenceApi()` returns the `RouteGroupBuilder` it mounted, so a host can attach its own
conventions — rate limiting, CORS, OpenAPI metadata. For endpoints that start jobs, rate limiting is
a realistic ask.

### 13.2 The one write that is not on the surface

The endpoint list and the rule are in the README. The reason the two writes are not equivalent: a
triggered run is loud, appears in history, and is over; a changed cron expression is silent and
permanent, and nobody notices it until the night it does not run. Pause is the one write that earns
its place anyway — halting scheduled work and paging a human is a real runbook, it is reversible, and
§12 scoped it.

`type` on every RFC 9457 document is a URN — `urn:cadence:problem:{slug}` — not an `https` URL: the
RFC wants an identifier, not a page, and an `https` type would name a domain nobody controls and a
project name that is not settled. Responses are explicit records behind a source-generated
`JsonSerializerContext`, not `JobRun` serialised directly, so a future storage column is not a public
API change.

**Three corrections the implementation forced.**

- A disallowed trigger kind answers **400**, not 409. Nothing the caller waits for changes a job's
  declared triggers, so it is a malformed request; 409 stays for a skipped dispatch and a paused
  scheduler, which are a correct request at the wrong moment.
- `GET /runs/{id}` had nothing to call — `IRunHistoryStore` had no by-id lookup. It has one now, on
  all three tiers.
- The 500-row cap bounds rows, not payload: both persistent tiers attach every run's progress entries
  to a query result, so a capped list was fetching 500 logs for a view showing none.
  `RunQuery.IncludeLog` (default `true`) lets a list skip the second query.

**Two boundaries.** An unparseable `status`, `from`, `to`, `limit` or `offset` returns the framework's
400 with an empty body, not RFC 9457 — binding fails before any endpoint filter can reformat it, and
owning those bodies would mean hand-rolling five parsers. And **the trigger endpoint accepts no
payload**: caller JSON would widen what a token can do from "start the job as configured" to "start
the job with arbitrary input", which needs its own size and shape rules and its own line in the trust
argument. In-process callers can still pass one.

### 13.3 The gate

One scheme, `CadenceToken`, reading `Authorization: Bearer`. Comparison hashes both sides with
SHA-256 before `FixedTimeEquals`, so the compare is fixed-length and token length does not leak. The
scheme name is public as `CadenceApiDefaults.AuthenticationScheme`, because a host writing its own
policy has to name it without hardcoding a literal.

Evaluated when `MapCadenceApi()` runs — startup, before the server listens. `IsRegistered` ORs three
independent signals, and `AllowUnauthenticated` overrides only the last:

| Condition | Result |
|---|---|
| `options.RequireAuthorization("policy")` | maps; the host's policy governs alone, whatever else is configured |
| one or more tokens configured | maps; `ReadPolicy` requires an authenticated `CadenceToken`, `OperatePolicy` also requires `Operate` |
| `options.Oidc` configured | maps; the same two policies also accept a signed-in user's cookie |
| none of those, but a store can persist tokens, `AllowUnauthenticated` false, not Development | maps; the policies apply and nothing satisfies them — every request refused until an operator configures a token or a provider |
| `AllowUnauthenticated = true` | maps, warning **every start**; the administration tree mounts where a store allows it but refuses everyone, since it needs a user principal this shape never produces |
| none of those, `IsDevelopment()` | maps, loud warning, **loopback callers only** |
| none of those, anything else | **throws**, naming all four remedies |

The composition rule is one sentence: **a named policy governs alone, and Cadence's own schemes
authenticate into it.** A token or a cookie produces a principal carrying `cadence:kind` and
`cadence:scope`; `ReadPolicy` requires either, and `OperatePolicy` — layered on top, only for trigger
and pause — additionally requires `Operate`.

Three rows look like defects and are not:

**A writable store satisfies the gate, but yields.** This is the row that answers the question an
operator actually asks: *why does `UseSqlStorage()` alone not lock this down?* Registering a writable
store is a statement about persistence, not authentication, so it satisfies the gate the same way a
token would — the deployment is administrable once a token exists — but it is the one condition that
yields, to both `AllowUnauthenticated` and Development, precisely because nothing has been configured
yet to override. Boot log `3002`/`3003` is how an operator tells the two apart without reading source.

**Development is not closed by a storage package.** Every SQL and Redis deployment registers a
writable store, so honouring that signal there would have taken the Development row away from all of
them: a deployment running Development with `UseSqlStorage()` and no token served loopback callers,
and would have started answering 401 to everything with no credential obtainable over HTTP, because
`/tokens` requires a user principal and a user requires a provider.

**A null `RemoteIpAddress` counts as loopback.** Kestrel over TCP always fills it in, so nothing
arriving over the network is null; null means a transport with no IP peer — an in-memory `TestServer`,
a Unix domain socket, a named pipe — none of which is the exposed TCP port the filter exists to close.
Refusing null would 403 every in-memory test host while closing nothing.

The loopback filter is on the Development branch alone — not on `AllowUnauthenticated`, where a proxy
or mesh makes every legitimate caller non-loopback, and not where a policy was applied, where the
request is already authenticated. The case it closes is `ASPNETCORE_ENVIRONMENT=Development` surviving
into a container, which cost nothing before this milestone and now exposes "run any registered job"
and "halt scheduling cluster-wide" to whatever can reach the port.

**The schemes and their built-in policies are registered conditionally, and only ever together**,
through `AddOptions<...>().Configure<IOptions<CadenceApiOptions>>(...)` rather than at `AddApi` time:
whether a token, a provider or a writable store exists cannot be evaluated until options bind, and
resolving `AuthenticationOptions` *causes* that binding, which is stronger than merely deferring until
after it. The policies defer on the identical condition, because a policy naming an unregistered
scheme is a 500 on every request — and the deployments that would hit that are `AllowUnauthenticated`
and Development, exactly where an operator least expects a hard failure.

**`PUT /pause` records who from the authenticated principal, never from the body.** The identity is
`token:{first 8 hex of the token's SHA-256}` — stable across restarts, and not a secret. A `setBy` a
caller could write would not be an audit field, so `PauseRequest` has no such member; under
`AllowUnauthenticated` it records `api`.

### 13.4 Health, and why the probes must not know about storage

The paths, tags and access split are in the README. What is not there is the enforcement.

**The liveness and readiness checks are given no store to query.** That is the guarantee, not a
convention: they cannot fail on a store blip because they cannot see one. Every replica shares one
store, so a readiness probe that is honest about it takes every pod out of the service
simultaneously — and the dashboard returns 503 during precisely the incident someone opened it to
investigate, while the rolling deploy that would have fixed it stalls. Liveness tied to the store is
worse still: a database hiccup becomes a cluster-wide crash loop, each restart re-running the
migrator against the store that is already struggling.

**The guarantee is structural.** A reflection test asserts each probe's constructor takes only an
allow-listed parameter type — not a blacklist of the store interfaces, which any of
`IServiceProvider`, `Func<IPauseStore>` or `Lazy<T>` would have walked straight through undetected.
`ReadinessHealthCheck` takes the job count as a plain `int` captured at registration rather than an
`IJobRegistry`, because that dependency would make the guarantee conventional — this solution
already has schedule sources backed by a database. A second one-line test asserts `Cadence.Core`
carries no reference to `Microsoft.AspNetCore.*` at all.

Storage health is reported to humans, alerting and the dashboard, never to the kubelet, and reports
`Degraded` rather than `Unhealthy`. This is where gap #3's "report degraded health" lands. Verified
both ways on both tiers: `AnUnreachableDatabaseIsDegradedNotUnhealthy` and its Redis twin need no
container, and `AReachableDatabaseIsHealthy` / `AReachableRedisIsHealthy` run against a real server —
in CI, and now confirmed locally. CI fails if either tier's suite skips its way past a real store.

`AddCadence()`, `UseSqlStorage()`, `UseRedisStorage()` and `AddApi()` are each safe to call twice.
Health-check registration is guarded explicitly, because the health-check service throws on a
duplicate name where the rest of DI would overwrite; `AddApi`'s deferred `AddScheme` is guarded on
`SchemeMap` for the same reason, and there the blast radius is wider — `AuthenticationOptions` is
app-wide, so an unguarded duplicate takes the *host's* authentication down with Cadence's.

### 13.5 Identity: OIDC for people, tokens for machines

v0.3.1. An OIDC provider authenticates people; Cadence authenticates machines itself. The rules — the
two scopes, the freshness requirement, break-glass configuration tokens, no server-side forced
logout — are in the README. What is not there is why the alternatives lost.

**ASP.NET Core Identity.** `UserManager<TUser>` is generic per user type, but `IdentityOptions` is not
— one instance per container (verified against `Microsoft.Extensions.Identity.Core` 9.0.4). Cadence is
a package added to somebody else's application, and many already call `AddIdentity<,>()`; registering
`AddIdentityCore<CadenceUser>()` alongside would configure *their* options object — our password
length becoming theirs, our lockout policy silently changing their account lockout. There is no
per-user-type variant to reach for instead.

**EF Core.** The SQL tier is hand-written ADO with embedded scripts journalled under `sp_getapplock`;
Identity's practical store is `Identity.EntityFrameworkCore`, and the Redis tier has no EF story at
all. Either meant a second data-access technology for one feature.

**SPA PKCE**, despite genuinely less state and a tighter revocation window. Silent renew needs the
provider's cookie reachable in a third-party context, which Safari and Chrome increasingly block, and
the dashboard would need a token library plus its own state, nonce, renewal and expiry handling. The
XSS argument between the two is narrower than it looks — `POST /tokens` exists, so an XSS in the
dashboard can mint a durable token under either architecture. The control that actually matters is the
freshness requirement, not where the ticket cookie sits.

**Two of the three N-replica consequences dissolved.** Store-backed sessions survive, satisfied by a
shared key ring and a self-contained cookie rather than a session table. Cache invalidation on
revocation dissolved because nothing is cached. Shared login rate limiting dissolved because there is
no login. The one thing the token handler answers without asking the store is a value lacking the
43-character Base64Url shape `ApiTokenSecret.Create()` produces — a format check, not a cache, which
keeps an unauthenticated caller from spending a seek and a pooled connection per request.

**The key ring needed a container of its own, and that is the non-obvious part.**
`DataProtectionOptions` and `KeyManagementOptions` are both single-instance and unnamed — the same
defect class that disqualified ASP.NET Core Identity — so setting the application discriminator or the
XML repository on them would relocate the *host's* key ring and change what every payload it has
already protected derives from, with registration order deciding which side won. Cadence therefore
builds its key ring in a separate container and attaches it to
`CookieAuthenticationOptions.DataProtectionProvider` on its own scheme's named options, which is the
per-scheme seam. What the host arranged for its own keys is carried into that container, which is what
keeps `ProtectKeysWithCertificate` composing.

**The dependency cost, stated once.** `Cadence.Api` takes a hard dependency on
`…Authentication.OpenIdConnect`, so a token-only consumer still carries it; `Cadence.Storage.Sql`
references `Microsoft.AspNetCore.DataProtection` and `Cadence.Storage.Redis` the `.StackExchangeRedis`
variant for the `IXmlRepository` each offers, so a consumer that wanted only the scheduler now pulls
Data Protection with its storage tier. Both are small and already in the shared framework's dependency
graph for any web host, which is the whole of the justification. The `Microsoft.*` floor is 10.0.11,
because GHSA-9mv3-2cwr-p262 affects 10.0.0 through 10.0.6.

**Three sign-out facts, each gotten wrong once.**

- **Sign-out carries `client_id`, not `id_token_hint`.** `SaveTokens` is false — nothing calls a
  downstream API, and provider tokens in the ticket would overflow the cookie into chunks — so there
  is no `id_token` to hint with. RP-Initiated Logout 1.0 permits `client_id` instead. The provider
  therefore shows its own logout confirmation, or a refusal page if it treats a missing hint as an
  error, rather than signing out silently.
- **A provider-initiated sign-out must name the session it is ending.** `RemoteSignOutPath` is handled
  inside the authentication middleware, before routing, so no endpoint filter reaches it, and the
  framework skips its own `sid` comparison when the request carries none — which left an
  `<img src="…/signout-oidc">` on any page able to sign an operator out. `OnRemoteSignOut` now
  requires a `sid` matching the current ticket's, which is why `sid` is on the claim allow-list.
- **The realm registers `/cadence/signout-callback-oidc`**, not `SignedOutRedirectUri` (`/cadence`).
  The callback path is the leg the provider redirects back to; the redirect URI is a hop Cadence makes
  afterward from its own handler, which the provider never sees. Registering the redirect URI instead
  yields a Keycloak 400.

### 13.6 The topology the trigger forces

`IJobTrigger.TriggerAsync` ends in `JobExecutor.DispatchAsync`, so **a triggered run executes in the
process that received the request.** Two consequences:

1. Behind a load balancer, a manual run's `InstanceId` is chosen by the ingress, not by Cadence. Reads,
   schedule edits and pause are correct from any replica, because they go to the shared store.
2. **A dashboard-only deployment that registers no jobs cannot trigger anything** — the registry is
   empty, so every trigger is a `JobNotFoundException`. Making it work needs cross-process dispatch,
   which is a queue, which §7 #1 and #4 cut on purpose.

So the supported shape is every replica mapping the API. `MapCadenceApi()` does **not** throw on an
empty registry — registering jobs behind a feature flag is legitimate — but it warns at map time, and
every 404 from `ProblemMapper.JobNotFound` names the replica's registered job count, so a misconfigured
pod diagnoses itself from whichever response body reaches the operator first.

### 13.7 What the dashboard settled

The README states the dashboard's narrower gate and the person-not-scope rule for schedule writes and
`Manual` triggers. Three things behind them:

**One options object, two verdicts.** A token-only configuration throws at `MapCadenceDashboard()`
while satisfying `MapCadenceApi()` right next to it. That is the two trees asking the same options
object different questions — no browser sends an `Authorization` header on the request that loads a
page, so a bare token is a legitimate machine credential and no evidence at all that a person can get
in — not the two disagreeing about one answer.

**`CadenceUiRoutes` is a public seam bought to avoid a production-to-production friend link.** The
operator tree reuses seven of the machine tree's handlers rather than copying them, because a copy is a
second place every future field and every future refusal has to be kept identical in — exactly the
drift a test would eventually catch and a changelog would not explain. `InternalsVisibleTo` was the
alternative and was declined: the three entries in this repo are `src → its own test project`, which
is a different thing from one shipped assembly reaching into another's internals, and they are not a
precedent for it. A public type reviewed once says plainly what crosses the boundary; a friend list
grows silently permissive. The cost is two types in `Cadence.Api.Routing`, `[EditorBrowsable(Never)]`,
existing solely so `Cadence.Dashboard` can call in. They are not a supported API, and the doc comment
says so — the honesty of that label is what the whole trade rests on, which is why the comment
currently claiming the repo uses no `InternalsVisibleTo` at all needs correcting.

**The person/machine line is derived, not written twice.** Both the schedule write and the `Manual`
trigger derive their host-policy exception from `PolicyName is null`, so they cannot drift apart. Under
a named policy Cadence adds no check of its own: a deployment wanting that line drawn has to draw it in
its own policy, because the alternative is Cadence overruling a policy the host named, which §13.3
settles against everywhere else.

## 14. Parked — written down, deliberately not built

Decisions recorded so they do not have to be re-derived, and so nobody mistakes them for something the
package does. It is here because half-designed futures interleaved with settled ones are what make a
design hard to reason about.

**§14.1 is no longer parked** — it is milestone v0.5 in [`plan-to-1.0.md`](plan-to-1.0.md), and §14.2
is dropped as a consequence. The rest is unscheduled.

### 14.1 Queue the claim, pull the work

**No longer parked — this is milestone v0.5.** The design, the migration table, the SQL and Redis pull
shapes and the semantics still to settle are in [`plan-to-1.0.md`](plan-to-1.0.md). What stays here is
the decision that shaped it:

**There is no leader, and there must not be one.** A leader container dispatching to subcontainers is
the obvious framing and the wrong one: election needs leases, leases need fencing, and that is
precisely the distributed-systems project §3.1 refused to become. Because concurrent claims are already
safe, nothing needs electing — the winner of a claim enqueues, and any instance pulls. A *declared*
role (an operator or an orchestrator saying "this process only ticks") is a different thing and is
allowed; an *elected* one is not.

**The current design was a stepping stone to this, not an obstacle.** The coordinator seam §11.2 proved
holds is exactly what makes the change cheap, and a version of Cadence that had skipped coordination to
stay simple would have a harder migration, not an easier one.

### 14.2 Tick jitter

**Dropped, not deferred.** Jittering each instance's tick phase by a random fraction of `TickInterval`
would have spread claim wins without touching the claim — the cheap fix for §9.4. Once §14.1 lands,
tick phase no longer decides who works, so jitter buys nothing. Recorded because it was the obvious
first answer, and because the reasoning is the general one: do not spend a change to a load-bearing
path on a problem the better answer removes.

### 14.3 Deployment under an orchestrator

Not documented for users yet, and should not be until someone has actually deployed it.

**There is nothing to configure.** A plain `Deployment` with `replicas: 3` — no StatefulSet, no
leader-election sidecar, no pod affinity, no ordinal identity. That is §3.1 paying out: the claim is a
row in the store, so nothing is tied to a pod's name or lifetime. The default `InstanceId`
(`{machine}:{pid}:{short-guid}`) already resolves `machine` to the pod name.

**The autoscaler does nothing — until v0.5.** Given §9.4, a horizontal autoscaler pointed at CPU adds
replicas that win nothing: the cluster scales and the throughput does not, and the only thing that
currently redistributes work is a rolling deploy changing which pod started first. Replica count is
failover capacity, and the README says so next to the guarantee. **§14.1 is what changes this, and both
this paragraph and that README section have to be rewritten when it lands** — not edited, since the
conclusion inverts.

**Three timeouts that all default to thirty seconds.** `terminationGracePeriodSeconds`,
`HostOptions.ShutdownTimeout` and `CadenceOptions.ShutdownDrainTimeout`. A job with a ten-minute
`MaxDuration` therefore gets SIGTERM, thirty seconds, and SIGKILL — the run dies mid-flight and the
janitor marks it `Lost` a heartbeat timeout later, which reads in history as an infrastructure failure
rather than the misconfiguration it is. The invariant is `terminationGracePeriodSeconds ≥
ShutdownTimeout ≥ ShutdownDrainTimeout ≥ the longest MaxDuration`.

**The inner pair is checked, and only warns.** `ShutdownBudgetProbe` runs on the boot path beside the
graph validator and logs one warning per violation. It does not throw, which is a deliberate departure
from §5: every timeout in the chain defaults to thirty seconds while `MaxDuration` does not, so the
common violation is one that existing applications are already running with, and failing their boot
would turn an upgrade into an outage. Nor could throwing be justified on the evidence —
`terminationGracePeriodSeconds` is outside the process, so a clean inner pair proves nothing about
whether the run survives. The remaining gap is that the bound comes from the *registered* maximum
durations; a writable schedule source can raise one later and that edit is unchecked. Validating the
outermost value stays deployment documentation.

### 14.4 The compose proof

Aspire demonstrates multi-replica scheduling but supervises it, which is what makes it a demonstration
rather than a deployment. A docker-compose PoC — real containers, one SQL Server, N workers behind a
load balancer — is where §14.3 could be shown rather than asserted: kill the leader and watch the next
claim move, shorten the grace period and watch a run land as `Lost`.

### 14.5 A schedule audit table

§13.7 records what v0.4 shipped instead: event `3007`, one log line per edit. This is the table that
would close the gaps that leaves, and the reason it is parked is that a job's full schedule is more
than its cron: `Overlap`, `MaxDuration` and `Settings` all change too, and a table logging only the
field the current message happens to name would need a second migration the day someone asks why
`MaxDuration` has no history.

```sql
CREATE TABLE CadenceScheduleAudit (
    Id            BIGINT IDENTITY PRIMARY KEY,
    JobName       NVARCHAR(200) NOT NULL,
    ChangedAtUtc  DATETIME2(3)  NOT NULL,
    ChangedBy     NVARCHAR(200) NOT NULL,
    PreviousJson  NVARCHAR(MAX) NULL,   -- NULL for a job's first override
    NewJson       NVARCHAR(MAX) NOT NULL
);
```

One row per write, the whole schedule serialised on each side rather than a column per field, so a
future field added to `JobSchedule` is covered without a migration. Not built because nothing in v0.4
reads it back — there is no screen to show it on, and building the table before the screen is the same
mistake §7 declined to make for `QueueOne`. Redis would carry the same rows as a capped list per job
(`LPUSH` plus `LTRIM`), which is a second thing to keep in step with SQL and one more reason this is
recorded rather than started.
