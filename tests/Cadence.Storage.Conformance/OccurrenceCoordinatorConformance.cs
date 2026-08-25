using Xunit;

namespace Cadence.Storage.Conformance;

/// <summary>
/// The behaviour every real <see cref="IOccurrenceCoordinator"/> must have.
/// </summary>
/// <remarks>
/// <para>
/// This suite is the whole clustering guarantee, written down. Anyone swapping the coordination
/// mechanism — for etcd, for a table they already own, for a Quartz-backed adapter — inherits these
/// and finds out immediately whether their implementation actually holds.
/// </para>
/// <para>
/// It cannot be run against the no-op coordinator, which grants everything. That is the point:
/// single-instance correctness is not evidence of anything, and these tests need a store two callers
/// can genuinely contend over.
/// </para>
/// </remarks>
public abstract class OccurrenceCoordinatorConformance
{
    /// <summary>An occurrence instant that is stable across runs.</summary>
    protected static readonly DateTimeOffset Occurrence = new(2026, 8, 24, 11, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Creates a coordinator sharing storage with every other coordinator from the same call to this
    /// method's owning fixture, so separate instances can contend.
    /// </summary>
    /// <param name="instanceId">Identifies the simulated instance.</param>
    protected abstract Task<IOccurrenceCoordinator> CreateAsync(string instanceId);

    [SkippableFact]
    public async Task AnUnclaimedOccurrenceIsGranted()
    {
        var coordinator = await CreateAsync("one");

        Assert.True(await coordinator.TryClaimAsync("job", Occurrence, Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task ASecondInstanceLosesTheSameOccurrence()
    {
        var first = await CreateAsync("one");
        var second = await CreateAsync("two");

        Assert.True(await first.TryClaimAsync("job", Occurrence, Guid.NewGuid(), default));
        Assert.False(await second.TryClaimAsync("job", Occurrence, Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task DifferentOccurrencesOfOneJobAreIndependent()
    {
        var coordinator = await CreateAsync("one");

        Assert.True(await coordinator.TryClaimAsync("job", Occurrence, Guid.NewGuid(), default));
        Assert.True(await coordinator.TryClaimAsync("job", Occurrence.AddHours(1), Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task DifferentJobsAtTheSameInstantAreIndependent()
    {
        var coordinator = await CreateAsync("one");

        Assert.True(await coordinator.TryClaimAsync("a", Occurrence, Guid.NewGuid(), default));
        Assert.True(await coordinator.TryClaimAsync("b", Occurrence, Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task ReClaimingWithTheSameRunIdIsGrantedAgain()
    {
        /*
            The idempotency property, and the reason the run id is assigned by the caller.

            A transient fault can drop the acknowledgement of a write that already committed. The
            retry then finds the row taken. If it could not tell its own commit from a peer's, it
            would report "someone else won" and skip a run it actually owns -- silently, with nothing
            in history and nobody alerted.
        */
        var coordinator = await CreateAsync("one");
        var runId = Guid.NewGuid();

        Assert.True(await coordinator.TryClaimAsync("job", Occurrence, runId, default));
        Assert.True(await coordinator.TryClaimAsync("job", Occurrence, runId, default));
    }

    [SkippableFact]
    public async Task ReClaimingWithADifferentRunIdIsRefused()
    {
        // The mirror of the test above: idempotency must not soften into "always grant on retry".
        var coordinator = await CreateAsync("one");

        Assert.True(await coordinator.TryClaimAsync("job", Occurrence, Guid.NewGuid(), default));
        Assert.False(await coordinator.TryClaimAsync("job", Occurrence, Guid.NewGuid(), default));
    }

    [SkippableFact]
    public async Task ExactlyOneOfManySimultaneousClaimsWins()
    {
        // The hammer. Every instance starts its tick on the same second, so this is not a contrived
        // scenario -- it is the normal case for a cluster whose clocks are in sync.
        const int Contenders = 32;

        var coordinators = new List<IOccurrenceCoordinator>(Contenders);

        for (var i = 0; i < Contenders; i++)
        {
            coordinators.Add(await CreateAsync($"instance-{i}"));
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = coordinators.Select(async coordinator =>
        {
            await ready.Task;
            return await coordinator.TryClaimAsync("job", Occurrence, Guid.NewGuid(), default);
        }).ToArray();

        ready.SetResult();

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(won => won));
    }
}
