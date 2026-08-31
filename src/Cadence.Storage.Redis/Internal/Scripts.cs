namespace Cadence.Storage.Redis.Internal;

/// <summary>
/// The Lua this tier runs, and the only place multi-key atomicity comes from.
/// </summary>
/// <remarks>
/// <para>
/// Redis executes a script as one unit, which is what lets a claim write the occurrence key and the
/// run's hash and its four index entries with nothing able to observe half of it. Every operation
/// here that touches more than one key is a script for that reason and not for round-trip savings.
/// </para>
/// <para>
/// Some scripts assemble key names from fragments passed in <c>ARGV</c> rather than declaring them
/// in <c>KEYS</c>. That is normally a mistake, because Redis Cluster routes a script by its declared
/// keys — but it is unavoidable here: <c>CompleteAsync</c> is handed a run id and has to reach that
/// run's job index, and the job name is only known after reading the hash. It is safe because the
/// default key prefix carries a cluster hash tag, so every Cadence key lives in one slot. Remove the
/// tag and these scripts break on a cluster; see <see cref="RedisStorageOptions.KeyPrefix"/>.
/// </para>
/// <para>
/// The fragments come from <see cref="RedisKeys.Parts"/>, which is also what the key builders use.
/// Writing <c>'runs:job:'</c> as a literal in here would work and would quietly duplicate the layout
/// that class owns — a rename there would leave these scripts addressing keys nothing writes any
/// more, with no compiler and no test to notice.
/// </para>
/// </remarks>
internal static class Scripts
{
    /// <summary>
    /// Claims an occurrence by writing the run, or reports that someone else holds it.
    /// </summary>
    /// <remarks>
    /// KEYS: occurrence, run hash, all-runs, job-runs, instance-runs, running, job-names.
    /// ARGV: run id, job name, scheduled ticks, trigger, running status, instance id, start ticks.
    /// Returns 1 when the caller may run the occurrence, 0 when it may not.
    /// <para>
    /// A holder equal to our own run id returns 1 without rewriting anything. That is the
    /// idempotency the interface asks for — a commit whose acknowledgement was lost — and not
    /// rewriting matters: the run may have finished since, and resurrecting it to Running would
    /// hand the janitor a live run to reap.
    /// </para>
    /// </remarks>
    public const string Claim =
        """
        local held = redis.call('GET', KEYS[1])
        if held then
          if held == ARGV[1] then return 1 end
          return 0
        end
        redis.call('SET', KEYS[1], ARGV[1])
        redis.call('HSET', KEYS[2],
          'job', ARGV[2], 'sched', ARGV[3], 'trig', ARGV[4],
          'status', ARGV[5], 'inst', ARGV[6], 'start', ARGV[7])
        redis.call('ZADD', KEYS[3], ARGV[7], ARGV[1])
        redis.call('ZADD', KEYS[4], ARGV[7], ARGV[1])
        redis.call('ZADD', KEYS[5], ARGV[7], ARGV[1])
        redis.call('ZADD', KEYS[6], ARGV[7], ARGV[1])
        redis.call('SADD', KEYS[7], ARGV[2])
        return 1
        """;

