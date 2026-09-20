using System.Globalization;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// THE ONE FOLD FOR THE WINGMAN TAB (the Wingman inspector, phase 2, ruling 6): trace rows in, the finished tab out.
/// Pure - no clock, no store, no settings - so every outcome word is tested by handing it rows.
///
/// EVERYTHING THE TAB SHOWS IS DECIDED HERE: the words for a trigger, an outcome and a cause; the filter group; the
/// colour the stop produced (as RECORDED - it is never worked out again from the verdict, see
/// <see cref="TurnVerdictTraceRowStamp"/>); what was accepted and why an answer was refused; and the carrying-on
/// clock, paired with the expiry that ended it. The Cockpit formats instants and lays out.
///
/// A REFUSED ANSWER NAMES ONE CHECK, verbatim. Validation stops at the first failed check, so the recorded reason
/// names exactly that one; there is no list of passed checks to show and none is invented.
/// </summary>
public static class WingmanStopsFold
{
    public const string GroupAll = "all";
    public const string GroupNeedsYou = "needs-you";
    public const string GroupCalm = "calm";
    public const string GroupFailed = "failed";
    public const string GroupNotAsked = "not-asked";

    /// <summary>What a row reads when its colour was not recorded.</summary>
    public const string NotRecorded = "Not recorded";

    private static readonly (string Key, string Label)[] Groups =
    {
        (GroupAll, "All"),
        (GroupNeedsYou, "Needs you"),
        (GroupCalm, "Calm"),
        (GroupFailed, "Failed"),
        (GroupNotAsked, "Not asked"),
    };

    /// <summary>The tab for one session, from its traces newest first.</summary>
    public static WingmanStopsResponse Fold(string sessionId, IReadOnlyList<TurnVerdictTrace> tracesNewestFirst)
    {
        ArgumentNullException.ThrowIfNull(tracesNewestFirst);

        // An expiry names the carrying-on verdict it replaced. Index both ways so each side can point at the other.
        var byVerdictId = new Dictionary<string, TurnVerdictTrace>(StringComparer.Ordinal);
        var expiryByReplaced = new Dictionary<string, TurnVerdictTrace>(StringComparer.Ordinal);
        // Oldest first, so when one verdict appears on two rows (a judgement and a later reuse) the first row wins
        // the verdict-id index, and the only expiry a verdict can have is found for both.
        foreach (var trace in tracesNewestFirst.Reverse())
        {
            if (trace.VerdictId is { Length: > 0 } id && !byVerdictId.ContainsKey(id))
                byVerdictId[id] = trace;
            if (trace.Outcome == TurnVerdictTraceOutcomes.Expired && trace.ReplacedVerdictId is { Length: > 0 } replaced)
                expiryByReplaced[replaced] = trace;
        }

        var stops = tracesNewestFirst.Select(t => Stop(t, byVerdictId, expiryByReplaced)).ToList();
        return new WingmanStopsResponse
        {
            SessionId = sessionId,
            Stops = stops,
            Groups = Groups.Select(g => new WingmanStopGroupDto
            {
                Key = g.Key,
                Label = g.Label,
                Count = g.Key == GroupAll ? stops.Count : stops.Count(s => s.Group == g.Key),
            }).ToList(),
        };
    }

    private static WingmanStopDto Stop(TurnVerdictTrace t,
        IReadOnlyDictionary<string, TurnVerdictTrace> byVerdictId,
        IReadOnlyDictionary<string, TurnVerdictTrace> expiryByReplaced)
    {
        var verdict = t.Verdict;
        // A verdict the tab shows: one a judge answered and the contract accepted, the same one reused for an unchanged
        // screen, or the one the carrying-on clock wrote. A refused or failed record carries no verdict word.
        var accepted = verdict is { Failed: false }
                       && t.Outcome is TurnVerdictTraceOutcomes.Judged or TurnVerdictTraceOutcomes.Reused
                           or TurnVerdictTraceOutcomes.Expired or TurnVerdictTraceOutcomes.ExpiryUndone;
        var outcomeText = OutcomeText(t.Outcome);
        var rowRecorded = !string.IsNullOrEmpty(t.RowColour);

        return new WingmanStopDto
        {
            TraceId = t.TraceId,
            ObservedAtUtc = t.TurnEndObservedAtUtc,
            RecordedAtUtc = t.RecordedAtUtc,
            Trigger = t.Trigger,
            TriggerText = TriggerText(t.Trigger),
            Outcome = t.Outcome,
            OutcomeText = outcomeText,
            Group = GroupOf(t.Outcome, verdict),
            RowRecorded = rowRecorded,
            RowColour = rowRecorded ? t.RowColour : null,
            RowColourHex = rowRecorded ? SessionColorPalette.HexFor(t.RowColour) : null,
            RowLabel = rowRecorded ? t.RowLabel ?? "" : NotRecorded,
            VerdictWord = accepted ? verdict!.Verdict : null,
            Confidence = accepted ? verdict!.Confidence : null,
            // THE STATE WORD IS WHAT THE READER IS SHOWN, from contract v3: it is what the judge answered and what
            // the row's colour comes from. The confidence clause is kept for a record stored BEFORE v3, which
            // carries one - showing an old reading as it was made rather than re-reading it under the new contract.
            VerdictText = accepted
                ? string.IsNullOrWhiteSpace(verdict!.Confidence)
                    ? StateText(verdict)
                    : $"{StateText(verdict)}, {verdict.Confidence} confidence"
                : "No verdict",
            Strip = new WingmanStopStripDto
            {
                Label = accepted && !string.IsNullOrWhiteSpace(verdict!.Label) ? verdict.Label : outcomeText,
                ReplyText = ReplyText(t),
                OutcomeText = outcomeText,
            },
            Saw = Saw(t),
            Asked = Asked(t),
            Answered = Answered(t),
            Did = Did(t, accepted, byVerdictId, expiryByReplaced),
        };
    }

