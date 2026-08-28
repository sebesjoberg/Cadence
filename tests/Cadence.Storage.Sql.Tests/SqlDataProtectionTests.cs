using System.Xml.Linq;
using Cadence;
using Cadence.Storage.Sql.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Storage.Sql.Tests;

/// <summary>
/// The key ring in the database, which is what makes the ticket cookie work across replicas.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SqlDataProtectionTests : IAsyncDisposable
{
    /// <summary>A dead port with SqlClient's retry off, so a connection fails in milliseconds.</summary>
    private const string Unreachable =
        "Server=127.0.0.1,1;Database=cadence;User Id=sa;Password=Unused_1234;" +
        "Encrypt=False;Connect Timeout=2;ConnectRetryCount=0";

    private readonly SqlServerFixture _fixture;
    private readonly List<ServiceProvider> _providers = [];

    public SqlDataProtectionTests(SqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task TwoProvidersOverOneDatabaseShareAKeyRing()
    {
        var options = await _fixture.CreateMigratedAsync("dataprotection");

        var first = Protector(options);
        var protectedPayload = first.Protect("a ticket");

        // A second provider, as a second replica would build it: same store, no shared memory.
        var second = Protector(options);

        Assert.Equal("a ticket", second.Unprotect(protectedPayload));
    }

    [SkippableFact]
    public async Task AStoredKeyComesBackAndAReStoreReplacesItsRow()
    {
        var options = await _fixture.CreateMigratedAsync("dpstore");
        var repository = new SqlXmlRepository(new SqlDatabase(options));

        repository.StoreElement(XElement.Parse("<key id='k1'><data>1</data></key>"), "key-1");
        repository.StoreElement(XElement.Parse("<key id='k2'><data>2</data></key>"), "key-2");

        // Re-storing the same friendly name replaces the row rather than adding one.
        repository.StoreElement(XElement.Parse("<key id='k1'><data>3</data></key>"), "key-1");

        var elements = repository.GetAllElements();

        Assert.Equal(2, elements.Count);
        Assert.Contains(elements, element => element.Element("data")?.Value == "3");
        Assert.Contains(elements, element => element.Element("data")?.Value == "2");
    }

    // The tier's registration alone. The same property with AddApi and a provider configured on top
    // is Cadence.Api.Tests' TheHostsOwnDataProtectionIsLeftWhereTheHostPutIt, which is where the
    // options this could reconfigure are written.
    [Fact]
    public void AHostsOwnKeyRingIsLeftWhereTheHostPutIt()
    {
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"cadence-dp-{Guid.NewGuid():N}"));
        directory.Create();

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCadence(cadence => cadence.UseSqlStorage(Unreachable));
            services.AddDataProtection().PersistKeysToFileSystem(directory);

            using var provider = services.BuildServiceProvider();

            // The tier's repository is in the container, and the host's configured one is what the
            // key ring uses.
            Assert.IsType<SqlXmlRepository>(provider.GetRequiredService<IXmlRepository>());
            Assert.IsType<FileSystemXmlRepository>(
                provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);

            var protector = provider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("test");

            // A key ring that had been redirected into SQL would fail against Unreachable rather
            // than round-trip, and would leave the host's folder empty.
            Assert.Equal("a ticket", protector.Unprotect(protector.Protect("a ticket")));
            Assert.NotEmpty(directory.GetFiles("*.xml"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ADefaultKeyRingDoesNotReachForTheTiersRepository()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCadence(cadence => cadence.UseSqlStorage(Unreachable));

        // No repository configured, which is the only shape in which something resolving
        // IXmlRepository from the container would reach the tier's.
        services.AddDataProtection();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        Assert.IsType<SqlXmlRepository>(provider.GetRequiredService<IXmlRepository>());
        Assert.IsNotType<SqlXmlRepository>(options.XmlRepository);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }
    }

    private IDataProtector Protector(SqlStorageOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddDataProtection()
            .SetApplicationName("Cadence")
            .AddKeyManagementOptions(management =>
                management.XmlRepository = new SqlXmlRepository(new SqlDatabase(options)));

        // Held rather than disposed here: the protector it hands back reads the key ring lazily.
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
    }
}