    /// <summary>
    /// Records a run as started, whether or not a claim already wrote it.
    /// </summary>
    /// <remarks>
    /// KEYS: run hash, all-runs, job-runs, instance-runs, running, job-names, exclusive.
    /// ARGV: run id, job name, scheduled ticks, trigger, running status, instance id, start ticks,
    /// exclusive key.
    /// Returns 1 when the run started, 0 when another run already holds the exclusive key.
    /// <para>
    /// The completion fields are cleared rather than left: a run id arriving here is starting, and
    /// carrying an earlier outcome would make it read as finished the moment anyone queried it.
    /// </para>
    /// <para>
    /// The exclusive key is taken first and inside the same script, so there is no instant in which
    /// it is held by a run this store cannot name. A read followed by a write would need to be two
    /// round trips, and two round trips is the race the whole mechanism exists to avoid. The run's
    /// own id is allowed through, so a claim followed by a start does not block itself.
    /// </para>
    /// </remarks>
    public const string Start =
        """
        if ARGV[8] ~= '' then
          local owner = redis.call('GET', KEYS[7])
          if owner and owner ~= ARGV[1] then return 0 end
          redis.call('SET', KEYS[7], ARGV[1])
          redis.call('HSET', KEYS[1], 'excl', ARGV[8])
        end
        redis.call('HSET', KEYS[1],
          'job', ARGV[2], 'sched', ARGV[3], 'trig', ARGV[4],
          'status', ARGV[5], 'inst', ARGV[6], 'start', ARGV[7])
        redis.call('HDEL', KEYS[1], 'done', 'dur', 'err')
        redis.call('ZADD', KEYS[2], ARGV[7], ARGV[1])
        redis.call('ZADD', KEYS[3], ARGV[7], ARGV[1])
        redis.call('ZADD', KEYS[4], ARGV[7], ARGV[1])
        redis.call('ZADD', KEYS[5], ARGV[7], ARGV[1])
        redis.call('SADD', KEYS[6], ARGV[2])
        return 1
        """;

    /// <summary>
    /// Records a run's outcome, or does nothing when the run is gone.
    /// </summary>
    /// <remarks>
    /// KEYS: run hash, running.
    /// ARGV: run id, status, completed ticks, duration ms, error, job-runs fragment,
    /// succeeded status, success suffix, exclusive fragment.
    /// <para>
    /// Doing nothing for an absent run is the contract: history the janitor already purged must not
    /// be resurrected by a completion arriving late, and a caller completing a run it never started
    /// is reporting about something this store has no opinion on.
    /// </para>
    /// </remarks>
    public const string Complete =
        """
        if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
        redis.call('HSET', KEYS[1], 'status', ARGV[2], 'done', ARGV[3], 'dur', ARGV[4])
        if ARGV[5] ~= '' then
          redis.call('HSET', KEYS[1], 'err', ARGV[5])
        else
          redis.call('HDEL', KEYS[1], 'err')
        end
        redis.call('ZREM', KEYS[2], ARGV[1])
        if ARGV[2] == ARGV[7] then
          local job = redis.call('HGET', KEYS[1], 'job')
          local started = redis.call('HGET', KEYS[1], 'start')
          if job and started then
            redis.call('ZADD', ARGV[6] .. job .. ARGV[8], started, ARGV[1])
          end
        end
        local excl = redis.call('HGET', KEYS[1], 'excl')
        if excl then
          local ekey = ARGV[9] .. excl
          if redis.call('GET', ekey) == ARGV[1] then redis.call('DEL', ekey) end
          redis.call('HDEL', KEYS[1], 'excl')
        end
        return 1
        """;

    /// <summary>
    /// Marks runs whose instance stopped heartbeating as lost.
    /// </summary>
    /// <remarks>
    /// KEYS: running, heartbeats.
    /// ARGV: heartbeat deadline ticks, now ticks, batch size, lost status, run fragment,
    /// scan offset, exclusive fragment.
    /// Returns {reaped, scanned}.
    /// <para>
    /// The offset is why this returns how many it looked at as well as how many it changed. Live
    /// runs stay in the running index, so a caller that advanced only on reaps would rescan the
    /// same healthy runs forever.
    /// </para>
    /// </remarks>
    public const string Reap =
        """
        local offset = tonumber(ARGV[6])
        local batch = tonumber(ARGV[3])
        local ids = redis.call('ZRANGE', KEYS[1], offset, offset + batch - 1)
        local reaped = 0
        for i = 1, #ids do
          local id = ids[i]
          local key = ARGV[5] .. id
          local instance = redis.call('HGET', key, 'inst')
          local abandoned = true
          if instance then
            local beat = redis.call('ZSCORE', KEYS[2], instance)
            if beat and tonumber(beat) >= tonumber(ARGV[1]) then abandoned = false end
          end
          if abandoned then
            local started = redis.call('HGET', key, 'start')
            local duration = 0
            if started then
              duration = math.floor((tonumber(ARGV[2]) - tonumber(started)) / 10000)
              if duration < 0 then duration = 0 end
            end
            redis.call('HSET', key, 'status', ARGV[4], 'done', ARGV[2], 'dur', duration)
            redis.call('ZREM', KEYS[1], id)
            -- Frees the key a dead instance held. Without this a Skip job whose owner was killed
            -- would be blocked by a run nobody will ever complete.
            local excl = redis.call('HGET', key, 'excl')
            if excl then
              local ekey = ARGV[7] .. excl
              if redis.call('GET', ekey) == id then redis.call('DEL', ekey) end
              redis.call('HDEL', key, 'excl')
            end
            reaped = reaped + 1
          end
        end
        return {reaped, #ids}
        """;

