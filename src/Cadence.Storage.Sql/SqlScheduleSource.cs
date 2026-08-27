using System.Collections.Immutable;
using System.Text.Json;
using Cadence.Storage.Sql.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Cadence.Storage.Sql;

/// <summary>
/// Reads and writes the schedule in SQL Server. This is the table that makes Cadence what it is:
/// rows here override what the code declared, and they can change while the application runs.
/// </summary>
/// <remarks>
/// <para>
/// Change detection polls a single version row rather than diffing the schedules themselves, so the
/// steady-state cost of noticing that nothing changed is one small read per instance per poll
/// interval. Every write bumps that row in the same transaction, so a poll can never see a schedule
/// change without also seeing the version move.
/// </para>
/// <para>
/// A failed poll is deliberately not an error: instances keep running the schedules they already
/// hold. A database blip should delay a schedule edit reaching an instance, not stop that instance
/// from scheduling.
/// </para>
/// </remarks>
public sealed class SqlScheduleSource : IWritableScheduleSource, IDisposable
{
    private readonly SqlDatabase _database;
    private readonly SqlStorageOptions _options;
    private readonly ISystemClock _clock;
    private readonly ILogger<SqlScheduleSource> _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private CancellationTokenSource _changed = new();
    private long _knownVersion = -1;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;
    private Task? _poll;
    private int _disposed;

