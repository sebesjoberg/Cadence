using Cadence.Api.Internal;
using Cadence.DependencyInjection;
using Cadence.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
        options.Configure<IConfiguration>((value, configuration) =>
        {
            TokenConfiguration.Bind(configuration, value);
            OidcConfiguration.Bind(configuration, value);
        });

        builder.Services.AddSingleton(services =>
            new TokenSet(services.GetRequiredService<IOptions<CadenceApiOptions>>().Value.Tokens));

        // The parameterless overload only fills in the authentication services with TryAdd and sets
        // no default scheme, so a host with its own authentication is unaffected.
        builder.Services.AddAuthentication();
        builder.Services.AddTransient<CadenceTokenHandler>();

        // The scheme appears only when a token could be presented. Neither half of that is known
        // until options have bound and the container is complete -- AddApi and UseSqlStorage are both
        // called inside the AddCadence callback, in whichever order the host chose -- so the
        // condition is deferred to when AuthenticationOptions is first resolved.
        //
        // The SchemeMap check makes a second AddApi call harmless: each call appends another of these
        // callbacks, they all run against one app-wide AuthenticationOptions, and AddScheme throws on
        // a duplicate name. Unguarded, a second call fails host startup and takes the host's own
        // authentication down with it -- and AddApi was safe to call twice before this scheme existed.
        builder.Services.AddOptions<AuthenticationOptions>()
            .Configure<IOptions<CadenceApiOptions>, IApiTokenStore, IHostEnvironment>(
                (authentication, api, store, environment) =>
            {
                if (TokenAuthentication.IsRegistered(api.Value, store, environment)
                    && !authentication.SchemeMap.ContainsKey(CadenceTokenDefaults.Scheme))
                {
                    authentication.AddScheme<CadenceTokenHandler>(CadenceTokenDefaults.Scheme, displayName: null);
                }

                // The two sign-in schemes key off the narrower half of that condition: without an
                // authority and a client id there is no handshake to perform.
                if (!api.Value.Oidc.IsConfigured)
                {
                    return;
                }

                if (!authentication.SchemeMap.ContainsKey(CadenceApiDefaults.CookieScheme))
                {
                    authentication.AddScheme<CookieAuthenticationHandler>(
                        CadenceApiDefaults.CookieScheme, displayName: null);
                }

                if (!authentication.SchemeMap.ContainsKey(CadenceApiDefaults.OidcScheme))
                {
                    authentication.AddScheme<OpenIdConnectHandler>(
                        CadenceApiDefaults.OidcScheme, displayName: null);
                }
            });

        ConfigureSignIn(builder.Services);

        builder.Services.AddAuthorization();

        // Deferred on the same condition as the scheme: a policy naming a scheme that is not
        // registered throws when it is evaluated, so the two have to appear and disappear together.
        // MapCadenceApi applies these on that same condition, so where they are absent they are also
        // unused. No duplicate guard here: AddPolicy overwrites by name, so a second AddApi call
        // re-registers the same policies rather than throwing.
        builder.Services.AddOptions<AuthorizationOptions>()
            .Configure<IOptions<CadenceApiOptions>, IApiTokenStore, IHostEnvironment>(
                (authorization, api, store, environment) =>
            {
                if (!TokenAuthentication.IsRegistered(api.Value, store, environment))
                {
                    return;
                }

                // Every scheme that is registered and no more: naming an absent one throws where the
                // policy is evaluated, and omitting a present one authenticates nobody through it.
                string[] schemes = api.Value.Oidc.IsConfigured
                    ? [CadenceTokenDefaults.Scheme, CadenceApiDefaults.CookieScheme]
                    : [CadenceTokenDefaults.Scheme];

                authorization.AddPolicy(CadenceTokenDefaults.ReadPolicy, policy => policy
                    .AddAuthenticationSchemes(schemes)
                    .RequireAuthenticatedUser()
                    .RequireClaim(CadenceTokenDefaults.KindClaim));

                // A signed-in user carries Operate, so this pair covers both kinds of principal.
                authorization.AddPolicy(CadenceTokenDefaults.OperatePolicy, policy => policy
                    .AddAuthenticationSchemes(schemes)
                    .RequireAuthenticatedUser()
                    .RequireClaim(CadenceTokenDefaults.ScopeClaim, nameof(ApiTokenScope.Operate)));
            });

        return builder;
    }

    /// <summary>
    /// Configures the ticket cookie and the handshake. Registered unconditionally: none of it runs
    /// until a handler resolves its options, which happens only where the schemes were added.
    /// </summary>
    private static void ConfigureSignIn(IServiceCollection services)
    {
        // What AddCookie() and AddOpenIdConnect() would have brought along. Their AddScheme calls are
        // the part that cannot be used here, because scheme registration has to stay conditional.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<CookieAuthenticationOptions>, PostConfigureCookieAuthenticationOptions>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectPostConfigureOptions>());

        // TryAdd so that a second AddApi call resolves the same key ring rather than a second one.
        services.TryAddSingleton<TicketKeyRing>();

        services.AddOptions<CookieAuthenticationOptions>(CadenceApiDefaults.CookieScheme)
            .Configure<IOptions<CadenceApiOptions>, TicketKeyRing>((cookie, api, keyRing) =>
            {
                var options = api.Value;

                // This scheme's own provider, over the key ring the storage tier registered, so that
                // a ticket minted on one replica is readable on the next. Null where Cadence manages
                // no key ring, and the framework's post-configure then fills in the host's.
                if (keyRing.Provider is { } provider)
                {
                    cookie.DataProtectionProvider = provider;
                }

                cookie.Cookie.Name = "cadence.session";
                cookie.Cookie.HttpOnly = true;

                cookie.Cookie.Path = CadenceApiDefaults.BasePath;

                // Secure unconditionally: it describes the browser's leg, so it stays correct behind
                // a TLS-terminating proxy, and browsers treat localhost as trustworthy.
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;

                // Lax, not Strict: returning from the provider is a cross-site top-level navigation,
                // and Strict withholds the cookie on exactly that request.
                cookie.Cookie.SameSite = SameSiteMode.Lax;

                cookie.SlidingExpiration = false;
                cookie.ExpireTimeSpan = options.Oidc.CookieLifetime;

                // This is an API: the handler's own 302 to a login page would answer the dashboard's
                // fetch with HTML, and the dashboard follows /auth/login itself.
                cookie.Events.OnRedirectToLogin = Refuse(StatusCodes.Status401Unauthorized);
                cookie.Events.OnRedirectToAccessDenied = Refuse(StatusCodes.Status403Forbidden);
            });

        services.AddOptions<OpenIdConnectOptions>(CadenceApiDefaults.OidcScheme)
            .Configure<IOptions<CadenceApiOptions>>((oidc, api) =>
            {
                var options = api.Value;

                var prefix = CadenceApiDefaults.BasePath;

                oidc.Authority = options.Oidc.Authority;
                oidc.ClientId = options.Oidc.ClientId;
                oidc.ClientSecret = options.Oidc.ClientSecret;
                oidc.RequireHttpsMetadata = options.Oidc.RequireHttpsMetadata;
                oidc.SignInScheme = CadenceApiDefaults.CookieScheme;

                oidc.ResponseType = "code";
                oidc.UsePkce = true;

                // No downstream API is called, and provider tokens held in the ticket are what make
                // these cookies overflow into chunks.
                oidc.SaveTokens = false;

                // Under the base path, so the handshake cannot collide with a host's own OIDC
                // registration on the framework's default paths.
                oidc.CallbackPath = $"{prefix}/signin-oidc";
                oidc.SignedOutCallbackPath = $"{prefix}/signout-callback-oidc";
                oidc.RemoteSignOutPath = $"{prefix}/signout-oidc";
                oidc.SignedOutRedirectUri = CadenceApiDefaults.BasePath;

                oidc.Scope.Clear();

                // openid first, whatever was configured: the request is not an OIDC one without it.
                if (!options.Oidc.Scopes.Contains("openid", StringComparer.Ordinal))
                {
                    oidc.Scope.Add("openid");
                }

                foreach (var scope in options.Oidc.Scopes)
                {
                    oidc.Scope.Add(scope);
                }

                // Unmapped, so the allow-list reads the provider's own claim names and writes the
                // configured required claim as the provider sent it.
                oidc.MapInboundClaims = false;

                oidc.Events.OnTokenValidated = context => TicketIdentity.BuildAsync(context, options.Oidc);
                oidc.Events.OnRemoteFailure = TicketIdentity.RefuseAsync;
                oidc.Events.OnRedirectToIdentityProvider = AuthEndpoints.RequestFreshSignInAsync;
                oidc.Events.OnRedirectToIdentityProviderForSignOut = AuthEndpoints.IdentifyClientAsync;
                oidc.Events.OnRemoteSignOut = AuthEndpoints.RefuseForgedSignOutAsync;
            });
    }

    private static Func<RedirectContext<CookieAuthenticationOptions>, Task> Refuse(int statusCode)
        => context =>
        {
            context.Response.StatusCode = statusCode;

            return Task.CompletedTask;
        };
}
