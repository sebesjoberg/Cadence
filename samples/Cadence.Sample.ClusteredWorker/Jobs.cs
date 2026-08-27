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
/// Runs longer than the interval between its own occurrences, deliberately, to make the caveat in
/// the README's first screen visible rather than merely stated.
/// </summary>
/// <remarks>
/// <para>
/// The occurrence is due every ten seconds and the run takes twenty-five. <see cref="OverlapPolicy.Skip"/>
/// is strict within one instance: whichever replica is mid-run will refuse its own next occurrence.
/// It cannot be strict across the cluster, because the claim answers "has anyone started this slot?"
/// and not "is anyone running this job?" — so a different replica claims the next slot and starts,
/// while the first is still going.
/// </para>
/// <para>
/// In the trace view that is two overlapping <c>cadence.job</c> spans carrying the same
/// <c>job.name</c> and different <c>job.instance_id</c>. That is the documented behaviour, not a
/// bug; a job needing a hard cross-instance guarantee has to take its own lock.
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
