using Cadence.Storage.Conformance;
using Cadence.Storage.Sql.Internal;
using Xunit;

namespace Cadence.Storage.Sql.Tests;

/// <summary>Runs the shared instance-directory contract against SQL Server.</summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SqlInstanceDirectoryTests : InstanceDirectoryConformance
{
    private readonly SqlServerFixture _fixture;

    public SqlInstanceDirectoryTests(SqlServerFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    protected override async Task<(IInstanceDirectory Directory, Func<InstanceInfo, CancellationToken, Task> Beat)>
        CreateAsync(CancellationToken cancellationToken)
    {
        var options = await _fixture.CreateMigratedAsync("instances");
        var directory = new SqlInstanceDirectory(new SqlDatabase(options));

        return (directory, (instance, ct) => SqlServerFixture.WriteInstanceAsync(options, instance, ct));
    }
}
