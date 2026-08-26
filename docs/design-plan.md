# Cadence — Design Plan (condensed)

A readable companion to the full spec. The spec is the reference; this is the map.

**What this file keeps.** Decisions and the reasons for them, measured behaviour that contradicted
an assumption, and everything still ahead. What it does not keep: the story of arriving at a
decision, or anything now readable from the code, the tests or the README. When a section's subject
ships, it is cut down to whatever would otherwise have to be re-derived.

---

## 1. What it is, in one paragraph

A NuGet job scheduler for .NET Generic Host apps where **the schedule lives in a
database and can be changed at runtime**. Jobs are plain classes resolved from DI,
one fresh scope per run. Multiple app instances can run at once without executing
the same scheduled slot twice. Everything beyond that — persistence, clustering,
dashboard, alerting — is a separate opt-in package.

**The actual product** is *DB-editable schedules + a dashboard + per-job alert rules*.
Distributed scheduling is table stakes that Hangfire and Quartz already have; it is
the cost, not the value.

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

Three storage interfaces, not one, because they differ in write volume, consistency
need, and what happens when they are unavailable. That split is what makes "no
infrastructure at all" a real mode instead of a degraded one.

---

## 3. The five things that actually matter

### 3.1 Claim the *occurrence*, not the job

The lock key is `{jobName}:{scheduledForUtc}`.

Why, in one line: a lock held for the duration of a run needs a TTL longer than the longest run,
which is unknowable, and that road ends in lease renewal, GC pauses and fencing tokens — a
distributed-systems project, not a scheduler feature. The README makes the argument in full.

Claiming the occurrence asks one question — *"has anyone already started this slot?"* — and once
answered it never needs re-answering. The consequence worth keeping here: the TTL only has to cover
clock skew plus tick jitter, so a fixed 60s is correct however long jobs run.

**The guarantee is: at most one instance *starts* a given occurrence** — not "at most one instance
is ever running this job". It is on the README's first screen, which is where this section asked for
it.

### 3.2 In SQL, the claim *is* the run row

```sql
CREATE UNIQUE INDEX UX_CadenceJobRun_Occurrence
    ON CadenceJobRun (JobName, ScheduledForUtc)
    WHERE ScheduledForUtc IS NOT NULL;   -- API/manual runs exempt
```

`TryClaim` is an `INSERT`. A unique violation (SQL Server 2601/2627, PostgreSQL 23505)
means someone else won. No lock primitive, and no window where a slot is claimed but
unrecorded.

Catch **only** those error codes. A blanket `catch` turns a dead connection into a
silently skipped run — the worst possible failure mode for a scheduler.

### 3.3 Never block the tick loop

No `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` anywhere in the tick path. One
synchronous wait on one slow job stalls every other schedule in the process. This is
the most common way schedulers of this shape die, so it gets an analyzer rule, not
just good intentions.

### 3.4 Two cancellation sources, kept apart

The `MaxDuration` CTS and the host-shutdown CTS are linked for the job, but tracked
separately so history can distinguish `TimedOut` from `Aborted`. Collapse them and the
history cannot answer "is this job slow, or is the host churning?".

The completion write uses `CancellationToken.None` — recording *why* a run ended must
not be cancelled by the shutdown that ended it.

### 3.5 The watchdog is the highest-value alert

A job that throws sends a failure alert. A scheduler that quietly died, or a job
someone disabled six weeks ago, sends **nothing**. `NotSucceededWithin` catches the
failure mode nobody notices. Make it prominent, and offer to create one automatically
(3× the cron interval) whenever a job is enabled in the dashboard.

Externally, alert on the `cadence.job.seconds_since_success` gauge. Absence of failure
is not evidence of success.

---

## 4. Layering, and what alerting adds to it

The table is in the README, which also carries the reason persistence and clustering are one step:
splitting them would let you deploy two instances with shared history and no coordinator, running
every occurrence twice while looking healthy in the logs. What is not there yet, because it is not
built:

**Alerting's key split:** channels (SMTP host, Twilio SID — secrets, infrastructure)
are registered in code. Rules ("email ops@ when invoice-sync fails twice") are edited
in the dashboard. The dashboard offers only channels that are actually registered, so
a rule can never reference a channel that will fail to dispatch.

**Throttling is not optional.** A `* * * * *` job failing all day is 1,440 emails.
The gate: consecutive-failure threshold → per-rule cooldown → suppression count in the
message body → one recovery notification.

---

## 5. Fail closed, twice

- **At boot:** every registered job is resolved from a real scope before the first
  tick. If it cannot be constructed, the process dies at deploy time instead of at
  02:00.
- **At the edge:** `MapCadenceApi()` / `MapCadenceDashboard()` refuse to map outside
  Development when nothing authenticates them. Shipping a package that can silently
  expose "run any registered job" to the internet is not acceptable. §13.3 has the
  full gate; the shape of it is that the refusal happens at *map* time, during startup
  and before the server listens, so a missing token fails the deploy rather than the
  night.

