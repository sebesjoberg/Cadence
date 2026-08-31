using Microsoft.Extensions.Logging;

namespace Cadence.Sample.ClusteredWorker;

/// <summary>
/// Fast, frequent, and boring on purpose. Three replicas all evaluate this schedule every five
/// seconds and every one of them tries to claim the slot; exactly one wins. The trace view shows
/// that as one <c>cadence.job</c> span per <c>job.scheduled_for</c>.
/// </summary>
/// <remarks>
/// <para>
/// The winner does not rotate, and watching this sample is the fastest way to learn that. Whichever
/// replica started first has its tick phase a few tens of milliseconds ahead of the others, and a
/// few tens of milliseconds is all a race to an <c>INSERT</c> needs — so it wins every occurrence
/// until it dies. Claiming an occurrence is a correctness mechanism, not a load balancer; the other
/// two replicas are failover capacity, and they take over immediately when the leader goes away.
/// </para>
/// <para>
/// It allows an <c>Api</c> trigger as well, which is the other half of the demonstration: a
/// triggered run executes wherever the request landed, so repeated triggers through Aspire's proxy
/// spread across replicas while the scheduled ones do not.
/// </para>
/// </remarks>
[ScheduledJob(
    Name = "tick-tock",
    Cron = "*/5 * * * * *",
    MaxDuration = "00:00:30",
    Triggers = TriggerKind.Schedule | TriggerKind.Api)]
public sealed class TickTockJob(ILogger<TickTockJob> logger) : IJob
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        logger.ClaimedOccurrence(context.ScheduledFor, context.InstanceId);

        context.Report("claimed and ran", new Dictionary<string, object?>
        {
            ["instance"] = context.InstanceId,
            ["scheduled_for"] = context.ScheduledFor?.ToString("O"),
        });

        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
    }
}

/// <summary>
/// Runs longer than the interval between its own occurrences, deliberately, so that cluster-wide
/// Skip is something you watch happen rather than something the README asserts.
/// </summary>
/// <remarks>
/// <para>
/// The occurrence is due every ten seconds and the run takes twenty-five, so occurrences keep
/// coming due while a run is still going. <see cref="OverlapPolicy.Skip"/> refuses every one of
/// them, on every replica: the running job holds its name as an exclusive key in the store, so the
/// replica that claims the next slot is told no and records a <see cref="RunStatus.Skipped"/> run
/// saying another instance is already running it.
/// </para>
/// <para>
/// In the trace view that is never two overlapping <c>cadence.job</c> spans for this job — which is
/// the point, and is what this sample checks. History fills with one running run and a trail of
/// skipped ones naming the replica that refused. Kill the replica mid-run and the block outlasts it
/// by a heartbeat timeout, because the key is only released when the janitor reaps the run its
/// owner never finished.
/// </para>
/// </remarks>
[ScheduledJob(
    Name = "slow-sweep",
    Cron = "*/10 * * * * *",
    Overlap = OverlapPolicy.Skip,
    MaxDuration = "00:01:00")]
public sealed class SlowSweepJob(ILogger<SlowSweepJob> logger) : IJob
{
    private static readonly TimeSpan RunDuration = TimeSpan.FromSeconds(25);

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        logger.SweepStarting(context.InstanceId, RunDuration.TotalSeconds);

        // Reported in steps so a run killed halfway leaves a partial trail in history — which is
        // what makes the janitor demo legible: the run is visibly unfinished, then marked Lost.
        for (var step = 1; step <= 5; step++)
        {
            await Task.Delay(RunDuration / 5, cancellationToken);
            context.Report($"swept {step * 20}%", new Dictionary<string, object?>
            {
                ["instance"] = context.InstanceId,
                ["percent"] = step * 20,
            });
        }

        logger.SweepFinished(context.InstanceId);
    }
}

/// <summary>
/// Has no cron, so it runs only when something asks. That makes it the honest test of the trigger
/// endpoint: <c>tick-tock</c> fires every five seconds anyway, so a triggered run is hard to pick
/// out of the noise, while a run of this job can only have come from a request.
/// </summary>
/// <remarks>
/// It is also the only job here whose <c>cron</c> and <c>timeZone</c> come back null from
/// <c>GET /cadence/api/jobs</c> — the shape a trigger-only job has on the wire.
/// </remarks>
[ScheduledJob(
    Name = "reindex-catalog",
    Triggers = TriggerKind.Api | TriggerKind.Manual,
    MaxDuration = "00:00:30")]
public sealed class ReindexCatalogJob(ILogger<ReindexCatalogJob> logger) : IJob
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        logger.ReindexStarting(context.InstanceId);

        for (var batch = 1; batch <= 3; batch++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            context.Report($"reindexed batch {batch} of 3", new Dictionary<string, object?>
            {
                ["instance"] = context.InstanceId,
                ["batch"] = batch,
            });
        }

        logger.ReindexFinished(context.InstanceId);
    }
}
