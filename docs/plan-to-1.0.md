# Cadence — the plan to 1.0

Where the project is, what is left, and in what order. The *why* behind everything already built is
[`design-plan.md`](design-plan.md), whose section numbers (§3.2, §13.4, …) are cited from source
comments and the README and do not move. This file cites them; it does not renumber them.

Every item below is tagged:

- **Settled** — follows from a decision already made. Build it; do not re-litigate it.
- **Proposed** — my reading of what the existing constraints imply. Needs a yes before it is built.
- **Open** — a real question with no answer yet. Settle it while building, and record the answer.

---

## Where we are

v0.1 through v0.4 are done: scheduling, two storage tiers under one conformance suite against a real
server in CI, distributed pause, the machine-callable API and its gate, health checks, OIDC sign-in
with API tokens, and the operator dashboard. Nothing is published to NuGet.

**And one structural problem is now visible.** Every replica is symmetric and does everything, so
every feature that needs "only one of you should do this" has invented its own answer:

| | Only one replica should… | Mechanism |
|---|---|---|
| 1 | start this occurrence | filtered unique index / permanent Lua claim (§3.2, §11.1) |
| 2 | see a consistent pause | shared store, version bumped in the same transaction; read-through for triggers, cached for the tick loop (§12) |
| 3 | notice a schedule edit | config poll, kept even on Redis because pub/sub is lossy (§11.3) |
| 4 | run a `Skip` job | exclusive job-name key on the run row, released by the outcome write or the reap (§8 gap 1) |
| 5 | know who is alive | heartbeat plus janitor reap |

Each is correct. Together they are five designs for one idea, and alerting's watchdog was on course to
be a sixth. The fix is already written down as §14.1 and was parked; it is now the next milestone, for
reasons under **v0.5**.

## What 1.0 means

1.0 is not "everything imagined". It is: the three pillars work, coordination is one concept rather
than six, the sharp edges are fixed or documented, and the package is published under a name that is
ours.

| | Gate |
|---|---|
| Topology | work is pulled, not raced for; adding a replica adds throughput |
| Alerting | rules editable in the dashboard, delivered over SMTP and SMS, throttled, watchdog included |
| Tooling | the tick-blocking analyzer exists, registration needs no startup reflection, jobs are testable without a host |
| Debt | the open gaps closed, or consciously deferred with a documented reason |
| Verification | every conformance suite green on both tiers in CI |
| Publishing | a name that is not taken, packed cleanly, with a support statement |

---

## Milestone v0.5 — Queue the claim, pull the work

Promoted from §14.1, which designed it fully and parked it. **Not a leader.** §3.1 and §14.1 both
reject election and are right to: leases need fencing, and that is a distributed-systems project
instead of a scheduler feature. This is a producer/consumer split in which the store *is* the queue.

### Why now rather than after 1.0

§14.1's stated reason for waiting was semantics, not difficulty — and every one of those semantics is
a **public contract that is free to change now and breaking to change later**:

- `RunStatus` gains `Pending`. It is a public enum in `Cadence.Abstractions` with explicit numeric
  values that consumers will `switch` over.
- `InstanceId` stops meaning "where it was claimed" and starts meaning "where it ran".
- `MaxCatchUp` needs re-examining against a queue that can back up.

Three further things it collects on the way, each of which the record currently apologises for:
load spreading (§9.4 — the first replica to start wins every race), the autoscaler that adds replicas
winning nothing (§14.3), and the dashboard-only replica that cannot trigger anything (§13.6, which
§14.1 says this "dissolves"). And it stops alerting from needing a sixth coordination mechanism.

### What changes

**Settled — claiming is untouched.** Every instance keeps ticking and racing to claim. The unique
index already makes concurrent claims safe, which is precisely why nothing needs electing.

| Call site | Today | After |
|---|---|---|
| `ScheduleTicker.cs:188` | `TryClaimAsync` | **unchanged** |
| `ScheduleTicker.cs:203` | `_executor.DispatchAsync(...)` | writes a `Pending` run and returns |
| — | — | **new:** the pull loop, the only genuinely new component |
| `CadenceJanitor.cs:111` | reaps an abandoned run to `Lost` | returns it to `Pending` so it is retried |
| `IJobTrigger` | dispatches in-process | enqueues — which is what dissolves §13.6 |

