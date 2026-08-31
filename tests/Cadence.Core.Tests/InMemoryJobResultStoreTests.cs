using System.Text;
using Cadence.Storage;
using Xunit;

namespace Cadence.Core.Tests;

/// <summary>
/// What the in-memory result store does beyond the shared contract: bound the heap it takes.
/// </summary>
public class InMemoryJobResultStoreTests
{
    [Fact]
    public async Task ResultsAreEvictedOldestFirstOnceTheCeilingIsPassed()
    {
        var store = new InMemoryJobResultStore(new InMemoryJobResultOptions { MaxTotalBytes = 300 });
        var expiry = DateTimeOffset.UtcNow.AddHours(1);

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        foreach (var runId in new[] { first, second, third })
        {
            await store.SaveAsync(
                runId, JobResult.Bytes(new byte[200], "application/octet-stream"), expiry, default);
        }

        // Three 200-byte results against a 300-byte ceiling: only the newest survives, and it
        // survives despite an expiry that has not passed. A size ceiling outranks retention here,
        // because the alternative is the scheduler taking the process down.
        Assert.Null(await store.DescribeAsync(first, default));
        Assert.Null(await store.DescribeAsync(second, default));
        Assert.NotNull(await store.DescribeAsync(third, default));
    }

    [Fact]
    public async Task ARewriteReplacesRatherThanAccumulatesAgainstTheCeiling()
    {
        var store = new InMemoryJobResultStore(new InMemoryJobResultOptions { MaxTotalBytes = 300 });
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var kept = Guid.NewGuid();
        var rewritten = Guid.NewGuid();

        await store.SaveAsync(kept, JobResult.Bytes(new byte[100], "text/plain"), expiry, default);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await store.SaveAsync(
                rewritten, JobResult.Bytes(new byte[100], "text/plain"), expiry, default);
        }

        // Five writes of the same run are 100 bytes held, not 500 — otherwise a job that retries
        // would evict every other result in the store on its way to succeeding.
        Assert.NotNull(await store.DescribeAsync(kept, default));
        Assert.NotNull(await store.DescribeAsync(rewritten, default));
    }

    [Fact]
    public async Task TheCallersBufferIsCopiedRatherThanRetained()
    {
        var store = new InMemoryJobResultStore();
        var runId = Guid.NewGuid();
        var buffer = Encoding.UTF8.GetBytes("original");

        await store.SaveAsync(
            runId, JobResult.Bytes(buffer, "text/plain"), DateTimeOffset.UtcNow.AddHours(1), default);

        // A serializer is free to reuse its buffer once Save has returned.
        Encoding.UTF8.GetBytes("OVERWRIT").CopyTo(buffer, 0);

        await using var stored = await store.OpenAsync(runId, default);
        using var reader = new StreamReader(stored!.Content, Encoding.UTF8);

        Assert.Equal("original", await reader.ReadToEndAsync());
    }
}
