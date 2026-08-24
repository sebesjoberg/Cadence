using System.Diagnostics;

namespace Cadence.Diagnostics;

/// <summary>
/// The names Cadence emits telemetry under. Register these with OpenTelemetry rather than looking
/// for a Cadence-specific metrics API — there isn't one, by design.
/// </summary>
public static class CadenceDiagnostics
{
    /// <summary>The <see cref="System.Diagnostics.ActivitySource"/> and meter name.</summary>
    public const string SourceName = "Cadence";

    /// <summary>Activity name for a single job run.</summary>
    public const string RunActivityName = "cadence.job";

    /// <summary>Activity event name for progress a job reports through <see cref="JobContext.Report"/>.</summary>
    public const string ProgressEventName = "cadence.job.progress";

    /// <summary>One activity per run, so a job's own HTTP and database calls nest under it.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, ThisAssemblyVersion);

    private static string ThisAssemblyVersion =>
        typeof(CadenceDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