Be honest in the README about what *cannot* be checked: a Roslyn analyzer cannot
validate a DI graph, because registrations happen through arbitrary runtime code. The
analyzer validates *registration metadata* — duplicate names, bad cron literals,
unparseable durations. The graph is validated at boot, from a scope. Do not promise
"build-time DI checking".

---

## 6. Build order

| | Milestone | Contents |
|---|---|---|
| **v0.1** | Core | `IJob`, registration, tick loop, per-run scope, dual cancellation, in-memory stores, boot probe, OTel, graceful drain — *done* |
| **v0.2** | Persistence & clustering | claim, run history, janitor, instance registry, on SQL Server **and** Redis — *done*, with the Aspire host demonstrating it between real processes |
| — | **decision point** | *resolved — see below* |
| v0.3 | Control surface | the machine-callable API, the auth gate, health checks, distributed pause — *done* |
| v0.3.1 | Identity | `ICredentialStore` on both tiers, its conformance suite, login and sessions, API-token creation and revocation — §13.5 |
| v0.4 | Dashboard | overview, detail, schedule editing, manual trigger; React + Vite + Mantine, oxlint and oxfmt, bundle embedded at pack time |
| v0.5 | Alerting | rules, throttling, watchdog, SMTP + Twilio |
| v0.6 | Tooling | source-generated registration, analyzers, test host |

### The decision point, resolved

Whether to build v0.3–v0.5 on Quartz.NET's clustering instead, should our own coordination layer
overrun its budget. It did not: one filtered unique index on a table run history needed anyway, a
`TryClaimAsync` that is an `INSERT` and a check for 2601/2627, and a conformance suite both tiers
are held to. Quartz would have traded that for a dependency, a second scheduling model to reconcile
with this one, and misfire semantics we do not control.

**Coordinator kept.** `IOccurrenceCoordinator` stays the only seam that knows how a claim is won —
no longer because we might swap it wholesale, but because a second tier has to slot in underneath
it without Core noticing. Redis then did, without changing it.

---

## 7. Recommended answers to the open questions

Questions 5 (distributed pause) and 6 (one `MapCadence()` or two) are settled and owned elsewhere:
§12 and §13.1.

| # | Question | Recommendation |
|---|---|---|
| 1 | `QueueOne` semantics | **Cut from v1.** It is the only policy needing a per-job coalescing queue, and its `ScheduledFor` has no clean answer. `Skip` + `AllowConcurrent` cover the real cases. |
| 2 | Per-job concurrency caps | **Defer.** Global `MaxConcurrentRuns` + `Skip` is enough for v1. |
| 3 | Payload JSON Schema | **No.** Leave payloads opaque; the job validates. Saves a dependency and a UI surface. |
| 4 | Retry within run vs. reschedule | **Cut `MaxAttempts` from v1.** In-run retry makes duration and timeout ambiguous in history. Later, do it as a new run with `Trigger = Retry` and `ScheduledForUtc = null`, which sidesteps claim uniqueness entirely. |

---

## 8. Gaps still open

Numbered as first written, so the references from §13 still resolve. Closed since: **#1** (`Skip`
cannot be strict across instances — accepted and documented as local-strict, cluster-best-effort, in
the README beside the guarantee), **#4** (IANA ids need ICU — `CronParser` detects it and names
`InvariantGlobalization`), **#5** (`IWritableScheduleSource` split out) and **#6**
(`JobContext.Report` batching — `BatchingLogAppender`, flushing on 100 entries or 250 ms, dropping
rather than blocking, because back-pressure on `Report` would make a slow database into a slow job).

**Gap 2. A 1s tick that re-evaluates every job does not scale with job count.** Since sub-second
scheduling is an explicit non-goal, keep an in-memory min-heap of next occurrences, rebuilt only
when the change token fires. The tick then peeks the head instead of walking N schedules and their
stores.

**Gap 3. Schedule store unavailable — boot versus tick.** Undefined today. Recommendation: never
fail boot on a store blip. Start from code defaults, report degraded health, raise an alert. A
database hiccup must not stop the whole application from starting. *"Report degraded health" got
its mechanism in v0.3 — §13.4: two probes whose constructors are proven store-free by an allow-list
test, storage checks that report `Degraded` from the storage packages themselves, and the access
split that keeps the degraded signal away from the orchestrator — every replica shares one store, so
a probe that fails on a store blip fails on all of them at once.*

**Gap 7. Alert state in memory means a crash-loop resets cooldowns and floods.** Warn at
boot if alerting is enabled without a persistent store.

---

## 9. Measured behaviour and deliberate deviations

Recorded here because each was an assumption in the original spec that turned out to need
correcting. All are pinned by tests in `Cadence.Core.Tests` or `Cadence.Storage.Sql.Tests`.

### 9.1 Daylight saving, measured against Cronos 0.8.4 / Europe/Stockholm

