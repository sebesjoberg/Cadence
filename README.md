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

`UseRedisStorage(connectionString)` does the same against Redis. The two are alternatives,
not layers — pick one; see [Choosing a storage tier](#choosing-a-storage-tier).

## The guarantee, precisely

**At most one instance *starts* a given occurrence.** That is not the same as "at most
one run of a job is ever in flight": a run that overruns its slot can be joined by the
next occurrence on another instance.

This is deliberate. A lock held for the length of a run needs a TTL longer than the
longest possible run, which is unknowable — so you end up with lease renewal, which
breaks under a GC pause or a network partition, which needs fencing tokens to recover
from safely. Cadence claims the *occurrence* instead: one question, asked once, answered
by a store that can only answer once. In SQL the claim **is** the run row; in Redis it is
written in the same script as the run. Either way there is no window where a slot is taken
but unrecorded.

The cost of that choice is the paragraph above, and one caveat worth knowing before you
rely on it: `OverlapPolicy.Skip` is strict within an instance and best-effort across a
cluster. If a long run is in flight on instance A and instance B claims the next
occurrence, B runs it.

### Replicas are failover, not load spreading

Claiming an occurrence is a race to a write, and every instance ticks on its own timer whose
phase is fixed by when the process started. The instance that starts earliest therefore wins
every race, and keeps winning until it stops — measured at three replicas against one SQL
Server, where the replica that started 40 ms ahead won **every** occurrence and the other two
won none.

Nothing is broken by that; one instance running each occurrence is exactly the guarantee. But
three replicas do not divide the work three ways, so do not size a cluster as though they do.
They are failover capacity that happens to be warm, and failover itself is immediate: kill the
leader mid-run and the next occurrence is claimed by the next-earliest instance, with the
interrupted run marked `Lost` once its heartbeat times out.

## Pausing

Two switches, held in the storage tier and read by every instance:

```csharp
await pauses.SetAsync(PauseScope.Schedule, "payment gateway incident", "ops@example.com", ct);
```

`PauseScope.Schedule` stops the tick loop claiming occurrences. `PauseScope.Triggers` refuses
manual and API runs. They are independent on purpose: during an incident the usual thing to want is
automatic work stopped and the ability to still run one job by hand. `PauseScope.All` closes both,
`PauseScope.None` reopens them.

A pause reaches other instances on the same change detection schedule edits use — within one poll
interval on SQL Server, pushed on Redis. Paused occurrences are treated as never having existed, so
resuming starts from the next one and replays nothing.

With no storage package the switches live in the process, which is all there is to pause.

## Layering

| Call | Gets you | Needs |
|---|---|---|
| `AddCadence()` | cron in code, in-memory history, single instance, OTel | nothing |
| `+ UseSqlStorage()` *or* `UseRedisStorage()` | persistence **and** clustering | a database, or a Redis |
| `+ MapCadenceApi()` | trigger, reads, pause | a token, or an auth policy |
| `+ MapCadenceDashboard()` | UI, schedule editing, manual trigger | a signed-in operator |
| `+ AddAlerting()` | rules, watchdog, throttling | channel config |

Persistence and clustering arrive together on purpose. Splitting them would let you
deploy two instances with shared history and no coordinator — which runs every
occurrence twice while looking perfectly healthy in the logs.

## Choosing a storage tier

`UseSqlStorage` and `UseRedisStorage` are alternatives. Both replace the same three
services, so calling both leaves you with whichever ran last on some of them and not
others; there is no configuration in which mixing them is what anyone meant.

They are held to the same contract — one conformance suite, run against both against a real
server on every build — so the choice is about operations, not behaviour:

| | SQL Server | Redis |
|---|---|---|
| **Durability** | a committed run is committed | as durable as your Redis is configured to be |
| **Schema** | tables created at startup, or [`scripts/sql`](scripts/sql) by hand | none; keys appear when written |
| **History queries** | any filter, indexed by the database | fast by job, instance and time; filtering by status alone walks the index |
| **Schedule changes** | polled, so up to `SchedulePollInterval` late | pushed on a channel, with the poll as a backstop |

**The durability difference is the one that decides it.** With Redis's defaults a restart can
lose recent writes, and that includes claims: an occurrence whose claim did not survive can be
claimed again, which is the one failure the coordinator exists to prevent. If you pick Redis,
enable AOF with `appendfsync everysec` and know that you are trading a bounded window of
double-execution risk for not running a database. If that trade sounds bad, it is — take SQL
Server. Redis is here for deployments that already run one and do not want a second store, and
for the [seam](docs/design-plan.md) it proves: `IOccurrenceCoordinator` really is the only
place that knows how a claim is won.

A claim also lives exactly as long as its run does, so a run aged out by retention releases its
occurrence. Retention is thirty days by default, which bounds how far back a replay could
double-execute; both tiers behave identically here, because in both the claim is the run.

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

The Redis tier has no equivalent, and no migrator, application lock or reviewable script
folder either. A key exists once something writes it, which removes the question rather
than answering it.

**Status:** pre-release. v0.2 — persistence and clustering, on SQL Server or Redis — is complete,
with both tiers held to one conformance suite that runs against a real server in CI, and the Aspire
multi-replica sample demonstrates it between real processes. v0.3, the control surface, is under
way: distributed pause has landed, and the machine-callable API, its auth gate and the health
checks are designed but not yet built — see §13 of the design plan. Sign-in, sessions and
dashboard-created API tokens follow in v0.3.1, and the dashboard itself in v0.4. Not yet published
to NuGet.

- [Design plan](docs/design-plan.md) — the map: key decisions, layering, build order.

## Licence

MIT
