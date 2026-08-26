namespace Cadence.Api.Tests;

/// <summary>
/// The jobs this assembly registers. <c>AddCadence</c> scans its calling assembly, which is this
/// one, so every <see cref="ApiTestHost"/> sees both — that is deliberate, and the reason they live
/// in a file of their own rather than nested inside the test class that reads them.
/// </summary>
internal static class ApiTestJobs
{
    /// <summary>The UTC job's stable name.</summary>
    public const string NightlyName = "api-tests-nightly";

    /// <summary>The UTC job's cron expression, as the job reads should report it.</summary>
    public const string NightlyCron = "0 3 * * *";

    /// <summary>The zoned job's stable name.</summary>
    public const string ZonedName = "api-tests-zoned";

    /// <summary>The zone the zoned job's cron is evaluated in, which is not UTC on purpose.</summary>
    public const string ZonedTimeZone = "Europe/Stockholm";

    [ScheduledJob(Name = NightlyName, Cron = NightlyCron)]
    internal sealed class Nightly : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    // Exists so the next-occurrence field is computed in a zone with a non-zero offset. Without it
    // the UTC normalization in the job reads would be untestable: every occurrence would already
    // be UTC and a missing conversion would look identical to a correct one.
    [ScheduledJob(Name = ZonedName, Cron = NightlyCron, TimeZone = ZonedTimeZone)]
    internal sealed class Zoned : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
