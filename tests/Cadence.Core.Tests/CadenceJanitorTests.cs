using Cadence.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cadence.Core.Tests;

/// <summary>
/// The janitor's pass sequencing and failure containment, against a fake maintenance tier.
/// </summary>
/// <remarks>
/// Each tier's own operations are covered where they run for real -- SQL against a live database in
/// <c>Cadence.Storage.Sql.Tests</c>, Redis in <c>Cadence.Storage.Redis.Tests</c>. What belongs here,
/// against a fake, is the policy that is tier-neutral: the order the five passes run in, and that
/// none of them is contained locally -- a throw propagates out of <see cref="CadenceJanitor"/>'s
/// <c>RunPassAsync</c> unchanged, because containing a failed pass is the hosted service's job.
/// </remarks>
public sealed class CadenceJanitorTests
{
    [Fact]
    public async Task APassPurgesExpiredTokensAfterDeadInstances()
    {
        var maintenance = new RecordingMaintenance();
        var janitor = CreateJanitor(maintenance);

        await janitor.RunPassAsync(default);

        Assert.Equal(
            ["reap", "purge-runs", "trim", "purge-instances", "purge-tokens"],
            maintenance.Calls);
    }

    [Fact]
    public async Task ATokenPurgeThatThrowsPropagatesLikeAnyOtherPass()
    {
        var maintenance = new RecordingMaintenance { ThrowOnTokenPurge = true };
        var janitor = CreateJanitor(maintenance);

        // No per-pass try/catch inside RunPassAsync -- containing a failed pass so it does not
        // escalate into a scheduling problem is ExecuteAsync's job, the same as for the other four.
        await Assert.ThrowsAsync<InvalidOperationException>(() => janitor.RunPassAsync(default));

        Assert.Contains("purge-tokens", maintenance.Calls);
    }

    private static CadenceJanitor CreateJanitor(IStorageMaintenance maintenance) =>
        new(
            maintenance,
            new JanitorOptions(),
            new FakeClock(DateTimeOffset.UtcNow),
            Options.Create(new CadenceOptions()),
            NullLogger<CadenceJanitor>.Instance);

    private sealed class RecordingMaintenance : IStorageMaintenance
    {
        public List<string> Calls { get; } = [];

        public bool ThrowOnTokenPurge { get; set; }

        public Task<int> ReapAbandonedRunsAsync(
            DateTimeOffset heartbeatDeadline, DateTimeOffset now, int batchSize, CancellationToken ct)
        {
            Calls.Add("reap");
            return Task.FromResult(0);
        }

        public Task<int> PurgeRunsByAgeAsync(DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        {
            Calls.Add("purge-runs");
            return Task.FromResult(0);
        }

        public Task<int> TrimRunsPerJobAsync(int maxRunsPerJob, int batchSize, CancellationToken ct)
        {
            Calls.Add("trim");
            return Task.FromResult(0);
        }

        public Task<int> PurgeDeadInstancesAsync(DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        {
            Calls.Add("purge-instances");
            return Task.FromResult(0);
        }

        public Task<int> PurgeExpiredApiTokensAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
        {
            Calls.Add("purge-tokens");

            if (ThrowOnTokenPurge)
            {
                throw new InvalidOperationException("Token purge failed.");
            }

            return Task.FromResult(0);
        }
    }
}