| Case | Actual behaviour | Consequence |
|---|---|---|
| `30 2 * * *` on spring-forward day (2026-03-29, 02:30 local does not exist) | **Fires at 03:00 local / 01:00 UTC** — the instant the clock jumps | The job still runs that night, half an hour late. It is **not** skipped. |
| `30 2 * * *` on autumn-back day (2026-10-25, 02:30 local happens twice) | Fires once, at the first 02:30 (00:30 UTC, CEST) | No duplicate. The second 02:30 produces nothing. |
| `*/15 * * * *` across autumn-back | Continues on wall clock; the repeated hour yields 02:00, 02:15, 02:30, 02:45 twice in local terms | Distinct UTC instants, so occurrence keys never collide. |

The original design note claimed the spring-forward occurrence was *skipped*. It is
not. This matters: anyone reading "skipped" would conclude a nightly 02:30 job misses
one night a year and might build a catch-up around it. It doesn't — it runs late.

### 9.2 The run id is assigned before the claim, not after it

`TryClaimAsync` takes the run id from its caller. §3.2 requires that — a claim that generates its
own id is two rows colliding on one occurrence — but the property that made it worth an interface
change is that **the claim becomes idempotent.** A transient fault can drop the acknowledgement of
an insert that already committed; a blind retry then gets 2627 back, reports "someone else won", and
skips a run this instance owns, silently, which §3.2 identifies as the worst failure mode available.
With a caller-assigned id the retry asks whether the existing row is its own and answers exactly.
Without one, that question has no answer.

Rejected, for the record: a separate `CadenceOccurrenceClaim` table, which needs no Core change but
reintroduces the claimed-but-unrecorded window and cannot be made retry-safe; and a blind `UPDATE`
from `StartAsync`, which leaves the claim ignorant of the run id and has the same
retry hole.

### 9.3 A disabled job's occurrences are treated as never having existed

While a job is disabled, its evaluation point advances with the clock. Re-enabling it
therefore starts from the next occurrence rather than replaying the disabled period.

The spec's `MaxCatchUp` rationale implied the opposite — that a `*/5` job disabled for
a month would queue ~8,600 runs on re-enable, capped. Capping a footgun is worse than
not having it: nobody who ticks "enabled" in a dashboard means "and replay the last
month". `MaxCatchUp` still guards the case it should, which is host downtime.

### 9.4 Occurrence claiming elects a leader; it does not spread load

Measured in `samples/Cadence.Sample.AppHost`, three replicas against one SQL Server, twice:

| Replica start order | Occurrences won |
|---|---|
| first, by ~40 ms | **all of them** |
| second | none |
| third | none |

Both runs the same shape, and the winner changed between runs exactly as start order did: every
replica ticks on its own one-second timer whose phase is fixed by when the process started, and the
claim is a race to an `INSERT`, so a tick firing 40 ms earlier wins every race there is. Nothing is
broken by that — §3.1's guarantee is *at most one*, and one is what runs. Killing the leader mid-run
moved every subsequent claim to the next-earliest replica within one occurrence, and the janitor
marked the interrupted run `Lost` 21 seconds later.

The README says this next to the guarantee, because someone sizing a cluster on the assumption that
replicas share the load will size it wrong.

**Two fixes exist and neither has been taken.** Tick jitter (§14.2) spreads wins without touching
the claim; pulling work from a queue instead of executing what was claimed (§14.1) makes tick phase
stop deciding who works at all, which is why the cheaper fix waits on the better one. §14.3 records
what the finding costs under an orchestrator, where an autoscaler adds replicas that win nothing.

### 9.5 `Cadence.Core` takes the full health-checks package, not the abstractions

v0.3's constraint was that health checks enter Core through
`Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` only. Core references
`Microsoft.Extensions.Diagnostics.HealthChecks` instead, because `AddHealthChecks()` and `AddCheck`
live in the larger package and there is no way to register a check without them. The spirit of the
constraint holds — a test asserts `Cadence.Core`'s assembly carries no reference to
`Microsoft.AspNetCore.*`, even transitively — but the cost is real and worth naming: every Core
consumer now gets `DefaultHealthCheckService` and the health-check publisher hosted service, the
latter inert with no publisher registered. Recorded so nobody rediscovers it as a violation.

---

## 10. The end-to-end samples

`samples/Cadence.Sample.Worker` consumes Cadence **as a package from a local feed**, not by project
reference, which is the point of it: that is how it caught `NU5039`, a declared `PackageReadmeFile`
that was never packed. It also proves the telemetry fan-out end to end — MEL console output, OTel
log records carrying `JobName`/`RunId`/`InstanceId` as scope attributes, a `cadence.job` span with
its tags and a `cadence.job.progress` event, and `seconds_since_success`.

