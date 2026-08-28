using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// Which key ring the ticket cookie uses: the storage tier's where Cadence manages one, the host's
/// arrangement everywhere else — and the host's own Data Protection either way.
/// </summary>
public sealed class DataProtectionTests
{
    private const string JobsPath = "/cadence/api/jobs";

    [Fact]
    public async Task TheStorageTiersRepositoryBecomesTheTicketsKeyRing()
    {
        var repository = new FakeXmlRepository();

        await using var host = await ApiTestHost.StartWithOidcAsync(
            services: collection => collection.AddSingleton<IXmlRepository>(repository));

        await host.SignInAsync("u1", "Ada");

        // Protecting the ticket is what makes the key ring generate a key, and the key landing here
        // is what says the cookie derives from the tier's ring rather than the host's.
        Assert.NotEmpty(repository.Elements);
    }

    [Fact]
    public async Task ATicketMintedOnOneReplicaIsReadableOnTheNext()
    {
        // One store, two separately built hosts: the property N replicas depend on.
        var repository = new FakeXmlRepository();

        await using var first = await StartSharingAsync(repository);
        await using var second = await StartSharingAsync(repository);

        var signedIn = await first.SignInAsync("u1", "Ada");
        var ticket = signedIn.Headers.GetValues("Set-Cookie").First().Split(';')[0];

        using var request = new HttpRequestMessage(HttpMethod.Get, JobsPath);
        request.Headers.Add("Cookie", ticket);
        request.Headers.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await second.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ATicketSurvivesTheReplicaWhereTheHostEncryptsItsKeysWithACertificate()
    {
        // Never installed in any certificate store, so the host's own decryption arrangement is the
        // only thing that can open the key ring again.
        using var certificate = SelfSignedCertificate();

        var repository = new FakeXmlRepository();

        await using var first = await StartSharingAsync(repository, certificate);
        await using var second = await StartSharingAsync(repository, certificate);

        var signedIn = await first.SignInAsync("u1", "Ada");
        var ticket = signedIn.Headers.GetValues("Set-Cookie").First().Split(';')[0];

        using var request = new HttpRequestMessage(HttpMethod.Get, JobsPath);
        request.Headers.Add("Cookie", ticket);
        request.Headers.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await second.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // One key, not two: the second replica read the first's rather than finding it undecryptable
        // and minting its own.
        Assert.Single(repository.Elements);
    }

    [Fact]
    public async Task TheHostsOwnDataProtectionIsLeftWhereTheHostPutIt()
    {
        // The defect this guards against: DataProtectionOptions and KeyManagementOptions are both
        // single-instance and unnamed, so a package configuring them reconfigures the host's own
        // Data Protection -- every payload it has already protected stops decrypting, and its key
        // ring moves into Cadence's store.
        var hostRepository = new FakeXmlRepository();

        // The host arranges its Data Protection first, which is the order that would let a package
        // overwrite it.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddDataProtection()
            .SetApplicationName("the-host-application")
            .AddKeyManagementOptions(management => management.XmlRepository = hostRepository);

        // The storage tier's repository, registered the way UseSqlStorage registers one.
        var tierRepository = new FakeXmlRepository();
        services.AddSingleton<IXmlRepository>(tierRepository);

        services.AddCadence(cadence => cadence.AddApi(options =>
        {
            options.Oidc.Authority = ApiTestHost.OidcAuthority;
            options.Oidc.ClientId = "cadence-tests";
            options.Oidc.ManageDataProtectionKeys = true;
        }));

        await using var provider = services.BuildServiceProvider();

        Assert.Equal(
            "the-host-application",
            provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);

        Assert.Same(
            hostRepository,
            provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);

        // And the host's own payloads still round-trip, through the repository it named.
        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("host");

        Assert.Equal("a payload", protector.Unprotect(protector.Protect("a payload")));
        Assert.NotEmpty(hostRepository.Elements);
        Assert.Empty(tierRepository.Elements);

        // The ticket cookie meanwhile has a provider of its own, which is what keeps the two apart.
        Assert.NotNull(provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CadenceApiDefaults.CookieScheme).DataProtectionProvider);
    }

