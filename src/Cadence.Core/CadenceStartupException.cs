namespace Cadence;

/// <summary>
/// Thrown when the scheduler cannot start with the configuration it was given. Always a
/// misconfiguration, always fatal, and always better raised at deploy time than at 02:00.
/// </summary>
public sealed class CadenceStartupException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong, and what to change.</param>
    public CadenceStartupException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an underlying cause.</summary>
    /// <param name="message">What is wrong, and what to change.</param>
    /// <param name="innerException">The underlying failure.</param>
    public CadenceStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