`samples/Cadence.Sample.AppHost` runs three replicas against one SQL Server; what it measured is
§9.4. **It is a demonstration, not the proof** — clustering is proven by `ClusteredSchedulingTests`,
five instances sharing one `SqlOccurrenceCoordinator` against a real SQL Server inside one test
process, in seconds, on every CI run. What only the Aspire host can show is real process boundaries,
a replica killed mid-run for the janitor to reap, and the `Skip` caveat happening in front of
someone — better in a sample we control than in someone's incident.

---

## 11. Two storage tiers, and what the second one proved

`Cadence.Storage.Redis` implements the same three interfaces as the SQL tier and is held to
the same three conformance suites. It is an **alternative**, not a layer: both replace the
same services, so calling both leaves whichever ran last winning on some and not others.

### 11.1 The claim is still the run

The obvious Redis coordinator is `SET key NX EX 60`, and it is wrong for this. A claim that expires
is a claim that can be won twice — not inside the tick's horizon, but by anything replaying an older
occurrence, which is exactly what catch-up after downtime does. §3.2 gets its property in SQL from
the claim being a permanent row; a tier whose claims quietly stop existing after a minute is not an
alternative to that, it is a different guarantee wearing the same interface. So the Redis claim is
permanent too, written by the same Lua script as the run's hash and its index entries, and removed
by the janitor with the run it belongs to.

### 11.2 The seam held, and one thing had to move

`IOccurrenceCoordinator` needed no change, which was the test §6 set for it. The janitor did: it
lived in `Cadence.Storage.Sql`, calling that store's internal maintenance methods, and Redis needed
the same four passes over completely different operations. Rather than a second copy of the policy —
reap before purge, batch, never escalate a failure into a scheduling problem — the policy moved to
`Cadence.Core` behind `IStorageMaintenance`, and each tier now supplies only the operations. The
part worth keeping: the seam that mattered second was the one nobody had named.

### 11.3 Where the tiers genuinely differ

Not in behaviour — the conformance suites are the point — but in operations, and the README's table
is the statement of that, durability verdict included.

One entry in it needs its reason recorded, because it looks like an omission: the schedule poll stays
enabled on Redis even though pub/sub delivers an edit in milliseconds. Redis pub/sub is
fire-and-forget with no redelivery, and a scheduler that had silently stopped noticing schedule edits
would look perfectly healthy while ignoring the dashboard.

---

## 12. Pause, and why it is two switches

Pause is **two switches, not one** — `PauseScope` is a flags enum, and paused occurrences are
treated as never having existed, which is §9.3's rule reused rather than a second policy. The README
states both, and why an operator wants a brake and an escape hatch separately. Three things it does
not state:

**The write rides the schedule version.** `SqlPauseStore` bumps `CadenceScheduleVersion` in the
transaction that writes the switches, and `RedisPauseStore` INCRs the counter and publishes on the
schedule channel. Neither tier adds anything for an instance to poll: the ticker re-reads the
switches on the same reload as the schedules, so a pause arrives on the machinery §11.3 already
measured. The cost of that reuse is one small read per instance per config poll, and the property
bought is that a pause and a schedule edit can never be observed out of order.

**The trigger gate reads through, the tick loop reads cached.** A trigger is rare enough to afford
a round trip, and someone pausing during an incident should not watch a run start ten seconds
later; the tick loop runs every second and cannot. The asymmetry is deliberate.

**In-memory, pause is process-local, and the conformance suite says so** — `IsDistributed` is
false for that tier and the cross-instance test skips rather than being quietly dropped. The
alternative, letting two in-memory stores share static state to make the test pass, would have made
the suite lie about the tier it was hardest to be honest about.

---

## 13. The control surface

v0.3 is the first package that is not a storage tier, and the first to reference ASP.NET Core.
Core stays on the `Extensions.*` abstractions.

### 13.1 Two trees, one options object

`MapCadenceApi()` mounts the machine-callable tree and authenticates it with a token.
`MapCadenceDashboard()` mounts the UI and the endpoints it needs, and authenticates those with an
operator session. Two trees rather than one because the callable API has to be switchable off: on
one tree "off" is a flag that leaves every route mounted and answering to a session, while on two it
is the absence of a line of code, so the routes do not exist and a leaked token has nothing to
reach. What the single tree was protecting still holds — one `CadenceApiOptions`, one gate, one
thing to secure — and the dashboard adds fields to that object in v0.4 rather than introducing a
second one.

Registration follows the storage tiers, because `CadenceBuilder.Services` was made public for
exactly this:

```csharp
builder.Services.AddCadence(cadence => cadence
    .UseSqlStorage(connectionString)
    .AddApi(api => api.BasePath = "/cadence"));

app.MapCadenceApi();          // v0.3
// app.MapCadenceDashboard(); // v0.4
```

`MapCadenceApi()` returns the `RouteGroupBuilder` it mounted, so a host can attach its own
conventions — rate limiting, CORS, OpenAPI metadata — to the tree it just added. For endpoints that
start jobs, rate limiting is a realistic ask.

