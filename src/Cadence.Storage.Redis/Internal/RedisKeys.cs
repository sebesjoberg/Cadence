using StackExchange.Redis;

namespace Cadence.Storage.Redis.Internal;

/// <summary>
/// Every key Cadence writes, in one place.
/// </summary>
/// <remarks>
/// <para>
/// The SQL tier gets its layout documented for it by the schema script; Redis has no such thing, so
/// this file is the schema. Anyone reasoning about what a janitor pass deletes, or why a query picks
/// one index over another, reads this first.
/// </para>
/// <para>
/// The prefix carries a Redis Cluster hash tag, so every key here lands in one slot. That is not an
/// optimisation — the Lua scripts touch several of these keys atomically, and a cluster refuses a
/// script whose keys span slots.
/// </para>
/// </remarks>
internal sealed class RedisKeys
{
    private readonly string _prefix;

    public RedisKeys(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        _prefix = prefix;
    }

    /// <summary>
    /// The key-name fragments the Lua scripts assemble, so both halves read from one definition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Some scripts have to build a key name themselves — <c>CompleteAsync</c> is handed a run id
    /// and must reach that run's job index, and the job name is only known after reading the hash.
    /// The obvious way to write that is a literal <c>'runs:job:'</c> inside the Lua, which silently
    /// duplicates the layout this class owns: change a method here and the scripts keep building
    /// the old name against a store that no longer uses it.
    /// </para>
    /// <para>
    /// So the fragments are defined once and used twice. The builders below assemble them, and
    /// <see cref="Scripts"/> receives the same strings as arguments. A rename now moves both.
    /// </para>
    /// </remarks>
    public Fragments Parts => new(
        Run: $"{_prefix}run:",
        LogSuffix: ":log",
        Occurrence: $"{_prefix}occ:",
        JobRuns: $"{_prefix}runs:job:",
        SuccessSuffix: ":success",
        InstanceRuns: $"{_prefix}runs:instance:",
        Exclusive: $"{_prefix}excl:");

    /// <summary>HASH holding one run's fields.</summary>
    public RedisKey Run(Guid runId) => $"{Parts.Run}{runId:N}";

    /// <summary>LIST of one run's progress entries, oldest first.</summary>
    public RedisKey RunLog(Guid runId) => $"{Parts.Run}{runId:N}{Parts.LogSuffix}";

    /// <summary>
    /// STRING holding the run id that owns an occurrence. This is the claim.
    /// </summary>
    /// <remarks>
    /// Written in the same script as the run hash, so there is never a moment where a slot is taken
    /// but unrecorded — the property §3.2 of the design plan asks of any coordinator.
    /// </remarks>
    public RedisKey Occurrence(string jobName, DateTimeOffset scheduledFor)
        => $"{Parts.Occurrence}{jobName}:{scheduledFor.UtcDateTime.Ticks}";

    /// <summary>
    /// STRING holding the run id that currently holds an exclusive key. This is cluster-strict
    /// <see cref="OverlapPolicy.Skip"/>.
    /// </summary>
    /// <remarks>
    /// Written in the same script as the run hash, for the reason <see cref="Occurrence"/> is: a
    /// key taken in one round trip and recorded in another leaves a window where a job is blocked
    /// by a run nothing can name. Released by the completion write and by the reap, so a dead
    /// instance blocks its job for a heartbeat timeout rather than forever.
    /// </remarks>
    public RedisKey Exclusive(string exclusiveKey) => $"{Parts.Exclusive}{exclusiveKey}";

    /// <summary>ZSET of every run, scored by start instant. The index of last resort.</summary>
    public RedisKey AllRuns => $"{_prefix}runs";

    /// <summary>ZSET of one job's runs, scored by start instant.</summary>
    public RedisKey JobRuns(string jobName) => $"{Parts.JobRuns}{jobName}";

    /// <summary>ZSET of one instance's runs, scored by start instant.</summary>
    public RedisKey InstanceRuns(string instanceId) => $"{Parts.InstanceRuns}{instanceId}";

    /// <summary>
    /// ZSET of one job's successful runs, scored by start instant.
    /// </summary>
    /// <remarks>
    /// Maintained rather than derived because the staleness watchdog asks for it on a timer, and
    /// deriving it means scanning a job's history backwards until a success turns up — which is
    /// unboundedly long on exactly the job the watchdog exists to catch.
    /// </remarks>
    public RedisKey JobSuccesses(string jobName) => $"{Parts.JobRuns}{jobName}{Parts.SuccessSuffix}";

