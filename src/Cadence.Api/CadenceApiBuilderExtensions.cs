using Cadence.Api.Internal;
using Cadence.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cadence.Api;

/// <summary>Adds the HTTP control surface to a <see cref="CadenceBuilder"/>.</summary>
public static class CadenceApiBuilderExtensions
{
    /// <summary>Registers the control surface's services and options.</summary>
    /// <param name="builder">The Cadence builder.</param>
    /// <param name="configure">Adjusts the options.</param>
    /// <returns>The builder, for chaining.</returns>
    public static CadenceBuilder AddApi(this CadenceBuilder builder, Action<CadenceApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<CadenceApiOptions>();

        if (configure is not null)
        {
            options.Configure(configure);
        }

        // Registered after the caller's callback so that tokens set in code are counted as such.
        options.Configure<IConfiguration>((value, configuration) => TokenConfiguration.Bind(configuration, value));

        builder.Services.AddSingleton(services =>
            new TokenSet(services.GetRequiredService<IOptions<CadenceApiOptions>>().Value.Tokens));

        // The parameterless overload only fills in the authentication services with TryAdd and sets
        // no default scheme, so a host with its own authentication is unaffected.
        builder.Services.AddAuthentication();
        builder.Services.AddTransient<CadenceTokenHandler>();

        // The scheme appears only when a token exists. Whether one does is not known until options
        // have bound, so the condition is deferred to when AuthenticationOptions is first resolved.
        builder.Services.AddOptions<AuthenticationOptions>()
            .Configure<IOptions<CadenceApiOptions>>((authentication, api) =>
            {
                if (api.Value.Tokens.Count > 0)
                {
                    authentication.AddScheme<CadenceTokenHandler>(CadenceTokenDefaults.Scheme, displayName: null);
                }
            });

        builder.Services.AddAuthorization();

        // Deferred on the same condition as the scheme: a policy naming a scheme that is not
        // registered throws when it is evaluated, so the two have to appear and disappear together.
        // MapCadenceApi selects options.PolicyName ?? (Tokens.Count > 0 ? Policy : null), so with no
        // token this policy is both absent and unused.
        builder.Services.AddOptions<AuthorizationOptions>()
            .Configure<IOptions<CadenceApiOptions>>((authorization, api) =>
            {
                if (api.Value.Tokens.Count == 0)
                {
                    return;
                }

                authorization.AddPolicy(CadenceTokenDefaults.Policy, policy => policy
                    .AddAuthenticationSchemes(CadenceTokenDefaults.Scheme)
                    .RequireAuthenticatedUser()
                    .RequireClaim(CadenceTokenDefaults.TokenClaim));
            });

        return builder;
    }
}
