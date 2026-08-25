using Cadence.Scheduling;
using Xunit;

namespace Cadence.Core.Tests;

public class CronParserTests
{
    [Theory]
    [InlineData("*/15 * * * *")]        // 5 fields
    [InlineData("0 */15 * * * *")]      // 6 fields, with seconds
    [InlineData("0 2 * * *")]
    public void ParsesFiveAndSixFieldExpressions(string expression)
    {
        Assert.True(CronParser.TryParse(expression, out var parsed, out var error));
        Assert.NotNull(parsed);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("* * *")]                    // too few fields
    [InlineData("* * * * * * *")]            // too many fields
    [InlineData("bogus * * * *")]
    [InlineData("99 * * * *")]
    public void RejectsMalformedExpressionsWithAnExplanation(string expression)
    {
        Assert.False(CronParser.TryParse(expression, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void FieldCountMismatchSaysWhatTheCountWas()
    {
        Assert.False(CronParser.TryParse("* * *", out _, out var error));
        Assert.Contains("3 fields", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NullOrEmptyTimezoneResolvesToUtc()
    {
        Assert.True(CronParser.TryResolveTimeZone(null, out var zone, out _));
        Assert.Equal(TimeZoneInfo.Utc, zone);
    }

    [Fact]
    public void ResolvesIanaIds()
    {
        Assert.True(CronParser.TryResolveTimeZone("Europe/Stockholm", out var zone, out var error));
        Assert.NotNull(zone);
        Assert.Null(error);
    }

    [Fact]
    public void UnknownTimezoneErrorNamesTheGlobalizationCause()
    {
        Assert.False(CronParser.TryResolveTimeZone("Mars/Olympus_Mons", out _, out var error));

        // The framework's own message does not mention invariant globalization, which is the usual
        // cause in a container, so ours has to.
        Assert.Contains("InvariantGlobalization", error, StringComparison.Ordinal);
    }
}
