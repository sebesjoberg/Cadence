using Microsoft.Extensions.Logging;

namespace Cadence.Sample.Worker;

/// <summary>
/// The sample's own log messages, as source-generated
/// <see cref="LoggerMessageAttribute"/> methods.
/// </summary>
/// <remarks>
/// <para>
/// An earlier version of this sample wrote <c>logger.LogInformation(...)</c> inline and waived
/// CA1848, on the theory that a sample should read the way consumer code usually reads. That was the
/// wrong trade: this is a greenfield repository where warnings are errors everywhere else, and a
/// sample that needs a waiver to compile is teaching the exemption, not the lesson.
/// </para>
/// <para>
/// It costs nothing here either. The message template survives, so the OpenTelemetry log record
/// still carries <c>Hello there, {Name}!</c> as its body with <c>Name</c> as a structured property —
/// which is the thing this sample exists to demonstrate — and the generated code does its own
/// <c>IsEnabled</c> check, so the argument is never evaluated for a disabled level.
/// </para>
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Hello there, {Name}!")]
    public static partial void Greeted(this ILogger logger, string name);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Starting. '{JobName}' runs every {Seconds} seconds; watch for the progress " +
                  "event on each span.")]
    public static partial void SampleStarting(this ILogger logger, string jobName, int seconds);
}
