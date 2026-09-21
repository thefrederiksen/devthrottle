using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Triggers;

/// <summary>A trigger's status, decided: <see cref="TriggerStatusKind"/> and the words a client prints.</summary>
public readonly record struct TriggerStatus(string Kind, string Text);

/// <summary>
/// RED, NEVER SILENCE (the Website Business Factory mission, product track). A trigger's status is decided here,
/// on the Gateway, and a client only renders it (CLAUDE.md rule 7):
///
///  - RED "no checks ran" when no check has been recorded within two intervals - of the last check, or, for a
///    trigger never checked, of its creation. A Director that stopped running the check reads as red, because
///    silence is never a quiet night. This is checked first: a newer silence outranks an older failure.
///  - RED "check failed: reason" when the last check broke the check contract, and RED "start failed: reason"
///    when the check counted work but the session could not be started. RED "start outcome unknown - waiting for
///    the session" when the Gateway could not know whether the start worked; the lock is held meanwhile.
///  - RED "session ... has not ended" when the last check counted work and was skipped because the session this
///    trigger started is still alive, and that session was started more than <see cref="LongRunningAfter"/> ago.
///    The one-at-a-time lock leans to never starting twice, so it reads a session as alive for as long as the
///    Gateway's last row for it says so - and a Director that died without unregistering leaves that row saying
///    Working for good. The lock then holds forever; this is what stops it holding forever with an OK face, and
///    the words name the way out: pausing and resuming the trigger releases the lock (<see cref="TriggerStore.Resume"/>).
///  - OK otherwise, including "waiting for the first check" for a new trigger inside its first two intervals.
/// </summary>
public static class TriggerStatusFold
{
    /// <summary>The prefix a failed start's reason carries, so the status can say which half failed.</summary>
    public const string StartFailedPrefix = "the session could not be started: ";

    /// <summary>The prefix the reason of a start whose outcome is not known carries: the Gateway stopped waiting, or
    /// the tunnel dropped mid-command, so the Director may have created the session. The lock is held meanwhile.</summary>
    public const string StartUnknownPrefix = "the session start outcome is not known: ";

    /// <summary>The status while a start whose outcome is not known holds the lock.</summary>
    public const string StartUnknownStatus = "start outcome unknown - waiting for the session";

    /// <summary>How long a started session may hold the lock, while there is work waiting, before the status turns
    /// red. Deliberately generous: a trigger's session doing a long piece of work is normal, and this only has to
    /// catch the lock that never lets go.</summary>
    public static readonly TimeSpan LongRunningAfter = TimeSpan.FromHours(6);

    public static TriggerStatus For(
        DateTime createdUtc, int intervalSeconds, DateTime? lastCheckUtc, string? lastOutcome, string? lastReason,
        string? lastSessionId, DateTime? lastStartedUtc, DateTime nowUtc, bool startOutcomeUnknown = false)
    {
        var silenceLimit = TimeSpan.FromSeconds(intervalSeconds * 2.0);
        var since = lastCheckUtc ?? createdUtc;
        if (nowUtc - since > silenceLimit)
            return new TriggerStatus(TriggerStatusKind.Red, "no checks ran");

        // Held by a start whose outcome is not known: red whatever the checks since have come to, until the session
        // is found and adopted or the lock lapses.
        if (startOutcomeUnknown)
            return new TriggerStatus(TriggerStatusKind.Red, StartUnknownStatus);

        if (lastCheckUtc is null)
            return new TriggerStatus(TriggerStatusKind.Ok, "waiting for the first check");

        if (lastOutcome == TriggerRunOutcome.Failed)
        {
            var reason = string.IsNullOrWhiteSpace(lastReason) ? "no reason was recorded" : lastReason;
            if (reason.StartsWith(StartUnknownPrefix, StringComparison.Ordinal))
                return new TriggerStatus(TriggerStatusKind.Red, StartUnknownStatus);
            return reason.StartsWith(StartFailedPrefix, StringComparison.Ordinal)
                ? new TriggerStatus(TriggerStatusKind.Red, "start failed: " + reason[StartFailedPrefix.Length..])
                : new TriggerStatus(TriggerStatusKind.Red, "check failed: " + reason);
        }

        if (lastOutcome == TriggerRunOutcome.SkippedRunning && lastStartedUtc is { } started)
        {
            var held = nowUtc - started;
            if (held > LongRunningAfter)
                return new TriggerStatus(TriggerStatusKind.Red,
                    $"session {lastSessionId ?? "(unknown)"} has not ended after {(int)held.TotalHours} hours; no new session starts until it does - pause and resume the trigger to release it");
        }

        return new TriggerStatus(TriggerStatusKind.Ok, "OK");
    }
}
