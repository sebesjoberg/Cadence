using Cadence.Api;
using Cadence.DependencyInjection;

namespace Cadence.Dashboard;

/// <summary>Adds the operator dashboard to a <see cref="CadenceBuilder"/>.</summary>
public static class CadenceDashboardBuilderExtensions
{
    /// <summary>Registers the dashboard's services, and the control surface it is built on.</summary>
    /// <remarks>
    /// The dashboard calls the same endpoints the API package maps, so <c>AddApi</c> is part of
    /// adding it rather than a second call the host has to remember. Both are safe to call twice,
    /// and a host that wants the machine tree as well calls <c>AddApi</c> too and configures once:
    /// there is one options object across the two trees.
    /// </remarks>
    /// <param name="builder">The Cadence builder.</param>
    /// <param name="configure">Configures the shared options object.</param>
    /// <returns>The builder, for chaining.</returns>
    public static CadenceBuilder AddDashboard(
        this CadenceBuilder builder, Action<CadenceApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddApi(configure);
    }
}
