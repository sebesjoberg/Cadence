namespace Cadence.Storage;

/// <summary>Filters for a run-history query.</summary>
public sealed record RunQuery
{
    /// <summary>Restrict to one job. Null returns every job.</summary>
    public string? JobName { get; init; }

    /// <summary>Restrict to these statuses. Null or empty returns every status.</summary>
    public IReadOnlyCollection<RunStatus>? Statuses { get; init; }

    /// <summary>Restrict to runs started at or after this instant.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Restrict to runs started before this instant.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Restrict to one instance.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Maximum rows to return, newest first.</summary>
    public int Limit { get; init; } = 100;

    /// <summary>Rows to skip, for paging.</summary>
    public int Offset { get; init; }
}