**Settled — there is no queue to install, because the claim already is one.** §3.2's "the claim is the
run row" also means the claim is the work item.

**Settled — the pull is filtered by what the puller can actually run.** This is the one change to
§14.1's original design, and it must be in from the start:

```sql
UPDATE TOP (1) CadenceJobRun WITH (READPAST, UPDLOCK, ROWLOCK)
   SET InstanceId = @me, Status = 'Running', StartedAt = SYSUTCDATETIME()
OUTPUT inserted.*
 WHERE Status = 'Pending'
   AND JobName IN (SELECT Name FROM @MyJobs);   -- this replica's registry
```

`READPAST` is what makes this safe for N instances at once: a puller skips rows another instance has
locked rather than blocking, and `UPDATE … OUTPUT` is atomic, so ten instances each get a different row
and none gets the same row twice. No broker, no new table, no new infrastructure.

### Heterogeneous fleets, for free

The filter is what lets **one pool of containers register jobs X, another register jobs Y, and both
share one scheduler.** Heavy ETL on a big-memory pool, light webhook work on a cheap pool, a job that
needs a particular network segment or a GPU — all of it is just two deployments registering different
jobs against one store.

**And it costs no new storage, because a puller already knows its own capabilities.** `IJobRegistry` is
in the process; the filter is the local registry, passed as a table-valued parameter on SQL and as the
key set to `BLPOP` on Redis (which takes multiple keys, unlike `BLMOVE` — so Redis wants a list per job
rather than one global list). Nothing has to be published anywhere for this to work.

Two consequences to design for rather than discover:

- **Settled — the index and the Redis key layout must assume the filter.** A pull that is
  `Status + JobName` wants an index that covers both, and one Redis list per job rather than one
  global list. These are the two most expensive things to change after 1.0 — a migration and a key
  layout — so they get designed for the filtered pull even if nothing exercises pools on day one.
- **Open — work nobody serves.** If the scheduler enqueues an occurrence for a job no live replica
  registers, the row sits `Pending` forever. That is *visible* rather than dangerous — it is in history,
  and it is precisely what a "no worker for this job" alert should fire on — but it needs a policy:
  does the janitor expire it, does the scheduler stop enqueuing after N unservable occurrences, or does
  it become a health signal and nothing more?
- **Open — what `MaxConcurrentRuns` means now.** It becomes the pull loop's concurrency, and with pools
  it is per-pool, which is more useful than a global number but is a different setting than the one
  documented today.

**The store-side catalog is an improvement on this, not a prerequisite for it.** Publishing each
replica's job list (see *The coordinator as a container image*) is what would let the *scheduler* skip
unservable work rather than enqueuing it, and let the dashboard show which replicas can run which job.
Worth doing — but after pools already work.

**Settled — every replica keeps doing both.** No `Role` option in this milestone. Every replica ticks
*and* pulls, which is today's deployment shape, needs no new configuration, and keeps §14.3's "there
is nothing to configure" true. A declared `Scheduler`/`Worker` split becomes cheap once the queue is
proven and is a follow-on, not part of this. It must never become an *elected* one.

**Settled — what does not move.** `JobExecutor`, the per-run scope, dual cancellation (§3.4) and the
history writes are unchanged: a puller runs the identical path. Both tiers' run tables and keys are
unchanged, because the queue is the rows already there. `IOccurrenceCoordinator` and
`UX_CadenceJobRun_Occurrence` are kept verbatim, now gating an enqueue rather than a dispatch.

### Semantics to settle while building

- **Open — the visibility timeout.** A pulled run whose instance dies is currently found by the
  heartbeat reap. Is that still the only mechanism, or does a pulled-but-not-started row need its own
  shorter timeout? The reap interval is tuned for long-running jobs, which is the wrong scale for a
  row that should have started within a second.
- **Open — `MaxCatchUp` against a backed-up queue.** Today it caps how many missed occurrences get
  planned. With a queue, the cap and the backlog are two different things, and truncating a queue that
  is merely slow would drop work that was going to run.
- **Open — does `Pending` count as a run in history?** Every reader — the API, the dashboard, the
  retention trim, the alerting conditions — has to have an answer. "Yes, and it is filterable" is the
  cheapest, but it means every existing status filter changes meaning slightly.