### 13.2 The surface, and the one write that is not on it

```
POST /cadence/api/jobs/{name}/trigger    202  { runId, jobName, instanceId }
GET  /cadence/api/jobs                   200  [ job summary ]
GET  /cadence/api/jobs/{name}            200  job detail + recent runs
GET  /cadence/api/runs?job=&status=&from=&to=&instance=&limit=&offset=
GET  /cadence/api/runs/{id}              200  run detail incl. log
GET  /cadence/api/pause                  200  { scope, reason, setBy, setAtUtc }
PUT  /cadence/api/pause                  204
```

The layering table promised "trigger / status / schedule endpoints" for this package.
**Schedule writes are not here.**
The two writes are not equivalent: a triggered run is loud, appears in history, and is over. A
changed cron expression is silent and permanent, and nobody notices it until the night it does not
run. So a token can start work and stop work, and only a person can change when work happens.

Pause is on it deliberately, and it is the one write that earns its place: an alert that halts
scheduled work and pages a human is a real runbook, it is reversible, and §12 already scoped it.

Status codes fall out of exceptions that already exist, as RFC 9457 `ProblemDetails`:

| Outcome | Status | Source |
|---|---|---|
| run started | 202 | `DispatchResult.Started` — `TriggerAsync` returns before the job finishes |
| overlap policy refused | 409 | `DispatchResult.Skipped`, carrying `SkipReason` |
| triggers paused | 409 | `SchedulerPausedException`, with who paused it and why |
| job not registered here | 404 | `JobNotFoundException` |
| trigger kind not allowed | 400 | `TriggerNotAllowedException`, listing what the job does allow |

Responses are explicit records behind a source-generated `JsonSerializerContext` rather than
`JobRun` serialised directly, because otherwise every future storage column would be a public API
change.

**Three corrections the implementation forced.**

A disallowed trigger kind answers **400**, not the 409 this table first said. Nothing the caller
waits for changes a job's declared triggers, so it is a malformed request; 409 stays for a skipped
dispatch and a paused scheduler, which are a correct request at the wrong moment.

`GET /runs/{id}` had nothing to call — `IRunHistoryStore` had no by-id lookup, only queries and the
two last-run reads. It has one now, on all three tiers.

The 500-row cap bounds rows, not payload: both persistent tiers attach every run's progress entries
to a query result, so a capped list request was fetching 500 logs to render a view showing none.
`RunQuery.IncludeLog` (default `true`, so no existing caller moved) lets a list skip the second
query entirely rather than fetching and discarding it.

**Two boundaries worth stating so nobody rediscovers them.** An unparseable `status`, `from`, `to`,
`limit` or `offset` returns the framework's 400 with an empty body, not an RFC 9457 document —
parameter binding fails before any endpoint filter can reformat it, and owning those bodies would
mean hand-rolling five parsers. Same class as the `{id:guid}` route constraint answering 404 for a
malformed run id. And the trigger endpoint accepts **no payload**: accepting caller JSON would
widen what a token can do from "start the job as configured" to "start the job with arbitrary
input", which needs its own size and shape rules and its own line in the trust argument.
In-process callers can still pass one.

### 13.3 The gate

One authentication scheme, `CadenceToken`, reading `Authorization: Bearer`. Comparison hashes both
sides with SHA-256 before `CryptographicOperations.FixedTimeEquals`, so the compare is
fixed-length and token length does not leak. The scheme name is public as
`CadenceApiDefaults.AuthenticationScheme`, because a host writing its own policy has to be able to
name it without hardcoding a literal.

Evaluated when `MapCadenceApi()` runs — startup, before the server listens:

| Condition | Result |
|---|---|
| `options.RequireAuthorization("policy")` | maps; the host's policy governs |
| one or more tokens configured | maps; built-in policy requires an authenticated `CadenceToken` |
| `options.AllowUnauthenticated = true` | maps, warning logged **every start** |
| none of those, `IsDevelopment()` | maps, loud warning, **loopback callers only** |
| none of those, anything else | **throws**, naming all three remedies |

The composition rule is one sentence: **a named policy governs alone, and the token scheme
authenticates into it.** Token auth produces a principal carrying a `cadence:token` claim and the
host's policy decides whether that is sufficient, so an app with OIDC can accept both by writing
one policy: `MapCadenceApi` selects `options.PolicyName ?? (Tokens.Count > 0 ? Policy : null)`.

**The scheme and its built-in policy are registered conditionally, and only ever together.** Both
are wired through `AddOptions<...>().Configure<IOptions<CadenceApiOptions>>(...)` rather than at
`AddApi` time, because whether a token exists cannot be evaluated until options bind — and
resolving `AuthenticationOptions` *causes* that binding, which is stronger than merely deferring
until after it. The policy is deferred on the identical condition as the scheme: a policy naming a
scheme that was never registered is a 500 on every request, and the deployments that would hit that
are `AllowUnauthenticated` and Development — exactly where an operator least expects a hard
failure.

