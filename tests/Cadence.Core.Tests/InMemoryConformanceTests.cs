using Cadence.Storage;
using Cadence.Storage.Conformance;

namespace Cadence.Core.Tests;

/// <summary>
/// Runs the shared storage contract against the in-memory tier.
/// </summary>
/// <remarks>
/// The same suite runs against SQL Server in <c>Cadence.Storage.Sql.Tests</c>. That is the whole
/// point of it: the two tiers are advertised as interchangeable, so the contract is written once and
/// both have to satisfy it.
/// </remarks>
public sealed class InMemoryRunHistoryStoreConformanceTests : RunHistoryStoreConformance
{
    /// <inheritdoc />
    protected override Task<IRunHistoryStore> CreateAsync()
        // Well above anything the conformance suite writes, so the ring never trims mid-test. The
        // trimming behaviour itself is this tier's own and is covered in
        // InMemoryRunHistoryStoreTests.
        => Task.FromResult<IRunHistoryStore>(
            new InMemoryRunHistoryStore(new InMemoryRunHistoryOptions { MaxRunsPerJob = 1000 }));
}

/// <summary>
/// Runs the shared schedule-source contract against the in-process test double.
/// </summary>
/// <remarks>
/// <see cref="MutableScheduleSource"/> is what the scheduling tests use to stand in for a database,
/// so holding it to the same contract as the real thing is what makes those tests worth trusting.
/// </remarks>
public sealed class MutableScheduleSourceConformanceTests : ScheduleSourceConformance
{
    /// <inheritdoc />
    protected override Task<IWritableScheduleSource> CreateAsync()
        => Task.FromResult<IWritableScheduleSource>(new MutableScheduleSource());
}

/// <summary>
/// Runs the shared pause contract against the in-memory tier, minus the one part it does not
/// claim: the switches live in this process and reach nobody else.
/// </summary>
public sealed class InMemoryPauseStoreConformanceTests : PauseStoreConformance
{
    /// <inheritdoc />
    protected override bool IsDistributed => false;

    /// <inheritdoc />
    protected override Task<IPauseStore> CreateAsync()
        => Task.FromResult<IPauseStore>(new InMemoryPauseStore(new SystemClock()));
}
