using Microsoft.Extensions.Configuration;

namespace Cadence.Api.Internal;

/// <summary>How many tokens each source supplied. Diagnostics only; values are never carried here.</summary>
internal sealed record TokenSources(int FromCode, int FromConfiguration, int FromEnvironment)
{
    public int Total => FromCode + FromConfiguration + FromEnvironment;
}

/// <summary>Collects tokens from every source, and reports where they came from.</summary>
/// <remarks>
/// Two sources is two places to look when a token does not work, so the count from each is logged
/// at boot. <c>CADENCE_API_TOKEN</c> exists because <c>Cadence__Api__Tokens__0=</c> is miserable in
/// a compose file.
/// </remarks>
internal static class TokenConfiguration
{
    public const string TokensPath = "Cadence:Api:Tokens";
    public const string EnvironmentKey = "CADENCE_API_TOKEN";

    /// <summary>Adds configured tokens to the options, and records how many each source supplied.</summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="options">The options to add to; tokens set in code are preserved.</param>
    public static void Bind(IConfiguration configuration, CadenceApiOptions options)
    {
        var fromCode = options.Tokens.Count;

        var fromConfiguration = 0;

        foreach (var value in configuration.GetSection(TokensPath).Get<string[]>() ?? [])
        {
            if (Add(options, value))
            {
                fromConfiguration++;
            }
        }

        var fromEnvironment = 0;

        foreach (var value in (configuration[EnvironmentKey] ?? string.Empty).Split(','))
        {
            if (Add(options, value))
            {
                fromEnvironment++;
            }
        }

        options.TokenSources = new TokenSources(fromCode, fromConfiguration, fromEnvironment);
    }

    private static bool Add(CadenceApiOptions options, string? value)
    {
        var token = value?.Trim();

        if (string.IsNullOrEmpty(token) || options.Tokens.Contains(token))
        {
            return false;
        }

        options.Tokens.Add(token);
        return true;
    }
}