- **Open — capacity.** `MaxConcurrentRuns` currently gates dispatch. It becomes the pull loop's
  concurrency, which is a cleaner place for it, but the interaction with `Skip` (§8 gap 1's exclusive
  key) needs stating: a `Skip` job's second occurrence should probably never be enqueued at all.
- **Proposed — do not add a second timer.** The pull loop should be a long-lived consumer with a
  blocking or backoff wait, not a second thing on a one-second tick. Two independent timers per
  replica is how the coordination sprawl started.

### Done when

- [ ] `ScheduleTicker` enqueues; nothing on the tick path dispatches
- [ ] The pull loop exists, with `READPAST` on SQL and `BLMOVE` on Redis, under one conformance suite
- [ ] A cluster test: N instances, one due occurrence, **one** run — and N instances, N due
      occurrences, **work on more than one instance** (the assertion §9.4 currently cannot make)
- [ ] Killing a puller mid-run returns its row to `Pending` and another instance completes it
- [ ] A fleet test: pool A registers jobs X, pool B registers jobs Y, one store — each pool runs only
      its own, and neither starves the other
- [ ] A replica never pulls a job it has not registered, asserted directly rather than inferred
- [ ] `RunStatus.Pending` is understood by the API, the dashboard, retention and the janitor
- [ ] A dashboard-only replica can trigger a job (§13.6 dissolved), with a test
- [ ] The README's "replicas are failover, not load spreading" section is rewritten, not merely edited
- [ ] §9.4 is re-measured on the Aspire host and the new numbers replace the old ones in the record

---

## Milestone v0.6 — Alerting

The missing third pillar. The most valuable thing in it is the alert nobody else sends: a job that
throws is loud, but a scheduler that quietly died, or a job someone disabled six weeks ago, is silent.
`NotSucceededWithin` is the feature (§3.5).

### Shape

**Settled — channels in code, rules in the store.** SMTP hosts and Twilio credentials are secrets and
infrastructure, so they are registered in code. Rules are operational and belong to whoever is on
call, so they are edited in the dashboard. The dashboard offers only channels actually registered, so
a rule can never name a channel that will fail to dispatch. (§4 stated this before any of it was
designed; it still holds.)

**Proposed — three packages, not one.** `Cadence.Alerting` carries rules, evaluation and throttling
and takes no new dependency; `Cadence.Alerting.Smtp` and `Cadence.Alerting.Twilio` carry one channel
and its dependency each. This is the storage-tier pattern, for the reason §13.5 records about
identity's dependency cost: a consumer who wants email should not pay for an SMS SDK, and each
channel's cost should be visible in the package graph.

### Firing: every notification is a work item

**Settled — sending an alert is work, pulled from the queue like any other.** Not only the watchdog:
*every* dispatch. This is the whole reason v0.5 comes first, and it is what the milestone rests on.

A notification is enqueued once — unique key `{ruleId}:{firedForUtc}`, gated by the same index that
gates an occurrence — and then pulled. Four properties fall out, and the third is the one that matters
most:

1. **One replica sends** because one replica pulls it, not because five raced and four lost. No sixth
   coordination mechanism.
2. **Enqueueing is one insert**, so §3.3's rule holds trivially: nothing on the tick path and nothing
   on the outcome write waits on an SMTP round trip. The completion write keeps using
   `CancellationToken.None` (§3.4) and gains no new way to fail.
3. **A failed send is retried, because the item is still there.** An earlier draft of this plan routed
   run-outcome alerts through an in-process channel that dropped under back-pressure, on the
   `BatchingLogAppender` precedent (§8 gap 6). That precedent does not transfer. Dropping a progress
   log entry costs a line of diagnostics; dropping the notification that says production is down is
   the single thing an alerting system must not do. A durable work item is the difference between
   best-effort and delivered.
4. **A replica dying mid-send loses nothing** — the item is re-pulled, which is the retry, free.

**Open — what the queue carries.** Either *"deliver this notification"* (low volume; evaluation stays
in-process) or *"evaluate this run"* (fully durable, but an item per run completion, which is the
highest-volume event in the system). Recommend the former: `IAlertStateStore` is already required to
be persistent (gap 7), so a replica that crashes mid-evaluation re-evaluates from stored state on the
next run or the next sweep, and nothing is lost by keeping evaluation cheap and in-process.

