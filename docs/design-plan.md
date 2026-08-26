# Cadence — Design Plan (condensed)

A readable companion to the full spec. The spec is the reference; this is the map.

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

Why this is the whole design: a lock held *for the duration of a run* needs a TTL
longer than the longest run — unknowable — so you are pushed into lease renewal,
which fails under GC pause or partition, which means you need fencing tokens. That is
a distributed-systems project, not a scheduler feature.

Claiming the occurrence asks one question — *"has anyone already started this slot?"* —
and once answered it never needs re-answering. The TTL only has to cover clock skew
plus tick jitter, so a fixed 60s is correct no matter how long jobs run.

**The guarantee is: at most one instance *starts* a given occurrence.** It is *not*
"at most one instance is ever running this job". A slow run can overlap the next
occurrence on a different instance. This is the thing users will misunderstand, so it
belongs on the README's first screen, not in a footnote.

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

## 4. Layering — what costs what

| Call | Gets you | Needs |
|---|---|---|
| `AddCadence()` | cron in code, in-memory history, single instance, OTel | nothing |
| `+ UseSqlStorage()` | persistence **and** clustering | a database |
| `+ MapCadenceApi()` | trigger, reads, pause | a token, or an auth policy |
| `+ MapCadenceDashboard()` | UI, schedule editing, manual trigger | a signed-in operator |
| `+ AddAlerting()` | rules, watchdog, throttling | channel config |

`UseSqlStorage()` alone is the documented "make it real" step — it brings the
coordinator with it, so persistence and clustering arrive together.

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
| **v0.1** | Core | `IJob`, registration, tick loop, per-run scope, dual cancellation, in-memory stores, boot probe, OTel, graceful drain |
| **v0.2** | Persistence & clustering | claim, run history, janitor, instance registry, on SQL Server **and** Redis — *done*, with the Aspire host demonstrating it between real processes |
| — | **decision point** | *resolved — see below* |
| v0.3 | Control surface | the machine-callable API, the auth gate, health checks, distributed pause — *pause has landed; the writable schedule source and change tokens landed early, in v0.2*. §13 is the design |
| v0.3.1 | Identity | `ICredentialStore` on both tiers, its conformance suite, login and sessions, API-token creation and revocation — §13.5 |
| v0.4 | Dashboard | overview, detail, schedule editing, manual trigger; React + Vite + Mantine, oxlint and oxfmt, bundle embedded at pack time |
| v0.5 | Alerting | rules, throttling, watchdog, SMTP + Twilio |
| v0.6 | Tooling | source-generated registration, analyzers, test host |

### The decision point, resolved: keep the coordinator

The question was whether to build v0.3–v0.5 on Quartz.NET's clustering instead, should our
own coordination layer overrun its budget. It did not. The whole cost:

- one filtered unique index, `UX_CadenceJobRun_Occurrence`, on a table run history needed anyway
- `SqlOccurrenceCoordinator`, whose `TryClaimAsync` is an `INSERT` and a check for 2601/2627
- a conformance suite, which both tiers are held to rather than only the SQL one

What it bought is in `ClusteredSchedulingTests`: two and five instances ticking against a real
SQL Server with one run per occurrence, successive occurrences landing on different instances,
and a schedule edited through one instance reaching the others.

Quartz would trade that for a dependency, a second scheduling model to reconcile with this one,
and misfire semantics we do not control. **Keep the coordinator.** `IOccurrenceCoordinator` stays
the only seam that knows how a claim is won — no longer because we might swap it wholesale, but
because a second tier has to slot in underneath it without Core noticing.

It since has. The Redis tier needed no change to that interface, which was the test set for it
here; what it did move was the janitor, and §11.2 records why.

---

## 7. Recommended answers to the open questions

