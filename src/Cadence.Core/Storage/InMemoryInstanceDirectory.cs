using System.Reflection;
using Microsoft.Extensions.Options;

namespace Cadence.Storage;

/// <summary>The no-infrastructure tier: this process is the deployment.</summary>
internal sealed class InMemoryInstanceDirectory : IInstanceDirectory
{
    private readonly IReadOnlyList<InstanceInfo> _self;
    private readonly ISystemClock _clock;

    public InMemoryInstanceDirectory(IOptions<CadenceOptions> options, ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;

        _self =
        [
            new InstanceInfo
            {
                InstanceId = options.Value.InstanceId,
                MachineName = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                AssemblyVersion = Assembly.GetEntryAssembly()?
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                StartedAtUtc = clock.UtcNow,
                LastHeartbeatUtc = clock.UtcNow,
            },
        ];
    }

    // The heartbeat is always now: there is no other process to have stopped beating.
    public Task<IReadOnlyList<InstanceInfo>> GetAllAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<InstanceInfo>>(
            [_self[0] with { LastHeartbeatUtc = _clock.UtcNow }]);
}
