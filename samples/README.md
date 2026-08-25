# Samples

## Cadence.Sample.Worker

A worker host that runs one job, `hello-there`, every ten seconds. It greets a different name each
run and does nothing else. The point is not the job — it is what surrounds it.

**It consumes Cadence as a NuGet package, not as a project reference.** A project reference would
hide packaging mistakes until the first real publish. This one found `NU5039` (a declared
`PackageReadmeFile` that was never packed) on its first run, which is exactly the job it is for.

```powershell
./scripts/pack.ps1                                  # build the local feed
dotnet run --project samples/Cadence.Sample.Worker  # ctrl+c to stop
```

### What it proves

Progress a job reports fans out to three places, and the sample shows all three at once:

| Path | What you see |
|---|---|
| **MEL** (`ILogger`) | `info: ...HelloThereJob[1] Hello there, Tony!` on the console |
| **OTel logs** | the same record with `Body` still the template `Hello there, {Name}!`, `Name` as a structured attribute, `EventName` `Greeted`, and `JobName` / `RunId` / `InstanceId` as scope attributes |
| **OTel traces** | a `cadence.job` span, tagged `job.name` / `job.run_id` / `job.trigger` / `job.scheduled_for` / `job.status`, carrying a `cadence.job.progress` event |
| **OTel metrics** | `cadence.runs`, `cadence.run.duration`, `cadence.job.seconds_since_success` |
| **Run history** | the dashboard's sink — in-memory here, since the dashboard itself is v0.4 |

Verified output from a real run:

```
Activity.DisplayName:        cadence.job
Activity.Duration:           00:00:00.2610805
Activity.Tags:
    job.name: hello-there
    job.run_id: f12729e6-f997-4d28-b5a7-ee85d23cfa8a
    job.trigger: Schedule
    job.scheduled_for: 2026-08-24T09:25:50.0000000+00:00
    job.status: Succeeded
Activity.Events:
    cadence.job.progress [2026-08-24 09:25:50 +00:00]
        message: greeted Tony
        data.name: Tony
```

### What it does not show

Clustering. This sample uses the in-memory stores and the no-op coordinator, deliberately: it exists
to prove the telemetry fan-out on the zero-infrastructure path, and adding SQL to it would blur both
purposes. For clustering, see the next sample.

## Cadence.Sample.AppHost

.NET Aspire orchestrating a SQL Server container and **three replicas of one worker**, all claiming
occurrences out of the same database. This is the sample that shows the guarantee on the README's
first screen — *at most one instance starts a given occurrence* — happening between real processes.

```powershell
./scripts/pack.ps1                              # build the local feed
dotnet run --project samples/Cadence.Sample.AppHost
```

Docker must be running; Aspire starts `mcr.microsoft.com/mssql/server:2022-latest` for you. The
AppHost prints a dashboard URL with a login token — that dashboard is the UI here. Cadence tags every
log record, span and metric with `JobName`, `RunId` and `InstanceId`, so "which replica ran what" is
answerable without `Cadence.Dashboard`, which is v0.4 and would add schedule editing and a history
view rather than visibility.

Two jobs run:

| Job | Cron | What it is for |
|---|---|---|
| `tick-tock` | every 5s | one span per `job.scheduled_for`, no matter how many replicas are up |
| `slow-sweep` | every 10s, takes 25s | overruns its own slot, on purpose |

### The four things worth watching

**1. One run per occurrence.** Filter the dashboard's traces to `cadence.job` and group by
`job.scheduled_for`. Every slot has exactly one span. Three replicas are all evaluating the same
schedule every second and all three try to claim; the unique index on `CadenceJobRun` picks one.

**2. The winner never rotates — and that is not load balancing.** One replica wins *every*
occurrence and the other two record nothing at all, until the winner dies. Whichever replica started
first has its tick phase a few tens of milliseconds ahead, and that is the whole race. Claiming an
occurrence is a correctness mechanism; the other replicas are failover capacity, not a share of the
work. If you came here expecting three replicas to divide the load three ways, this is the sample
correcting you.

