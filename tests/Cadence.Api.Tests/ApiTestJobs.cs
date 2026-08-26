namespace Cadence.Api.Tests;

/// <summary>
/// The one job this assembly registers. <c>AddCadence</c> scans its calling assembly, which is this
/// one, so every <see cref="ApiTestHost"/> sees it — that is deliberate, and the reason it lives in
/// a file of its own rather than nested inside the test class that reads it.
/// </summary>
internal static class ApiTestJobs
{
    /// <summary>The scanned job's stable name.</summary>
    public const string NightlyName = "api-tests-nightly";

    /// <summary>The scanned job's cron expression, as the job reads should report it.</summary>
    public const string NightlyCron = "0 3 * * *";

    [ScheduledJob(Name = NightlyName, Cron = NightlyCron)]
    internal sealed class Nightly : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
