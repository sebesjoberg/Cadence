using Cadence.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Validation;

/// <summary>
/// Reports at boot when the configured shutdown budget cannot let a run finish.
/// </summary>
/// <remarks>
/// <para>
/// The full chain is <c>terminationGracePeriodSeconds ≥ HostOptions.ShutdownTimeout ≥
/// CadenceOptions.ShutdownDrainTimeout ≥ the longest MaxDuration</c>. Only the inner pair is inside
/// the process, so only the inner pair is checked here; the outermost value belongs to whatever is
/// supervising the process and is deployment documentation.
/// </para>
/// <para>
/// This warns and never throws. Every value in the chain defaults to thirty seconds while a job's
/// <c>MaxDuration</c> does not, so the common misconfiguration is one an existing application is
/// already running with — failing its boot would turn an upgrade into an outage. It is also only
/// ever advice: the value that decides whether the process is killed outright is outside the
/// process, so a clean inner pair is not a guarantee of anything.
/// </para>
/// <para>
/// The bound comes from the <em>registered</em> maximum durations, which is what is knowable before
/// the first schedule read. A writable schedule source can raise one later, and that edit is not
/// checked here.
/// </para>
/// </remarks>
public sealed class ShutdownBudgetProbe
{
    private readonly IJobRegistry _registry;
    private readonly CadenceOptions _options;
    private readonly HostOptions _hostOptions;
    private readonly ILogger<ShutdownBudgetProbe> _logger;

    /// <summary>Creates the probe.</summary>
    /// <param name="registry">The registered jobs, whose longest maximum duration sets the floor.</param>
    /// <param name="options">Supplies the drain timeout.</param>
    /// <param name="hostOptions">Supplies the host's own shutdown budget.</param>
    /// <param name="logger">Receives one warning per violation.</param>
    public ShutdownBudgetProbe(
        IJobRegistry registry,
        IOptions<CadenceOptions> options,
        IOptions<HostOptions> hostOptions,
        ILogger<ShutdownBudgetProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hostOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _options = options.Value;
        _hostOptions = hostOptions.Value;
        _logger = logger;
    }

    /// <summary>Logs one warning for each way the budget truncates a run.</summary>
    public void Report()
    {
        foreach (var problem in ShutdownBudget.Check(
            _hostOptions.ShutdownTimeout,
            _options.ShutdownDrainTimeout,
            _registry.All))
        {
            _logger.ShutdownBudgetTooShort(problem);
        }
    }
}
