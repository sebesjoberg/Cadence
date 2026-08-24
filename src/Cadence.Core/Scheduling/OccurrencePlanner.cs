namespace Cadence.Scheduling;

/// <summary>
/// Decides which of a job's due occurrences should actually run. Pure, so the missed-run policies
/// and the catch-up cap are testable without a clock or a host.
/// </summary>
public static class OccurrencePlanner
{
    /// <summary>
    /// Hard ceiling on how many occurrences are enumerated in one pass. A sub-minute cron and a
    /// long outage can otherwise produce millions, and walking them costs more than the runs are
    /// worth. Hitting this ceiling is reported as <see cref="OccurrencePlan.TooFarBehind"/>.
    /// </summary>
    public const int MaxEnumeratedOccurrences = 10_000;

    /// <summary>Plans the occurrences to run for one job.</summary>
    /// <param name="schedule">The job's effective schedule.</param>
    /// <param name="lastEvaluated">The instant this job was last evaluated up to, exclusive.</param>
    /// <param name="now">The current instant, inclusive.</param>
    /// <param name="maxCatchUp">Cap on replayed occurrences under <see cref="MissedRunPolicy.RunAll"/>.</param>
    /// <returns>Which occurrences to run, and what was deliberately dropped.</returns>
    public static OccurrencePlan Plan(
        EffectiveSchedule schedule,
        DateTimeOffset lastEvaluated,
        DateTimeOffset now,
        int maxCatchUp)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var due = new List<DateTimeOffset>();
        var tooFarBehind = false;
        var cursor = lastEvaluated;

        while (true)
        {
            var next = schedule.Cron.GetNextOccurrence(cursor, schedule.TimeZone, inclusive: false);

            if (next is null || next.Value > now)
            {
                break;
            }

            due.Add(next.Value);
            cursor = next.Value;

            if (due.Count >= MaxEnumeratedOccurrences)
            {
                tooFarBehind = true;
                break;
            }
        }

        if (tooFarBehind)
        {
            // We cannot cheaply tell which occurrence was the most recent, and replaying a
            // backlog this size is never what the operator wants. Drop the lot and resume.
            return new OccurrencePlan
            {
                Occurrences = [],
                DroppedCount = due.Count,
                TooFarBehind = true,
            };
        }

        return due.Count switch
        {
            0 => OccurrencePlan.Empty,

            // The common case: one occurrence came due since the last tick. No policy applies.
            1 => new OccurrencePlan { Occurrences = due },

            _ => ApplyMissedPolicy(schedule.Descriptor.OnMissed, due, maxCatchUp),
        };
    }

    private static OccurrencePlan ApplyMissedPolicy(
        MissedRunPolicy policy,
        List<DateTimeOffset> due,
        int maxCatchUp)
    {
        switch (policy)
        {
            case MissedRunPolicy.RunOnce:
                // Fire once for the most recent missed slot, then resume normally.
                return new OccurrencePlan
                {
                    Occurrences = [due[^1]],
                    DroppedCount = due.Count - 1,
                };

            case MissedRunPolicy.RunAll:
                var cap = Math.Max(1, maxCatchUp);
                if (due.Count <= cap)
                {
                    return new OccurrencePlan { Occurrences = due };
                }

                // When the cap truncates, keep the most recent occurrences: the newest data is
                // what the operator actually wants processed.
                return new OccurrencePlan
                {
                    Occurrences = due.GetRange(due.Count - cap, cap),
                    DroppedCount = due.Count - cap,
                    TruncatedByCap = true,
                };

            case MissedRunPolicy.SkipToNext:
            default:
                return new OccurrencePlan { Occurrences = [], DroppedCount = due.Count };
        }
    }
}
