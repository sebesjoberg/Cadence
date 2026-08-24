namespace Cadence.Scheduling;

/// <summary>The outcome of a resolution pass.</summary>
/// <param name="Schedules">Effective schedules, keyed by job name.</param>
/// <param name="Problems">Jobs whose configuration could not be used, and why.</param>
public sealed record ScheduleResolution(
    IReadOnlyDictionary<string, EffectiveSchedule> Schedules,
    IReadOnlyList<ScheduleProblem> Problems);
