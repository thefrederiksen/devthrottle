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
///    when the check counted work but the session could not be started.
///  - OK otherwise, including "waiting for the first check" for a new trigger inside its first two intervals.
/// </summary>
public static class TriggerStatusFold
{
    /// <summary>The prefix a failed start's reason carries, so the status can say which half failed.</summary>
    public const string StartFailedPrefix = "the session could not be started: ";

    public static TriggerStatus For(
        DateTime createdUtc, int intervalSeconds, DateTime? lastCheckUtc, string? lastOutcome, string? lastReason,
        DateTime nowUtc)
    {
        var silenceLimit = TimeSpan.FromSeconds(intervalSeconds * 2.0);
        var since = lastCheckUtc ?? createdUtc;
        if (nowUtc - since > silenceLimit)
            return new TriggerStatus(TriggerStatusKind.Red, "no checks ran");

        if (lastCheckUtc is null)
            return new TriggerStatus(TriggerStatusKind.Ok, "waiting for the first check");

        if (lastOutcome == TriggerRunOutcome.Failed)
        {
            var reason = string.IsNullOrWhiteSpace(lastReason) ? "no reason was recorded" : lastReason;
            return reason.StartsWith(StartFailedPrefix, StringComparison.Ordinal)
                ? new TriggerStatus(TriggerStatusKind.Red, "start failed: " + reason[StartFailedPrefix.Length..])
                : new TriggerStatus(TriggerStatusKind.Red, "check failed: " + reason);
        }

        return new TriggerStatus(TriggerStatusKind.Ok, "OK");
    }
}