    /// <summary>
    /// Deletes finished runs started before a cut-off, with everything that points at them.
    /// </summary>
    /// <remarks>
    /// KEYS: all-runs.
    /// ARGV: older-than ticks, batch size, run fragment, running status, scan offset,
    /// job-runs fragment, success suffix, occurrence fragment, instance-runs fragment, log suffix.
    /// Returns {deleted, scanned}.
    /// <para>
    /// The occurrence key goes with the run. In this tier the claim is the run, so leaving it would
    /// keep a slot marked taken by a run nobody can look up — and would leak a key per occurrence
    /// forever.
    /// </para>
    /// </remarks>
    public const string PurgeByAge =
        """
        local ids = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', '(' .. ARGV[1],
          'LIMIT', tonumber(ARGV[5]), tonumber(ARGV[2]))
        local deleted = 0
        for i = 1, #ids do
          local id = ids[i]
          local key = ARGV[3] .. id
          local status = redis.call('HGET', key, 'status')
          if status ~= ARGV[4] then
            local job = redis.call('HGET', key, 'job')
            local instance = redis.call('HGET', key, 'inst')
            local scheduled = redis.call('HGET', key, 'sched')
            redis.call('ZREM', KEYS[1], id)
            if job then
              redis.call('ZREM', ARGV[6] .. job, id)
              redis.call('ZREM', ARGV[6] .. job .. ARGV[7], id)
              if scheduled and scheduled ~= '' then
                redis.call('DEL', ARGV[8] .. job .. ':' .. scheduled)
              end
            end
            if instance then
              redis.call('ZREM', ARGV[9] .. instance, id)
            end
            redis.call('DEL', key)
            redis.call('DEL', key .. ARGV[10])
            deleted = deleted + 1
          end
        end
        return {deleted, #ids}
        """;

    /// <summary>
    /// Trims one job's history down to its most recent finished runs.
    /// </summary>
    /// <remarks>
    /// KEYS: job-runs, all-runs.
    /// ARGV: max runs per job, batch size, run fragment, running status, job name,
    /// job-runs fragment, success suffix, occurrence fragment, instance-runs fragment, log suffix.
    /// <para>
    /// Only the newest cap-plus-batch entries are examined, so one pass is bounded however long a
    /// job's history is; a backlog larger than that is finished off by the next pass. Running runs
    /// are skipped rather than counted, so a job at its cap whose current run is still going does
    /// not have that run occupy one of the slots it is allowed to keep.
    /// </para>
    /// </remarks>
    public const string TrimJob =
        """
        local cap = tonumber(ARGV[1])
        local batch = tonumber(ARGV[2])
        local ids = redis.call('ZREVRANGE', KEYS[1], 0, cap + batch - 1)
        local kept = 0
        local deleted = 0
        for i = 1, #ids do
          if deleted >= batch then break end
          local id = ids[i]
          local key = ARGV[3] .. id
          local status = redis.call('HGET', key, 'status')
          if status ~= ARGV[4] then
            if kept < cap then
              kept = kept + 1
            else
              local instance = redis.call('HGET', key, 'inst')
              local scheduled = redis.call('HGET', key, 'sched')
              redis.call('ZREM', KEYS[1], id)
              redis.call('ZREM', KEYS[2], id)
              redis.call('ZREM', ARGV[6] .. ARGV[5] .. ARGV[7], id)
              if scheduled and scheduled ~= '' then
                redis.call('DEL', ARGV[8] .. ARGV[5] .. ':' .. scheduled)
              end
              if instance then
                redis.call('ZREM', ARGV[9] .. instance, id)
              end
              redis.call('DEL', key)
              redis.call('DEL', key .. ARGV[10])
              deleted = deleted + 1
            end
          end
        end
        return deleted
        """;

