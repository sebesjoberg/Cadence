using Cadence.Scheduling;
using Xunit;

namespace Cadence.Core.Tests;

public class OccurrencePlannerTests
{
    private static EffectiveSchedule Schedule(
        string cron,
        MissedRunPolicy onMissed = MissedRunPolicy.SkipToNext,
        TimeZoneInfo? timeZone = null)
    {
        var descriptor = new JobDescriptor
        {
            Name = "test-job",
            ImplementationType = typeof(OccurrencePlannerTests),
            DefaultCron = cron,
            OnMissed = onMissed,
        };

        return new EffectiveSchedule
        {
            Descriptor = descriptor,
            Cron = CronParser.Parse(cron),
            CronText = cron,
            TimeZone = timeZone ?? TimeZoneInfo.Utc,
            Enabled = true,
            Overlap = OverlapPolicy.Skip,
        };
    }

    [Fact]
    public void NothingDuePlansNothing()
    {
        var plan = OccurrencePlanner.Plan(
            Schedule("0 * * * *"),
            lastEvaluated: Occurrences.Utc(2026, 8, 24, 10, 0),
            now: Occurrences.Utc(2026, 8, 24, 10, 30),
            maxCatchUp: 10);

        Assert.Empty(plan.Occurrences);
        Assert.Equal(0, plan.DroppedCount);
    }

    [Fact]
    public void ASingleDueOccurrenceRunsRegardlessOfPolicy()
    {
        foreach (var policy in Enum.GetValues<MissedRunPolicy>())
        {
            var plan = OccurrencePlanner.Plan(
                Schedule("0 * * * *", policy),
                lastEvaluated: Occurrences.Utc(2026, 8, 24, 10, 30),
                now: Occurrences.Utc(2026, 8, 24, 11, 0),
                maxCatchUp: 10);

            var occurrence = Assert.Single(plan.Occurrences);
            Assert.Equal(Occurrences.Utc(2026, 8, 24, 11, 0), occurrence);
            Assert.Equal(0, plan.DroppedCount);
        }
    }

    [Fact]
    public void SkipToNextDropsEveryMissedOccurrence()
    {
        var plan = OccurrencePlanner.Plan(
            Schedule("0 * * * *", MissedRunPolicy.SkipToNext),
            lastEvaluated: Occurrences.Utc(2026, 8, 24, 10, 0),
            now: Occurrences.Utc(2026, 8, 24, 14, 0),
            maxCatchUp: 10);

        Assert.Empty(plan.Occurrences);
        Assert.Equal(4, plan.DroppedCount);
        Assert.False(plan.TruncatedByCap);
    }

    [Fact]
    public void RunOnceRunsOnlyTheMostRecentMissedOccurrence()
    {
        var plan = OccurrencePlanner.Plan(
            Schedule("0 * * * *", MissedRunPolicy.RunOnce),
            lastEvaluated: Occurrences.Utc(2026, 8, 24, 10, 0),
            now: Occurrences.Utc(2026, 8, 24, 14, 0),
            maxCatchUp: 10);

        var occurrence = Assert.Single(plan.Occurrences);
        Assert.Equal(Occurrences.Utc(2026, 8, 24, 14, 0), occurrence);
        Assert.Equal(3, plan.DroppedCount);
    }

    [Fact]
    public void RunAllReplaysEverythingWithinTheCapInOrder()
    {
        var plan = OccurrencePlanner.Plan(
            Schedule("0 * * * *", MissedRunPolicy.RunAll),
            lastEvaluated: Occurrences.Utc(2026, 8, 24, 10, 0),
            now: Occurrences.Utc(2026, 8, 24, 14, 0),
            maxCatchUp: 10);

        Assert.Equal(4, plan.Occurrences.Count);
        Assert.Equal(Occurrences.Utc(2026, 8, 24, 11, 0), plan.Occurrences[0]);
        Assert.Equal(Occurrences.Utc(2026, 8, 24, 14, 0), plan.Occurrences[^1]);
        Assert.False(plan.TruncatedByCap);
    }

    [Fact]
    public void RunAllKeepsTheNewestOccurrencesWhenTheCapTruncates()
    {
        var plan = OccurrencePlanner.Plan(
            Schedule("0 * * * *", MissedRunPolicy.RunAll),
            lastEvaluated: Occurrences.Utc(2026, 8, 24, 0, 0),
            now: Occurrences.Utc(2026, 8, 24, 20, 0),
            maxCatchUp: 3);

        Assert.True(plan.TruncatedByCap);
        Assert.Equal(3, plan.Occurrences.Count);
        Assert.Equal(17, plan.DroppedCount);

        // Newest kept, because the most recent data is what the operator wants processed.
        Assert.Equal(Occurrences.Utc(2026, 8, 24, 18, 0), plan.Occurrences[0]);
        Assert.Equal(Occurrences.Utc(2026, 8, 24, 20, 0), plan.Occurrences[^1]);
    }

    [Fact]
    public void AnUnreasonableBacklogIsAbandonedRatherThanEnumerated()
    {
        // Every second, for a year: enumerating this is more expensive than any of the runs are
        // worth, and replaying it is never the intent.
        var plan = OccurrencePlanner.Plan(
            Schedule("* * * * * *", MissedRunPolicy.RunAll),
            lastEvaluated: Occurrences.Utc(2025, 8, 24, 0, 0),
            now: Occurrences.Utc(2026, 8, 24, 0, 0),
            maxCatchUp: 10);

        Assert.True(plan.TooFarBehind);
        Assert.Empty(plan.Occurrences);
    }
}
