namespace Cadence.Storage;

/// <summary>One progress entry reported by a running job.</summary>
public sealed record JobLogEntry
{
    /// <summary>When the entry was reported, not when it was written.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Human-readable message.</summary>
    public required string Message { get; init; }

    /// <summary>Structured values attached to the entry.</summary>
    public IReadOnlyDictionary<string, object?>? Data { get; init; }
}
