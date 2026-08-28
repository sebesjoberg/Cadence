namespace Cadence.Api;

/// <summary>The run a trigger started.</summary>
/// <param name="RunId">The run's id.</param>
/// <param name="JobName">The job that was started.</param>
/// <param name="InstanceId">The instance that accepted the trigger.</param>
public sealed record TriggerResponse(Guid RunId, string JobName, string InstanceId);

/// <summary>One job, as the list shows it.</summary>
/// <param name="Name">The job's stable name.</param>
/// <param name="Cron">The effective cron expression, or null for a trigger-only job.</param>
/// <param name="TimeZone">The zone the expression is evaluated in.</param>
/// <param name="Enabled">Whether the scheduler acts on this schedule.</param>
/// <param name="AllowedTriggers">Which triggers the job accepts, as flag names.</param>
/// <param name="NextOccurrenceUtc">The next occurrence, or null when there is none.</param>
/// <param name="LastRun">The most recent run, or null when the job has never run.</param>
public sealed record JobSummaryResponse(
    string Name,
    string? Cron,
    string? TimeZone,
    bool Enabled,
    string AllowedTriggers,
    DateTimeOffset? NextOccurrenceUtc,
    RunSummaryResponse? LastRun);

/// <summary>One job in detail, with its recent runs.</summary>
/// <param name="Job">The job summary.</param>
/// <param name="Overlap">Effective overlap policy.</param>
/// <param name="MaxDuration">Effective maximum duration, or null for no limit.</param>
/// <param name="Settings">Runtime-editable settings handed to the job.</param>
/// <param name="RecentRuns">The most recent runs, newest first.</param>
public sealed record JobDetailResponse(
    JobSummaryResponse Job,
    string? Overlap,
    TimeSpan? MaxDuration,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<RunSummaryResponse> RecentRuns);

/// <summary>One run, without its log.</summary>
/// <param name="RunId">The run's id.</param>
/// <param name="JobName">The job that ran.</param>
/// <param name="Status">The run's status.</param>
/// <param name="Trigger">How the run was started.</param>
/// <param name="InstanceId">The instance that executed it.</param>
/// <param name="ScheduledForUtc">The occurrence it belongs to, or null for a triggered run.</param>
/// <param name="StartedAtUtc">When it began.</param>
/// <param name="CompletedAtUtc">When it ended, or null while running.</param>
/// <param name="Duration">How long it took, or null while running.</param>
/// <param name="Error">Exception detail, for a failed run.</param>
public sealed record RunSummaryResponse(
    Guid RunId,
    string JobName,
    string Status,
    string Trigger,
    string InstanceId,
    DateTimeOffset? ScheduledForUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    TimeSpan? Duration,
    string? Error);

/// <summary>One run, with its log.</summary>
/// <param name="Run">The run.</param>
/// <param name="Log">Progress the job reported, oldest first.</param>
public sealed record RunDetailResponse(RunSummaryResponse Run, IReadOnlyList<LogEntryResponse> Log);

/// <summary>One progress entry.</summary>
/// <param name="TimestampUtc">When the entry was reported.</param>
/// <param name="Message">The message.</param>
public sealed record LogEntryResponse(DateTimeOffset TimestampUtc, string Message);

/// <summary>A page of runs.</summary>
/// <param name="Runs">The runs, newest first.</param>
/// <param name="Limit">The limit actually applied, after clamping.</param>
/// <param name="Offset">The offset applied.</param>
public sealed record RunPageResponse(IReadOnlyList<RunSummaryResponse> Runs, int Limit, int Offset);

/// <summary>The cluster-wide pause switches.</summary>
/// <param name="Scope">What is paused, as flag names.</param>
/// <param name="Reason">Why, as given by whoever set it.</param>
/// <param name="SetBy">Who set it.</param>
/// <param name="SetAtUtc">When it was last set, or null when never.</param>
public sealed record PauseResponse(string Scope, string? Reason, string? SetBy, DateTimeOffset? SetAtUtc);

/// <summary>A request to move the pause switches.</summary>
/// <param name="Scope">What to pause. <c>None</c> resumes everything.</param>
/// <param name="Reason">Free text shown to operators.</param>
public sealed record PauseRequest(string Scope, string? Reason);

/// <summary>
/// What the storage tier reports about itself.
/// </summary>
/// <remarks>
/// An explicit shape rather than the framework's <c>HealthReport</c>, which is a storage-shaped type
/// by another name: returning it would make every field it grows a change to this package's wire
/// contract.
/// </remarks>
/// <param name="Status">The worst status among the checks, or <c>Healthy</c> when there are none.</param>
/// <param name="Checks">One entry per registered storage check.</param>
public sealed record StorageHealthResponse(string Status, IReadOnlyList<StorageCheckResponse> Checks);

/// <summary>One storage check's answer.</summary>
/// <param name="Name">The registration name, such as <c>cadence-sql</c>.</param>
/// <param name="Status">The status the check reported. A store that is down reports <c>Degraded</c>.</param>
/// <param name="Description">What the check has to say, in prose.</param>
/// <param name="Error">The failure's message, or null when the check succeeded.</param>
/// <param name="Duration">How long the check took — the round trip to the store.</param>
public sealed record StorageCheckResponse(
    string Name,
    string Status,
    string? Description,
    string? Error,
    TimeSpan Duration);

/// <summary>A token as administration sees it. Carries no secret and no digest.</summary>
public sealed record ApiTokenResponse(
    Guid Id,
    string Name,
    string Fingerprint,
    string Scope,
    DateTimeOffset CreatedAtUtc,
    string? CreatedBy,
    DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// A newly created token. <paramref name="Token"/> is the only time the secret is ever returned.
/// </summary>
public sealed record ApiTokenCreatedResponse(
    Guid Id,
    string Name,
    string Fingerprint,
    string Scope,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string Token);

/// <summary>What a caller posts to create a token.</summary>
public sealed record ApiTokenRequest(string? Name, string? Scope, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// Who the caller is, as <c>/auth/me</c> reports it. <paramref name="Kind"/> is <c>user</c> or
/// <c>token</c>; a token's name is its audit identity rather than a person's.
/// </summary>
public sealed record AuthMeResponse(string Kind, string? Name, string? Subject, string? Scope);
