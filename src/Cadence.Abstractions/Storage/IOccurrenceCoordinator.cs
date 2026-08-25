namespace Cadence.Storage;

/// <summary>
/// Decides which instance runs a given occurrence.
/// </summary>
/// <remarks>
/// <para>
/// The claim is scoped to a <em>slot</em> — a job name plus a scheduled instant — not to a run's
/// duration. That is the load-bearing decision in Cadence: a lock held for the length of a run
/// needs a TTL longer than the longest possible run, which is unknowable, which forces lease
/// renewal, which fails under GC pause or partition, which requires fencing tokens to recover
/// from safely. Claiming the slot asks one question — "has anyone already started this?" — and
/// once answered it never needs re-answering.
/// </para>
/// <para>
/// So the guarantee is: <b>at most one instance starts a given occurrence</b>. It is not "at most
/// one instance is ever running this job". A run that overruns into the next occurrence can be
/// followed by a second run on another instance; see <see cref="OverlapPolicy"/>.
/// </para>
/// <para>
/// This is also the only seam that knows how claims are won, so swapping the coordination
/// mechanism should never require changes elsewhere.
/// </para>
/// </remarks>
public interface IOccurrenceCoordinator
{
    /// <summary>
    /// Attempts to win the right to execute one occurrence.
    /// </summary>
    /// <param name="jobName">The job's stable name.</param>
    /// <param name="scheduledFor">The occurrence instant, in UTC.</param>
    /// <param name="runId">
    /// The id the run will be recorded under if this claim succeeds, assigned by the caller before
    /// the attempt. It is passed in rather than generated afterwards for two reasons. A store that
    /// records the claim and the run as the same row — which is what removes the window where a
    /// slot is claimed but unrecorded — needs the id at claim time. And it makes the claim
    /// idempotent: an implementation whose write committed but whose acknowledgement was lost can
    /// retry, find the existing row, and tell "someone else won" apart from "this is my own commit,
    /// acknowledged late". Without a caller-assigned id that question has no answer, and answering
    /// it wrongly silently skips a run this instance owns.
    /// </param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>
    /// True when this instance may execute the occurrence; false when another instance already
    /// holds it. Implementations must let genuine infrastructure failures propagate rather than
    /// returning false — swallowing a connection error turns a dead store into a silently
    /// skipped run, which is the worst failure a scheduler can have.
    /// </returns>
    Task<bool> TryClaimAsync(
        string jobName,
        DateTimeOffset scheduledFor,
        Guid runId,
        CancellationToken cancellationToken);
}
