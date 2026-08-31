namespace Cadence.Storage.Redis;

/// <summary>Settings for the Redis storage tier.</summary>
public sealed class RedisStorageOptions
{
    /// <summary>
    /// StackExchange.Redis configuration string, for example <c>localhost:6379</c>.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Prefixed onto every key Cadence writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The braces are not decoration. In Redis Cluster the substring inside <c>{}</c> is the hash
    /// tag, and only keys sharing a tag are guaranteed to land in the same slot — which the Lua
    /// scripts here require, because a claim touches the occurrence key and several indexes in one
    /// atomic step. Removing the tag works on a single node and fails on a cluster, so it is the
    /// default rather than an option buried in documentation.
    /// </para>
    /// <para>
    /// Change it to run two independent Cadence deployments against one Redis, or to isolate tests.
    /// </para>
    /// </remarks>
    public string KeyPrefix { get; set; } = "{cadence}:";

    /// <summary>Logical database number to use.</summary>
    public int Database { get; set; } = -1;

    /// <summary>How often this instance refreshes its heartbeat.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How stale an instance's heartbeat must be before the janitor treats it as gone and marks its
    /// unfinished runs <see cref="RunStatus.Lost"/>.
    /// </summary>
    /// <remarks>
    /// Must be comfortably larger than <see cref="HeartbeatInterval"/>: a GC pause or a brief
    /// network blip should not get a live instance's runs reaped out from under it. The default is
    /// four intervals.
    /// </remarks>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Largest result this tier will store, below the host-wide
    /// <see cref="CadenceOptions.MaxResultBytes"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is where the two storage tiers genuinely stop being interchangeable. SQL Server streams
    /// a result out of a <c>VARBINARY(MAX)</c> column a buffer at a time; Redis has no streaming
    /// read, so every byte of a result crosses the wire and sits in memory on both ends at once,
    /// against a server whose whole dataset is resident. Eight megabytes is a size that behaves;
    /// forty is a server-wide latency event.
    /// </para>
    /// <para>
    /// Exceeding it throws rather than truncating or silently declining to store, so a job whose
    /// output outgrew this tier fails visibly on the run that did it. Raise it deliberately, or
    /// put results somewhere else by registering your own <see cref="IJobResultStore"/>.
    /// </para>
    /// </remarks>
    public long MaxResultBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>How often the janitor purges history, trims per job and reaps abandoned runs.</summary>
    public TimeSpan JanitorInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How many records the janitor touches per operation, looping until a pass is done.</summary>
    public int JanitorBatchSize { get; set; } = 1000;

    /// <summary>
    /// How often the schedule source checks whether an external actor changed configuration.
    /// </summary>
    /// <remarks>
    /// Redis can push, and this tier subscribes to a channel so an edit normally arrives at once.
    /// The poll stays as a backstop: a dropped subscription is invisible to the subscriber, and a
    /// scheduler that silently stops noticing schedule changes is worse than one that reads a
    /// counter every ten seconds.
    /// </remarks>
    public TimeSpan SchedulePollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many progress entries to buffer before flushing, and the ceiling on one flush batch.
    /// </summary>
    public int ProgressBatchSize { get; set; } = 100;

    /// <summary>
    /// How long a buffered progress entry may wait before being flushed even if the batch is not
    /// full.
    /// </summary>
    public TimeSpan ProgressFlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Validates the options and throws when a value cannot work.</summary>
    /// <exception cref="ArgumentException">The connection string or key prefix is unusable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its supported range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException(
                "No connection string was supplied. Pass one to UseRedisStorage, or set " +
                $"{nameof(RedisStorageOptions)}.{nameof(ConnectionString)}.",
                nameof(ConnectionString));
        }

        if (string.IsNullOrWhiteSpace(KeyPrefix))
        {
            throw new ArgumentException(
                "The key prefix cannot be blank: Cadence's keys would then be indistinguishable " +
                "from anything else sharing the database.",
                nameof(KeyPrefix));
        }

        RequirePositive(HeartbeatInterval, nameof(HeartbeatInterval));
        RequirePositive(HeartbeatTimeout, nameof(HeartbeatTimeout));
        RequirePositive(JanitorInterval, nameof(JanitorInterval));
        RequirePositive(SchedulePollInterval, nameof(SchedulePollInterval));
        RequirePositive(ProgressFlushInterval, nameof(ProgressFlushInterval));

        if (HeartbeatTimeout <= HeartbeatInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeartbeatTimeout),
                HeartbeatTimeout,
                $"The heartbeat timeout must be longer than {nameof(HeartbeatInterval)} " +
                $"({HeartbeatInterval}), and should be several times longer. Otherwise a single " +
                "missed beat lets the janitor mark a live instance's runs as lost.");
        }

        if (JanitorBatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(JanitorBatchSize), JanitorBatchSize, "The janitor batch size must be at least one.");
        }

        if (ProgressBatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProgressBatchSize), ProgressBatchSize, "The progress batch size must be at least one.");
        }
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be positive.");
        }
    }
}
