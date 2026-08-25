using Xunit;

namespace Cadence.Storage.Conformance;

/// <summary>
/// The behaviour every <see cref="IPauseStore"/> must have.
/// </summary>
/// <remarks>
/// Two switches that move independently, and a change that reaches an instance which did not make
/// it. The second is the whole point of the seam — a pause only one process hears about is an
/// option, not a control.
/// </remarks>
public abstract class PauseStoreConformance
{
    /// <summary>
    /// Creates a store. Called more than once in a test, and every store a single test creates must
    /// share one backing store, so instances genuinely see each other's writes.
    /// </summary>
    protected abstract Task<IPauseStore> CreateAsync();

    /// <summary>Gives a store a chance to notice a write made through another instance.</summary>
    /// <param name="store">The store that has to catch up.</param>
    protected virtual Task PollAsync(IPauseStore store) => Task.CompletedTask;

    /// <summary>
    /// Whether a write reaches instances that did not make it. False for the in-memory tier, which
    /// holds the switches in one process and says so.
    /// </summary>
    protected virtual bool IsDistributed => true;

    [SkippableFact]
    public async Task NothingIsPausedToBeginWith()
    {
        var store = await CreateAsync();

        var state = await store.GetAsync(default);

        Assert.Equal(PauseScope.None, state.Scope);
        Assert.False(state.IsSchedulePaused);
        Assert.False(state.AreTriggersPaused);
        Assert.Null(state.SetAtUtc);
    }

    [SkippableFact]
    public async Task APauseReadsBackWithItsReasonAndAuthor()
    {
        var store = await CreateAsync();

        var written = await store.SetAsync(PauseScope.All, "payment gateway incident", "ops@example.com", default);
        var read = await store.GetAsync(default);

        Assert.Equal(PauseScope.All, read.Scope);
        Assert.Equal("payment gateway incident", read.Reason);
        Assert.Equal("ops@example.com", read.SetBy);
        Assert.NotNull(read.SetAtUtc);
        Assert.Equal(written.Scope, read.Scope);
        Assert.Equal(written.SetAtUtc, read.SetAtUtc);
    }

    [SkippableTheory]
    [InlineData(PauseScope.Schedule, true, false)]
    [InlineData(PauseScope.Triggers, false, true)]
    [InlineData(PauseScope.All, true, true)]
    [InlineData(PauseScope.None, false, false)]
    public async Task TheTwoSwitchesMoveIndependently(PauseScope scope, bool schedule, bool triggers)
    {
        var store = await CreateAsync();

        await store.SetAsync(scope, reason: null, setBy: null, default);
        var state = await store.GetAsync(default);

        Assert.Equal(schedule, state.IsSchedulePaused);
        Assert.Equal(triggers, state.AreTriggersPaused);
    }

    [SkippableFact]
    public async Task ResumingClearsTheReasonAsWellAsTheScope()
    {
        var store = await CreateAsync();

        await store.SetAsync(PauseScope.All, "incident", "ops", default);
        await store.SetAsync(PauseScope.None, reason: null, setBy: null, default);

        var state = await store.GetAsync(default);

        Assert.Equal(PauseScope.None, state.Scope);
        Assert.Null(state.Reason);
        Assert.Null(state.SetBy);
    }

    [SkippableFact]
    public async Task APauseSetOnOneInstanceIsSeenByAnother()
    {
        Skip.IfNot(IsDistributed, "This tier holds the switches in one process, by design.");

        var setter = await CreateAsync();
        var reader = await CreateAsync();

        await setter.SetAsync(PauseScope.Schedule, "deploying", "release-bot", default);
        await PollAsync(reader);

        var state = await reader.GetAsync(default);

        Assert.True(state.IsSchedulePaused);
        Assert.False(state.AreTriggersPaused);
        Assert.Equal("deploying", state.Reason);
    }
}
