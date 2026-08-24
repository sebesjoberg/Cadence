namespace Cadence;

/// <summary>What to do when a registered job cannot be resolved from the container at boot.</summary>
public enum StartupValidation
{
    /// <summary>
    /// Default. Fail the host. Discovering at 02:00 that a nightly job cannot construct its
    /// dependencies is exactly the failure this prevents.
    /// </summary>
    ThrowOnStartup = 0,

    /// <summary>Start, but leave the unresolvable jobs disabled and log an error for each.</summary>
    DisableFailingJobs = 1,

    /// <summary>Start and schedule everything anyway, logging a warning. Each run will then fail.</summary>
    WarnOnly = 2,
}
