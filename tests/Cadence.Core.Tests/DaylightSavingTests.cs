using Cadence.Scheduling;
using Xunit;

namespace Cadence.Core.Tests;

/// <summary>
/// Pins the daylight-saving behaviour the design plan claims, against Europe/Stockholm. These are
/// not hypothetical cases: a 02:30 nightly job hits both of them every year.
/// </summary>
public class DaylightSavingTests
{
    private static readonly CronExpressionHolder NightlyAtHalfPastTwo = new("30 2 * * *");

    [Fact]
    public void SpringForwardFiresAtTheTransitionRatherThanSkippingTheDay()
    {
        // 2026-03-29: Stockholm goes 02:00 CET -> 03:00 CEST, so 02:30 local never happens.
        var dayBefore = Occurrences.Utc(2026, 3, 28, 1, 30);   // 02:30 CET

        var next = NightlyAtHalfPastTwo.Expression.GetNextOccurrence(
            dayBefore, Occurrences.Stockholm, inclusive: false);

        // Measured, not assumed: Cronos does NOT skip the day. It fires at the instant the clock
        // jumps — 03:00 local, 01:00 UTC. A nightly job therefore still runs on the transition day,
        // half an hour late. The original design note claiming a skip here was simply wrong.
        Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), next!.Value.UtcDateTime);
        Assert.Equal(3, TimeZoneInfo.ConvertTime(next.Value, Occurrences.Stockholm).Hour);
    }

    [Fact]
    public void TheDayAfterSpringForwardReturnsToTheConfiguredTime()
    {
        var transition = Occurrences.Utc(2026, 3, 29, 1, 0);

        var next = NightlyAtHalfPastTwo.Expression.GetNextOccurrence(
            transition, Occurrences.Stockholm, inclusive: false);

        Assert.Equal(new DateTime(2026, 3, 30, 0, 30, 0, DateTimeKind.Utc), next!.Value.UtcDateTime);
    }

    [Fact]
    public void AutumnBackFiresOnceForTheHourThatHappensTwice()
    {
        // 2026-10-25: Stockholm goes 03:00 CEST -> 02:00 CET, so 02:30 local happens twice —
        // once at 00:30Z (CEST) and once at 01:30Z (CET).
        var before = Occurrences.Utc(2026, 10, 24, 0, 30);

        var first = NightlyAtHalfPastTwo.Expression.GetNextOccurrence(
            before, Occurrences.Stockholm, inclusive: false);

        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), first!.Value.UtcDateTime);

        // The second 02:30 that day must not produce a second occurrence.
        var second = NightlyAtHalfPastTwo.Expression.GetNextOccurrence(
            first.Value, Occurrences.Stockholm, inclusive: false);

        Assert.Equal(new DateTime(2026, 10, 26, 1, 30, 0, DateTimeKind.Utc), second!.Value.UtcDateTime);
    }

    [Fact]
    public void OccurrenceKeysStayUniqueAcrossTheRepeatedHour()
    {
        // The claim key is derived from UTC, so even an every-15-minutes job crossing the repeated
        // hour produces distinct occurrences with no collisions.
        var schedule = new CronExpressionHolder("*/15 * * * *");
        var cursor = Occurrences.Utc(2026, 10, 24, 23, 0);
        var seen = new HashSet<DateTimeOffset>();

        for (var i = 0; i < 24; i++)
        {
            var next = schedule.Expression.GetNextOccurrence(cursor, Occurrences.Stockholm, inclusive: false);
            Assert.NotNull(next);
            Assert.True(seen.Add(next!.Value), $"Occurrence {next.Value:O} was produced twice.");
            cursor = next.Value;
        }
    }

    private sealed class CronExpressionHolder(string text)
    {
        public Cronos.CronExpression Expression { get; } = CronParser.Parse(text);
    }
}
