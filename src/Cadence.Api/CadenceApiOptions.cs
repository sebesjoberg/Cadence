using Cadence.Api.Internal;

namespace Cadence.Api;

/// <summary>
/// Settings for the HTTP control surface. Shared by <c>MapCadenceApi</c> and, from v0.4, the
/// dashboard: §7 answer #6 kept one options object even after the map calls were split in two.
/// </summary>
public sealed class CadenceApiOptions
{
    /// <summary>The prefix both trees mount under.</summary>
    public string BasePath { get; set; } = "/cadence";

    /// <summary>
    /// Tokens accepted as <c>Authorization: Bearer</c>. Configuring one satisfies the mapping gate.
    /// Bound from <c>Cadence:Api:Tokens</c> and from <c>CADENCE_API_TOKEN</c> (comma-separated) in
    /// addition to anything set here in code; boot logs how many each source supplied.
    /// </summary>
    public IList<string> Tokens { get; } = [];

    internal TokenSources TokenSources { get; set; } = new(0, 0, 0);

    /// <summary>
    /// Maps the endpoints with no authentication of Cadence's own, for a deployment where a proxy
    /// or service mesh has already authenticated the caller. Logged as a warning on every start,
    /// because the alternative to an awkward named flag is somebody doing something worse.
    /// </summary>
    public bool AllowUnauthenticated { get; set; }

    /// <summary>The authorization policy the endpoints require, when the host names one.</summary>
    public string? PolicyName { get; private set; }

    /// <summary>
    /// Requires an authorization policy of the host's own. A named policy governs alone: the token
    /// scheme authenticates into it rather than bypassing it, so a host with its own identity
    /// provider can accept both by writing one policy. That policy must list
    /// <see cref="CadenceApiDefaults.AuthenticationScheme"/> among its authentication schemes:
    /// Cadence makes it no host's default scheme, so a policy naming none authenticates nobody.
    /// </summary>
    /// <param name="policyName">The policy name, as registered with ASP.NET Core authorization.</param>
    public void RequireAuthorization(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        PolicyName = policyName;
    }
}
