using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// THE CARRYING-ON CLOCK (the Wingman-on-every-turn mission, ruling 6). A "continues-alone" verdict turns a red
/// row purple on the agent's word that it will carry on by itself. That word has to be kept: when the agent has
/// not worked by the deadline, the row goes red again and says why.
///
/// The deadline is the agent's announced next wake-up plus two minutes when the stop carried one, else ten
/// minutes after the verdict was judged. On expiry a "needed-you" verdict labelled "Said it would continue and
/// did not" is stored IN PLACE OF the carrying-on one - as a new, later record, so the judge's own answer stays
/// in the history the grading reads - with the original receipt carried over.
///
/// A Working transition before the deadline stops the clock by construction: it deletes the session's stored
/// verdicts, so there is no carrying-on verdict left to expire.
///
/// Pure. The clock is passed in, so every rule here is tested at any moment without waiting for one.
/// </summary>
public static class TurnVerdictWatchdog
{
    /// <summary>The label on the verdict stored when the clock runs out.</summary>
    public const string ExpiredLabel = "Said it would continue and did not";

    /// <summary>What is recorded as the answering "model" on that verdict. No model was asked: the clock wrote
    /// it, and the record says so rather than borrowing the judge's name.</summary>
    public const string ClockModel = "carrying-on-clock";

    /// <summary>The grace after an announced next wake-up.</summary>
    public static readonly TimeSpan AfterAnnouncedWake = TimeSpan.FromMinutes(2);

    /// <summary>The whole allowance when no wake-up was announced.</summary>
    public static readonly TimeSpan WithoutAnnouncedWake = TimeSpan.FromMinutes(10);

    /// <summary>
    /// When this verdict's carrying-on clock runs out, or null when no clock is running: it is not an accepted
    /// "continues-alone" verdict, or a session it owns is still working.
    ///
    /// THE OWNER'S OWN SESSIONS (owner ruling, 2026-09-15). A session that owns other sessions is carrying on while
    /// any of them is working, so its clock does not run then. Once none is, the clock starts from the moment the
    /// last one stopped - the latest last activity across them - when that is later than the verdict's own
    /// starting point: the judging moment plus ten minutes, or the announced wake-up plus two.
    /// </summary>
    /// <param name="owned">The session's owned sessions, or null when it owns none.</param>
    public static DateTime? DeadlineFor(TurnVerdictDto verdict, OwnedSessionsFacts? owned = null)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (verdict.Failed
            || !string.Equals(verdict.Verdict, TurnVerdictVocabulary.ContinuesAlone, StringComparison.Ordinal))
            return null;
        if (owned is { Working: > 0 }) return null;

        var lastOwnedStop = owned?.LastActivityAtUtc is { } stopped ? Utc(stopped) : (DateTime?)null;
        return verdict.NextScheduledWakeUtc is { } wake
            ? Later(Utc(wake), lastOwnedStop) + AfterAnnouncedWake
            : Later(Utc(verdict.JudgedAtUtc), lastOwnedStop) + WithoutAnnouncedWake;
    }

    /// <summary>True when this verdict has a running carrying-on clock and <paramref name="nowUtc"/> is at or past
    /// its deadline.</summary>
    public static bool IsExpired(TurnVerdictDto verdict, DateTime nowUtc, OwnedSessionsFacts? owned = null)
        => DeadlineFor(verdict, owned) is { } deadline && Utc(nowUtc) >= deadline;

    private static DateTime Later(DateTime start, DateTime? other)
        => other is { } o && o > start ? o : start;

    /// <summary>
    /// The verdict stored in place of an expired carrying-on one: "needed-you", the expiry label, the original
    /// receipt, the same stop and screen, judged now. Nothing the original offered as an answer is carried - it
    /// said the session needed nothing, which is exactly what turned out to be wrong.
    /// </summary>
    public static TurnVerdictDto Expire(TurnVerdictDto original, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(original);
        return new TurnVerdictDto
        {
            VerdictId = Guid.NewGuid().ToString("N"),
            JudgedAtUtc = Utc(nowUtc),
            TurnEndObservedAtUtc = original.TurnEndObservedAtUtc,
            ScreenHash = original.ScreenHash,
            Model = ClockModel,
            ContractVersion = original.ContractVersion,
            PackageKind = original.PackageKind,
            Failed = false,
            Verdict = TurnVerdictVocabulary.NeededYou,
            Confidence = SessionOrdering.ConfidenceHigh,
            Evidence = original.Evidence,
            Label = ExpiredLabel,
            Summary = "It said it would carry on by itself, and it has not worked since.",
            AgentRecommends = null,
            AnswerVia = "reply",
            Menu = null,
            Options = new List<TurnVerdictOptionDto>(),
            Risk = TurnVerdictVocabulary.RiskNone,
            Spoken = "It said it would continue, and it did not.",
            NextScheduledWakeUtc = null,
            FinishedKind = null,
        };
    }

    private static DateTime Utc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
