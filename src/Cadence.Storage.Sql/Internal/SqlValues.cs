using System.Data;
using Microsoft.Data.SqlClient;

namespace Cadence.Storage.Sql.Internal;

/// <summary>
/// Converts between Cadence's types and the column types they are stored in.
/// </summary>
/// <remarks>
/// The instant conversion is the one that matters. Every time Cadence writes comes from
/// <c>ISystemClock.UtcNow</c>, so the offset is always zero and storing it would waste four bytes a
/// row and — worse — make an equality comparison depend on the stored offset rather than on the
/// instant. The claim's unique index compares occurrence instants across instances whose local time
/// zones may differ, so that distinction is not academic. Instants therefore go in as UTC
/// <c>DATETIME2(3)</c> and come back out pinned to <see cref="TimeSpan.Zero"/>.
/// </remarks>
internal static class SqlValues
{
    /// <summary>Converts an instant to the value stored in a DATETIME2 column.</summary>
    /// <param name="value">Any instant; its offset is normalised away.</param>
    public static DateTime ToDb(DateTimeOffset value) => value.UtcDateTime;

    /// <summary>Converts a stored DATETIME2 back to an instant.</summary>
    /// <param name="value">The stored value, which is always UTC.</param>
    public static DateTimeOffset FromDb(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>Reads a non-null instant column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">Column index.</param>
    public static DateTimeOffset GetInstant(SqlDataReader reader, int ordinal)
        => FromDb(reader.GetDateTime(ordinal));

    /// <summary>Reads a nullable instant column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">Column index.</param>
    public static DateTimeOffset? GetInstantOrNull(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : FromDb(reader.GetDateTime(ordinal));

    /// <summary>Reads a nullable string column.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">Column index.</param>
    public static string? GetStringOrNull(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Adds an instant parameter.</summary>
    /// <param name="command">The command.</param>
    /// <param name="name">Parameter name, including the leading @.</param>
    /// <param name="value">The instant, or null.</param>
    public static SqlParameter AddInstant(SqlCommand command, string name, DateTimeOffset? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.DateTime2);
        parameter.Scale = 3;
        parameter.Value = value is { } instant ? ToDb(instant) : DBNull.Value;
        return parameter;
    }

    /// <summary>Adds an nvarchar parameter.</summary>
    /// <param name="command">The command.</param>
    /// <param name="name">Parameter name, including the leading @.</param>
    /// <param name="value">The text, or null.</param>
    /// <param name="size">Declared column size, or -1 for MAX.</param>
    public static SqlParameter AddText(SqlCommand command, string name, string? value, int size)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.NVarChar, size);
        parameter.Value = value ?? (object)DBNull.Value;
        return parameter;
    }

    /// <summary>Adds a uniqueidentifier parameter.</summary>
    /// <param name="command">The command.</param>
    /// <param name="name">Parameter name, including the leading @.</param>
    /// <param name="value">The id.</param>
    public static SqlParameter AddGuid(SqlCommand command, string name, Guid value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.UniqueIdentifier);
        parameter.Value = value;
        return parameter;
    }

    /// <summary>Adds a tinyint parameter from an enum value.</summary>
    /// <param name="command">The command.</param>
    /// <param name="name">Parameter name, including the leading @.</param>
    /// <param name="value">The enum value, which must fit in a byte.</param>
    public static SqlParameter AddEnum<TEnum>(SqlCommand command, string name, TEnum value)
        where TEnum : struct, Enum
    {
        var parameter = command.Parameters.Add(name, SqlDbType.TinyInt);
        parameter.Value = Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture);
        return parameter;
    }

    /// <summary>Adds a nullable bigint parameter from a duration, stored as whole milliseconds.</summary>
    /// <param name="command">The command.</param>
    /// <param name="name">Parameter name, including the leading @.</param>
    /// <param name="value">The duration, or null.</param>
    public static SqlParameter AddDuration(SqlCommand command, string name, TimeSpan? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.BigInt);
        parameter.Value = value is { } span ? (long)span.TotalMilliseconds : DBNull.Value;
        return parameter;
    }

    /// <summary>Reads a nullable duration column stored as whole milliseconds.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="ordinal">Column index.</param>
    public static TimeSpan? GetDurationOrNull(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : TimeSpan.FromMilliseconds(reader.GetInt64(ordinal));
}
