using Microsoft.Extensions.Logging;

namespace Cadence.Dashboard.Internal;

/// <summary>
/// Every log message the dashboard writes, as source-generated
/// <see cref="LoggerMessageAttribute"/> methods, so wording and event ids stay stable for anyone
/// alerting on them. Event ids start at 3200; 3000-3199 belong to <c>Cadence.Api</c>.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 3200,
        Level = LogLevel.Error,
        Message = "MapCadenceDashboard() refused to map at {BasePath}: nothing configured could " +
                  "sign a person in. A configured bearer token does not count here, however many " +
                  "there are -- no browser presents one, so a token-only deployment would ship a " +
                  "dashboard nobody could sign into.")]
    public static partial void GateRefused(this ILogger logger, string basePath);

    [LoggerMessage(
        EventId = 3201,
        Level = LogLevel.Warning,
        Message = "Cadence's dashboard is mapped with AllowUnauthenticated set, so it performs no " +
                  "authentication of its own. Anything that can reach {BasePath} can read the " +
                  "schedule, halt scheduling and administer tokens. This is only safe when " +
                  "something in front of this application authenticates operators.")]
    public static partial void MappedWithAuthenticationDisabled(this ILogger logger, string basePath);

    [LoggerMessage(
        EventId = 3202,
        Level = LogLevel.Warning,
        Message = "Cadence's dashboard is mapped with nothing that would authenticate it. Anything " +
                  "on this host that can reach {BasePath} can halt scheduling. This is allowed in " +
                  "Development only, where non-loopback callers are refused; outside it, " +
                  "MapCadenceDashboard() will refuse to map. Configure CadenceApiOptions.Oidc, or " +
                  "name an authorization policy, before deploying.")]
    public static partial void MappedUnauthenticatedInDevelopment(this ILogger logger, string basePath);
}
