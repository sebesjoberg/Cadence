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
| `+ MapCadenceApi()` | trigger / status / schedule endpoints | an auth policy |
| `+ EnableDashboard()` | UI, schedule editing | an auth policy |
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
  Development when no authorization metadata is attached. Shipping a package that can
  silently expose "run any registered job" to the internet is not acceptable.

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
| **v0.2** | Persistence & clustering | claim, run history, janitor, instance registry, on SQL Server **and** Redis — *done*; the Aspire host follows on its own branch before v0.3 |
| — | **decision point** | *resolved — see below* |
| v0.3 | Control surface | API, the auth gate, distributed pause — *the writable schedule source and change tokens landed early, in v0.2* |
| v0.4 | Dashboard | overview, detail, schedule editing, manual trigger |
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
| 5 | Distributed pause | **Yes, v1.** One row, and it is exactly what you want during an incident. |
| 6 | Merge Api + Dashboard | **Merge the auth surface, keep the packages.** One `MapCadence()`, one policy, one options object; Dashboard depends on Api. Halves what has to be documented and secured. |

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

---

## 10. The end-to-end sample, and what it is waiting for

`samples/Cadence.Sample.Worker` runs one job every ten seconds and consumes Cadence
**as a package from a local feed**, not by project reference — which is why it caught
`NU5039` (a declared `PackageReadmeFile` that was never packed) on its first run.

It proves the telemetry fan-out end to end: MEL console output, OTel log records
carrying `JobName`/`RunId`/`InstanceId` as scope attributes, a `cadence.job` span with
the §14 tags and a `cadence.job.progress` event, and the metrics including
`seconds_since_success`.

### The Aspire version: one blocker cleared, one standing

The target sample — Aspire orchestrating SQL Server, two worker replicas contending for
the same occurrences, the dashboard rendering history — was blocked on two things:

| Blocker | Milestone | Status |
|---|---|---|
| `Cadence.Storage.Sql` | v0.2 | **Cleared.** Without the unique-index claim, two replicas both run every occurrence, so the sample would have demonstrated the bug rather than the guarantee |
| `Cadence.Dashboard` | v0.4 | Standing. The history sink has no reader, and in-memory history is per-instance and dies with the process — so the first Aspire host reads history from the log, not a UI |

**The first of the two consequences recorded here was wrong, and the correction matters.**
It read: *"The Aspire host is where clustering gets proven — N replicas, one run per
occurrence, cannot be tested in-process against `NoOpCoordinator`."* The premise is right
and the conclusion does not follow. `NoOpCoordinator` is not the only alternative: five
instances can share one `SqlOccurrenceCoordinator` against a real SQL Server inside one
test process, which is exactly what `ClusteredSchedulingTests` does. Clustering was proven
there, in seconds, on every CI run — where an Aspire host proves it once, by hand, for
whoever is watching the logs at the time.

So the Aspire host is a **demonstration**, not the proof, and it should be built for what
only it can show:

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