**The Development branch answers loopback callers only.** An endpoint filter on the group returns
403 with a `ProblemDetails` naming all three remedies when `RemoteIpAddress` is not loopback. It is
applied on that branch alone — not to `AllowUnauthenticated`, where a proxy or mesh in front makes
every legitimate caller non-loopback, and not where a policy was applied, where the request is
already authenticated. The case it closes is `ASPNETCORE_ENVIRONMENT=Development` surviving into a
container, which is among the commonest .NET misconfigurations and cost nothing before this
milestone; it now exposes "run any registered job" and "halt scheduling cluster-wide" to whatever
can reach the port. A developer on localhost sees no difference at all, which is why this is a
filter and not a second flag.

**A null `RemoteIpAddress` counts as loopback.** Kestrel over TCP always fills it in, so nothing
arriving over the network is null; null means a transport with no IP peer — an in-memory
`TestServer`, a Unix domain socket, a named pipe — none of which is the exposed TCP port the filter
exists to close. Refusing null instead would 403 every in-memory test host and every socket-fronted
deployment while closing nothing.

`AllowUnauthenticated` exists for an authenticating proxy or an mTLS mesh, and because without it
people reach for something worse — an anonymous wrapper, or a second port with no gate on it. One
named, logged, reviewable flag is the better failure.

**HTTPS is documented, not enforced.** A bearer token over plaintext is a leaked token, but TLS
terminates at the ingress in every real deployment, so an app-level requirement would break the
standard topology. Trying to detect whether something upstream terminated TLS guesses wrong in
both directions, so we do not guess.

Tokens come from configuration, and also from `CADENCE_API_TOKEN` (comma-separated) because
`Cadence__Api__Tokens__0=` is miserable in a compose file. Two sources is two places to look when
it does not work, so boot logs which source supplied them and how many.

**`PUT /pause` records who from the authenticated principal, never from the body.** The identity is
`token:{first 8 hex of the token's SHA-256}` — stable across restarts, so repeated pauses attribute
to the same caller, and not a secret. A `setBy` a caller could write would not be an audit field, so
`PauseRequest` has no such member; under `AllowUnauthenticated`, where there is no principal, it
records `api`.

### 13.4 Health, and why the probes must not know about storage

| Check | Tag | Registered by |
|---|---|---|
| `cadence-live` | `cadence.live` | `AddCadence()` — the process is up |
| `cadence-ready` | `cadence.ready` | `AddCadence()` — boot probe passed, jobs registered |
| `cadence-sql` | `cadence.storage` | `UseSqlStorage()` — `SELECT 1` |
| `cadence-redis` | `cadence.storage` | `UseRedisStorage()` — `PING` |

**The liveness and readiness checks are given no store to query.** That is the enforcement, not a
convention: they cannot fail on a store blip because they cannot see one. Every replica shares one
store, so a readiness probe that is honest about it takes every pod out of the service
simultaneously — and the dashboard returns 503 during precisely the incident someone opened it to
investigate, while the rolling deploy that would have fixed it stalls. The strict version is worse
still: liveness tied to the store turns a database hiccup into a cluster-wide crash loop, each
restart re-running the migrator against the store that is already struggling.

**The guarantee is structural, not a promise kept by discipline.** A reflection test asserts each
probe's constructor takes only an allow-listed parameter type — not a blacklist of the store
interfaces, which any of `IServiceProvider`, `Func<IPauseStore>` or `Lazy<T>` would have walked
straight through undetected. `ReadinessHealthCheck` takes the job count as a plain `int` captured
at registration rather than an `IJobRegistry`, because that dependency would make the guarantee
conventional rather than structural — this solution already has schedule sources backed by a
database. A second, one-line test asserts `Cadence.Core`'s assembly carries no reference to
`Microsoft.AspNetCore.*` at all.

Storage health is therefore reported to humans, alerting and the dashboard, never to the kubelet,
and it reports `Degraded` rather than `Unhealthy`. This is where §8 gap #3's "report degraded
health" lands. That behaviour is pinned by a test against a closed port, which needs no container;
the tests that exercise it against a live SQL Server or Redis are written but have not run on this
machine, where Docker is unavailable, so nothing here should be read as verified against a real
store.

**All three tags are namespaced, and that is what makes the guarantee hold under composition.**
`MapCadenceHealth()` selects purely by tag, so bare `live` and `ready` would silently adopt a host
check written the way the ASP.NET Core documentation writes them — `AddSqlServer(cs, tags:
["ready"])` — and put that database back on the readiness probe by the front door, which is the
cluster-wide 503 this whole section exists to prevent. A host check tagged `live` or `ready` now
joins neither probe.

Storage checks are registered by the storage packages as ordinary `IHealthCheck`s, which needs no
new Cadence seam. `MapCadenceHealth()` is a convenience with configurable paths; the tags are
documented so an app that already maps `/health` composes its own. The access split is
load-bearing:

- `/health/live`, `/health/ready` — **anonymous**. The kubelet cannot present a token.
- `/cadence/api/health/storage` — **behind the gate**. It returns the last store error.

`AddCadence()`, `UseSqlStorage()`, `UseRedisStorage()` and `AddApi()` are each safe to call twice.
Health-check registration is guarded explicitly, because the health check service throws on a
duplicate registration name where the rest of DI would just overwrite; `AddApi`'s deferred
`AddScheme` is guarded on `SchemeMap` for the same reason, and there the blast radius is wider —
`AuthenticationOptions` is app-wide, so an unguarded duplicate takes the *host's* authentication
down with Cadence's.

### 13.5 Identity, and the tier that has no store

v0.3.1. The shape is already set by §8 gap #5: `ICredentialStore` and `IWritableCredentialStore`,
because "turn off creation of users and tokens" should not be a flag that can be misconfigured. It
is the in-memory tier not implementing the writable half.

| | No storage package | `UseSqlStorage()` / `UseRedisStorage()` |
|---|---|---|
| Admin | one, from the environment | persisted; the environment admin bootstraps first boot |
| API tokens | from the environment, read-only | created, listed and revoked at runtime |
| Creation endpoints | not mounted | mounted |

**A session should be an opaque token in that same store, not a JWT.** The dashboard is a browser
client, so a JWT lives either in `localStorage`, where any XSS reads it, or in a cookie — and once
it is in a cookie it has bought nothing, while still costing a signing key that must reach every
replica identically and revocation that does not work, because an issued JWT cannot be withdrawn.
The store that API tokens need already exists by then; a session is another row in it. Logout
logs out, and there is no key to distribute.

Three consequences of N replicas each serving the dashboard, because the rule is that nothing
which matters may live in process memory:

1. **Sessions must be store-backed**, which is the argument above arriving from a second
   direction. Sign in on one replica, get routed to another, stay signed in — with no sticky
   sessions to configure.
2. **Revocation has to invalidate caches.** A replica that caches credentials keeps honouring a
   token revoked elsewhere until its cache expires — a silent window in which a withdrawn token
   still starts jobs. §11.3's change token and pub-sub already solve exactly this for schedule
   edits: polled on SQL, pushed on Redis, poll kept as the backstop. Reuse it rather than
   inventing a second mechanism.
3. **Login rate limiting has to be shared.** Per-process counters give an attacker rotating
   across replicas N times the attempt budget, and each replica's log looks unremarkable.
   Store-backed counters, or documented as the ingress's job — but not left implicit.

### 13.6 The topology the trigger forces

`IJobTrigger.TriggerAsync` ends in `JobExecutor.DispatchAsync`, so **a triggered run executes in
the process that received the request.** Two consequences, and the second is the one worth
documenting before someone deploys it:

1. Behind a load balancer, a manual run's `InstanceId` is chosen by the ingress, not by Cadence.
   Reads, schedule edits and pause are all correct from any replica, because they go to the shared
   store.
2. **A dashboard-only deployment that registers no jobs cannot trigger anything** — the registry is
   empty, so every trigger is a `JobNotFoundException`. Making it work needs cross-process
   dispatch, which is a queue, which §7 #1 and #4 cut on purpose.

So the supported shape is every replica mapping the API. `MapCadenceApi()` does **not** throw on an
empty registry — registering jobs behind a feature flag is legitimate, and a hard failure would
break it. Instead it warns at map time, and the trigger endpoint's 404 names the cause: *no job
named 'x' is registered in this instance (0 jobs registered)*. A misconfigured pod then diagnoses
itself from one response body.

---

## 14. Parked — written down, deliberately not built

Everything in this section is a decision recorded so it does not have to be re-derived, and so
nobody mistakes it for something the package does. None of it is scheduled. It is here because
half-designed futures interleaved with settled ones are what make a design hard to reason about,
and the fix is to say which is which.

### 14.1 Queue the claim, pull the work

§9.4 measured that occurrence claiming elects a leader rather than spreading load. This is the
architecture that would actually fix it, and the reason it is recorded rather than built is that it
changes semantics, not that it is hard.

**Split claiming from executing.** Every instance keeps ticking and racing to claim, exactly as
now — the unique index already makes concurrent claims safe. The winner writes a `Pending` run
instead of executing one. Then every instance, winner or not, pulls from that queue when it has
capacity. Load spreads because idle instances pull.

**There is no queue to install, because the claim already is one.** §3.2's "the claim is the run
row" turns out to also mean the claim is the work item:

```sql
UPDATE TOP (1) CadenceJobRun WITH (READPAST, UPDLOCK, ROWLOCK)
   SET InstanceId = @me, Status = 'Running', StartedAt = SYSUTCDATETIME()
OUTPUT inserted.*
 WHERE Status = 'Pending';
```