    // ----------------------------------------------------------------------------------------------- groups

    private static string? GroupOf(string outcome, TurnVerdictDto? verdict) => outcome switch
    {
        TurnVerdictTraceOutcomes.Judged => verdict is { Failed: false } && IsCalm(verdict.Verdict) ? GroupCalm : GroupNeedsYou,
        TurnVerdictTraceOutcomes.Expired => GroupNeedsYou,
        TurnVerdictTraceOutcomes.ExpiryUndone => GroupCalm,
        TurnVerdictTraceOutcomes.Refused or TurnVerdictTraceOutcomes.DidNotAnswer
            or TurnVerdictTraceOutcomes.RateLimited or TurnVerdictTraceOutcomes.Unavailable => GroupFailed,
        TurnVerdictTraceOutcomes.Reused or TurnVerdictTraceOutcomes.Skipped
            or TurnVerdictTraceOutcomes.Cancelled or TurnVerdictTraceOutcomes.Joined => GroupNotAsked,
        _ => null,
    };

    /// <summary>The two verdict words the row fold calls calm (<see cref="SessionOrdering.IsCalmVerdict"/>).</summary>
    private static bool IsCalm(string word)
        => word is SessionOrdering.VerdictFinished or SessionOrdering.VerdictContinuesAlone;

    // ----------------------------------------------------------------------------------------------- words

    /// <summary>The one word a reading answered with, or the stored verdict word for a record so old that the fold
    /// cannot name a state for it - never an empty cell, which reads as a broken row.</summary>
    private static string StateText(TurnVerdictDto verdict)
        => string.IsNullOrWhiteSpace(verdict.State) ? verdict.Verdict : verdict.State;

    internal static string TriggerText(string trigger) => trigger switch
    {
        "turn-end" => "The session stopped",
        "voice" => "Voice mode asked for a narration",
        "sweep" => "The idle sweep found a stop",
        "on-demand" => "A person asked for an explanation",
        "snooze-expiry" => "A snooze ran out",
        "clock" => "The carrying-on clock ran out",
        _ => $"Recorded as \"{trigger}\"",
    };

    internal static string OutcomeText(string outcome) => outcome switch
    {
        TurnVerdictTraceOutcomes.Judged => "Judged",
        TurnVerdictTraceOutcomes.Refused => "The answer was refused",
        TurnVerdictTraceOutcomes.DidNotAnswer => "The judge did not answer",
        TurnVerdictTraceOutcomes.RateLimited => "The judge was rate limited",
        TurnVerdictTraceOutcomes.Unavailable => "The verdict could not be formed",
        TurnVerdictTraceOutcomes.Reused => "The screen was unchanged, so the stored verdict was used again",
        TurnVerdictTraceOutcomes.Expired => "Said it would continue and did not",
        TurnVerdictTraceOutcomes.ExpiryUndone => "Carrying on after all - its own sessions are running",
        TurnVerdictTraceOutcomes.Skipped => "Skipped",
        TurnVerdictTraceOutcomes.Cancelled => "Cancelled",
        TurnVerdictTraceOutcomes.Joined => "Joined a judgement already running",
        _ => $"Recorded by a newer Gateway as \"{outcome}\"",
    };

