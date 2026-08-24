namespace Cadence;

/// <summary>
/// The only source of the current time in Cadence. Nothing outside the implementation calls
/// <see cref="DateTimeOffset.UtcNow"/>, so cron evaluation, catch-up and staleness are all
/// testable without sleeping.
/// </summary>
public interface ISystemClock
{
    /// <summary>The current instant, in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