**3. The `Skip` caveat, live.** `slow-sweep` takes longer than the gap between its occurrences.
Within one replica `Skip` is strict, so you will see runs recorded as `Skipped`. Across replicas it
cannot be, because the claim answers *"has anyone started this slot?"* and not *"is anyone running
this job?"* — so once the leader is busy, a different replica claims the next slot and starts while
the first is still going. Two overlapping spans, same `job.name`, different `job.instance_id`. That
is documented behaviour, not a bug; a job needing a hard cross-instance guarantee has to take its own
lock.

**4. The janitor reaping a dead replica.** Kill the winning replica *hard* — Task Manager, or
`Stop-Process -Id <pid> -Force` — while a `slow-sweep` is in flight. Do not use the dashboard's Stop
button: that is a graceful shutdown, so Cadence drains, records the run as `Aborted`, and deletes its
registry row. There is nothing for the janitor to find.

After a hard kill the heartbeat simply stops. Within `HeartbeatTimeout` plus one janitor pass the run
flips from `Running` to `Lost` — a distinct status meaning *nobody recorded an outcome at all*, which
is a different conversation with an operator than `Aborted`. Meanwhile the next-earliest replica
starts winning claims immediately.

Measured on the run this sample was written against: killed at 08:23:35 mid-sweep, marked `Lost` at
08:23:56, and the surviving replica had taken over by the next occurrence at 08:23:40.

### Editing a schedule by hand

The dashboard that would do this properly is v0.4, so for now it is SQL — and there is one trap worth
knowing. `CadenceJobSchedule` holds **overrides**, not a seeded copy of what the code declared, so a
job running on its `[ScheduledJob]` cron has no row yet: you `INSERT`, you do not `UPDATE`.

The second half matters more. Replicas do not poll the schedule table; they poll a single-row version
counter, which is what keeps "nothing changed" cheap. `UpsertAsync` bumps it in the same transaction.
A hand-written statement has to bump it too, or the edit sits in the table and nothing ever reads it:

```sql
BEGIN TRANSACTION;
INSERT INTO CadenceJobSchedule (JobName, CronExpression, TimeZoneId, Enabled, UpdatedAtUtc, UpdatedBy)
VALUES ('tick-tock', '*/20 * * * * *', 'UTC', 1, SYSUTCDATETIME(), 'me');
UPDATE CadenceScheduleVersion SET Version = Version + 1 WHERE Id = 1;
COMMIT TRANSACTION;
```

All three replicas pick it up within `SchedulePollInterval`, which this sample shortens to 5 seconds.

### Timings are demo values, not defaults

The worker sets `HeartbeatInterval` 5s, `HeartbeatTimeout` 20s, `JanitorInterval` 15s and
`SchedulePollInterval` 5s. The real defaults are 15s / 60s / 5min / 10s, which are right for a
deployment and wrong for standing in front of a screen — the janitor demo would take five minutes.
The relationship the defaults encode is preserved: the timeout is still four heartbeats, so one
missed beat never gets a live replica's runs reaped out from under it.

### It consumes Cadence as a package too

Like `Cadence.Sample.Worker`, and for the same reason. This is also the first thing anywhere in the
repository that consumes `Cadence.Storage.Sql` as a NuGet package rather than by project reference,
so it is the first check that the SQL tier packs correctly at all. Run `./scripts/pack.ps1` first, or
the restore fails.

The one exception is the AppHost's reference to the worker project, which is not consumption — it is
how Aspire is told which project to launch, and what generates the `Projects.*` type.

### What it still cannot show

A history view and schedule editing in a UI. Both are `Cadence.Dashboard`, which is v0.4. The Aspire
dashboard covers live telemetry — logs, traces, metrics, per replica — but it reads OTel, not
`CadenceJobRun`, so "what ran last Tuesday" is still a SQL query.

**The guarantee is also proven without any of this.** `ClusteredSchedulingTests` runs five instances
against a Testcontainers SQL Server with a fake clock and asserts one run per occurrence, on every CI
build, deterministically. This sample exists for what a test cannot do: real processes, real kills,
real restarts, and something to look at.
