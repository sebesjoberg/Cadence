using Cadence.Api.Internal;

namespace Cadence.Api;

/// <summary>
/// Settings for the HTTP control surface. Shared by <c>MapCadenceApi</c> and the dashboard: one
/// options object was kept even after the map calls were split in two.
/// </summary>
public sealed class CadenceApiOptions
{
    /// <summary>
    /// Tokens accepted as <c>Authorization: Bearer</c>. Configuring one satisfies the mapping gate.
    /// Bound from <c>Cadence:Api:Tokens</c> and from <c>CADENCE_API_TOKEN</c> (comma-separated) in
    /// addition to anything set here in code; boot logs how many each source supplied.
    /// </summary>
    public IList<string> Tokens { get; } = [];

    internal TokenSources TokenSources { get; set; } = new(0, 0, 0);

    /// <summary>How people sign in. Configuring an authority and a client id is what turns it on.</summary>
    public CadenceOidcOptions Oidc { get; } = new();

    /// <summary>What the dashboard presents. Read only where <c>MapCadenceDashboard()</c> is called.</summary>
    public CadenceDashboardOptions Dashboard { get; } = new();

    /// <summary>
    /// Maps the endpoints with no authentication of Cadence's own, for a deployment where a proxy
    /// or service mesh has already authenticated the caller. Logged as a warning on every start,
    /// because the alternative to an awkward named flag is somebody doing something worse.
    /// </summary>
    public bool AllowUnauthenticated { get; set; }

    /// <summary>
    /// Mounts the token administration tree under a host-named policy, which then governs it. Has no
    /// effect where no policy is named: Cadence's own user-principal rule governs the tree there.
    /// </summary>
    /// <remarks>
    /// Off by default, and the tree is absent rather than mounted-and-refusing, because whether the
    /// routes exist and who may reach them are independent facts: mounting depends on a writable
    /// store, governing depends on the host's policy. An operator who named a policy for reads,
    /// triggers and pause has not thereby asked for credential administration behind it, where
    /// whatever that policy already admits — a bearer token included — could mint and revoke. Setting
    /// this says the policy is meant to cover that too. <c>MapCadenceApi()</c> warns when a writable
    /// store is registered under a host-named policy and this is unset.
    /// </remarks>
    public bool AllowTokenAdministrationUnderHostPolicy { get; set; }

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