**Open — delivery retry policy.** A durable item retried forever against a misconfigured SMTP host is
its own incident. Needs a cap, a backoff, and a dead-letter state an operator can see in the
dashboard — which is a fifth `RunStatus`-like concern and should be settled before it is built.

### The storage seam

**Settled — two new abstractions, both tiers, a conformance suite each.** Alerting adds to the
existing list rather than getting an exemption. `IAlertRuleStore` / `IWritableAlertRuleStore` mirrors
`IScheduleSource` / `IWritableScheduleSource`; `IAlertStateStore` holds the per-rule consecutive-failure
count, last-fired, cooldown-until and suppression count, and has no existing analogue.

**Settled — alert state must be persistent, and boot must say so when it is not.** This is gap 7:
state in memory means a crash-loop resets every cooldown and floods the on-call phone. A warning at
boot, not a throw, in the same place the shutdown-budget probe warns (§14.3) and for the same reason —
an in-memory deployment is a legitimate way to try the package out.

**Settled — the janitor's new pass goes in Core.** §11.2 moved that policy behind
`IStorageMaintenance` precisely so a second tier could not fork it. Alert state outliving its rule,
and fired-notification records past retention, are purged there.

### Conditions

**Proposed — four, and no more for 1.0.**

| Condition | Fires when | Notes |
|---|---|---|
| `OnFailure(n)` | n consecutive failed runs | n=1 is the common case |
| `NotSucceededWithin(t)` | no successful run in t | the watchdog; the reason this milestone exists |
| `DurationExceeded(t)` | a run took longer than t | distinct from `MaxDuration`, which kills the run |
| `OnLost` | the janitor found a run whose instance died | see the open question below |
| `NoWorkerFor(job)` | an occurrence has been `Pending` longer than t with no replica able to run it | falls out of v0.5's fleet filter; the failure mode a pool deployment actually has |

**Settled — `NoWorkerFor` is why this list grew.** Once pools exist (v0.5), the characteristic
production failure is no longer "the job failed" but "the pool that serves this job is gone, and the
work is piling up unserved". Nothing else in the system reports that, and it is the same class of
silence `NotSucceededWithin` exists for.

**Open — what `OnLost` means after v0.5.** Today a reaped run is `Lost` and nobody is told, which is
worth alerting on. After v0.5 the janitor returns an abandoned run to `Pending` and it is retried, so
the condition is no longer "work was dropped" but "a replica died mid-work" — still worth knowing,
lower urgency, and possibly better expressed as a threshold over a window than a per-run alert.

### The throttling gate

**Settled.** A `* * * * *` job failing all day is 1,440 emails. In order: consecutive-failure
threshold → per-rule cooldown → suppression count in the message body → exactly one recovery
notification. State lives in `IAlertStateStore` so the cooldown holds cluster-wide, for the reason §12
puts the pause switches in the store rather than in each process.

**Settled — offer the watchdog automatically.** When a job is enabled in the dashboard, offer a
`NotSucceededWithin` rule at 3× the cron interval. The alert with the highest value is the one nobody
thinks to configure.

### Who may edit a rule

**Settled — a person, not a scope.** An alert rule is silent, permanent configuration whose failure
mode is *not being paged*. That is the same class as a cron expression, and §13.2 and §13.7 already
drew the line: rule writes require a user principal, not `Operate`, and are not on the machine tree —
exactly as schedule writes are not. Under a host-named policy that policy governs alone, derived from
`PolicyName is null` so it cannot drift from the two rules that already work this way.

### Open questions

- **Rule scope.** Per-job, or can one rule fan across jobs (`jobName = null` meaning all)? Fan-out is
  what an operator wants for "page me if anything fails", but it complicates the state key and the
  suppression count.
- **The SMTP library.** `System.Net.Mail.SmtpClient` is documented by Microsoft as not recommended for
  new work; MailKit is the usual answer and is a real dependency. Only `Cadence.Alerting.Smtp` pays
  either way, but the choice should be deliberate. Twilio needs no SDK — its REST API is a
  form-encoded POST, so `HttpClient` covers it.
