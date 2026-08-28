using Microsoft.Extensions.Logging;

namespace Cadence.Api.Internal;

/// <summary>
/// Every log message the control surface writes, as source-generated
/// <see cref="LoggerMessageAttribute"/> methods, so wording and event ids stay stable for anyone
/// alerting on them.
/// </summary>
internal static partial class Log
{
    // 3000-3099: mapping and the gate.

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Warning,
        Message = "Cadence's API is mapped with nothing that would authenticate it. Anything on " +
                  "this host that can reach {BasePath} can trigger jobs and halt scheduling. This " +
                  "is allowed in Development only, where non-loopback callers are refused; outside " +
                  "it, MapCadenceApi() will refuse to map. Supply a token, or name an authorization " +
                  "policy, before deploying.")]
    public static partial void MappedUnauthenticatedInDevelopment(this ILogger logger, string basePath);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Cadence's API is mapped with AllowUnauthenticated set, so it performs no " +
                  "authentication of its own. Anything that can reach {BasePath} can trigger jobs. " +
                  "This is only safe when something in front of this application authenticates " +
                  "callers.")]
    public static partial void MappedWithAuthenticationDisabled(this ILogger logger, string basePath);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Information,
        Message = "Cadence's API accepted {TotalCount} token(s): {FromCode} set in code, " +
                  "{FromConfiguration} from Cadence:Api:Tokens, {FromEnvironment} from " +
                  "CADENCE_API_TOKEN. Values are never logged.")]
    public static partial void TokenSourcesBound(
        this ILogger logger,
        int totalCount,
        int fromCode,
        int fromConfiguration,
        int fromEnvironment);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Warning,
        Message = "Cadence's API is mapped at {BasePath} with token authentication enforced by a " +
                  "writable token store, and no token is configured. Any token already issued still " +
                  "works; if none has been, every request is refused and no new one can be issued " +
                  "over HTTP, because creating one requires a signed-in user. Supply a token " +
                  "(CADENCE_API_TOKEN, or Cadence:Api:Tokens), or configure CadenceApiOptions.Oidc " +
                  "so somebody can sign in and issue one.")]
    public static partial void MappedWithNoTokensIssued(this ILogger logger, string basePath);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Warning,
        Message = "Cadence's API is mapped with OIDC configured but no RequiredClaimType set. Any " +
                  "user the identity provider authenticates can trigger jobs and pause the cluster. " +
                  "Set Cadence:Api:Oidc:RequiredClaimType to restrict who that is.")]
    public static partial void OidcHasNoRequiredClaim(this ILogger logger);

    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Warning,
        Message = "Cadence's API is mapped at {BasePath} under the host's own policy " +
                  "'{PolicyName}', so the token administration routes are not mapped and answer " +
                  "404. That policy governs alone, and it was not written to admit credential " +
                  "administration -- anything it admits, including a bearer token, could otherwise " +
                  "mint and revoke tokens. Set " +
                  "CadenceApiOptions.AllowTokenAdministrationUnderHostPolicy to mount them behind " +
                  "it.")]
    public static partial void TokenAdministrationNotMounted(
        this ILogger logger,
        string basePath,
        string policyName);

    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Warning,
        Message = "Cadence's API is mapped at {BasePath} with AllowUnauthenticated set, and it is " +
                  "ignored: something else authenticates this deployment -- a configured token, an " +
                  "identity provider, or a policy of the host's -- and that enforcement stands. " +
                  "Callers still need a credential. Remove the flag, or remove what enforces " +
                  "authentication, so the configuration says one thing.")]
    public static partial void AuthenticationDisabledButIgnored(this ILogger logger, string basePath);

    // 3100-3199: identity.

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Warning,
        Message = "Cadence cleared the session cookie, but could not read the identity provider's " +
                  "discovery document to complete a provider-side sign-out. The user is signed out " +
                  "here and may still be signed in at the provider.")]
    public static partial void ProviderSignOutUnavailable(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Warning,
        Message = "Cadence refused a sign-in: the handshake did not complete. A user missing the " +
                  "claim required by Cadence:Api:Oidc:RequiredClaimType reaches this, and so does a " +
                  "stale or replayed callback.")]
    public static partial void SignInRefused(this ILogger logger, Exception? exception);

    [LoggerMessage(
        EventId = 3102,
        Level = LogLevel.Warning,
        Message = "Cadence could not carry the host's key-decryption certificates into the ticket " +
                  "key ring: this runtime's Data Protection does not expose the type they are " +
                  "held in. The host encrypts its keys at rest, so the ticket key ring " +
                  "may not be readable back -- each replica would then fall back to a key of its " +
                  "own, and a user is signed out whenever a request reaches a different one. A " +
                  "certificate named by thumbprint and installed in the certificate store is " +
                  "unaffected; one loaded from a file is not. Setting " +
                  "CadenceApiOptions.Oidc.ManageDataProtectionKeys to false leaves the key ring " +
                  "entirely to the host.")]
    public static partial void TicketKeyDecryptionNotCarried(this ILogger logger);
}
