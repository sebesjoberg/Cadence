using Cronos;

namespace Cadence.Scheduling;

/// <summary>
/// Parses cron expressions, choosing between the 5- and 6-field forms by counting fields.
/// </summary>
/// <remarks>
/// Parsing happens at write time and at boot, never lazily in the tick loop: an unparseable
/// expression that reaches the loop would otherwise throw once a second forever.
/// </remarks>
public static class CronParser
{
    /// <summary>Parses an expression, or throws with a message naming the expression.</summary>
    /// <param name="expression">A 5- or 6-field cron expression.</param>
    /// <returns>The parsed expression.</returns>
    /// <exception cref="FormatException">The expression has the wrong field count or is invalid.</exception>
    public static CronExpression Parse(string expression)
    {
        if (!TryParse(expression, out var parsed, out var error))
        {
            throw new FormatException(error);
        }

        return parsed!;
    }

    /// <summary>Attempts to parse an expression.</summary>
    /// <param name="expression">A 5- or 6-field cron expression.</param>
    /// <param name="parsed">The parsed expression, when successful.</param>
    /// <param name="error">A message explaining the failure, when unsuccessful.</param>
    /// <returns>True when the expression parsed.</returns>
    public static bool TryParse(string? expression, out CronExpression? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "The cron expression is empty.";
            return false;
        }

        var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var format = fields.Length switch
        {
            5 => CronFormat.Standard,
            6 => CronFormat.IncludeSeconds,
            _ => (CronFormat?)null,
        };

        if (format is null)
        {
            error = $"'{expression}' has {fields.Length} fields; a cron expression needs 5, or 6 to include seconds.";
            return false;
        }

        try
        {
            parsed = CronExpression.Parse(expression, format.Value);
            return true;
        }
        catch (CronFormatException ex)
        {
            error = $"'{expression}' is not a valid cron expression: {ex.Message}";
            return false;
        }
    }

    /// <summary>Resolves an IANA timezone id, explaining the two ways this usually fails.</summary>
    /// <param name="timeZoneId">An IANA id such as <c>Europe/Stockholm</c>, or null for UTC.</param>
    /// <param name="timeZone">The resolved zone, when successful.</param>
    /// <param name="error">A message explaining the failure, when unsuccessful.</param>
    /// <returns>True when the zone resolved.</returns>
    public static bool TryResolveTimeZone(string? timeZoneId, out TimeZoneInfo? timeZone, out string? error)
    {
        timeZone = null;
        error = null;

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            timeZone = TimeZoneInfo.Utc;
            return true;
        }

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            // The usual cause on a slim container image is globalization-invariant mode, where
            // the ICU data that maps IANA ids is absent. Name it, because the framework's own
            // message does not.
            error = $"Timezone '{timeZoneId}' was not found. " +
                    "If this is a container image, check that InvariantGlobalization is not enabled — " +
                    "IANA timezone ids need ICU data.";
            return false;
        }
        catch (InvalidTimeZoneException ex)
        {
            error = $"Timezone '{timeZoneId}' is corrupt: {ex.Message}";
            return false;
        }
    }
}