- **Retention.** Does fired-notification history obey `RetentionOptions` alongside run history, or get
  its own budget? Run history is high-volume and alerts are not.

### Done when

- [ ] `IAlertRuleStore`, `IWritableAlertRuleStore` and `IAlertStateStore` on in-memory, SQL and Redis
- [ ] A conformance suite per interface, green on both persistent tiers in CI against a real server
- [ ] A cluster test: N instances, one due watchdog rule, **one** notification sent
- [ ] A throttling test: threshold, cooldown, suppression count, single recovery
- [ ] Boot warns when alerting is registered without a persistent state store (gap 7)
- [ ] Dashboard rules screen, offering only registered channels, requiring a user principal
- [ ] The enable-a-job flow offers the 3× watchdog rule
- [ ] README section, and both Aspire samples wiring one real rule
- [ ] `Cadence.Alerting` takes no dependency beyond what Core already has

---

## Milestone v0.7 — Tooling

Three deliverables, in descending order of value.

### The analyzer

**Settled — the tick-blocking rule is why this milestone exists.** §3.3 says a synchronous wait in the
tick path stalls every schedule in the process, and that it "gets an analyzer rule, not just good
intentions". That promise is currently unkept.

**Proposed — four diagnostics, shipped as an analyzer asset on `Cadence.Core` so consumers get them
without opting in.**

| | Diagnostic |
|---|---|
| Error | `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` inside `IJob.ExecuteAsync` or any Cadence-invoked path |
| Error | two jobs registered under the same name |
| Error | an invalid cron literal or unparseable duration in `[ScheduledJob]` or a fluent registration |
| Warning | an `IJob` carrying `[ScheduledJob]` that nothing ever registers |

**Settled — do not promise build-time DI checking.** §5 is explicit: registrations happen through
arbitrary runtime code, so an analyzer cannot validate the graph. It validates *registration
metadata*; the graph is validated at boot from a real scope. The README must not blur those.

### Source-generated registration

**Settled — the target is `AddJobsFrom(Assembly)`**, which today walks `assembly.GetTypes()` at
startup: reflection on the boot path, hostile to trimming and AOT.

**Proposed — generate, keep the old call working.** The generator emits registrations from
`[ScheduledJob]` at compile time; `AddJobsFrom` stays source-compatible and prefers generated metadata
when present, falling back to the reflection walk otherwise. **Open:** whether an assembly with the
generator active should make the reflection path throw rather than silently double-register.

### The test host

**Proposed — `Cadence.Testing`, built on what already exists.** `ISystemClock` is already an
abstraction and the in-memory stores are already a supported tier, so this is mostly assembly rather
than new machinery.

```csharp
await using var host = CadenceTestHost.Create(c => c.AddJob<InvoiceSync>());
host.Clock.AdvanceTo("2026-09-01T02:30:00Z");
var run = await host.WaitForRunAsync("invoice-sync");
Assert.Equal(RunStatus.Succeeded, run.Status);
```

**Open:** whether the harness drives the real ticker and pull loop on a fake clock, or dispatches
directly. Driving the real path is the only version that would catch a tick-path regression, but it
makes every test sensitive to timing.

### Done when

- [ ] The four diagnostics ship, with a test per rule including negative cases
- [ ] `AddJobsFrom` needs no runtime reflection when the generator is active, and is source-compatible when it is not
- [ ] `Cadence.Testing` can assert a scheduled run with no real clock, database or host
- [ ] The README's tooling claims match what the analyzer actually checks

---

## One wiring change, separable from all of the above

**Proposed — `MapCadence()`.** Today registration and mapping are two trees that must agree and
nothing enforces it: `AddDashboard()` without `MapCadenceDashboard()` silently serves no UI, and
`MapCadenceDashboard()` with only a token configured throws at startup (§13.7). Three map calls to
keep in sync with your `Add*` calls is the wiring cost.

```csharp
builder.Services.AddCadence(c => c.UseSqlStorage(cs).AddApi().AddDashboard());
app.MapCadence();   // maps exactly what was registered
```

§13.1's reason for two trees survives intact and is in fact better served: the callable API is
switched off by *not calling `.AddApi()`*, so the services do not exist either — strictly stronger
than leaving them registered and unmapped. `MapCadenceApi()`, `MapCadenceDashboard()` and
`MapCadenceHealth()` stay as the escape hatch for a host attaching conventions to one tree, or
mounting trees on different pipelines.

