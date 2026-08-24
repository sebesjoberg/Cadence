namespace Cadence;

/// <summary>
/// The real clock. The only place in Cadence that reads the machine's current time.
/// </summary>
public sealed class SystemClock : ISystemClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
