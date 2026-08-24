# Cadence

A job scheduler for .NET where **schedules live in a database and are editable at
runtime**. Jobs are plain classes resolved from DI, one fresh scope per run, and
multiple app instances coordinate so no scheduled slot runs twice.

```csharp
builder.Services.AddCadence();   // in-memory, single instance, no infrastructure

[ScheduledJob(Name = "invoice-sync", Cron = "0 */15 * * * *")]
public sealed class InvoiceSyncJob(IInvoiceService svc) : IJob
{
    public Task ExecuteAsync(JobContext ctx, CancellationToken ct) => svc.SyncAsync(ct);
}
```

Add `.UseSqlStorage(connectionString)` to get persistence and clustering:

```csharp
builder.Services.AddCadence(cadence => cadence
    .UseSqlStorage(builder.Configuration.GetConnectionString("Cadence")!));
```

That one call moves schedules, run history and occurrence claiming into SQL Server,
registers this instance's heartbeat, and starts the janitor. Dashboard, API and
alerting are separate opt-in packages.

## The guarantee, precisely

**At most one instance *starts* a given occurrence.** That is not the same as "at most
one run of a job is ever in flight": a run that overruns its slot can be joined by the
next occurrence on another instance.

This is deliberate. A lock held for the length of a run needs a TTL longer than the
longest possible run, which is unknowable — so you end up with lease renewal, which
breaks under a GC pause or a network partition, which needs fencing tokens to recover
from safely. Cadence claims the *occurrence* instead: one question, asked once, answered
by a unique index. In SQL the claim **is** the run row, so there is no window where a
slot is taken but unrecorded.

The cost of that choice is the paragraph above, and one caveat worth knowing before you
rely on it: `OverlapPolicy.Skip` is strict within an instance and best-effort across a
cluster. If a long run is in flight on instance A and instance B claims the next
occurrence, B runs it.

## Layering

| Call | Gets you | Needs |
|---|---|---|
| `AddCadence()` | cron in code, in-memory history, single instance, OTel | nothing |
| `+ UseSqlStorage()` | persistence **and** clustering | a database |
| `+ MapCadenceApi()` | trigger / status / schedule endpoints | an auth policy |
| `+ EnableDashboard()` | UI, schedule editing | an auth policy |
| `+ AddAlerting()` | rules, watchdog, throttling | channel config |

Persistence and clustering arrive together on purpose. Splitting them would let you
deploy two instances with shared history and no coordinator — which runs every
occurrence twice while looking perfectly healthy in the logs.

## Schema

`UseSqlStorage` creates its tables at startup by default. Where the application's
principal has no DDL rights, or schema changes go through a release process, turn it off
and apply [`scripts/sql`](scripts/sql) by hand:

```csharp
.UseSqlStorage(connectionString, sql => sql.AutoMigrate = false)
```

Those scripts are the same ones the migrator runs, copied out of the assembly at build
time so the reviewable copy cannot drift. Every statement is guarded, so applying them
by hand and then leaving `AutoMigrate` on is harmless.

Instances that boot together all try to migrate; the first wins an application lock and
the rest wait, then find nothing to do.

**Status:** pre-release, v0.2 in progress. Not yet published to NuGet.

- [Design plan](docs/design-plan.md) — the map: key decisions, layering, build order.

## Licence

MIT
