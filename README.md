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

Add `.UseSqlStorage(connectionString)` to get persistence and clustering. Dashboard,
API and alerting are separate opt-in packages.

**Status:** pre-release, v0.1 in progress. Not yet published to NuGet.

- [Design plan](docs/design-plan.md) — the map: key decisions, layering, build order.

## Licence

MIT
