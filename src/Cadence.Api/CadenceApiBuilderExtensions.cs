using Cadence.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

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

        return builder;
    }
}