| # | Question | Recommendation |
|---|---|---|
| 1 | `QueueOne` semantics | **Cut from v1.** It is the only policy needing a per-job coalescing queue, and its `ScheduledFor` has no clean answer. `Skip` + `AllowConcurrent` cover the real cases. |
| 2 | Per-job concurrency caps | **Defer.** Global `MaxConcurrentRuns` + `Skip` is enough for v1. |
| 3 | Payload JSON Schema | **No.** Leave payloads opaque; the job validates. Saves a dependency and a UI surface. |
| 4 | Retry within run vs. reschedule | **Cut `MaxAttempts` from v1.** In-run retry makes duration and timeout ambiguous in history. Later, do it as a new run with `Trigger = Retry` and `ScheduledForUtc = null`, which sidesteps claim uniqueness entirely. |
| 5 | Distributed pause | **Yes, v1 — done, as two switches rather than one.** See §12. |
| 6 | Merge Api + Dashboard | **Revised in v0.3: one options object, but two map calls.** The original answer was one `MapCadence()`. Building it found two audiences with two authentication mechanisms — a token for machines, a session for people — and one requirement that settles it: the callable API has to be switchable off. With one tree, "off" leaves every route mounted and answering to a session; with two it means not calling `MapCadenceApi()`, so the routes do not exist and a leaked token has nothing to reach. What survives of #6 is the part that mattered: one `CadenceApiOptions`, one gate, one thing to document and secure. See §13.1. |

---

## 8. Gaps worth closing before v0.1

1. **`Skip` cannot be strict across instances.** Checking run history for an in-flight
   run is a read with a race window — two instances can both see "nothing running" and
   both start. Either accept and document it as *local-strict, cluster-best-effort*, or
   reintroduce a job-level lock and the lease problem with it. Take the first option and
   say so plainly; this is the likeliest source of bug reports.

2. **A 1s tick that re-evaluates every job does not scale with job count.** Since
   sub-second scheduling is an explicit non-goal, keep an in-memory min-heap of next
   occurrences, rebuilt only when the change token fires. The tick then peeks the head
   instead of walking N schedules and their stores.

3. **Schedule store unavailable — boot versus tick.** Undefined today. Recommendation:
   never fail boot on a store blip. Start from code defaults, report degraded health,
   raise an alert. A database hiccup must not stop the whole application from starting.
   *"Report degraded health" gets a mechanism in v0.3 — §13.4 — and one constraint that
   was not obvious when this was written: the degraded signal must not reach the
   orchestrator. Every replica shares one store, so a probe that fails on a store blip
   fails on all of them at once.*

4. **IANA timezone ids need ICU.** `InvariantGlobalization=true` — common in slim
   containers — breaks `FindSystemTimeZoneById` for IANA ids. Detect at boot and fail
   with a message that names the property, rather than throwing per-tick later.

5. **`IScheduleSource.IsWritable` + `UpsertAsync` on one interface** forces
   non-writable sources to throw. Split out `IWritableScheduleSource`; the dashboard
   already branches on the capability.

6. **`JobContext.Report` writing straight through to SQL** lets a chatty job hammer the
   database. Buffer and batch-flush. *Closed in v0.2: `BatchingLogAppender` inside
   `SqlRunHistoryStore`, flushing on 100 entries or 250 ms. The buffer drops rather than
   blocks — back-pressure on `Report` would make a slow database into a slow job.*

7. **Alert state in memory means a crash-loop resets cooldowns and floods.** Warn at
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

§3.2 says the claim *is* the run row. The v0.1 code could not do that: `TryClaimAsync` took
only `(jobName, scheduledFor)` and `JobExecutor.DispatchAsync` generated its own
`Guid.NewGuid()` afterwards, so the claim's insert and the history insert were two rows
colliding on the same occurrence. `RecordSkippedAsync` had the identical problem — claim
wins, overlap gate skips, and the skip record collides with the claim row.

v0.2 adds a `runId` parameter to `TryClaimAsync` and threads a pre-assigned id through
`DispatchAsync` and `RecordSkippedAsync`. The seam stays one method returning `bool`.

Beyond making §3.2 implementable, this buys a property that is otherwise unreachable: **the
claim becomes idempotent.** A transient fault can drop the acknowledgement of an insert that
already committed; a blind retry then gets 2627 back, reports "someone else won", and skips a
run this instance owns — silently, which §3.2 identifies as the worst failure mode available.
With a caller-assigned id the retry asks whether the existing row is its own and answers
exactly. Without one, that question has no answer.

The alternatives, for the record. A separate `CadenceOccurrenceClaim` table needs no Core
change but reintroduces the claimed-but-unrecorded window and cannot be made retry-safe. A
blind `UPDATE` from `StartAsync` leaves the claim ignorant of the run id and has the same
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

