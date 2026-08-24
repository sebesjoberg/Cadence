namespace Cadence;

/// <summary>
/// Registration-time warnings, collected while the container is being built and logged once the
/// logger exists.
/// </summary>
/// <param name="Warnings">The warnings.</param>
public sealed record RegistrationDiagnostics(IReadOnlyList<string> Warnings);
