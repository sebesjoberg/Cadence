using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Api.Internal;

/// <summary>
/// The Data Protection provider the ticket cookie is protected with, over the key ring the storage
/// tier registered.
/// </summary>
/// <remarks>
/// <para>
/// Built in a container of its own rather than by configuring the host's. <c>DataProtectionOptions</c>
/// and <c>KeyManagementOptions</c> are both single-instance and unnamed, so setting
/// <c>ApplicationDiscriminator</c> or <c>XmlRepository</c> on them changes what the host's own
/// protected payloads derive from and moves its key ring. <c>CookieAuthenticationOptions</c> is named
/// per scheme, so its <c>DataProtectionProvider</c> is the seam that reaches Cadence's cookie alone.
/// </para>
/// <para>
/// <see cref="Provider"/> is null where Cadence manages no key ring — no provider configured,
/// <c>ManageDataProtectionKeys</c> false, or no storage tier to put one in — and the cookie handler
/// then falls back to the host's provider, which is what "leave it entirely to the host" means.
/// </para>
/// </remarks>
internal sealed class TicketKeyRing : IDisposable
{
    /// <summary>The Data Protection application name every Cadence replica shares.</summary>
    public const string ApplicationName = "Cadence";

    private const string KeyDecryptionOptionsType =
        "Microsoft.AspNetCore.DataProtection.XmlEncryption.XmlKeyDecryptionOptions";

    private const string InternalDecryptorType =
        "Microsoft.AspNetCore.DataProtection.XmlEncryption.IInternalEncryptedXmlDecryptor";

    private readonly ServiceProvider? _container;

    public TicketKeyRing(IOptions<CadenceApiOptions> options, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);

        var oidc = options.Value.Oidc;

        if (!oidc.IsConfigured
            || !oidc.ManageDataProtectionKeys
            || services.GetService<IXmlRepository>() is not { } repository)
        {
            return;
        }

        _container = Build(repository, services);
        Provider = _container.GetRequiredService<IDataProtectionProvider>();
    }

    /// <summary>The provider, or null where the host's own arrangement stands.</summary>
    public IDataProtectionProvider? Provider { get; }

    /// <inheritdoc />
    public void Dispose() => _container?.Dispose();

    private static ServiceProvider Build(IXmlRepository repository, IServiceProvider services)
    {
        // Read, never written: what the host arranged for its own keys is carried over below so that
        // ProtectKeysWithCertificate and the rest still compose with this.
        var host = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var loggers = services.GetRequiredService<ILoggerFactory>();

        var container = new ServiceCollection();

        // As an instance, which this container does not own: it disposes what it constructs, so the
        // host's logger factory outlives the child.
        container.AddSingleton(loggers);

        container.AddDataProtection()

            // Every replica derives its keys from this name, whatever each host's own is.
            .SetApplicationName(ApplicationName)
            .AddKeyManagementOptions(management =>
            {
                management.XmlRepository = repository;
                management.AutoGenerateKeys = host.AutoGenerateKeys;
                management.NewKeyLifetime = host.NewKeyLifetime;

                // Only what the host actually set: a null here would discard the default this
                // container has already filled in.
                if (host.XmlEncryptor is { } encryptor)
                {
                    management.XmlEncryptor = encryptor;
                }

                if (host.AuthenticatedEncryptorConfiguration is { } configuration)
                {
                    management.AuthenticatedEncryptorConfiguration = configuration;
                }

                foreach (var sink in host.KeyEscrowSinks)
                {
                    management.KeyEscrowSinks.Add(sink);
                }
            });

        ForwardKeyDecryption(container, services, host, loggers);

        return container.BuildServiceProvider();
    }

    /// <summary>
    /// Registers the host's key-decryption services in the child, as instances it does not own.
    /// </summary>
    /// <remarks>
    /// <c>EncryptedXmlDecryptor</c> resolves both of these from whichever container owns the key
    /// ring. <c>IOptions&lt;XmlKeyDecryptionOptions&gt;</c> is the one that carries weight: it is
    /// where <c>ProtectKeysWithCertificate</c> and <c>UnprotectKeysWithAnyCertificate</c> put their
    /// certificates, in the host's container. <c>IInternalEncryptedXmlDecryptor</c> is consumed too
    /// but is null on a stock host, so it is forwarded only for one that registers it. Both types
    /// are internal to Data Protection, so they are named rather than referenced; where a runtime
    /// does not have them the child keeps its own, and only a host that encrypts its keys is worse
    /// off for it.
    /// </remarks>
    private static void ForwardKeyDecryption(
        IServiceCollection container,
        IServiceProvider services,
        KeyManagementOptions host,
        ILoggerFactory loggers)
    {
        var protection = typeof(KeyManagementOptions).Assembly;

        if (protection.GetType(KeyDecryptionOptionsType) is { } options)
        {
            Forward(typeof(IOptions<>).MakeGenericType(options));
        }
        else if (host.XmlEncryptor is not null)
        {
            loggers.CreateLogger("Cadence.Api").TicketKeyDecryptionNotCarried();
        }

        if (protection.GetType(InternalDecryptorType) is { } decryptor)
        {
            Forward(decryptor);
        }

        void Forward(Type service)
        {
            if (services.GetService(service) is { } instance)
            {
                container.AddSingleton(service, instance);
            }
        }
    }
}
