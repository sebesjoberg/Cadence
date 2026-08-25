using System.Text.Json;
using Cadence.Scheduling;
using Cadence.Storage;

namespace Cadence.Execution;

/// <inheritdoc cref="IJobTrigger" />
public sealed class JobTrigger : IJobTrigger
{
    private readonly IJobRegistry _registry;
    private readonly ScheduleResolver _resolver;
    private readonly IPauseStore _pauses;
    private readonly JobExecutor _executor;

    /// <summary>Creates the trigger.</summary>
    /// <param name="registry">The registered jobs.</param>
    /// <param name="resolver">Resolves effective run settings.</param>
    /// <param name="pauses">The cluster-wide pause switches.</param>
    /// <param name="executor">Starts the run.</param>
    public JobTrigger(
        IJobRegistry registry,
        ScheduleResolver resolver,
        IPauseStore pauses,
        JobExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(pauses);
        ArgumentNullException.ThrowIfNull(executor);

        _registry = registry;
        _resolver = resolver;
        _pauses = pauses;
        _executor = executor;
    }

    /// <inheritdoc />
    public async Task<DispatchResult> TriggerAsync(
        string jobName,
        TriggerKind trigger = TriggerKind.Manual,
        JsonElement? payload = null,
        CancellationToken cancellationToken = default)
    {
        if (!_registry.TryGet(jobName, out var descriptor))
        {
            throw new JobNotFoundException(jobName);
        }

        if (trigger == TriggerKind.Schedule)
        {
            throw new TriggerNotAllowedException(
                jobName,
                trigger,
                "Scheduled occurrences are dispatched by the tick loop so that exactly one instance " +
                "claims each one. Use Manual or Api to trigger a run out of band.");
        }

        if (!descriptor!.AllowedTriggers.HasFlag(trigger))
        {
            throw new TriggerNotAllowedException(
                jobName,
                trigger,
                $"'{jobName}' allows {descriptor.AllowedTriggers}.");
        }

        // Read rather than taken from the ticker's cache: a trigger is rare enough to afford the
        // round trip, and a pause set seconds ago should already be refusing.
        var pause = await _pauses.GetAsync(cancellationToken).ConfigureAwait(false);

        if (pause.AreTriggersPaused)
        {
            throw new SchedulerPausedException(jobName, pause);
        }

        var settings = await _resolver.ResolveRunSettingsAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);

        // ScheduledFor stays null: a triggered run belongs to no occurrence, so it is exempt from
        // claiming. An explicit trigger is deliberate and should not be silently suppressed because
        // another instance happens to be running the same job — the overlap policy still applies.
        return await _executor.DispatchAsync(
            descriptor,
            settings,
            scheduledFor: null,
            trigger,
            payload,
            cancellationToken).ConfigureAwait(false);
    }
}
