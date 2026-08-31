using Cadence.Storage.Redis.Internal;
using StackExchange.Redis;

namespace Cadence.Storage.Redis;

/// <summary>Keeps run results in one hash per run, expiring on the key's own TTL.</summary>
/// <remarks>
/// <para>
/// The expiry Cadence passes becomes a Redis TTL, so this tier needs no sweep: <see cref="PurgeAsync"/>
/// returns zero the same way the token purge does on a tier whose keys expire themselves.
/// </para>
/// <para>
/// Reads are buffered, not streamed, because Redis has no partial read of a value that would let
/// them be anything else. That, and not the storage cost, is why
/// <see cref="RedisStorageOptions.MaxResultBytes"/> is an order of magnitude below what the SQL
/// tier carries — see the remarks there before raising it.
/// </para>
/// </remarks>
public sealed class RedisJobResultStore : IJobResultStore
{
    private const string ContentTypeField = "contentType";
    private const string FileNameField = "fileName";
    private const string LengthField = "length";
    private const string CreatedField = "createdAt";
    private const string ExpiresField = "expiresAt";
    private const string ContentField = "content";

    private readonly RedisConnection _connection;
    private readonly ISystemClock _clock;
    private readonly long _maxResultBytes;

    /// <summary>Creates the store.</summary>
    /// <param name="connection">The shared multiplexer and key layout.</param>
    /// <param name="clock">Supplies the instant a result is recorded as stored at.</param>
    /// <param name="options">Supplies this tier's result size ceiling.</param>
    internal RedisJobResultStore(
        RedisConnection connection,
        ISystemClock clock,
        RedisStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _connection = connection;
        _clock = clock;
        _maxResultBytes = options.MaxResultBytes;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The result is larger than <see cref="RedisStorageOptions.MaxResultBytes"/>.
    /// </exception>
    public async Task SaveAsync(
        Guid runId,
        JobResult result,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Length > _maxResultBytes)
        {
            throw new InvalidOperationException(
                $"A {result.Length:N0} byte result exceeds the Redis tier's ceiling of " +
                $"{_maxResultBytes:N0} bytes. Redis has no streaming read, so a result of this size " +
                "is held whole in memory on the server and again here. Raise " +
                $"{nameof(RedisStorageOptions)}.{nameof(RedisStorageOptions.MaxResultBytes)} " +
                "deliberately, register a result store of your own, or use the SQL tier.");
        }

        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);
        var key = _connection.Keys.Result(runId);
        var now = _clock.UtcNow;

        var batch = database.CreateBatch();

        // The hash is replaced rather than merged: a retry that produced a shorter result must not
        // leave the previous run's trailing bytes behind it.
        var delete = batch.KeyDeleteAsync(key);

        var write = batch.HashSetAsync(
            key,
            [
                new HashEntry(ContentTypeField, result.ContentType),
                new HashEntry(FileNameField, result.FileName ?? RedisValue.EmptyString),
                new HashEntry(LengthField, result.Length),
                new HashEntry(CreatedField, now.UtcTicks),
                new HashEntry(ExpiresField, expiresAt.UtcTicks),
                new HashEntry(ContentField, result.Content.ToArray()),
            ]);

        // A TTL in the past would leave the key immortal rather than deleting it, so an expiry that
        // has already passed is written as the shortest life Redis will accept.
        var life = expiresAt - now;
        var expire = batch.KeyExpireAsync(key, life > TimeSpan.Zero ? life : TimeSpan.FromSeconds(1));

        batch.Execute();

        await Task.WhenAll(delete, write, expire).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<JobResultInfo?> DescribeAsync(Guid runId, CancellationToken cancellationToken)
    {
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        var fields = await database.HashGetAsync(
            _connection.Keys.Result(runId),
            [ContentTypeField, FileNameField, LengthField, CreatedField, ExpiresField])
            .ConfigureAwait(false);

        return Describe(runId, fields);
    }

    /// <inheritdoc />
    public async Task<StoredJobResult?> OpenAsync(Guid runId, CancellationToken cancellationToken)
    {
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        var fields = await database.HashGetAsync(
            _connection.Keys.Result(runId),
            [ContentTypeField, FileNameField, LengthField, CreatedField, ExpiresField, ContentField])
            .ConfigureAwait(false);

        if (Describe(runId, fields) is not { } info)
        {
            return null;
        }

        var content = (byte[]?)fields[5] ?? [];

        return new StoredJobResult(info, new MemoryStream(content, writable: false));
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid runId, CancellationToken cancellationToken)
    {
        var database = await _connection.GetDatabaseAsync().ConfigureAwait(false);

        await database.KeyDeleteAsync(_connection.Keys.Result(runId)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always zero. Each result key carries its own TTL, so expiry is the server's job and there is
    /// nothing for a sweep to find.
    /// </remarks>
    public Task<int> PurgeAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
        => Task.FromResult(0);

    private static JobResultInfo? Describe(Guid runId, RedisValue[] fields)
    {
        if (fields[0].IsNull)
        {
            return null;
        }

        var fileName = (string?)fields[1];

        return new JobResultInfo
        {
            RunId = runId,
            ContentType = (string?)fields[0] ?? "application/octet-stream",
            FileName = string.IsNullOrEmpty(fileName) ? null : fileName,
            Length = (long)fields[2],
            CreatedAt = new DateTimeOffset((long)fields[3], TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset((long)fields[4], TimeSpan.Zero),
        };
    }
}
