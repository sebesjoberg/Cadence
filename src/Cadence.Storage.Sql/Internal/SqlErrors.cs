using Microsoft.Data.SqlClient;

namespace Cadence.Storage.Sql.Internal;

/// <summary>
/// Classifies SQL Server errors, so nothing in Cadence has to guess from a message.
/// </summary>
/// <remarks>
/// This exists as its own type because getting it wrong has an asymmetric cost. Treating a dead
/// connection as "someone else won the claim" silently skips a run, and a silently skipped run is
/// the worst failure a scheduler can have — nobody is alerted, nothing appears in history, and the
/// work just does not happen. So the set of errors that mean "lost the race" is enumerated
/// explicitly and everything else propagates.
/// </remarks>
internal static class SqlErrors
{
    /// <summary>Cannot insert duplicate key row in object with unique index.</summary>
    private const int DuplicateKeyInIndex = 2601;

    /// <summary>Violation of unique constraint.</summary>
    private const int DuplicateKeyConstraint = 2627;

    /// <summary>
    /// True when the exception is a uniqueness violation, and therefore means another instance
    /// already holds the row.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns>
    /// True only for 2601 and 2627. Every other error — including connection and timeout failures —
    /// returns false so the caller lets it propagate.
    /// </returns>
    public static bool IsUniqueViolation(SqlException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // The collection, not just exception.Number: a batch can report several errors and the
        // uniqueness one is not necessarily first.
        foreach (SqlError error in exception.Errors)
        {
            if (error.Number is DuplicateKeyInIndex or DuplicateKeyConstraint)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the failure is worth retrying — a dropped connection, a throttled Azure SQL
    /// request, a deadlock victim.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns>True when a retry could plausibly succeed.</returns>
    public static bool IsTransient(SqlException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // The driver maintains this list; duplicating it here would only let it go stale.
        return exception.IsTransient;
    }
}