Both runs, the same shape, and the winner changed between runs exactly as start order did. The cause
is not subtle: every replica ticks on its own one-second timer, whose phase is fixed by when the
process started, and the claim is a race to an `INSERT`. A replica whose tick fires 40 ms earlier
wins every race there is, forever, until it stops.

Nothing here is broken — §3.1's guarantee is about *at most one*, and one is what runs. But "three
replicas, so the work is spread three ways" is the obvious reading of a cluster, and it is wrong:
the other replicas are failover capacity that happens to be warm. Failover itself is immediate;
killing the leader mid-run moved every subsequent claim to the next-earliest replica within one
occurrence, and the janitor marked the interrupted run `Lost` 21 seconds later.

This belongs in the README next to the guarantee, because someone sizing a cluster on the assumption
that replicas share the load will size it wrong.

**A fix exists and is deliberately not being taken yet.** Jittering each instance's tick phase by a
random fraction of `TickInterval` would spread wins across replicas without touching the claim. It is
a small change to the tick loop and a real change to a load-bearing path, so it wants its own
decision rather than a ride on a sample's branch. Two things to weigh when it comes up: whether
even distribution is a property worth *promising* once it has been observed, and whether jitter makes
the tick's relationship to `ScheduledForUtc` harder to reason about than the current fixed phase.

Designing v0.3's deployment story raised the stakes on the finding without settling the fix. Under
an orchestrator this stops being a curiosity about sample output: a horizontal autoscaler pointed
at CPU adds replicas that win nothing, so the cluster scales while the throughput does not, and the
only thing that currently redistributes work is a rolling deploy reshuffling which pod started
first — §14.3.

It also turned up a better answer than jitter, which is why neither has been taken: if instances
pull work from a queue instead of executing what they claimed, tick phase stops deciding who works
and jitter buys nothing. That is §14.1, and §14.2 records why the cheaper fix is waiting on it.

---

## 10. The end-to-end samples

`samples/Cadence.Sample.Worker` runs one job every ten seconds and consumes Cadence
**as a package from a local feed**, not by project reference — which is why it caught
`NU5039` (a declared `PackageReadmeFile` that was never packed) on its first run.

It proves the telemetry fan-out end to end: MEL console output, OTel log records
carrying `JobName`/`RunId`/`InstanceId` as scope attributes, a `cadence.job` span with
the spec's §14 tags and a `cadence.job.progress` event, and the metrics including
`seconds_since_success`.

### The Aspire version: built, and v0.3 clears the last blocker

`samples/Cadence.Sample.AppHost` now runs three replicas against one SQL Server. What it
measured is §9.4. It was blocked on two things:

| Blocker | Milestone | Status |
|---|---|---|
| `Cadence.Storage.Sql` | v0.2 | **Cleared.** Without the unique-index claim, two replicas both run every occurrence, so the sample would have demonstrated the bug rather than the guarantee |
| `Cadence.Dashboard` | v0.4 | **Cleared early, by v0.3.** The blocker was never the UI, it was that the history sink had no reader, so the sample fell back to Aspire's own dashboard — which shows OTel, not `CadenceJobRun`. `GET /cadence/api/runs` is a reader, and it arrives a milestone before the dashboard does |

**The first of the two consequences recorded here was wrong, and the correction matters.**
It read: *"The Aspire host is where clustering gets proven — N replicas, one run per
occurrence, cannot be tested in-process against `NoOpCoordinator`."* The premise is right
and the conclusion does not follow. `NoOpCoordinator` is not the only alternative: five
instances can share one `SqlOccurrenceCoordinator` against a real SQL Server inside one
test process, which is exactly what `ClusteredSchedulingTests` does. Clustering was proven
there, in seconds, on every CI run — where an Aspire host proves it once, by hand, for
whoever is watching the logs at the time.

So the Aspire host is a **demonstration**, not the proof, and it was built for what only it can
show:

1. **Real process boundaries.** Separate processes, separate `InstanceId`s, a real network
   between them and the database, and a replica that can be killed mid-run to watch the
   janitor reap it. The in-process test deliberately fakes all of that away.
