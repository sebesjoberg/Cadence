namespace Cadence;

/// <summary>How a run came to be started.</summary>
/// <remarks>
/// Also used as the set of triggers a job permits, via
/// <see cref="JobDescriptor.AllowedTriggers"/>, which is why it is a flags enum.
/// </remarks>
[Flags]
public enum TriggerKind
{
    /// <summary>No trigger. Not a valid state for a run.</summary>
    None = 0,

    /// <summary>A cron occurrence came due.</summary>
    Schedule = 1,

    /// <summary>An HTTP call to the trigger endpoint.</summary>
    Api = 2,

    /// <summary>A button in the dashboard, or a direct in-process call.</summary>
    Manual = 4,

    /// <summary>The job is configured to run once when the host starts.</summary>
    Startup = 8,
}
