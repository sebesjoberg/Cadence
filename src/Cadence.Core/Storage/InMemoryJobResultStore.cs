namespace Cadence.Storage;

/// <summary>
/// Holds results on the process heap, bounded by total size and dropped on restart.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="InMemoryRunHistoryStore"/>, and it comes with the same caveat one
/// size larger: a result is bytes rather than a row, so this store evicts by total size as well as
/// by expiry, and a result the dashboard offered a moment ago can be gone by the time somebody
/// clicks it. Anything a caller genuinely has to be able to collect belongs in a persistent tier.
/// </remarks>
public sealed class InMemoryJobResultStore : IJobResultStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _byRun = [];
    private readonly long _maxTotalBytes;

    private long _totalBytes;

    /// <summary>Creates the store.</summary>
    /// <param name="options">Size ceiling. Defaults are used when null.</param>
    public InMemoryJobResultStore(InMemoryJobResultOptions? options = null)
        => _maxTotalBytes = Math.Max(1, (options ?? new InMemoryJobResultOptions()).MaxTotalBytes);

    /// <inheritdoc />
    public Task SaveAsync(
        Guid runId,
        JobResult result,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Copied because the caller owns the buffer it handed over and may reuse it.
        var content = result.Content.ToArray();

        lock (_gate)
        {
            RemoveLocked(runId);

            _byRun[runId] = new Entry(
                new JobResultInfo
                {
                    RunId = runId,
                    ContentType = result.ContentType,
                    FileName = result.FileName,
                    Length = content.Length,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = expiresAt,
                },
                content);

            _totalBytes += content.Length;
            EvictLocked();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<JobResultInfo?> DescribeAsync(Guid runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_byRun.TryGetValue(runId, out var entry) ? entry.Info : null);
        }
    }

    /// <inheritdoc />
    public Task<StoredJobResult?> OpenAsync(Guid runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_byRun.TryGetValue(runId, out var entry))
            {
                return Task.FromResult<StoredJobResult?>(null);
            }

            return Task.FromResult<StoredJobResult?>(
                new StoredJobResult(entry.Info, new MemoryStream(entry.Content, writable: false)));
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(Guid runId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            RemoveLocked(runId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> PurgeAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var expired = _byRun
                .Where(pair => pair.Value.Info.ExpiresAt <= now)
                .Select(pair => pair.Key)
                .Take(Math.Max(1, batchSize))
                .ToArray();

            foreach (var runId in expired)
            {
                RemoveLocked(runId);
            }

            return Task.FromResult(expired.Length);
        }
    }

    private void RemoveLocked(Guid runId)
    {
        if (_byRun.Remove(runId, out var existing))
        {
            _totalBytes -= existing.Content.Length;
        }
    }

    private void EvictLocked()
    {
        while (_totalBytes > _maxTotalBytes && _byRun.Count > 1)
        {
            var oldest = _byRun.MinBy(pair => pair.Value.Info.CreatedAt).Key;
            RemoveLocked(oldest);
        }
    }

    private sealed record Entry(JobResultInfo Info, byte[] Content);
}
