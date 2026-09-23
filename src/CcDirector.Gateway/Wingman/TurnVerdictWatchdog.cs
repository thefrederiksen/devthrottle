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
/// AN EXPIRY IS UNDONE when the session owns a live session again (the owner's ruling, 2026-09-17):
/// <see cref="CarryOnAgain"/> puts a carrying-on verdict back and the clock starts from that moment. Without it
/// the clock could only ever add red - a row that went red while its Worker was inside one long silent command
/// stayed red until the session stopped again, however plainly its sessions were running.
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
    /// "continues-alone" verdict, or a session it owns is still ALIVE.
    ///
    /// THE OWNER'S OWN SESSIONS (owner ruling, 2026-09-15, amended 2026-09-17). A session that owns other sessions
    /// is carrying on while any of them is alive, so its clock does not run then. Once none is, the clock starts
    /// from the moment the last one stopped - the latest last activity across them - when that is later than the
    /// verdict's own starting point: the judging moment plus ten minutes, or the announced wake-up plus two.
    ///
    /// ALIVE, NOT WORKING (the owner's ruling of 2026-09-17: "if a session is running underneath it and there is
    /// no question for the user, the row must not be red"). This used to stop only on
    /// <see cref="OwnedSessionsFacts.Working"/>, and Working comes from terminal silence - the Director flips a
    /// session to waiting after ten quiet seconds, whatever the reason. So a Worker eight minutes into one long
    /// command that printed nothing was indistinguishable from a stopped Worker, and the Architect that owned it
    /// was marked as having broken its word (issue 2992). <see cref="OwnedSessionsFacts.Live"/> answers the
    /// question that was actually being asked: is a session still running underneath this one?
    ///
    /// Working still stops it too. Every working session is a live one, so the two can only agree; the pair is
    /// kept so that a caller which knows only that a session is working - and nothing about liveness - still
    /// stops the clock rather than starting it.
    /// </summary>
    /// <param name="owned">The session's owned sessions, or null when it owns none.</param>
    public static DateTime? DeadlineFor(TurnVerdictDto verdict, OwnedSessionsFacts? owned = null)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (verdict.Failed
            || !string.Equals(verdict.Verdict, TurnVerdictVocabulary.ContinuesAlone, StringComparison.Ordinal))
            return null;
        if (owned is { Working: > 0 } or { Live: > 0 }) return null;

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

    /// <summary>The opening sentence for an expired carrying-on verdict - the whole correction, said once and
    /// plainly. It LEADS the body (the owner's ruling of 2026-09-16: the correction is the part that is news) and
    /// the description follows it, so the news is never delayed behind the body (issue 2243).</summary>
    public const string ExpiredCorrection = "It said it would continue, and it did not.";

    /// <summary>How the description an expiry carries is introduced. NOT "It had said:" - a second sentence
    /// opening "It had said:" after a first opening "It said it would continue" states the same fact twice in
    /// a row, and on 2026-09-23 the owner heard exactly that on every expiry: the stop narrated two times
    /// (issue 2243). "Its last reading was" attributes the description to the Wingman's own earlier reading -
    /// the past moment, not another restatement of the promise - so the quote cannot be heard as the stalled
    /// session still asserting it, which is what the old frame existed to prevent.</summary>
    public const string ExpiredReadingFrame = "Its last reading was:";

    /// <summary>
    /// The verdict stored in place of an expired carrying-on one: "needed-you", the expiry label, the original
    /// receipt, the same stop and screen, judged now. Nothing the original offered as an ANSWER is carried - it
    /// said the session needed nothing, which is exactly what turned out to be wrong.
    ///
    /// ITS DESCRIPTION IS CARRIED, and that distinction is the point. The original verdict held two different
    /// things: a claim about what would happen next (falsified - dropped) and an account of what the session was
    /// DOING (still true - kept). Dropping both left the spoken line as nine fixed words, and on 2026-09-16 the
    /// owner heard exactly that and nothing else: a two-second clip saying a session had stalled, with no way to
    /// tell which one or what it had been doing. Voice narrates the Wingman's output (owner's ruling,
    /// 2026-09-15), so when the Wingman's output is content-free the narration is too.
    ///
    /// THE FACT IS SAID ONCE (issue 2243, the owner's ruling of 2026-09-23). The line this used to produce opened
    /// with "It said it would continue, and it did not." and then replayed the old reading behind "It had said:" -
    /// two sentences in a row opening "It said / It had said" - and the owner heard the stop narrated two times, on
    /// every expiry on the live fleet. The body now opens with the correction once, and the description follows
    /// behind <see cref="ExpiredReadingFrame"/>: attributed to the Wingman's last reading rather than restated as
    /// another sentence about the promise.
    ///
    /// ONE BODY IN ALL THREE FIELDS - the same rule every reading follows from contract v3 (owner ruling,
    /// 2026-09-18): <c>Summary</c>, <c>Spoken</c> and <c>Narration</c> hold the SAME text, written once. This record
    /// is written by the CLOCK and no model is asked, so the words are these; carrying the body in only some of
    /// the three fields would leave the Wingman screen reading one thing and the ear hearing another - the
    /// defect the whole v3 redesign removed.
    /// </summary>
    public static TurnVerdictDto Expire(TurnVerdictDto original, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(original);
        // ONLY A CARRYING-ON VERDICT MAY BE EXPIRED. A finished or needing-you stop is not a broken promise, so
        // the clock never rewrites it - and a caller that hands one here is a defect, not a wording choice. The
        // wrong read of issue 2243 (a finished report judged continues-alone) is fixed where it arises, in the
        // judge's prompt; this guard is the mechanical backstop that the "did not continue" words can only
        // ever be written against a verdict that promised to continue.
        if (original.Failed
            || !string.Equals(original.Verdict, TurnVerdictVocabulary.ContinuesAlone, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Only an accepted carrying-on verdict may be expired; this one is "
                + $"{(original.Failed ? "failed" : $"\"{original.Verdict}\"")} and carries no promise to keep.");
        var description = original.Summary?.Trim();
        var body = string.IsNullOrWhiteSpace(description)
            ? ExpiredCorrection
            : $"{ExpiredCorrection} {ExpiredReadingFrame} {description}";
        return new TurnVerdictDto
        {
            VerdictId = Guid.NewGuid().ToString("N"),
            JudgedAtUtc = Utc(nowUtc),
            TurnEndObservedAtUtc = original.TurnEndObservedAtUtc,
            ScreenHash = original.ScreenHash,
            ScreenReuseHash = original.ScreenReuseHash,
            Model = ClockModel,
            ContractVersion = original.ContractVersion,
            PackageKind = original.PackageKind,
            Failed = false,
            Verdict = TurnVerdictVocabulary.NeededYou,
            Confidence = SessionOrdering.ConfidenceHigh,
            Evidence = original.Evidence,
            Label = ExpiredLabel,
            // ONE BODY, written once, in all three fields - see the note above. The correction opens it and the
            // description follows behind its frame, so the news leads and the fact is said once.
            Summary = body,
            AgentRecommends = null,
            AnswerVia = "reply",
            Menu = null,
            Options = new List<TurnVerdictOptionDto>(),
            Risk = TurnVerdictVocabulary.RiskNone,
            Spoken = body,
            Narration = body,
            NextScheduledWakeUtc = null,
            FinishedKind = null,
        };
    }

    /// <summary>The label on the verdict stored when an expiry is undone.</summary>
    public const string CarryingOnAgainLabel = "Carrying on - its own sessions are running";

    /// <summary>The spoken lead for an undone expiry - the correction, first and plainly, exactly as the
    /// expiry's own correction opens its body.</summary>
    public const string CarryingOnAgainSpokenLead = "It is carrying on: its own sessions are running.";

    /// <summary>What an undone expiry says it is DOING, in the same place the expiry says its own sentence.</summary>
    public const string CarryingOnAgainSummary = "Sessions it owns are running, so it is carrying on by itself.";

    /// <summary>
    /// True when THE CLOCK wrote this verdict - <see cref="Expire"/> did, and no model was asked. The one record
    /// an undo may touch. A judge's own "needed-you" is never undone by the clock: the judge read a screen and
    /// said a person is wanted, and no fact about the sessions underneath it contradicts that.
    /// </summary>
    public static bool IsClockExpiry(TurnVerdictDto verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return !verdict.Failed
               && string.Equals(verdict.Model, ClockModel, StringComparison.Ordinal)
               && string.Equals(verdict.Verdict, TurnVerdictVocabulary.NeededYou, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE EXPIRY UNDONE (the owner's ruling, 2026-09-17). The verdict stored in place of a clock expiry when the
    /// session owns a live session again: "continues-alone", the original receipt, the same stop and screen,
    /// judged NOW - so the carrying-on clock starts again from this moment and not from the stop it is about.
    ///
    /// IT EXISTS BECAUSE AN EXPIRY WAS ONE-WAY. The clock stored a needed-you record and nothing ever reversed
    /// it, so the row stayed red for as long as the session did not stop again - the owner watched an Architect
    /// sit red for twenty-eight minutes with its Manager plainly working. A rule that can only add red is not a
    /// clock, it is a ratchet.
    ///
    /// THE EXPIRY'S OWN SENTENCE IS NOT CARRIED. The receipt is (it is the agent's own words, and still true),
    /// but the expiry's body asserts that the session said it would continue and did not, which is precisely
    /// what the live owned session falsifies - carrying it would leave a purple row stating the case for red.
    /// </summary>
    public static TurnVerdictDto CarryOnAgain(TurnVerdictDto expired, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(expired);
        return new TurnVerdictDto
        {
            VerdictId = Guid.NewGuid().ToString("N"),
            JudgedAtUtc = Utc(nowUtc),
            TurnEndObservedAtUtc = expired.TurnEndObservedAtUtc,
            ScreenHash = expired.ScreenHash,
            ScreenReuseHash = expired.ScreenReuseHash,
            Model = ClockModel,
            ContractVersion = expired.ContractVersion,
            PackageKind = expired.PackageKind,
            Failed = false,
            Verdict = TurnVerdictVocabulary.ContinuesAlone,
            Confidence = SessionOrdering.ConfidenceHigh,
            Evidence = expired.Evidence,
            Label = CarryingOnAgainLabel,
            Summary = CarryingOnAgainSummary,
            AgentRecommends = null,
            AnswerVia = "reply",
            Menu = null,
            Options = new List<TurnVerdictOptionDto>(),
            Risk = TurnVerdictVocabulary.RiskNone,
            Spoken = CarryingOnAgainSpokenLead,
            // One text, read or heard - see the note on Expire above.
            Narration = CarryingOnAgainSpokenLead,
            // The clock runs again from the judging moment, on the ten-minute rule: the announced wake-up the
            // original verdict carried is long past, and an expiry has already dropped it.
            NextScheduledWakeUtc = null,
            FinishedKind = null,
        };
    }

    private static DateTime Utc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
