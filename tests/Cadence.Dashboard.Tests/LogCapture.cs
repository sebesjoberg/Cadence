using Microsoft.Extensions.Logging;

namespace Cadence.Dashboard.Tests;

/// <summary>Collects log records so a test can assert on what was warned about.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly List<(LogLevel Level, int EventId, string Message)> _records = [];

    public IReadOnlyList<(LogLevel Level, int EventId, string Message)> Records
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Sink(this);

    public void Dispose()
    {
    }

    public bool HasWarning(int eventId) =>
        Records.Any(r => r.Level == LogLevel.Warning && r.EventId == eventId);

    public int Count(LogLevel level, int eventId) =>
        Records.Count(r => r.Level == level && r.EventId == eventId);

    private void Add(LogLevel level, int eventId, string message)
    {
        lock (_records)
        {
            _records.Add((level, eventId, message));
        }
    }

    private sealed class Sink(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Add(logLevel, eventId.Id, formatter(state, exception));
    }
}