Small, independent, and safe to do in any gap. Drop it without consequence if it is not the wiring
friction that was meant.

---

## The coordinator as a container image

Recorded because it is the natural next thought after v0.5, and because it splits cleanly into a half
that is nearly free and a half that is a redesign. The two are easy to conflate.

**The cheap half — a role, not a product.** After v0.5 the two jobs of a replica are already separate:
tick-and-enqueue, and pull-and-execute. A `Role = Scheduler | Worker | Both` option then costs almost
nothing, needs no election, and a published image is simply *that same package* configured
`Role = Scheduler`. One binary, one coordination model, one set of semantics — the container is
packaging, not architecture. This is the version to want, and it is a follow-on to v0.5.

**The other half — a coordinator the user does not build — is a registration.** The first draft of this
section called it a redesign. It is not: the mechanism already exists. `SqlInstanceRegistry` upserts an
instance row and its heartbeat, `IInstanceDirectory` reads them back, and the janitor already purges
rows whose heartbeat lapsed. `InstanceInfo` already carries `InstanceId`, `MachineName`, `ProcessId`,
`AssemblyVersion` and the two timestamps. **A worker registering the jobs it serves is a field on a
record it already writes**, on a schedule it already keeps.

That makes a coordinator with an empty registry (§13.6) a solved problem rather than a blocker, and it
answers three of the four questions the first draft raised:

| Question | Answer registration gives |
|---|---|
| No worker has registered yet | The coordinator schedules nothing for a job it has never seen. Fail-safe, and correct |
| A job whose type was deleted | Its catalog entry expires with the last instance that served it — the heartbeat does the work. Better than today, where a schedule row for a job nothing registers silently does nothing forever |
| Does the catalog invert code-as-source? | No. Code still supplies job definitions; workers *report* them, and the store caches them for a process that never compiled them. Unchanged layering |

**Settled by the existing model — two workers disagreeing.** Code supplies *defaults*; the store holds
operator *overrides* on top. So a disagreement only matters for a job with no override, only during a
rolling deploy, and "the newest registration wins" is self-healing within one heartbeat. No new rule
needed.

**Open — and this is the one real cost: scheduling becomes coupled to liveness.** If heartbeats lapse
— a network blip, a GC pause, a slow store — a worker's jobs leave the catalog and the coordinator
stops scheduling them. Today that cannot happen: the registry is local to each process, so heartbeat
trouble never stops scheduling. This is the exact mirror of §13.4's argument for keeping the probes away
from the store, and it needs the same answer §8 gap 3 prescribes: schedule from the **last known**
catalog with a grace period far longer than the heartbeat timeout, report `Degraded`, and never
silently stop. A scheduling outage caused by a heartbeat problem would be a worse failure than the one
the catalog exists to fix.

**Proposed — keep the write cheap.** The heartbeat carries a hash of the catalog; the full catalog is
written only when the hash changes. Boot writes it once, a deploy rewrites it once, and steady state
costs the heartbeat exactly what it costs today.

**Worth one line in the trust argument:** any process that can write to the store can now register a
job definition. That is not really a new boundary — store write access already lets you rewrite
schedules — but it should be stated rather than discovered.

**Where this leaves it.** Most of the value arrives before any container does: v0.5's capability-filtered
pull already gives heterogeneous pools — one deployment registering jobs X, another registering jobs Y,
one shared scheduler — with no catalog, no image and no new storage. The technical path from there is
`Role`, then the job catalog as a field on instance registration with the liveness safeguard above. None of that is new
architecture, and the catalog is worth having on its own merits — it is what would let the dashboard
show *which replicas can actually run this job*, which nothing can answer today.

What remains a decision rather than a task is **shipping an image**, because that is a commitment and
not an artifact: base-image CVE patching on someone else's schedule, multi-arch, signing, an SBOM, and
a version matrix against the NuGet packages. And it moves the product — §1 and §2 sell "no
infrastructure at all is a real mode rather than a degraded one", which a *required* coordinator makes
false, putting Cadence in Temporal's aisle rather than Hangfire's. Keep the coordinator optional and
that claim survives; make it mandatory and the README's first screen has to change.

