using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// A SCHEDULED RUN CANNOT CLOSE ITSELF WITH SESSION DONE WITHOUT REPORTING (6 November 2026). "Reported" means one command,
/// <c>cc-devthrottle run result ok|problem</c>. <c>cc-devthrottle session done</c> only flags the session for deletion and
/// records nothing on the run, so a run that closed that way was recorded "did not report" even when it had done all its
/// work: two seats on devlinux did exactly that, obeying a schedule seed that said "close this session with
/// cc-devthrottle session done as your last act".
///
/// So the Gateway refuses that one request: a session asking for ITS OWN deletion while its own scheduled run is still
/// waiting on its report. Nothing is flagged, and the refusal names the command that does both jobs - reporting ok closes
/// the session by itself. A deletion asked for by a person, or by another session, is never refused here: they are not
/// the run, and stopping a run that went wrong must stay one step. A session stopping itself through
/// <c>cc-devthrottle session stop</c> is NOT refused here: that door asks for a reason, is stamped "closed itself", and the
/// run is still recorded "did not report", so the owner hears of it.
///
/// The run is found by the run store's lookup by session (<see cref="CronRunHistoryStore.FindBySession"/>), so every
/// kind of run that is recorded as a pending run of a session is covered by the same rule.
/// </summary>
public static class ScheduledRunDeletionGuard
{
    /// <summary>The words a scheduled run's own session is refused with, while its run has not reported.</summary>
    public const string NotReported =
        "This session is a scheduled run that has not reported. " +
        "Run `cc-devthrottle run result ok` (it closes this session for you) or " +
        "`cc-devthrottle run result problem \"<one line why>\"`. Nothing was flagged for deletion.";

    /// <summary>
    /// The refusal for a request to delete <paramref name="targetSessionId"/>, or null when the request may go ahead.
    /// Refused only when the calling session IS the target and its own run (read through
    /// <paramref name="runOfSession"/>) is still pending. <paramref name="runOfSession"/> is asked only in that case,
    /// so no other caller pays for the lookup.
    /// </summary>
    public static string? RefusalFor(string? callingSessionId, string targetSessionId, Func<string, CronRunRecord?> runOfSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSessionId);
        ArgumentNullException.ThrowIfNull(runOfSession);

        if (string.IsNullOrWhiteSpace(callingSessionId))
            return null;
        if (!string.Equals(callingSessionId.Trim(), targetSessionId.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;

        var run = runOfSession(targetSessionId);
        if (run is null || run.Result != CronRunResults.Pending)
            return null;

        FileLog.Write($"[ScheduledRunDeletionGuard] RefusalFor: session={targetSessionId} asked to delete itself while run {run.RunId} has not reported; refused");
        return NotReported;
    }
}