    internal SqlScheduleSource(
        SqlDatabase database,
        SqlStorageOptions options,
        ISystemClock clock,
        ILogger<SqlScheduleSource> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobSchedule>> GetAllAsync(CancellationToken cancellationToken)
    {
        SchedulePollIfDue();

        var rows = await _database.QueryAsync(
            $"SELECT {ScheduleColumns} FROM {_database.Table("CadenceJobSchedule")};",
            bind: null,
            ReadSchedule,
            cancellationToken).ConfigureAwait(false);

        return rows;
    }

    /// <inheritdoc />
    public async Task<JobSchedule?> GetAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        var rows = await _database.QueryAsync(
            $"""
            SELECT {ScheduleColumns}
            FROM {_database.Table("CadenceJobSchedule")}
            WHERE JobName = @JobName;
            """,
            command => SqlValues.AddText(command, "@JobName", jobName, 200),
            ReadSchedule,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    /// <exception cref="ScheduleConflictException">
    /// The stored row has a different version, meaning someone else edited it since this caller read
    /// it. Re-read and re-apply rather than retrying blind — the other edit may be the one that
    /// matters.
    /// </exception>
    public async Task UpsertAsync(JobSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        /*
            Insert-or-update plus the version bump in one statement, inside one transaction, so a
            reader can never observe a changed schedule with an unchanged version.

            A version of zero means "I did not read this row first, just make it so" -- which is what
            a source that does not version rows produces, and what a first-write caller has. Any other
            value must match what is stored.
        */
        var sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            DECLARE @current INT = (
                SELECT Version FROM {_database.Table("CadenceJobSchedule")} WITH (UPDLOCK, HOLDLOCK)
                WHERE JobName = @JobName);

            IF @current IS NULL
            BEGIN
                INSERT INTO {_database.Table("CadenceJobSchedule")}
                    (JobName, CronExpression, TimeZoneId, Enabled, Overlap, MaxDurationMs,
                     SettingsJson, Version, UpdatedAtUtc)
                VALUES
                    (@JobName, @CronExpression, @TimeZoneId, @Enabled, @Overlap, @MaxDurationMs,
                     @SettingsJson, 1, @UpdatedAtUtc);

                SET @Outcome = 1;
            END
            ELSE IF @Version = 0 OR @Version = @current
            BEGIN
                UPDATE {_database.Table("CadenceJobSchedule")}
                SET CronExpression = @CronExpression,
                    TimeZoneId     = @TimeZoneId,
                    Enabled        = @Enabled,
                    Overlap        = @Overlap,
                    MaxDurationMs  = @MaxDurationMs,
                    SettingsJson   = @SettingsJson,
                    Version        = @current + 1,
                    UpdatedAtUtc   = @UpdatedAtUtc
                WHERE JobName = @JobName;

                SET @Outcome = 1;
            END
            ELSE
            BEGIN
                SET @Outcome = 0;
                SET @StoredVersion = @current;
            END

            IF @Outcome = 1
                UPDATE {_database.Table("CadenceScheduleVersion")} SET Version = Version + 1 WHERE Id = 1;

            COMMIT TRANSACTION;
            """;

        var outcome = 0;
        var storedVersion = 0;

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _database.Command(connection, sql);

        SqlValues.AddText(command, "@JobName", schedule.JobName, 200);
        SqlValues.AddText(command, "@CronExpression", schedule.CronExpression, 200);
        SqlValues.AddText(command, "@TimeZoneId", schedule.TimeZoneId, 100);
        command.Parameters.AddWithValue("@Enabled", schedule.Enabled);
        AddNullableEnum(command, "@Overlap", schedule.Overlap);
        SqlValues.AddDuration(command, "@MaxDurationMs", schedule.MaxDuration);
        SqlValues.AddText(command, "@SettingsJson", SerialiseSettings(schedule.Settings), -1);
        command.Parameters.AddWithValue("@Version", schedule.Version);
        SqlValues.AddInstant(command, "@UpdatedAtUtc", _clock.UtcNow);

        var outcomeParameter = command.Parameters.Add("@Outcome", System.Data.SqlDbType.Int);
        outcomeParameter.Direction = System.Data.ParameterDirection.Output;

        var storedParameter = command.Parameters.Add("@StoredVersion", System.Data.SqlDbType.Int);
        storedParameter.Direction = System.Data.ParameterDirection.Output;
        storedParameter.Value = 0;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        outcome = outcomeParameter.Value as int? ?? 0;
        storedVersion = storedParameter.Value as int? ?? 0;

        if (outcome != 1)
        {
            throw new ScheduleConflictException(schedule.JobName, schedule.Version, storedVersion);
        }

        // This instance made the change, so it does not need to wait for its own poll to notice.
        Signal();
    }

    /// <inheritdoc />
    public IChangeToken GetChangeToken()
    {
        SchedulePollIfDue();

        lock (_gate)
        {
            return new CancellationChangeToken(_changed.Token);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Guarded because the container captures this instance once per service type it is
        // registered under, and disposes every capture.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        _shutdown.Dispose();

        lock (_gate)
        {
            _changed.Dispose();
        }
    }

    /// <summary>Checks the version row now, rather than waiting for the poll interval.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True when the version had moved.</returns>
    internal async Task<bool> PollAsync(CancellationToken cancellationToken)
    {
        var current = await _database.ScalarAsync<long>(
            $"SELECT Version FROM {_database.Table("CadenceScheduleVersion")} WHERE Id = 1;",
            bind: null,
            cancellationToken).ConfigureAwait(false);

        long previous;

        lock (_gate)
        {
            previous = _knownVersion;
            _knownVersion = current;
        }

        // The first poll establishes the baseline. Firing the token then would make every instance
        // reload on its first tick for no reason.
        if (previous < 0 || previous == current)
        {
            return false;
        }

        _logger.ScheduleVersionChanged(previous, current);
        Signal();
        return true;
    }

    /// <summary>
    /// Starts a poll if one is due, without waiting for it.
    /// </summary>
    /// <remarks>
    /// Not awaited on purpose. This is reached from the tick loop's schedule read, and blocking that
    /// on an extra round trip would put the version check on the scheduling path — where a slow
    /// database would delay dispatching work that is already due.
    /// </remarks>
    private void SchedulePollIfDue()
    {
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested
                || (_poll is { IsCompleted: false })
                || _clock.UtcNow - _lastPoll < _options.SchedulePollInterval)
            {
                return;
            }

            _lastPoll = _clock.UtcNow;
            _poll = PollQuietlyAsync();
        }
    }

    private async Task PollQuietlyAsync()
    {
        try
        {
            await PollAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Disposed mid-poll.
        }
        catch (Exception ex)
        {
            _logger.SchedulePollFailed(ex);
        }
    }

    private void Signal()
    {
        CancellationTokenSource previous;

        lock (_gate)
        {
            previous = _changed;
            _changed = new CancellationTokenSource();
        }

        // Cancelled outside the lock: the registered callbacks run inline, and one of them reads the
        // schedules, which would deadlock against a caller already holding the gate.
        previous.Cancel();
        previous.Dispose();
    }

    private const string ScheduleColumns = """
        JobName, CronExpression, TimeZoneId, Enabled, Overlap, MaxDurationMs, SettingsJson, Version
        """;

    private static JobSchedule ReadSchedule(Microsoft.Data.SqlClient.SqlDataReader reader) => new()
    {
        JobName = reader.GetString(0),
        CronExpression = reader.GetString(1),
        TimeZoneId = reader.GetString(2),
        Enabled = reader.GetBoolean(3),
        Overlap = reader.IsDBNull(4) ? null : (OverlapPolicy)reader.GetByte(4),
        MaxDuration = SqlValues.GetDurationOrNull(reader, 5),
        Settings = DeserialiseSettings(SqlValues.GetStringOrNull(reader, 6)),
        Version = reader.GetInt32(7),
    };

    private static void AddNullableEnum<TEnum>(
        Microsoft.Data.SqlClient.SqlCommand command,
        string name,
        TEnum? value)
        where TEnum : struct, Enum
    {
        var parameter = command.Parameters.Add(name, System.Data.SqlDbType.TinyInt);

        parameter.Value = value is { } set
            ? Convert.ToByte(set, System.Globalization.CultureInfo.InvariantCulture)
            : DBNull.Value;
    }

    private static string? SerialiseSettings(IReadOnlyDictionary<string, string> settings)
        => settings.Count == 0 ? null : JsonSerializer.Serialize(settings);

    private static IReadOnlyDictionary<string, string> DeserialiseSettings(string? json)
    {
        if (json is null)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? (IReadOnlyDictionary<string, string>)ImmutableDictionary<string, string>.Empty;
        }
        catch (JsonException)
        {
            // A settings blob someone hand-edited into invalid JSON should not stop the job from
            // being scheduled; the job sees no settings and the operator sees an empty dictionary.
            return ImmutableDictionary<string, string>.Empty;
        }
    }
}
