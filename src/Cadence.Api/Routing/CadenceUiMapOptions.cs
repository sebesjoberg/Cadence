using System.ComponentModel;

namespace Cadence.Api.Routing;

/// <summary>
/// How the operator tree should be policied. Supplied by <c>Cadence.Dashboard</c>, which owns the
/// gate that decides these values.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record CadenceUiMapOptions
{
    /// <summary>Applies the cookie policy and the CSRF session-header filter.</summary>
    public required bool CookiePolicy { get; init; }

    /// <summary>Restricts the tree to loopback callers, for the Development branch.</summary>
    public required bool LoopbackOnly { get; init; }

    /// <summary>A host-named policy, which governs alone when set.</summary>
    public string? PolicyName { get; init; }
}