    /// <summary>
    /// Removes instances last seen before a cut-off.
    /// </summary>
    /// <remarks>
    /// KEYS: heartbeats, instances.
    /// ARGV: older-than ticks, batch size.
    /// </remarks>
    public const string PurgeInstances =
        """
        local ids = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', '(' .. ARGV[1],
          'LIMIT', 0, tonumber(ARGV[2]))
        for i = 1, #ids do
          redis.call('ZREM', KEYS[1], ids[i])
          redis.call('HDEL', KEYS[2], ids[i])
        end
        return #ids
        """;

    /// <summary>
    /// Writes a schedule if the caller's version is still current, and advances both versions.
    /// </summary>
    /// <remarks>
    /// KEYS: schedules, schedule versions, global version counter.
    /// ARGV: job name, expected version, document.
    /// Returns {1, newVersion} on success and {0, currentVersion} on a conflict.
    /// <para>
    /// An expected version of zero writes unconditionally. That is what a caller who never read the
    /// row has, and refusing it would leave them unable to write at all.
    /// </para>
    /// <para>
    /// The global counter is bumped inside the same script as the write. It is what every instance
    /// polls, so a write visible in the hash but not yet counted is a schedule change no replica
    /// would ever notice.
    /// </para>
    /// </remarks>
    public const string UpsertSchedule =
        """
        local current = tonumber(redis.call('HGET', KEYS[2], ARGV[1]) or '0')
        local expected = tonumber(ARGV[2])
        if expected ~= 0 and expected ~= current then
          return {0, current}
        end
        local advanced = current + 1
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[3])
        redis.call('HSET', KEYS[2], ARGV[1], advanced)
        redis.call('INCR', KEYS[3])
        return {1, advanced}
        """;

    /// <summary>
    /// Writes a token's hash, its expiry TTL and its index entry as one unit.
    /// </summary>
    /// <remarks>
    /// KEYS: token hash, tokens index.
    /// ARGV: id, name, fingerprint, scope, created ticks, subject, by, expires ticks,
    /// expiry unix ms, digest hex.
    /// <para>
    /// Atomic because any part landing without the others is worse than nothing: a hash without
    /// its TTL resolves forever, since the TTL is the only thing enforcing expiry here and the
    /// janitor has no pass to run; a hash without its index entry authenticates but can never be
    /// listed or revoked.
    /// </para>
    /// </remarks>
    public const string CreateToken =
        """
        redis.call('HSET', KEYS[1],
          'id', ARGV[1], 'name', ARGV[2], 'fp', ARGV[3], 'scope', ARGV[4],
          'created', ARGV[5], 'sub', ARGV[6], 'by', ARGV[7], 'expires', ARGV[8])
        if ARGV[8] ~= '0' then
          redis.call('PEXPIREAT', KEYS[1], ARGV[9])
        end
        redis.call('HSET', KEYS[2], ARGV[1], ARGV[10])
        return 1
        """;

    /// <summary>
    /// Writes the pause switches and bumps the schedule version counter.
    /// </summary>
    /// <remarks>
    /// KEYS: pause hash, schedule version counter.
    /// ARGV: scope, reason, set-by, set-at ticks.
    /// Returns the advanced version, which is what the caller publishes so subscribers reload.
    /// <para>
    /// The bump is what makes pause arrive: it rides the change detection schedules already use,
    /// rather than adding a second thing for every instance to poll.
    /// </para>
    /// </remarks>
    public const string SetPause =
        """
        redis.call('HSET', KEYS[1],
          'scope', ARGV[1], 'reason', ARGV[2], 'by', ARGV[3], 'at', ARGV[4])
        return redis.call('INCR', KEYS[2])
        """;
}
