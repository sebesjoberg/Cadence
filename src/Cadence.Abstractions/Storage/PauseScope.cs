namespace Cadence.Storage;

/// <summary>What a pause stops. The two switches are independent.</summary>
[Flags]
public enum PauseScope
{
    /// <summary>Nothing is paused.</summary>
    None = 0,

    /// <summary>
    /// The tick loop claims no occurrences. Paused occurrences are treated as never having
    /// existed, so resuming starts from the next one rather than replaying the paused window.
    /// </summary>
    Schedule = 1,

    /// <summary>
    /// <see cref="TriggerKind.Manual"/> and <see cref="TriggerKind.Api"/> runs are refused. Left
    /// clear during an incident to keep the operator's escape hatch open.
    /// </summary>
    Triggers = 2,

    /// <summary>Both switches. Nothing starts on any instance.</summary>
    All = Schedule | Triggers,
}
