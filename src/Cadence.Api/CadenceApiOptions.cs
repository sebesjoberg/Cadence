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
    /// Tokens accepted as <c>Authorization: Bearer</c>. Also populated from
    /// <c>CADENCE_API_TOKEN</c> and <c>Cadence:Api:Tokens</c>.
    /// </summary>
    public IList<string> Tokens { get; } = [];

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
    /// provider can accept both by writing one policy.
    /// </summary>
    /// <param name="policyName">The policy name, as registered with ASP.NET Core authorization.</param>
    public void RequireAuthorization(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        PolicyName = policyName;
    }
}
