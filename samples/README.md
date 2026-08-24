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
| **MEL** (`ILogger`) | `info: ...HelloThereJob[0] Hello there, Tony!` on the console |
| **OTel logs** | the same record with `JobName`, `RunId`, `InstanceId` as scope attributes |
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

### What it cannot show yet

The fuller sample — .NET Aspire orchestrating a SQL Server container, Cadence using the SQL
schedule source and occurrence coordinator, two worker replicas contending for the same
occurrences, and the dashboard rendering run history — was blocked on two things. One has landed:

| Blocker | Milestone | Status |
|---|---|---|
| `Cadence.Storage.Sql` — the unique-index claim, persistent history, the janitor | v0.2 | **done** |
| `Cadence.Dashboard` — somewhere for the history sink to actually be read | v0.4 | outstanding |

This sample still uses the in-memory stores and the no-op coordinator, so it demonstrates the
scheduling, execution and telemetry paths but not clustering or the UI.

**The clustering guarantee is proven, just not here.** `ClusteredSchedulingTests` runs five
independent instances — separate containers, separate executors, separate instance ids, one
Testcontainers database — against the real tick loop with a fake clock, and asserts one run per
occurrence. That is the "N replicas, one run per occurrence" test, and driving the loop directly
makes it deterministic in a way an Aspire host never would be.

What Aspire would add on top is the things a test cannot show: real processes, real restarts, and
a dashboard to look at. Worth building once v0.4 gives it something to render. When it is built,
two replicas will expose the `Skip` caveat immediately — a long-running job on replica A, the next
occurrence claimed by replica B, and the job runs anyway. Better to see that in a sample we control
than in someone's incident.
