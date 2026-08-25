using System.Data;
using Microsoft.Data.SqlClient;

namespace Cadence.Storage.Sql.Internal;

/// <summary>
/// Opens connections and runs commands against the Cadence database, with the configured schema and
/// command timeout already applied.
/// </summary>
/// <remarks>
/// A thin wrapper rather than a repository: every caller still writes its own SQL, because the
/// statements here are few, fixed, and in two cases depend on their exact shape. What this
/// centralises is the boring, easy-to-get-subtly-wrong part — timeout, schema qualification, and
/// disposing the connection on every path.
/// </remarks>
internal sealed class SqlDatabase
{
    private readonly SqlStorageOptions _options;

    public SqlDatabase(SqlStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>The configured schema, already validated as a plain identifier.</summary>
    public string Schema => _options.SchemaName;

    /// <summary>Qualifies a Cadence table name with the configured schema.</summary>
    /// <param name="table">Unquoted table name.</param>
    public string Table(string table) => $"[{_options.SchemaName}].[{table}]";

    /// <summary>Opens a connection. The caller disposes it.</summary>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_options.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates a command carrying the configured timeout.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="sql">The statement.</param>
    public SqlCommand Command(SqlConnection connection, string sql)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        return command;
    }

    /// <summary>Runs a non-query statement on its own connection and returns rows affected.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="bind">Adds parameters.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    public async Task<int> ExecuteAsync(
        string sql,
        Action<SqlCommand>? bind,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, sql);
        bind?.Invoke(command);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a scalar query on its own connection.</summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="sql">The query.</param>
    /// <param name="bind">Adds parameters.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The value, or default when the query returned no row or a null.</returns>
    public async Task<T?> ScalarAsync<T>(
        string sql,
        Action<SqlCommand>? bind,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, sql);
        bind?.Invoke(command);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>Runs a query and projects every row.</summary>
    /// <typeparam name="T">The projected type.</typeparam>
    /// <param name="sql">The query.</param>
    /// <param name="bind">Adds parameters.</param>
    /// <param name="read">Projects the current row.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    public async Task<List<T>> QueryAsync<T>(
        string sql,
        Action<SqlCommand>? bind,
        Func<SqlDataReader, T> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, sql);
        bind?.Invoke(command);

        var results = new List<T>();

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(read(reader));
        }

        return results;
    }
}
