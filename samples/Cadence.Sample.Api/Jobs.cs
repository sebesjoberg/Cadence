namespace Cadence.Sample.Api;

[ScheduledJob(
    Name = "inventory-sweep",
    Cron = "*/15 * * * * *",
    MaxDuration = "00:00:30",
    Triggers = TriggerKind.Schedule | TriggerKind.Api)]
public sealed class InventorySweepJob : IJob
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        for (var shelf = 1; shelf <= 3; shelf++)
        {
            context.Report($"counted shelf {shelf} of 3");
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        }
    }
}

[ScheduledJob(
    Name = "reindex-catalog",
    MaxDuration = "00:00:30",
    Triggers = TriggerKind.Api | TriggerKind.Manual)]
public sealed class ReindexCatalogJob : IJob
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        context.Report("rebuilding the index");
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        context.Report("index swapped in");
    }
}

// Schedule only, so triggering it over HTTP is a 400 rather than a 202.
[ScheduledJob(Name = "nightly-report", Cron = "0 3 * * *", MaxDuration = "00:00:30")]
public sealed class NightlyReportJob : IJob
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        context.Report("nothing to report");
        return Task.CompletedTask;
    }
}
