namespace Cadence;

/// <summary>Host-wide scheduler settings.</summary>
public sealed class CadenceOptions
{
    /// <summary>
    /// How often the loop looks for due occurrences. One second is the floor; sub-second
    /// scheduling is explicitly not supported.
    /// </summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Identifies this process in run history and the instance registry. Defaults to
    /// <c>{machine}:{pid}:{short-guid}</c>, which is unique per process rather than per host, so
    /// two instances on one machine are still distinguishable.
    /// </summary>
    public string InstanceId { get; set; } = BuildDefaultInstanceId();

    /// <summary>How often to re-read schedules when the source cannot signal changes itself.</summary>
    public TimeSpan ConfigPollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait for in-flight runs to finish on shutdown before recording them as aborted.</summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Per-instance cap on simultaneous runs. Without it, a <c>*/1</c> job with
    /// <see cref="OverlapPolicy.AllowConcurrent"/> and a slow dependency exhausts the thread pool
    /// and takes the host down with it.
    /// </summary>
    public int MaxConcurrentRuns { get; set; } = 20;

    /// <summary>
    /// Upper bound on occurrences replayed for one job under
    /// <see cref="MissedRunPolicy.RunAll"/>. A warning is logged when the cap truncates.
    /// </summary>
    public int MaxCatchUp { get; set; } = 10;

    /// <summary>
    /// Largest result a run may produce. A job that returns more fails rather than storing a
    /// truncated one.
    /// </summary>
    /// <remarks>
    /// A ceiling exists because a result is built in memory and then written whole: without one, a
    /// job whose output scales with its input takes the host down on the day somebody asks it for
    /// a year of data instead of a week. Raise it deliberately, having checked the storage tier
    /// can carry it.
    /// </remarks>
    public long MaxResultBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>What to do when a registered job cannot be resolved from the container at boot.</summary>
    public StartupValidation Validation { get; set; } = StartupValidation.ThrowOnStartup;

    /// <summary>How much run history to keep. Enforced by the janitor.</summary>
    public RetentionOptions Retention { get; set; } = new();

    /// <summary>Validates the options and throws when a value cannot work.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its supported range.</exception>
    public void Validate()
    {
        if (TickInterval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(TickInterval),
                TickInterval,
                "The tick interval must be at least one second. Cadence does not support sub-second scheduling.");
        }

        if (ConfigPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConfigPollInterval), ConfigPollInterval, "The config poll interval must be positive.");
        }

        if (ShutdownDrainTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ShutdownDrainTimeout), ShutdownDrainTimeout, "The shutdown drain timeout cannot be negative.");
        }

        if (MaxConcurrentRuns < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentRuns), MaxConcurrentRuns, "At least one concurrent run must be allowed.");
        }

        if (MaxCatchUp < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCatchUp), MaxCatchUp, "The catch-up cap must be at least one.");
        }

        if (MaxResultBytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxResultBytes), MaxResultBytes, "The result size ceiling must be positive.");
        }

        if (string.IsNullOrWhiteSpace(InstanceId))
        {
            throw new ArgumentOutOfRangeException(
                nameof(InstanceId), InstanceId, "The instance id cannot be blank.");
        }
    }

    private static string BuildDefaultInstanceId()
    {
        var shortId = Guid.NewGuid().ToString("N")[..8];
        return $"{Environment.MachineName}:{Environment.ProcessId}:{shortId}";
    }
}
