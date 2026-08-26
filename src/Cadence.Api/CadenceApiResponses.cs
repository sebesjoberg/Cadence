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
