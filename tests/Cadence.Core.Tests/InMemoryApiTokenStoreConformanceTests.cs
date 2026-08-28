using Cadence.Storage;
using Cadence.Storage.Conformance;

namespace Cadence.Core.Tests;

/// <summary>
/// Runs the token contract against the tier that has no storage package.
/// </summary>
/// <remarks>
/// Most of the suite skips here, and that is the point: the same file proves the tier resolves
/// nothing and that it deliberately lacks the administer half.
/// </remarks>
public sealed class InMemoryApiTokenStoreConformanceTests : ApiTokenStoreConformance
{
    /// <inheritdoc />
    protected override Task<IApiTokenStore> CreateAsync()
        => Task.FromResult<IApiTokenStore>(new ConfiguredApiTokenStore());

    /// <inheritdoc />
    protected override bool IsWritable => false;
}