The one thing to avoid throughout: a coordinator container with *different* coordination semantics
from the library. One binary in two roles is the whole point; two products would reintroduce exactly
the sprawl v0.5 exists to remove.

---

## Debt to clear before 1.0

| | Item | Size | Why it gates 1.0 |
|---|---|---|---|
| Gap 2 | Tick re-evaluates every job every second; needs the min-heap of next occurrences, rebuilt on the change token | medium | Scales with job count. Do it **between v0.5 and v0.6** so the enqueue path is settled first and the alerting sweep is built on it |
| Gap 3 | Boot fails on a schedule-store blip. Start from code defaults, report degraded, alert | small | A database hiccup must not stop the application starting. The degraded-health half shipped in §13.4; the boot half did not |
| Gap 8 | `dotnet pack` logs a `GenerateEmbeddedFilesManifest` warning `build` never does | small | `pack` is what publishes. Settle with `dotnet pack /bl` |
| — | Storage degraded-health tests have never run against a live store — written on a machine without Docker | small | CI has real servers; run them there and stop caveating the claim |
| — | `CadenceUiRoutes.cs:15` states the repo uses no `InternalsVisibleTo`. Three exist (Core, Sql, Redis → their own test projects) | trivial | The justification for that public seam rests on the sentence being true. The argument survives; the wording does not |

---

## Publishing

**The name has to be settled first, and it is the only blocking item with no engineering in it.**
"Cadence" collides with the CNCF project and three existing NuGet ids. Everything here can be built
under the current name; none of it can be *published* under it. Rename cost rises with every
milestone — the name is in namespaces, assembly names, the `Cadence` Data Protection application name,
`CADENCE_*` variables, `cadence.*` metric names, health-check tags and the `/cadence` route prefix.

Then: pack clean (gap 8), a support and versioning statement, a CHANGELOG, and README badges pointing
at real published versions.

---

## Not in 1.0

| | Why not | Where |
|---|---|---|
| `QueueOne` overlap policy | Needs a per-job coalescing queue and its `ScheduledFor` has no clean answer. Worth revisiting *after* v0.5, which gives it the queue it wanted | §7 |
| Per-job concurrency caps | Global `MaxConcurrentRuns` + `Skip` is enough | §7 |
| Payload JSON Schema | Payloads stay opaque; the job validates | §7 |
| `MaxAttempts` / in-run retry | Ambiguous duration and timeout in history. Later, as a new run with `Trigger = Retry` | §7 |
| Tick jitter | **Dropped, not deferred.** It spread claim wins without touching the claim; once pulling replaces dispatching, tick phase no longer decides who works and jitter buys nothing | §14.2 |
| A declared `Scheduler`/`Worker` role | A cheap follow-on once the queue is proven. Must never become an elected one | — |
| A published coordinator container image | The role is cheap; a coordinator that does not know the user's jobs needs a job catalog in the store, which is new architecture. See *The coordinator as a container image* | §13.6 |
| Orchestrator deployment docs | Should not be written until someone has actually deployed it — and §14.3's autoscaler paragraph needs rewriting after v0.5 anyway | §14.3 |
| A docker-compose proof | Aspire demonstrates; compose would prove | §14.4 |
| A schedule audit table | v0.4 ships a log line. A table must cover the whole schedule, not just cron, and nothing reads it back yet | §14.5 |

---

## Order of work

1. **Name decision.** No code. Blocks publishing and gets more expensive weekly.
2. **The trivial debt** — the `CadenceUiRoutes` comment; run the storage-health tests in CI.
3. **Gap 8**, because publishing depends on `pack` and the answer is one binary log away.
4. **Gap 3**, the boot fallback. Small, and a correctness hole rather than polish.
5. **v0.5 — queue the claim, pull the work.** The breaking semantics land here, pre-1.0.
6. **Gap 2**, the min-heap, on the settled enqueue path.
7. **v0.6 — alerting.** The watchdog is a queue item, not a sixth coordination design.
8. **v0.7 — tooling.** The analyzer first: its promise is already in the README.
9. **Publish.**

Steps 1–4 are independent and can run in parallel. 5, 6 and 7 are ordered on purpose.
`MapCadence()` fits in any gap.
