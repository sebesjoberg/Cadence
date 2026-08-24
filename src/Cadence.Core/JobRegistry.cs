namespace Cadence;

/// <inheritdoc cref="IJobRegistry" />
public sealed class JobRegistry : IJobRegistry
{
    private readonly Dictionary<string, JobDescriptor> _byName;

    /// <summary>Creates the registry from the descriptors collected during registration.</summary>
    /// <param name="descriptors">The registered jobs.</param>
    /// <exception cref="CadenceStartupException">Two jobs share a name.</exception>
    public JobRegistry(IEnumerable<JobDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        _byName = new Dictionary<string, JobDescriptor>(StringComparer.Ordinal);

        foreach (var descriptor in descriptors)
        {
            if (_byName.TryGetValue(descriptor.Name, out var existing))
            {
                // Names are the identity that DB configuration and history hang off, so a
                // collision silently merges two jobs' schedules and history. Fail at boot.
                throw new CadenceStartupException(
                    $"Two jobs are registered under the name '{descriptor.Name}': " +
                    $"{existing.ImplementationType.FullName} and {descriptor.ImplementationType.FullName}. " +
                    "Job names must be unique.");
            }

            _byName.Add(descriptor.Name, descriptor);
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<JobDescriptor> All => _byName.Values;

    /// <inheritdoc />
    public bool TryGet(string name, out JobDescriptor? descriptor)
        => _byName.TryGetValue(name, out descriptor);
}