2. **Two replicas will expose the `Skip` caveat immediately.** A long-running job on
   replica A, next occurrence claimed by replica B, `Skip` configured, and the job runs
   anyway. Better to see that in a sample we control than in someone's incident.

---

## 11. Two storage tiers, and what the second one proved

`Cadence.Storage.Redis` implements the same three interfaces as the SQL tier and is held to
the same three conformance suites. It is an **alternative**, not a layer: both replace the
same services, so calling both leaves whichever ran last winning on some and not others.

### 11.1 The claim is still the run

The obvious Redis coordinator is `SET key NX EX 60`, and it is wrong for this. A claim that
expires is a claim that can be won twice — not inside the tick's horizon, but by anything
replaying an older occurrence, which is exactly what catch-up after downtime does. §3.2 gets
its property in SQL from the claim being a permanent row; a tier whose claims quietly stop
existing after a minute is not an alternative to that, it is a different guarantee wearing
the same interface.

So the Redis claim is permanent too, written by the same Lua script as the run's hash and
its index entries, and removed by the janitor with the run it belongs to. Retention therefore
bounds how far back double-execution is prevented — thirty days by default — and both tiers
behave identically, because in both the claim *is* the run.

### 11.2 The seam held, and one thing had to move

`IOccurrenceCoordinator` needed no change, which was the test §6 set for it.

The janitor did. It lived in `Cadence.Storage.Sql`, calling that store's internal maintenance
methods, and Redis needed the same four passes over a completely different set of operations.
Rather than a second copy of the policy — reap before purge, batch, never escalate a failure
into a scheduling problem — the policy moved to `Cadence.Core` behind `IStorageMaintenance`,
and each tier now supplies only the operations. That is the shape §6 asked for and the
coordinator alone would not have revealed: the seam that mattered second was the one nobody
had named.

### 11.3 Where the tiers genuinely differ

Not in behaviour — the conformance suites are the point — but in operations:

| | SQL Server | Redis |
|---|---|---|
| Durability | a committed run is committed | whatever the Redis is configured for |
| Schema | migrator, application lock, reviewable scripts | none; keys appear when written |
| Query surface | any filter, indexed | fast by job, instance and time; status alone walks the index |
| Schedule changes | polled | pushed, with the poll kept as a backstop |

**Durability is the deciding one, and the README says so plainly rather than selling the
tier.** With Redis's defaults a restart can lose recent writes, claims included, which is
the one failure the coordinator exists to prevent. Anyone choosing Redis is trading a bounded
window of double-execution risk for not running a database, and should be told that in those
words before they choose.

The pushed schedule change is the one place Redis is straightforwardly better: an edit
reaches other instances in milliseconds instead of within a poll interval. The poll stays
anyway — Redis pub/sub is fire-and-forget with no redelivery, and a scheduler that silently
stopped noticing schedule edits would look perfectly healthy while ignoring the dashboard.

---

## 12. Pause, and why it is two switches

§7 answered "distributed pause?" with *yes, one row*. Building it turned one row into two switches,
because the incident it exists for wants them apart: stop the automatic work, keep the ability to
run one job by hand. A single switch forces a choice between an operator with no brake and an
operator with no escape hatch. `PauseScope` is therefore a flags enum — `Schedule`, `Triggers`,
both, neither — and the one row holds it along with who set it and why.

**A paused occurrence is treated as never having existed**, which is §9.3's rule reused rather than
a second policy: the ticker takes the same branch as a disabled job, so the evaluation point
advances and resuming starts from the next occurrence. Anyone who pauses for an hour and expects
the hour back on resume is expecting the thing §9.3 explains nobody means.

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
operator session. §7 #6 asked for a single `MapCadence()`; two audiences with two authentication
mechanisms did not survive it, and one requirement decided it — the callable API has to be
switchable off. On one tree "off" is a flag that leaves every route mounted and answering to a
session. On two it is the absence of a line of code, so the routes do not exist and a leaked token
has nothing to reach.

What §7 #6 was actually protecting still holds: one `CadenceApiOptions`, one gate, one thing to
secure. The dashboard adds fields to that object in v0.4 rather than introducing a second one.

Registration follows the storage tiers, because `CadenceBuilder.Services` was made public for
exactly this:

