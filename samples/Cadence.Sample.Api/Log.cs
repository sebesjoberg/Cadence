using Microsoft.Extensions.Logging;

namespace Cadence.Sample.Api;

/// <summary>
/// The sample's own log messages, source-generated for the same reason as the other samples':
/// warnings are errors here, and a sample needing a CA1848 waiver teaches the waiver.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Storage: SQL Server, from ConnectionStrings:cadence. Run history is shared with " +
                  "every other instance on that database.")]
    public static partial void UsingSqlStorage(this ILogger logger);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Storage: in-memory. No ConnectionStrings:cadence, so run history is this " +
                  "process's alone and empties on restart.")]
    public static partial void UsingInMemoryStorage(this ILogger logger);
}