    internal static string CauseText(string? cause) => cause switch
    {
        null or "" => "No cause was recorded",
        ActivityCauses.Held => "A live session that owns this one holds it",
        ActivityCauses.SessionNotLive => "The session was not on the roster",
        ActivityCauses.BrandNew => "The session had not taken a turn yet",
        ActivityCauses.SessionExit => "The session had exited",
        ActivityCauses.WorkingObservation => "The session was working",
        ActivityCauses.JudgeSwitchOff => "Judging is switched off for this account",
        // Retired on 19 September 2026 and no longer written; kept so an older stored row still reads in plain words.
        ActivityCauses.ReattemptNeverJudges => "A narration retry found no verdict to reuse, and a retry never asks the judge",
        ActivityCauses.RateLimited => "The judge was rate limited",
        ActivityCauses.InFlightCap => "This account already had as many judgements running as it allows",
        ActivityCauses.AlreadyJudging => "A judgement for this session was already running",
        ActivityCauses.Shutdown => "The Gateway was shutting down",
        ActivityCauses.Unknown => "No cause could be decided",
        _ => $"Recorded as \"{cause}\"",
    };

    private static string ReplyText(TurnVerdictTrace t)
    {
        if (t.RawReply is not null && t.ReplySeconds is { } seconds) return $"Answered in {Seconds(seconds)}";
        if (t.RawReply is not null) return "Answered";
        return t.Prompt is null ? "Not asked" : "No answer arrived";
    }

    private static string Seconds(double seconds)
        => seconds.ToString("0.0", CultureInfo.InvariantCulture) + " seconds";

    // ----------------------------------------------------------------------------------------------- blocks

    private static WingmanStopSawDto Saw(TurnVerdictTrace t)
    {
        if (t.Package is not { } p)
        {
            return new WingmanStopSawDto
            {
                Kept = false,
                NotKeptText = t.PackageOmitted
                    ? "Not kept - over the size ceiling"
                    : t.Outcome == TurnVerdictTraceOutcomes.Reused
                        ? "Not read again - the screen was unchanged, so the stored verdict was used"
                        : "Nothing was read for this stop",
            };
        }

        var failure = p.Kind == TurnVerdictPackageKind.TerminalFailure;
        var source = p.SourceText;
        var facts = new List<WingmanStopFactDto>();
        void Fact(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) facts.Add(new WingmanStopFactDto { Name = name, Value = value });
        }

        Fact("What was shown", failure ? "The terminal failure" : "The agent's reply");
        Fact("Session title", p.SessionTitle);
        Fact("First prompt", p.FirstUserPrompt);
        Fact("Previous verdict", p.PreviousVerdictLabel);
        Fact("Agent", p.AgentKind);
        Fact("Why the stop was seen", p.TurnEndCause);
        Fact("How sure the detector was", p.TurnEndConfidence);
        Fact("Wake-ups pending", p.PendingWakeUps?.ToString(CultureInfo.InvariantCulture));
        Fact("Next announced wake-up (UTC)", p.NextScheduledWakeUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        if (p.OwnedSessions is { } owned)
            Fact("Sessions it owns", $"{owned.Working} working, {owned.Stopped} stopped, {owned.NeedYou} need you");
        Fact("Conversation", p.ConversationAvailable ? "Shown" : "Not available");
        Fact("Alternate screen", p.IsAlternateScreen ? "Yes" : "No");
        Fact("Screen hash", p.ScreenHash);

        return new WingmanStopSawDto
        {
            Kept = true,
            ScreenRows = p.ScreenRows.ToList(),
            SourceHeading = string.IsNullOrEmpty(source) ? null : failure ? "Failure text" : "Latest reply",
            SourceText = string.IsNullOrEmpty(source) ? null : source,
            RecentTurns = string.IsNullOrEmpty(p.RecentTurns) ? null : p.RecentTurns,
            Facts = facts,
        };
    }

    private static WingmanStopAskedDto Asked(TurnVerdictTrace t) => new()
    {
        Prompt = t.Prompt,
        Cut = t.PromptTruncated,
        CutText = t.PromptTruncated
            ? $"Cut at {TurnVerdictTraceStore.MaxPromptChars.ToString("N0", CultureInfo.InvariantCulture)} characters - the judge was sent the whole prompt"
            : null,
        NotAskedText = t.Prompt is not null ? null : NotAskedText(t),
    };

    private static WingmanStopAnsweredDto Answered(TurnVerdictTrace t) => new()
    {
        RawReply = t.RawReply,
        Cut = t.RawReplyTruncated,
        CutText = t.RawReplyTruncated
            ? $"Cut at {TurnVerdictTraceStore.MaxRawReplyChars.ToString("N0", CultureInfo.InvariantCulture)} characters - the answer was longer"
            : null,
        ReplySeconds = t.RawReply is null ? null : t.ReplySeconds,
        NoAnswerText = t.RawReply is not null ? null
            : t.Prompt is not null ? NoAnswerText(t.Outcome) : NotAskedText(t),
    };

