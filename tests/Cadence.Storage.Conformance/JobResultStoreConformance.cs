using System.Text;
using Xunit;

namespace Cadence.Storage.Conformance;

/// <summary>
/// The behaviour every <see cref="IJobResultStore"/> must have.
/// </summary>
/// <remarks>
/// <para>
/// A result is bytes plus the three things needed to hand them back: a media type, a name, and a
/// length. Everything here is about those surviving a round trip unchanged, describing being
/// cheaper than reading, and a result being deletable without its run.
/// </para>
/// <para>
/// Expiry is asserted as stored metadata, not as elapsed time. Tiers reclaim differently — SQL
/// sweeps, Redis lets a TTL do it — so a test that slept would be testing the reclaim mechanism
/// rather than the contract. <see cref="ExpiresOnItsOwn"/> is where a tier says which it is.
/// </para>
/// </remarks>
public abstract class JobResultStoreConformance
{
    /// <summary>
    /// Creates a store. Every store a single test creates must share one backing store.
    /// </summary>
    protected abstract Task<IJobResultStore> CreateAsync();

    /// <summary>
    /// Whether the tier reclaims expired results itself, making <see cref="IJobResultStore.PurgeAsync"/>
    /// a no-op that returns zero. True for a tier whose keys carry a TTL.
    /// </summary>
    protected virtual bool ExpiresOnItsOwn => false;

    /// <summary>The largest result this tier is asked to carry in these tests.</summary>
    /// <remarks>
    /// Overridden downwards by a tier with a lower ceiling. The point of the test is that a result
    /// too large to be one buffer survives, not that any particular size does.
    /// </remarks>
    protected virtual int LargeResultBytes => 1024 * 1024;

    [SkippableFact]
    public async Task AStoredResultReadsBackByteForByte()
    {
        var store = await CreateAsync();
        var runId = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes("customer,rows\nContoso,3\n");

        await store.SaveAsync(
            runId,
            JobResult.Bytes(content, "text/csv; charset=utf-8", "report.csv"),
            DateTimeOffset.UtcNow.AddHours(1),
            default);

        await using var stored = await store.OpenAsync(runId, default);

        Assert.NotNull(stored);
        Assert.Equal("text/csv; charset=utf-8", stored.Info.ContentType);
        Assert.Equal("report.csv", stored.Info.FileName);
        Assert.Equal(content.Length, stored.Info.Length);

        using var buffer = new MemoryStream();
        await stored.Content.CopyToAsync(buffer, CancellationToken.None);

        Assert.Equal(content, buffer.ToArray());
    }

    [SkippableFact]
    public async Task ALargeResultSurvivesTheRoundTrip()
    {
        var store = await CreateAsync();
        var runId = Guid.NewGuid();

        // Deterministic rather than random, so a failure names which byte moved.
        var content = new byte[LargeResultBytes];
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 251);
        }

        await store.SaveAsync(
            runId,
            JobResult.Bytes(content, "application/octet-stream"),
            DateTimeOffset.UtcNow.AddHours(1),
            default);

        await using var stored = await store.OpenAsync(runId, default);

        Assert.NotNull(stored);
        Assert.Equal(content.Length, stored.Info.Length);

        using var buffer = new MemoryStream();
        await stored.Content.CopyToAsync(buffer, CancellationToken.None);

        Assert.Equal(content, buffer.ToArray());
    }

    [SkippableFact]
    public async Task DescribingReturnsTheMetadataWithoutTheBytes()
    {
        var store = await CreateAsync();
        var runId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(3);

        await store.SaveAsync(
            runId,
            JobResult.Bytes(new byte[64], "application/json; charset=utf-8"),
            expiresAt,
            default);

        var info = await store.DescribeAsync(runId, default);

        Assert.NotNull(info);
        Assert.Equal(runId, info.RunId);
        Assert.Equal("application/json; charset=utf-8", info.ContentType);
        Assert.Null(info.FileName);
        Assert.Equal(64, info.Length);

        // Whole milliseconds: DATETIME2(3) is the coarsest representation any tier stores.
        Assert.Equal(
            expiresAt.UtcDateTime.Ticks / TimeSpan.TicksPerMillisecond,
            info.ExpiresAt.UtcDateTime.Ticks / TimeSpan.TicksPerMillisecond);
    }

    [SkippableFact]
    public async Task AMissingResultIsNullRatherThanAnError()
    {
        var store = await CreateAsync();
        var absent = Guid.NewGuid();

        Assert.Null(await store.DescribeAsync(absent, default));
        Assert.Null(await store.OpenAsync(absent, default));
    }

    [SkippableFact]
    public async Task SavingTwiceReplacesRatherThanAppends()
    {
        var store = await CreateAsync();
        var runId = Guid.NewGuid();

        await store.SaveAsync(
            runId,
            JobResult.Text("the first attempt, which was longer"),
            DateTimeOffset.UtcNow.AddHours(1),
            default);

        await store.SaveAsync(
            runId,
            JobResult.Csv("short\n", "second.csv"),
            DateTimeOffset.UtcNow.AddHours(1),
            default);

        await using var stored = await store.OpenAsync(runId, default);

        Assert.NotNull(stored);
        Assert.Equal("second.csv", stored.Info.FileName);
        Assert.Equal(6, stored.Info.Length);

        using var reader = new StreamReader(stored.Content, Encoding.UTF8);
        Assert.Equal("short\n", await reader.ReadToEndAsync());
    }

    [SkippableFact]
    public async Task DeletingRemovesTheResultAndIsIdempotent()
    {
        var store = await CreateAsync();
        var runId = Guid.NewGuid();

        await store.SaveAsync(
            runId,
            JobResult.Text("gone shortly"),
            DateTimeOffset.UtcNow.AddHours(1),
            default);

        await store.DeleteAsync(runId, default);
        Assert.Null(await store.DescribeAsync(runId, default));

        await store.DeleteAsync(runId, default);
    }

    [SkippableFact]
    public async Task PurgingTakesExpiredResultsAndLeavesLiveOnes()
    {
        var store = await CreateAsync();
        var expired = Guid.NewGuid();
        var live = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await store.SaveAsync(expired, JobResult.Text("stale"), now.AddHours(-1), default);
        await store.SaveAsync(live, JobResult.Text("current"), now.AddHours(1), default);

        var purged = await store.PurgeAsync(now, 100, default);

        if (ExpiresOnItsOwn)
        {
            // A tier whose keys expire themselves has nothing for a sweep to find, and says so by
            // returning zero rather than by pretending it deleted something.
            Assert.Equal(0, purged);
        }
        else
        {
            Assert.Equal(1, purged);
            Assert.Null(await store.DescribeAsync(expired, default));
        }

        Assert.NotNull(await store.DescribeAsync(live, default));
    }

    [SkippableFact]
    public async Task PurgingHonoursTheBatchSize()
    {
        Skip.If(ExpiresOnItsOwn, "This tier reclaims on its own TTL and never sweeps in batches.");

        var store = await CreateAsync();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 3; i++)
        {
            await store.SaveAsync(Guid.NewGuid(), JobResult.Text("stale"), now.AddHours(-1), default);
        }

        Assert.Equal(2, await store.PurgeAsync(now, 2, default));
    }
}