    /// <summary>
    /// ZSET of runs still in <see cref="RunStatus.Running"/>, scored by start instant.
    /// </summary>
    /// <remarks>
    /// The reap pass needs "every unfinished run" and nothing else. Without this it would walk all
    /// of history to find the handful that matter.
    /// </remarks>
    public RedisKey RunningRuns => $"{_prefix}runs:running";

    /// <summary>SET of job names that have ever recorded a run, so the trim pass can iterate.</summary>
    public RedisKey JobNames => $"{_prefix}jobs";

    /// <summary>HASH holding one run's result: its metadata fields plus the bytes.</summary>
    /// <remarks>
    /// One key rather than a metadata hash beside a bytes string, so the pair cannot half-expire:
    /// a TTL is set on the key, and describing a result that has aged out is a miss rather than
    /// metadata pointing at bytes that are gone.
    /// </remarks>
    public RedisKey Result(Guid runId) => $"{_prefix}result:{runId:N}";

    /// <summary>HASH of instance id to its registration details.</summary>
    public RedisKey Instances => $"{_prefix}instances";

    /// <summary>ZSET of instance id scored by last heartbeat instant.</summary>
    public RedisKey Heartbeats => $"{_prefix}instances:beat";

    /// <summary>HASH holding the cluster-wide pause switches.</summary>
    public RedisKey Pause => $"{_prefix}pause";

    /// <summary>HASH holding one token's fields, keyed by its digest. This is the lookup.</summary>
    /// <remarks>
    /// Keyed by digest rather than by id because resolution happens on every authenticated request
    /// and id is only needed to administer.
    /// </remarks>
    public RedisKey Token(string digestHex) => $"{_prefix}token:{digestHex}";

    /// <summary>HASH of token id to digest hex, so listing and revoking never scan.</summary>
    public RedisKey Tokens => $"{_prefix}tokens";

    /// <summary>LIST of the Data Protection key ring's XML elements, in the order written.</summary>
    /// <remarks>
    /// The layout is the framework's, not Cadence's: <c>RedisXmlRepository</c> owns what goes in
    /// here, and this file only names the key it goes in.
    /// </remarks>
    public RedisKey DataProtectionKeys => $"{_prefix}dataprotection:keys";

    /// <summary>HASH of job name to its stored schedule, version excluded.</summary>
    public RedisKey Schedules => $"{_prefix}schedules";

    /// <summary>
    /// HASH of job name to that schedule's version, for optimistic concurrency.
    /// </summary>
    /// <remarks>
    /// Parallel to the document rather than inside it, so the upsert script can compare and advance
    /// a version without parsing JSON in Lua.
    /// </remarks>
    public RedisKey ScheduleVersions => $"{_prefix}schedules:rowver";

    /// <summary>STRING counter bumped by every schedule write.</summary>
    public RedisKey ScheduleVersion => $"{_prefix}schedules:version";

    /// <summary>Channel a schedule write publishes to, so subscribers do not wait for a poll.</summary>
    public RedisChannel ScheduleChannel
        => RedisChannel.Literal($"{_prefix}schedules:changed");

    /// <summary>
    /// The key-name pieces shared between the builders above and the Lua scripts.
    /// </summary>
    /// <param name="Run">Prefix a run's hash key starts with; the run id follows.</param>
    /// <param name="LogSuffix">Appended to a run's key to reach its progress list.</param>
    /// <param name="Occurrence">Prefix an occurrence key starts with; job and instant follow.</param>
    /// <param name="JobRuns">Prefix a job's run index starts with; the job name follows.</param>
    /// <param name="SuccessSuffix">Appended to a job's run index to reach its success index.</param>
    /// <param name="InstanceRuns">Prefix an instance's run index starts with; the id follows.</param>
    /// <param name="Exclusive">Prefix of the exclusive-key strings.</param>
    public readonly record struct Fragments(
        string Run,
        string LogSuffix,
        string Occurrence,
        string JobRuns,
        string SuccessSuffix,
        string InstanceRuns,
        string Exclusive);
}