    private static string NoAnswerText(string outcome) => outcome switch
    {
        TurnVerdictTraceOutcomes.DidNotAnswer => "No answer arrived before the timeout",
        TurnVerdictTraceOutcomes.RateLimited => "The provider said not now",
        TurnVerdictTraceOutcomes.Cancelled => "The session worked before an answer arrived",
        _ => "No answer arrived",
    };

    private static string NotAskedText(TurnVerdictTrace t) => t.Outcome switch
    {
        TurnVerdictTraceOutcomes.Reused => "Not asked - the screen was unchanged, so the stored verdict was used again",
        TurnVerdictTraceOutcomes.Expired => "Not asked - the carrying-on clock wrote this verdict, no model did",
        TurnVerdictTraceOutcomes.ExpiryUndone => "Not asked - the carrying-on clock took its own verdict back, no model did",
        TurnVerdictTraceOutcomes.Joined => "Not asked - this stop joined a judgement already running",
        _ => "The judge was not asked",
    };

    private static WingmanStopDidDto Did(TurnVerdictTrace t, bool accepted,
        IReadOnlyDictionary<string, TurnVerdictTrace> byVerdictId,
        IReadOnlyDictionary<string, TurnVerdictTrace> expiryByReplaced)
    {
        var verdict = t.Verdict;
        var did = new WingmanStopDidDto
        {
            Accepted = accepted && t.Outcome == TurnVerdictTraceOutcomes.Judged,
            Decision = t.Outcome switch
            {
                TurnVerdictTraceOutcomes.Judged when accepted => "Accepted",
                TurnVerdictTraceOutcomes.Reused when accepted => "Used again - accepted when it was first judged",
                TurnVerdictTraceOutcomes.Expired when accepted => "Written by the carrying-on clock",
                TurnVerdictTraceOutcomes.ExpiryUndone when accepted => "Written by the carrying-on clock, taking back its own expiry",
                TurnVerdictTraceOutcomes.Refused => "Refused",
                _ when verdict is { Failed: true } => "No verdict formed",
                _ => "Not asked",
            },
            Reason = verdict is { Failed: true } ? verdict.FailureReason : null,
            VerdictLabel = accepted ? verdict!.Label : null,
            VerdictSummary = accepted ? verdict!.Summary : null,
            CauseText = t.Outcome switch
            {
                TurnVerdictTraceOutcomes.Skipped or TurnVerdictTraceOutcomes.Cancelled or TurnVerdictTraceOutcomes.Joined => CauseText(t.Cause),
                TurnVerdictTraceOutcomes.Unavailable => string.IsNullOrEmpty(t.Cause) ? null : $"It met an exception: {t.Cause}",
                _ => null,
            },
        };

        if (t.Outcome == TurnVerdictTraceOutcomes.Expired && t.ReplacedVerdictId is { Length: > 0 } replaced)
        {
            did.ReplacedText = "Replaced the verdict that said it would carry on by itself";
            did.ReplacedTraceId = byVerdictId.TryGetValue(replaced, out var carryingOn) ? carryingOn.TraceId : null;
            did.Clock = new WingmanStopClockDto
            {
                SetToRunOutAtUtc = carryingOn?.ClockDeadlineUtc,
                RanOutAtUtc = verdict?.JudgedAtUtc,
                Text = "The carrying-on clock ran out with no work since, so the row went back to needing you.",
            };
        }
        else if (accepted && verdict!.Verdict == TurnVerdictVocabulary.ContinuesAlone)
        {
            var ranOut = verdict.VerdictId is { Length: > 0 } id && expiryByReplaced.TryGetValue(id, out var expiry) ? expiry : null;
            did.Clock = new WingmanStopClockDto
            {
                SetToRunOutAtUtc = t.ClockDeadlineUtc,
                RanOutAtUtc = ranOut?.Verdict?.JudgedAtUtc,
                RanOutTraceId = ranOut?.TraceId,
                Text = ClockText(t.ClockDeadlineUtc is not null, ranOut is not null),
            };
        }

        return did;
    }

    private static string ClockText(bool deadlineRecorded, bool ranOut)
    {
        var set = deadlineRecorded
            ? "When it was judged, the clock was set to run out at the time shown. It moves later while the sessions it waits on keep working."
            : "No deadline was recorded: the clock does not run while a session it owns is working, and stops recorded before deadlines were kept have none.";
        var end = ranOut
            ? " It ran out, and the row turned red: \"Said it would continue and did not\"."
            : " It did not run out in the stops shown here.";
        return set + end;
    }
}
