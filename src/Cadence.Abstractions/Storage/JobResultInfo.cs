namespace Cadence.Storage;

/// <summary>What is known about a stored result without reading its bytes.</summary>
public sealed record JobResultInfo
{
    /// <summary>The run that produced it.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The media type the bytes are served with.</summary>
    public required string ContentType { get; init; }

    /// <summary>Suggested filename, or null to serve the bytes inline.</summary>
    public string? FileName { get; init; }

    /// <summary>How many bytes the result holds.</summary>
    public required long Length { get; init; }

    /// <summary>When it was stored.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it becomes eligible for deletion.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