```csharp
builder.Services.AddCadence(cadence => cadence
    .UseSqlStorage(connectionString)
    .AddApi(api => api.BasePath = "/cadence"));

app.MapCadenceApi();          // v0.3
// app.MapCadenceDashboard(); // v0.4
```

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

§4 originally promised "trigger / status / schedule endpoints". **Schedule writes are not here.**
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

`DispatchResult.Skipped` is the row to watch. It is not an error, and it is not success, and the
easy mistake is to answer 200 with an empty body — which tells a caller that a run started when
none did. Three further choices:

- **The endpoint passes `TriggerKind.Api`, not `Manual`**, so history separates "someone clicked"
  from "something called us". `JobTrigger` already refuses `Schedule` on its own.
- **`limit` is capped at 500** whatever is asked for. `RunQuery.Limit` has no ceiling of its own,
  which makes an unbounded `limit` a one-request denial against the history store.
- **Responses are explicit records behind a source-generated `JsonSerializerContext`.** Serialising
  `JobRun` directly would make every future storage column a public API change.

### 13.3 The gate

One authentication scheme, `CadenceToken`, reading `Authorization: Bearer`. Comparison hashes both
sides with SHA-256 before `CryptographicOperations.FixedTimeEquals`, so the compare is
fixed-length and token length does not leak.

Evaluated when `MapCadenceApi()` runs — startup, before the server listens:

| Condition | Result |
|---|---|
| `options.RequireAuthorization("policy")` | maps; the host's policy governs |
| one or more tokens configured | maps; built-in policy requires an authenticated `CadenceToken` |
| `options.AllowUnauthenticated = true` | maps, warning logged **every start** |
| none of those, `IsDevelopment()` | maps, loud warning |
| none of those, anything else | **throws**, naming all three remedies |

The composition rule is one sentence: **a named policy governs alone, and the token scheme
authenticates into it.** Token auth produces a principal carrying a `cadence:token` claim and the
host's policy decides whether that is sufficient, so an app with OIDC can accept both by writing
one policy. No OR-policy machinery.

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

### 13.4 Health, and why the probes must not know about storage

| Check | Tag | Registered by |
|---|---|---|
| `cadence-live` | `live` | `AddCadence()` — the process is up |
| `cadence-ready` | `ready` | `AddCadence()` — boot probe passed, jobs registered |
| `cadence-sql` | `cadence.storage` | `UseSqlStorage()` — `SELECT 1` |
| `cadence-redis` | `cadence.storage` | `UseRedisStorage()` — `PING` |

**The liveness and readiness checks are given no store to query.** That is the enforcement, not a
convention: they cannot fail on a store blip because they cannot see one. Every replica shares one
store, so a readiness probe that is honest about it takes every pod out of the service
simultaneously — and the dashboard returns 503 during precisely the incident someone opened it to
investigate, while the rolling deploy that would have fixed it stalls. The strict version is worse
still: liveness tied to the store turns a database hiccup into a cluster-wide crash loop, each
restart re-running the migrator against the store that is already struggling.

Storage health is therefore reported to humans, alerting and the dashboard, never to the kubelet,
and it reports `Degraded` rather than `Unhealthy`. This is where §8 gap #3's "report degraded
health" lands.

Storage checks are registered by the storage packages as ordinary `IHealthCheck`s, which needs no
new Cadence seam. `MapCadenceHealth()` is a convenience with configurable paths; the tags are
documented so an app that already maps `/health` composes its own. The access split is
load-bearing:

- `/health/live`, `/health/ready` — **anonymous**. The kubelet cannot present a token.
- `/cadence/api/health/storage` — **behind the gate**. It returns the last store error.

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
ShutdownTimeout ≥ ShutdownDrainTimeout ≥ the longest MaxDuration`, and nothing checks it. Two of
the three are outside the process, so only the inner pair can be validated; that much is a boot
probe in the §5 spirit, and the outer one is deployment documentation.

### 14.4 The compose proof

Aspire demonstrates multi-replica scheduling but supervises it, which is what makes it a
demonstration rather than a deployment. A docker-compose PoC — real containers, one SQL Server, N
workers behind a load balancer — is where §14.3 could be shown to someone rather than asserted:
kill the leader and watch the next claim move, shorten the grace period and watch a run land as
`Lost`.
