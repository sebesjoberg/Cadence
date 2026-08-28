using Xunit;

namespace Cadence.Storage.Conformance;

/// <summary>What every tier's instance directory must do. SQL, Redis and in-memory all take this.</summary>
public abstract class InstanceDirectoryConformance
{
    /// <summary>Builds a directory plus a way to write heartbeats into whatever backs it.</summary>
    protected abstract Task<(IInstanceDirectory Directory, Func<InstanceInfo, CancellationToken, Task> Beat)>
        CreateAsync(CancellationToken cancellationToken);

    [SkippableFact]
    public async Task ReturnsEveryRegisteredInstance()
    {
        var (directory, beat) = await CreateAsync(default);

        await beat(Instance("a", DateTimeOffset.UtcNow), default);
        await beat(Instance("b", DateTimeOffset.UtcNow), default);

        var all = await directory.GetAllAsync(default);

        Assert.Equal(["a", "b"], all.Select(i => i.InstanceId).OrderBy(id => id));
    }

    [SkippableFact]
    public async Task ReturnsStaleInstancesRatherThanFilteringThem()
    {
        var (directory, beat) = await CreateAsync(default);

        var stale = Instance("cold", DateTimeOffset.UtcNow.AddHours(-4));
        await beat(stale, default);

        var all = await directory.GetAllAsync(default);

        var found = Assert.Single(all, i => i.InstanceId == "cold");
        Assert.True(found.LastHeartbeatUtc < DateTimeOffset.UtcNow.AddHours(-1));
    }

    [SkippableFact]
    public async Task NormalisesEveryInstantToUtc()
    {
        var (directory, beat) = await CreateAsync(default);

        await beat(Instance("z", DateTimeOffset.UtcNow), default);

        var only = Assert.Single(await directory.GetAllAsync(default));

        Assert.Equal(TimeSpan.Zero, only.StartedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, only.LastHeartbeatUtc.Offset);
    }

    private static InstanceInfo Instance(string id, DateTimeOffset heartbeat) => new()
    {
        InstanceId = id,
        MachineName = "host-" + id,
        ProcessId = 1234,
        AssemblyVersion = "0.4.0",
        StartedAtUtc = heartbeat.AddMinutes(-30),
        LastHeartbeatUtc = heartbeat,
    };
}
