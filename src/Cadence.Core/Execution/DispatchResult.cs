namespace Cadence.Execution;

/// <summary>The outcome of asking the executor to start a run.</summary>
public sealed record DispatchResult
{
    private DispatchResult()
    {
    }

    /// <summary>The run id, when one was started.</summary>
    public Guid? RunId { get; private init; }

    /// <summary>Why no run was started, when it was not.</summary>
    public string? SkipReason { get; private init; }

    /// <summary>True when a run was started.</summary>
    public bool WasStarted => RunId is not null;

    /// <summary>Creates a started result.</summary>
    /// <param name="runId">The run that was started.</param>
    public static DispatchResult Started(Guid runId) => new() { RunId = runId };

    /// <summary>Creates a skipped result.</summary>
    /// <param name="reason">Why nothing was started.</param>
    public static DispatchResult Skipped(string reason) => new() { SkipReason = reason };
}
