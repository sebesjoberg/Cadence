namespace Cadence.Storage;

/// <summary>One process that has registered itself as part of this deployment.</summary>
public sealed record InstanceInfo
{
    /// <summary>The instance's stable id, as it appears on a run.</summary>
    public required string InstanceId { get; init; }

    /// <summary>The host the process is running on.</summary>
    public required string MachineName { get; init; }

    /// <summary>The operating system process id.</summary>
    public required int ProcessId { get; init; }

    /// <summary>The entry assembly's informational version, where one could be read.</summary>
    public string? AssemblyVersion { get; init; }

    /// <summary>When the process registered itself.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>When it last confirmed it was alive.</summary>
    public required DateTimeOffset LastHeartbeatUtc { get; init; }
}
