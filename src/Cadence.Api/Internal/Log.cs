using Microsoft.Extensions.Logging;

namespace Cadence.Api.Internal;

/// <summary>
/// Every log message the control surface writes, as source-generated
/// <see cref="LoggerMessageAttribute"/> methods, so wording and event ids stay stable for anyone
/// alerting on them.
/// </summary>
internal static partial class Log
{
    // 3000-3099: mapping and the gate.

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Warning,
        Message = "Cadence's API is mapped with nothing that would authenticate it. This is allowed " +
                  "in Development only; outside it, MapCadenceApi() will refuse to map. Supply a " +
                  "token, or name an authorization policy, before deploying.")]
    public static partial void MappedUnauthenticatedInDevelopment(this ILogger logger);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Cadence's API is mapped with AllowUnauthenticated set, so it performs no " +
                  "authentication of its own. Anything that can reach {BasePath} can trigger jobs. " +
                  "This is only safe when something in front of this application authenticates " +
                  "callers.")]
    public static partial void MappedWithAuthenticationDisabled(this ILogger logger, string basePath);
}