`READPAST` is what makes that safe for N instances at once: a puller skips rows another instance
has locked rather than blocking on them, and the `UPDATE … OUTPUT` is atomic, so ten instances
against one database each get a different row and none gets the same row twice. Redis has the same
shape with `BLMOVE` into a per-worker processing list. No broker, no new table, no new
infrastructure — the shared store is the coordination, exactly as it is today.

**And no leader.** A leader container dispatching to subcontainers is the obvious framing and the
wrong one: election needs leases, leases need fencing, and that is precisely the
distributed-systems project §3.1 refused to become. Because concurrent claims are already safe,
nothing needs electing.

What the migration costs, which is the point of writing it down:

| Piece | Fate |
|---|---|
| `IOccurrenceCoordinator`, `UX_CadenceJobRun_Occurrence` | **Kept verbatim.** Still answers "has this slot been taken", now gating an enqueue |
| `JobExecutor`, per-run scope, dual cancellation, history writes | **Unchanged.** A puller runs the identical path |
| Both tiers' run tables and keys | **Unchanged.** The queue is the rows already there |
| `ScheduleTicker` | Claim → enqueue, instead of claim → dispatch |
| The pull loop | **New.** The only genuinely new component |
| `IJobTrigger` | Enqueues instead of dispatching in-process — which dissolves §13.6 |
| An instance dying mid-item | The janitor already reaps runs whose heartbeat timed out; returning them to `Pending` rather than marking `Lost` is a policy change inside the component §11.2 moved into Core |

One new component and two rewired call sites, over seams that stay put. **The current design is a
stepping stone to this, not an obstacle** — the coordinator seam §11.2 proved holds is exactly what
makes it cheap, and a version of Cadence that had skipped coordination to stay simple would have a
harder migration, not an easier one.

Why not now: `InstanceId` stops meaning "where it was claimed", visibility timeouts appear,
`MaxCatchUp` needs re-examining against a queue that can back up, and history gains a `Pending`
state that every reader has to understand. Semantics to think about, not code to type.

### 14.2 Tick jitter

The small fix for §9.4 — jitter each instance's tick phase by a random fraction of `TickInterval`
so wins spread across instances without touching the claim. Still not taken, and §14.1 is the
reason it may never be: if pulling replaces dispatching, tick phase stops deciding who works and
jitter buys nothing. Taking jitter first would be spending a change to a load-bearing path on a
problem the better answer removes.

### 14.3 Deployment under an orchestrator

None of this is documented for users yet, and should not be until someone has actually deployed it.

**There is nothing to configure.** A plain `Deployment` with `replicas: 3` — no StatefulSet, no
leader-election sidecar, no pod affinity, no ordinal identity. That is §3.1 paying out rather than
luck: the claim is a row in the store, so nothing is tied to a pod's name or lifetime. `InstanceId`
needs no work either, since the default `{machine}:{pid}:{short-guid}` already resolves `machine`
to the pod name.

**The autoscaler does nothing.** Given §9.4, a horizontal autoscaler pointed at CPU adds replicas
that win nothing: the cluster scales and the throughput does not, and the only thing that currently
redistributes work is a rolling deploy changing which pod started first. Until §14.1 or §14.2,
replica count is failover capacity, and the README says so next to the guarantee.

**Three timeouts that all default to thirty seconds.** `terminationGracePeriodSeconds`,
`HostOptions.ShutdownTimeout` and `CadenceOptions.ShutdownDrainTimeout`. A job with a ten-minute
`MaxDuration` therefore gets SIGTERM, thirty seconds, and SIGKILL — the run dies mid-flight and the
janitor marks it `Lost` a heartbeat timeout later, which reads in history as an infrastructure
failure rather than the misconfiguration it is. The invariant is `terminationGracePeriodSeconds ≥
ShutdownTimeout ≥ ShutdownDrainTimeout ≥ the longest MaxDuration`.

**The inner pair is now checked, and only warns.** `ShutdownBudgetProbe` runs on the boot path
beside the graph validator and logs one warning per violation. It does not throw, which is a
departure from §5's fail-closed default and deliberate: every timeout in the chain defaults to
thirty seconds while `MaxDuration` does not, so the common violation is one that existing
applications are already running with, and failing their boot would turn an upgrade into an outage.
Nor could throwing ever be justified on the evidence — `terminationGracePeriodSeconds` is outside
the process, so a clean inner pair proves nothing about whether the run survives. The remaining
gap is that the bound comes from the *registered* maximum durations, which is what is knowable
before the first schedule read; a writable schedule source can raise one later and that edit is
unchecked. Validating the outermost value stays deployment documentation.

### 14.4 The compose proof

Aspire demonstrates multi-replica scheduling but supervises it, which is what makes it a
demonstration rather than a deployment. A docker-compose PoC — real containers, one SQL Server, N
workers behind a load balancer — is where §14.3 could be shown to someone rather than asserted:
kill the leader and watch the next claim move, shorten the grace period and watch a run land as
`Lost`.
