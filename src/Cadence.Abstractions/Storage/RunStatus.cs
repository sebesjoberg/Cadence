namespace Cadence.Storage;

/// <summary>The outcome of a run.</summary>
public enum RunStatus
{
    /// <summary>Started and not yet finished.</summary>
    Running = 0,

    /// <summary>Completed without throwing.</summary>
    Succeeded = 1,

    /// <summary>Threw an exception.</summary>
    Failed = 2,

    /// <summary>Cancelled because it exceeded its maximum duration.</summary>
    TimedOut = 3,

    /// <summary>Cancelled by host shutdown.</summary>
    Aborted = 4,

    /// <summary>Never started, because the overlap policy or a capacity limit prevented it.</summary>
    Skipped = 5,

    /// <summary>
    /// Left in <see cref="Running"/> by an instance that stopped heartbeating, and reaped by the
    /// janitor. Distinct from <see cref="Aborted"/>: nobody recorded an outcome at all.
    /// </summary>
    Lost = 6,
}
