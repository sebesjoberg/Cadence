using System.Diagnostics;
using Cadence.Diagnostics;
using Cadence.Execution;
using Cadence.Storage;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cadence.Core.Tests;

/// <summary>
/// Progress a job reports has to reach OpenTelemetry, not just Cadence's own history table — via the
/// run's span and via the standard <see cref="ILogger"/> pipeline the host already exports.
/// </summary>
public class ProgressTelemetryTests
{
    [Fact]
    public void ProgressBecomesAnEventOnTheRunActivity()
    {
        var recorded = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CadenceDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = recorded.Add,
        };

        ActivitySource.AddActivityListener(listener);

        var sink = BuildSink(out _);

        using (var activity = CadenceDiagnostics.ActivitySource.StartActivity(
                   CadenceDiagnostics.RunActivityName))
        {
            Assert.NotNull(activity);
            sink.Report(Guid.NewGuid(), "processed 400 of 12000", new Dictionary<string, object?>
            {
                ["done"] = 400,
            });
        }

        var span = Assert.Single(recorded);
        var progress = Assert.Single(span.Events, e => e.Name == CadenceDiagnostics.ProgressEventName);

        Assert.Equal("processed 400 of 12000", progress.Tags.Single(t => t.Key == "message").Value);

        // Namespaced, so a caller's own key can never shadow "message".
        Assert.Equal(400, progress.Tags.Single(t => t.Key == "data.done").Value);
    }

    [Fact]
    public void ProgressIsWrittenThroughTheStandardLoggerPipeline()
    {
        var sink = BuildSink(out var logs);

        sink.Report(Guid.NewGuid(), "halfway there", data: null);

        // Information, not Debug: this is the job telling an operator where it is.
        var entry = Assert.Single(logs);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("halfway there", entry.Message);
    }

    [Fact]
    public async Task ProgressAlsoLandsInHistoryForTheDashboardToReadBack()
    {
        var history = new InMemoryRunHistoryStore();
        var clock = new FakeClock(Occurrences.Utc(2026, 8, 24, 2, 0));
        var sink = new RunHistoryProgressSink(history, clock, new CapturingLogger<RunHistoryProgressSink>([]));

        var runId = Guid.NewGuid();
        await history.StartAsync(
            new JobRunStart
            {
                RunId = runId,
                JobName = "job",
                Trigger = TriggerKind.Manual,
                InstanceId = "test:1:aaaaaaaa",
                StartedAt = clock.UtcNow,
            },
            CancellationToken.None);

        sink.Report(runId, "step one", null);

        var run = await WaitForLogEntryAsync(history, runId);

        var entry = Assert.Single(run.Log);
        Assert.Equal("step one", entry.Message);

        // Timestamped when reported, not when written, so an out-of-order append still sorts right.
        Assert.Equal(clock.UtcNow, entry.Timestamp);
    }

    private static async Task<JobRun> WaitForLogEntryAsync(InMemoryRunHistoryStore history, Guid runId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var run = await history.GetLastRunAsync("job", CancellationToken.None);
            if (run is { Log.Count: > 0 } && run.RunId == runId)
            {
                return run;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The progress entry never reached history.");
    }

    private static RunHistoryProgressSink BuildSink(out List<(LogLevel Level, string Message)> logs)
    {
        logs = [];

        return new RunHistoryProgressSink(
            new InMemoryRunHistoryStore(),
            new FakeClock(Occurrences.Utc(2026, 8, 24, 2, 0)),
            new CapturingLogger<RunHistoryProgressSink>(logs));
    }
}

/// <summary>Captures what was logged, so the OTel-compatible path can be asserted on.</summary>
internal sealed class CapturingLogger<T>(List<(LogLevel Level, string Message)> entries) : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => entries.Add((logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
