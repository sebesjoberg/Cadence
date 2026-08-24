using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Cadence.Storage.Sql.Internal;

/// <summary>
/// Buffers job-reported progress and writes it in batches.
/// </summary>
/// <remarks>
/// <para>
/// <c>JobContext.Report</c> is called from job code at whatever rate the job likes, and writing each
/// entry straight through means a loop reporting per item hammers the database with single-row
/// inserts. Nothing about progress justifies that: it is a diagnostic, read by a human looking at one
/// run, so a quarter-second of latency costs nothing and a round trip per entry costs a lot.
/// </para>
/// <para>
/// The buffer is bounded and drops rather than blocks. A job that outruns the writer is already
/// reporting more than anyone will read, and the alternative — applying back-pressure to
/// <c>Report</c> — would make a slow database into a slow job, which is exactly what the un-awaited
/// write in the core progress sink exists to avoid.
/// </para>
/// </remarks>
internal sealed class BatchingLogAppender : IAsyncDisposable
{
    /// <summary>
    /// Columns per row in the flush statement, used to keep a batch under SQL Server's 2,100
    /// parameter ceiling.
    /// </summary>
    private const int ParametersPerRow = 4;

    private const int MaxRowsPerStatement = 2000 / ParametersPerRow;

    private readonly SqlDatabase _database;
    private readonly SqlStorageOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<Pending> _queue;
    private readonly Task _pump;

    private int _dropped;

    public BatchingLogAppender(SqlDatabase database, SqlStorageOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _options = options;
        _logger = logger;

        // FullMode.Wait, so TryWrite reports a full buffer by returning false rather than silently
        // discarding under the covers. The dropping decision belongs here, where it can be counted.
        _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(options.ProgressBatchSize * 50)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Queues an entry. Never blocks, and never throws.</summary>
    /// <param name="runId">The run the entry belongs to.</param>
    /// <param name="entry">The entry.</param>
    public void Enqueue(Guid runId, JobLogEntry entry)
    {
        if (!_queue.Writer.TryWrite(new Pending(runId, entry, Barrier: null)))
        {
            // Counted rather than logged per entry: whatever is filling the buffer is doing it fast,
            // so a line each would just move the flood from the database to the log.
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>
    /// Waits until everything queued before this call has been written.
    /// </summary>
    /// <remarks>
    /// A barrier through the same channel, so it cannot overtake the entries it is waiting on.
    /// Callers that need read-your-writes — tests, and any reader that has just reported — use this;
    /// nothing on the job path does.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the wait, not the write.</param>
    public async Task FlushNowAsync(CancellationToken cancellationToken)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _queue.Writer
                .WriteAsync(new Pending(default, null, barrier), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Already disposed; the pump drained on its way out, so there is nothing left to wait for.
            return;
        }

        await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drains the buffer and stops the pump.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();

        try
        {
            // The pump drains what is queued before it exits, so shutdown does not silently discard
            // the last few entries of the run that was in flight.
            await _pump.WaitAsync(_options.CommandTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Progress is a diagnostic; a failure to flush the tail of it must not fail shutdown.
        }

        ReportDropped();
    }

    private async Task PumpAsync()
    {
        var batch = new List<Pending>(_options.ProgressBatchSize);
        var barriers = new List<TaskCompletionSource>();

        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            barriers.Clear();

            // Take what is already queued, up to a batch. The wait above is spent waiting for the
            // first entry, not after one has arrived, so a lone report is written promptly instead of
            // being held for the whole flush interval while the queue is otherwise idle.
            while (batch.Count < _options.ProgressBatchSize && _queue.Reader.TryRead(out var pending))
            {
                if (pending.Barrier is { } barrier)
                {
                    barriers.Add(barrier);
                }
                else
                {
                    batch.Add(pending);
                }
            }

            if (batch.Count > 0)
            {
                await FlushAsync(batch).ConfigureAwait(false);
            }

            // Completed after the flush, so a caller that awaited the barrier can read back
            // everything it reported before it.
            foreach (var barrier in barriers)
            {
                barrier.TrySetResult();
            }

            ReportDropped();
        }

        // The channel is complete. Anything still waiting on a barrier has to be released, or a
        // FlushNowAsync racing dispose would never return.
        while (_queue.Reader.TryRead(out var leftover))
        {
            leftover.Barrier?.TrySetResult();
        }
    }

    private async Task FlushAsync(List<Pending> batch)
    {
        for (var offset = 0; offset < batch.Count; offset += MaxRowsPerStatement)
        {
            var slice = batch.GetRange(offset, Math.Min(MaxRowsPerStatement, batch.Count - offset));

            try
            {
                await WriteAsync(slice).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Includes the case where the run was purged between report and flush, which takes
                // its log rows with it. Losing a diagnostic is acceptable; failing a job over one is
                // not, and this code is not on the job's path anyway.
                _logger.ProgressFlushFailed(ex, slice.Count);
            }
        }
    }

    private async Task WriteAsync(List<Pending> slice)
    {
        var sql = new StringBuilder()
            .Append("INSERT INTO ").Append(_database.Table("CadenceJobRunLog"))
            .Append(" (RunId, TimestampUtc, Message, DataJson) VALUES ");

        for (var i = 0; i < slice.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            sql.Append("(@r").Append(i)
               .Append(", @t").Append(i)
               .Append(", @m").Append(i)
               .Append(", @d").Append(i)
               .Append(')');
        }

        sql.Append(';');

        // CancellationToken.None: the write is already off the job's path, and abandoning it halfway
        // through shutdown would lose entries the pump has taken responsibility for.
        await _database.ExecuteAsync(
            sql.ToString(),
            command =>
            {
                for (var i = 0; i < slice.Count; i++)
                {
                    var pending = slice[i];

                    SqlValues.AddGuid(command, $"@r{i}", pending.RunId);
                    SqlValues.AddInstant(command, $"@t{i}", pending.Entry!.Timestamp);
                    SqlValues.AddText(command, $"@m{i}", Truncate(pending.Entry.Message), 2000);
                    SqlValues.AddText(command, $"@d{i}", Serialise(pending.Entry.Data), -1);
                }
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private void ReportDropped()
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);

        if (dropped > 0)
        {
            _logger.ProgressDropped(dropped);
        }
    }

    /// <summary>Fits a message to the column rather than failing the write over it.</summary>
    private static string Truncate(string message)
        => message.Length <= 2000 ? message : string.Concat(message.AsSpan(0, 1997), "...");

    private static string? Serialise(IReadOnlyDictionary<string, object?>? data)
    {
        if (data is null || data.Count == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(data);
        }
        catch (NotSupportedException)
        {
            // A value the serialiser cannot handle is the job author's problem to fix, but it must
            // not cost them the message itself.
            return null;
        }
    }

    /// <summary>
    /// A queued entry, or — when <see cref="Entry"/> is null — a flush barrier to complete once
    /// everything ahead of it has been written.
    /// </summary>
    private readonly record struct Pending(Guid RunId, JobLogEntry? Entry, TaskCompletionSource? Barrier);
}
