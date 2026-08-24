using Cadence.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cadence.Validation;

/// <summary>
/// Resolves every registered job from a real scope before the first tick.
/// </summary>
/// <remarks>
/// <para>
/// This is the part <c>ValidateOnBuild</c> cannot cover, because jobs are resolved from a scope
/// rather than the root provider. Discovering at 02:00 that a nightly job cannot construct its
/// dependencies is the failure this exists to prevent.
/// </para>
/// <para>
/// It is also the honest limit of what is checkable: a Roslyn analyzer cannot validate a DI graph,
/// because registrations happen through arbitrary runtime code. This probe can, because by the
/// time it runs the container is built.
/// </para>
/// </remarks>
public sealed class JobGraphValidator
{
    private readonly IJobRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RegistrationDiagnostics _registrationDiagnostics;
    private readonly CadenceOptions _options;
    private readonly ILogger<JobGraphValidator> _logger;

    /// <summary>Creates the validator.</summary>
    /// <param name="registry">The registered jobs.</param>
    /// <param name="scopeFactory">Creates the probe scope.</param>
    /// <param name="registrationDiagnostics">Warnings collected while the container was built.</param>
    /// <param name="options">Supplies the configured failure behaviour.</param>
    /// <param name="logger">Receives the outcome.</param>
    public JobGraphValidator(
        IJobRegistry registry,
        IServiceScopeFactory scopeFactory,
        RegistrationDiagnostics registrationDiagnostics,
        IOptions<CadenceOptions> options,
        ILogger<JobGraphValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(registrationDiagnostics);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _scopeFactory = scopeFactory;
        _registrationDiagnostics = registrationDiagnostics;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Probes every job and applies the configured failure behaviour.
    /// </summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The names of jobs that must not be scheduled.</returns>
    /// <exception cref="CadenceStartupException">
    /// A job could not be resolved and <see cref="StartupValidation.ThrowOnStartup"/> is configured.
    /// </exception>
    public async Task<IReadOnlySet<string>> ValidateAsync(CancellationToken cancellationToken)
    {
        foreach (var warning in _registrationDiagnostics.Warnings)
        {
            _logger.RegistrationWarning(warning);
        }

        var failures = new List<(string JobName, string Message)>();

        foreach (var descriptor in _registry.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var scope = _scopeFactory.CreateAsyncScope();

            try
            {
                _ = scope.ServiceProvider.GetRequiredService(descriptor.ImplementationType);
            }
            catch (Exception ex)
            {
                failures.Add((descriptor.Name, $"{descriptor.ImplementationType.Name}: {ex.Message}"));
            }
        }

        if (failures.Count == 0)
        {
            _logger.AllJobsResolved(_registry.All.Count);
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var detail = Environment.NewLine + string.Join(
            Environment.NewLine,
            failures.Select(f => $"  - {f.JobName} ({f.Message})"));

        switch (_options.Validation)
        {
            case StartupValidation.DisableFailingJobs:
                _logger.JobsDisabledByValidation(failures.Count, detail);
                return failures.Select(f => f.JobName).ToHashSet(StringComparer.Ordinal);

            case StartupValidation.WarnOnly:
                _logger.JobsUnresolvable(failures.Count, detail);
                return new HashSet<string>(StringComparer.Ordinal);

            case StartupValidation.ThrowOnStartup:
            default:
                throw new CadenceStartupException(
                    $"{failures.Count} registered job(s) could not be resolved from the container:{detail}");
        }
    }
}
