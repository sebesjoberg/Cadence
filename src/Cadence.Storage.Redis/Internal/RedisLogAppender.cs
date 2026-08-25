using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Cadence.Storage.Redis.Internal;

/// <summary>
/// Buffers job-reported progress and pushes it in batches.
/// </summary>
/// <remarks>
/// <para>
/// Same policy as the SQL tier's appender, and for the same reason. <c>JobContext.Report</c> is
/// called from job code at whatever rate the job likes; writing each entry straight through means a
/// loop reporting per item spends a round trip per item. Progress is a diagnostic read by a human
/// looking at one run, so a quarter-second of latency costs nothing and a round trip per entry costs
/// a lot.
/// </para>
/// <para>
/// The buffer is bounded and drops rather than blocks. A job outrunning the writer is already
/// reporting more than anyone will read, and back-pressure on <c>Report</c> would turn a slow store
/// into a slow job.
/// </para>
/// <para>
/// Not shared with the SQL appender despite the shared policy: that one builds multi-row statements
/// around SQL Server's parameter ceiling, which is most of its body and none of this one's.
/// </para>
/// </remarks>
internal sealed class RedisLogAppender : IAsyncDisposable
{
    private const int QueueCapacity = 10_000;

    private readonly RedisConnection _connection;
    private readonly RedisStorageOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<Pending> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;

    private int _dropped;

    public RedisLogAppender(RedisConnection connection, RedisStorageOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _options = options;
        _logger = logger;

        _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Queues an entry, dropping it if the buffer is full.</summary>
    public void Append(Guid runId, JobLogEntry entry)
    {
        if (!_queue.Writer.TryWrite(new Pending(runId, RedisValues.SerialiseLogEntry(entry))))
        {
            var dropped = Interlocked.Increment(ref _dropped);

            // Logged on powers of ten rather than per entry: a job flooding the buffer would
            // otherwise flood the log about it too.
            if (IsPowerOfTen(dropped))
            {
                _logger.ProgressDropped(dropped);
            }
        }
    }

    /// <summary>Waits until everything queued so far has been written.</summary>
    /// <remarks>
    /// For tests, which need "the buffer is empty" to be a thing they can wait for rather than sleep
    /// through. Nothing in the scheduler calls this.
    /// </remarks>
    public async Task FlushAsync()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_queue.Writer.TryWrite(new Pending(Guid.Empty, null, signal)))
        {
            return;
        }

        await signal.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown raced the pump. Progress entries are diagnostics; losing the last few to a
            // stopping host is not worth surfacing.
        }

        _shutdown.Dispose();
    }

    private static bool IsPowerOfTen(int value)
    {
        while (value >= 10 && value % 10 == 0)
        {
            value /= 10;
        }

        return value == 1;
    }

    private async Task PumpAsync()
    {
        var batch = new List<Pending>(_options.ProgressBatchSize);

        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();

            var deadline = DateTime.UtcNow + _options.ProgressFlushInterval;

            while (batch.Count < _options.ProgressBatchSize && _queue.Reader.TryRead(out var pending))
            {
                batch.Add(pending);
            }

            // Give a partly-filled batch the flush interval to fill up, so a job reporting steadily
            // still gets batched rather than writing one entry per wake-up.
            while (batch.Count < _options.ProgressBatchSize && DateTime.UtcNow < deadline)
            {
                if (_queue.Reader.TryRead(out var pending))
                {
                    batch.Add(pending);
                    continue;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(5)).ConfigureAwait(false);
            }

            await WriteAsync(batch).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(List<Pending> batch)
    {
        var entries = batch.Where(p => p.Payload is not null).ToList();

        if (entries.Count > 0)
        {
            try
            {
                var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);
                var keys = _connection.Keys;

                var pushes = entries
                    .GroupBy(p => p.RunId)
                    .Select(group => database.ListRightPushAsync(
                        keys.RunLog(group.Key),
                        [.. group.Select(p => (RedisValue)p.Payload!)]))
                    .ToArray();

                await Task.WhenAll(pushes).ConfigureAwait(false);
            }
            catch (RedisException ex)
            {
                // Progress is a diagnostic. Failing to store it must not fail the run that reported
                // it, and this pump has no caller to propagate to anyway.
                _logger.ProgressFlushFailed(ex, entries.Count);
            }
        }

        foreach (var pending in batch)
        {
            pending.Signal?.TrySetResult();
        }
    }

    private sealed record Pending(Guid RunId, string? Payload, TaskCompletionSource? Signal = null);
}
