using System.Net;
using System.Net.Http.Json;
using Cadence.Api.Routing;
using Cadence.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// The cluster's instances, as the operator tree reports them. Stale rows are the point of the
/// read, so the endpoint's job is to hand them all over and say how stale is stale.
/// </summary>
public sealed class InstanceEndpointTests
{
    private const string InstancesPath = CadenceApiDefaults.UiPath + "/instances";

    /// <summary>An offset that is not UTC, so a missing normalization cannot pass unnoticed.</summary>
    private static readonly TimeSpan Zoned = TimeSpan.FromHours(2);

    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, Zoned);

    private static readonly CadenceUiMapOptions Open =
        new() { CookiePolicy = false, LoopbackOnly = false };

    [Fact]
    public async Task TheDeadInstanceIsListedWithTheLiveOne()
    {
        await using var host = await StartAsync(new FakeInstanceDirectory(Live, Dead));

        var body = await ReadAsync(host);

        Assert.Equal(["live-1", "dead-1"], body.Instances.Select(instance => instance.InstanceId).ToArray());
    }

    [Fact]
    public async Task EachInstanceCarriesWhatIdentifiesTheProcess()
    {
        await using var host = await StartAsync(new FakeInstanceDirectory(Live));

        var body = await ReadAsync(host);

        var instance = Assert.Single(body.Instances);
        Assert.Equal("box-a", instance.MachineName);
        Assert.Equal(4711, instance.ProcessId);
        Assert.Equal("0.4.0+abc", instance.AssemblyVersion);
    }

    [Fact]
    public async Task TheInstantsAreUtc()
    {
        await using var host = await StartAsync(new FakeInstanceDirectory(Dead));

        var body = await ReadAsync(host);
        var instance = Assert.Single(body.Instances);

        Assert.Equal(TimeSpan.Zero, instance.StartedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, instance.LastHeartbeatUtc.Offset);
        Assert.Equal(Dead.LastHeartbeatUtc, instance.LastHeartbeatUtc);
    }

    // The number the janitor reaps by, not one of this endpoint's own: a UI marking staleness at a
    // different threshold than the reaper uses would contradict the run history beside it.
    [Fact]
    public async Task TheHeartbeatTimeoutIsTheJanitorsOwn()
    {
        var janitor = new JanitorOptions { HeartbeatTimeout = TimeSpan.FromSeconds(90) };

        await using var host = await StartAsync(
            new FakeInstanceDirectory(Live), collection => collection.AddSingleton(janitor));

        var body = await ReadAsync(host);

        Assert.Equal(TimeSpan.FromSeconds(90), body.HeartbeatTimeout);
    }

    // A tier that persists nothing registers no janitor, and the read still has to answer.
    [Fact]
    public async Task TheTimeoutFallsBackToTheJanitorDefaultWhereNoJanitorRuns()
    {
        await using var host = await StartAsync(new FakeInstanceDirectory(Live));

        var body = await ReadAsync(host);

        Assert.Equal(new JanitorOptions().HeartbeatTimeout, body.HeartbeatTimeout);
    }

    [Fact]
    public async Task TheReadIsNotOnTheMachineTree()
    {
        await using var host = await StartAsync(new FakeInstanceDirectory(Live));

        var response = await host.Client.GetAsync(CadenceApiDefaults.ApiPath + "/instances");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static InstanceInfo Live { get; } = new()
    {
        InstanceId = "live-1",
        MachineName = "box-a",
        ProcessId = 4711,
        AssemblyVersion = "0.4.0+abc",
        StartedAtUtc = Now.AddHours(-3),
        LastHeartbeatUtc = Now,
    };

    private static InstanceInfo Dead { get; } = new()
    {
        InstanceId = "dead-1",
        MachineName = "box-b",
        ProcessId = 4712,
        StartedAtUtc = Now.AddDays(-2),
        LastHeartbeatUtc = Now.AddHours(-9),
    };

    private static async Task<InstancesResponse> ReadAsync(ApiTestHost host)
    {
        var response = await host.Client.GetAsync(InstancesPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<InstancesResponse>();
        Assert.NotNull(body);

        return body;
    }

    private static Task<ApiTestHost> StartAsync(
        FakeInstanceDirectory directory, Action<IServiceCollection>? services = null)
        => ApiTestHost.StartAsync(
            api => api.AllowUnauthenticated = true,
            collection =>
            {
                // AddCadence offers the in-memory directory with TryAdd and this hook runs after
                // it, so the plain registration is what resolves -- the storage packages' rule.
                collection.AddSingleton<IInstanceDirectory>(directory);
                services?.Invoke(collection);
            },
            endpoints: routes => CadenceUiRoutes.Map(routes, Open));

    private sealed class FakeInstanceDirectory(params InstanceInfo[] instances) : IInstanceDirectory
    {
        public Task<IReadOnlyList<InstanceInfo>> GetAllAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstanceInfo>>(instances);
    }
}
