namespace Cadence.Storage;

/// <summary>A stored result, opened for reading.</summary>
/// <remarks>
/// Owns the underlying stream and whatever the tier had to hold open to produce it — a connection,
/// a reader, a file handle — so disposing it is what releases them.
/// </remarks>
public sealed class StoredJobResult : IAsyncDisposable
{
    private readonly IAsyncDisposable? _lifetime;

    /// <summary>Creates an opened result.</summary>
    /// <param name="info">What is known about it without reading.</param>
    /// <param name="content">The bytes, positioned at the start.</param>
    /// <param name="lifetime">
    /// Anything the tier must keep alive for <paramref name="content"/> to stay readable, disposed
    /// after the stream is.
    /// </param>
    public StoredJobResult(JobResultInfo info, Stream content, IAsyncDisposable? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(content);

        Info = info;
        Content = content;
        _lifetime = lifetime;
    }

    /// <summary>What is known about the result without reading it.</summary>
    public JobResultInfo Info { get; }

    /// <summary>The bytes.</summary>
    public Stream Content { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);

        if (_lifetime is not null)
        {
            await _lifetime.DisposeAsync().ConfigureAwait(false);
        }
    }
}
