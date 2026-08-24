using System.Collections.Immutable;

namespace Cadence.Scheduling;

/// <summary>The per-run configuration the executor needs, whatever started the run.</summary>
public sealed record RunSettings
{
    /// <summary>Effective overlap policy.</summary>
    public required OverlapPolicy Overlap { get; init; }

    /// <summary>Effective maximum duration, or null for no limit.</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>Runtime-editable settings handed to the job.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; }
        = ImmutableDictionary<string, string>.Empty;
}
