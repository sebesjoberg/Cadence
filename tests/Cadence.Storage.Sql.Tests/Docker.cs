namespace Cadence.Storage.Sql.Tests;

/// <summary>
/// Works out whether a Docker daemon is reachable, once per process.
/// </summary>
/// <remarks>
/// Probed by looking for the endpoint rather than by trying to start a container: the answer is
/// needed before the fixture does any work, and a failed container start takes tens of seconds to
/// time out. A false positive here just means the fixture reports the real error instead.
/// </remarks>
internal static class Docker
{
    /// <summary>Why Docker is unusable, or null when it looks available.</summary>
    public static string? SkipReason { get; } = Detect();

    private static string? Detect()
    {
        const string Missing =
            "No Docker daemon was found, so the SQL Server tests cannot run. Start Docker Desktop " +
            "(or set DOCKER_HOST) and run the tests again; the rest of the suite does not need it.";

        // An explicit endpoint is taken at face value: whoever set it knows better than this probe.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            // Named pipes are enumerable as files. Docker Desktop publishes docker_engine whichever
            // backend it is using, so this covers the Linux and Windows container modes both.
            try
            {
                var pipes = Directory.GetFiles(@"\\.\pipe\");

                return pipes.Any(p => p.EndsWith("docker_engine", StringComparison.OrdinalIgnoreCase))
                    ? null
                    : Missing;
            }
            catch (IOException)
            {
                return Missing;
            }
            catch (UnauthorizedAccessException)
            {
                return Missing;
            }
        }

        return File.Exists("/var/run/docker.sock") ? null : Missing;
    }
}