    [Fact]
    public async Task NothingIsRegisteredWhenKeyManagementIsOff()
    {
        var repository = new FakeXmlRepository();

        await using var host = await ApiTestHost.StartWithOidcAsync(
            configure: options => options.Oidc.ManageDataProtectionKeys = false,
            services: collection => collection.AddSingleton<IXmlRepository>(repository));

        await host.SignInAsync("u1", "Ada");

        // The host's provider protected the ticket, so nothing reached the tier.
        Assert.Empty(repository.Elements);
        Assert.Same(
            host.Services.GetRequiredService<IDataProtectionProvider>(),
            CookieOptions(host).DataProtectionProvider);
        Assert.Equal(ContentRoot(host), Protection(host).ApplicationDiscriminator);
    }

    [Fact]
    public async Task NothingIsRegisteredWithoutOidc()
    {
        // A token-only deployment: no provider, so no ticket to protect.
        var repository = new FakeXmlRepository();

        await using var host = await ApiTestHost.StartAsync(
            configure: options => options.Tokens.Add("read-token"),
            services: collection => collection.AddSingleton<IXmlRepository>(repository));

        Assert.Null(KeyManagement(host).XmlRepository);
        Assert.Equal(ContentRoot(host), Protection(host).ApplicationDiscriminator);
        Assert.Empty(repository.Elements);
    }

    [Fact]
    public async Task TheAppWideOptionsAreUntouchedEvenWhereCadenceManagesTheKeyRing()
    {
        await using var host = await ApiTestHost.StartWithOidcAsync(
            services: collection => collection.AddSingleton<IXmlRepository, FakeXmlRepository>());

        Assert.IsNotType<FakeXmlRepository>(KeyManagement(host).XmlRepository);
        Assert.Equal(ContentRoot(host), Protection(host).ApplicationDiscriminator);
    }

    private static Task<ApiTestHost> StartSharingAsync(IXmlRepository repository)
        => ApiTestHost.StartWithOidcAsync(
            services: collection => collection.AddSingleton(repository));

    private static Task<ApiTestHost> StartSharingAsync(
        IXmlRepository repository, X509Certificate2 certificate)
        => ApiTestHost.StartWithOidcAsync(
            services: collection =>
            {
                collection.AddSingleton(repository);

                // The host's own ring gets a repository of its own. Its keys are encrypted with a
                // certificate that dies with the test, and the default repository is a directory
                // every application on the machine shares.
                collection.AddDataProtection()
                    .AddKeyManagementOptions(management =>
                        management.XmlRepository = new FakeXmlRepository())
                    .ProtectKeysWithCertificate(certificate)
                    .UnprotectKeysWithAnyCertificate(certificate);
            });

    /// <summary>A certificate that exists only for the lifetime of the test.</summary>
    private static X509Certificate2 SelfSignedCertificate()
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=cadence-key-ring-tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var now = DateTimeOffset.UtcNow;

        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(1));
    }

    private static KeyManagementOptions KeyManagement(ApiTestHost host)
        => host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

    private static DataProtectionOptions Protection(ApiTestHost host)
        => host.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value;

    private static CookieAuthenticationOptions CookieOptions(ApiTestHost host)
        => host.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CadenceApiDefaults.CookieScheme);

    /// <summary>The discriminator a host that nobody configured derives for itself.</summary>
    private static string ContentRoot(ApiTestHost host)
        => host.Services.GetRequiredService<IHostEnvironment>().ContentRootPath;

    /// <summary>Stands in for a storage tier's repository, which is registered the same way.</summary>
    private sealed class FakeXmlRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> Elements
        {
            get
            {
                lock (_elements)
                {
                    return [.. _elements];
                }
            }
        }

        public IReadOnlyCollection<XElement> GetAllElements() => Elements;

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_elements)
            {
                _elements.Add(element);
            }
        }
    }
}
