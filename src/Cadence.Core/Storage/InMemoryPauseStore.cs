namespace Cadence.Storage;

/// <summary>
/// Holds the pause switches in the process. Correct for a single instance, and honest about being
/// nothing more: pausing here pauses this process, and a second instance never hears about it.
/// </summary>
/// <remarks>
/// The default, like the in-memory history store, so pause works on the zero-infrastructure path
/// where there is nobody to distribute it to. A storage package replaces it.
/// </remarks>
public sealed class InMemoryPauseStore : IPauseStore
{
    private readonly ISystemClock _clock;

    private volatile PauseState _state = PauseState.None;

    /// <summary>Creates the store.</summary>
    /// <param name="clock">Stamps when a switch was moved.</param>
    public InMemoryPauseStore(ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <inheritdoc />
    public Task<PauseState> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_state);

    /// <inheritdoc />
    public Task<PauseState> SetAsync(
        PauseScope scope,
        string? reason,
        string? setBy,
        CancellationToken cancellationToken)
    {
        var state = new PauseState
        {
            Scope = scope,
            Reason = reason,
            SetBy = setBy,
            SetAtUtc = _clock.UtcNow,
        };

        _state = state;
        return Task.FromResult(state);
    }
}
