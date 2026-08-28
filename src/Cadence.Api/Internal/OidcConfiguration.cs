using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Cadence.Api.Internal;

/// <summary>
/// Binds <c>Cadence:Api:Oidc:*</c> onto the options, then lets the three environment variables
/// override what the section said.
/// </summary>
/// <remarks>
/// Bound property by property rather than through the reflection binder, so that
/// <see cref="CadenceOidcOptions.Scopes"/> is replaced by a configured list instead of appended to,
/// and so a blank setting leaves a value set in code alone.
/// </remarks>
internal static class OidcConfiguration
{
    public const string SectionPath = "Cadence:Api:Oidc";
    public const string AuthorityKey = "CADENCE_OIDC_AUTHORITY";
    public const string ClientIdKey = "CADENCE_OIDC_CLIENT_ID";
    public const string ClientSecretKey = "CADENCE_OIDC_CLIENT_SECRET";

    /// <summary>Reads the sign-in settings into the options.</summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="options">The options to bind onto; values set in code survive a blank setting.</param>
    public static void Bind(IConfiguration configuration, CadenceApiOptions options)
    {
        var section = configuration.GetSection(SectionPath);
        var oidc = options.Oidc;

        oidc.Authority = First(configuration[AuthorityKey], section["Authority"], oidc.Authority);
        oidc.ClientId = First(configuration[ClientIdKey], section["ClientId"], oidc.ClientId);
        oidc.ClientSecret = First(configuration[ClientSecretKey], section["ClientSecret"], oidc.ClientSecret);
        oidc.RequiredClaimType = First(section["RequiredClaimType"], oidc.RequiredClaimType);
        oidc.RequiredClaimValue = First(section["RequiredClaimValue"], oidc.RequiredClaimValue);

        if (TimeSpan.TryParse(section["CookieLifetime"], CultureInfo.InvariantCulture, out var lifetime))
        {
            oidc.CookieLifetime = lifetime;
        }

        if (TimeSpan.TryParse(section["TokenCreationMaxAge"], CultureInfo.InvariantCulture, out var maxAge))
        {
            oidc.TokenCreationMaxAge = maxAge;
        }

        if (bool.TryParse(section["RequireHttpsMetadata"], out var requireHttps))
        {
            oidc.RequireHttpsMetadata = requireHttps;
        }

        if (bool.TryParse(section["ManageDataProtectionKeys"], out var manageKeys))
        {
            oidc.ManageDataProtectionKeys = manageKeys;
        }

        var scopes = section.GetSection("Scopes").Get<string[]>() ?? [];

        if (scopes.Length > 0)
        {
            oidc.Scopes.Clear();

            foreach (var scope in scopes.Where(scope => !string.IsNullOrWhiteSpace(scope)))
            {
                oidc.Scopes.Add(scope.Trim());
            }
        }
    }

    private static string? First(params string?[] candidates)
        => candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))?.Trim();
}
