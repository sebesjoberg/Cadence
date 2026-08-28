using Cadence.Storage;
using Microsoft.Extensions.Hosting;

namespace Cadence.Api.Internal;

/// <summary>The one condition Cadence's schemes, its policies and their use all key off.</summary>
internal static class TokenAuthentication
{
    /// <summary>
    /// Whether Cadence authenticates this deployment itself, by a token or by a signed-in user.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A store that can issue tokens, not merely a registered one: <c>ConfiguredApiTokenStore</c> is
    /// always registered and resolves nothing, so "a store exists" would register the scheme in every
    /// deployment.
    /// </para>
    /// <para>
    /// A configured provider counts too, and brings the token scheme with it: the built-in policies
    /// name every registered scheme, so the pair appears and disappears together.
    /// </para>
    /// <para>
    /// A configured token beats <c>AllowUnauthenticated</c> and a writable store yields to it:
    /// configuring a token is an explicit statement about authentication, whereas registering a
    /// storage tier is a statement about persistence, and the inferred signal must not override the
    /// explicit instruction.
    /// </para>
    /// </remarks>
    /// <param name="options">The control surface's options.</param>
    /// <param name="store">The registered token store.</param>
    /// <param name="environment">The host environment, for §13.3's Development branch.</param>
    public static bool IsRegistered(
        CadenceApiOptions options,
        IApiTokenStore store,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (options.Tokens.Count > 0 || options.Oidc.IsConfigured)
        {
            return true;
        }

        if (options.AllowUnauthenticated || store is not IWritableApiTokenStore)
        {
            return false;
        }

        // The inferred signal, and the one place it yields: in Development with nothing else
        // configured, §13.3's loopback branch stands. Every SQL and Redis deployment registers a
        // writable store, so honouring it there would turn the branch off for all of them -- and no
        // credential is obtainable over HTTP in that shape, because /tokens needs a user principal
        // and a user needs a provider. A host-named policy is different: it needs the scheme
        // registered to authenticate into, whatever the environment.
        return options.PolicyName is not null || !environment.IsDevelopment();
    }
}
