using System;
using System.Linq;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="SessionOrdering"/> - the shared client-side policy the Cockpit's
/// session rail uses for desktop-stable ordering (tree view) and needs-you-first triage.
/// </summary>
public sealed class SessionOrderingTests
{
    private static SessionDto S(string id, int sortOrder = 0, string color = "blue",
        bool onHold = false, DateTime createdAt = default, string briefingState = "None") => new()
    {
        SessionId = id,
        SortOrder = sortOrder,
        StatusColor = color,
        // Issue #1177 (Phase 2): the fold now derives the base color from the RAW ActivityState, not the
        // cooked StatusColor. These cases set `color` (a cooked color) but historically omitted the raw
        // activity that color implies; supply it here as the INVERSE of ColorFromActivityState so every
        // asserted output color stays byte-identical while the fold reads raw facts. Only "red"/"blue"
        // are mapped (the colors these cases assert through the base); grey/OnHold win before the base,
        // so those are left with the default state.
        ActivityState = color switch
        {
            "red" => "WaitingForInput",
            "blue" => "Working",
            _ => "",
        },
        OnHold = onHold,
        BriefingState = briefingState,
        CreatedAt = createdAt == default ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : createdAt,
    };

    [Fact]
    public void InDesktopOrder_SortsBySortOrder_Ascending()
    {
        var sessions = new[] { S("c", 2), S("a", 0), S("b", 1) };

        var ordered = SessionOrdering.InDesktopOrder(sessions);

        Assert.Equal(new[] { "a", "b", "c" }, ordered.Select(s => s.SessionId));
    }

    [Fact]
    public void InDesktopOrder_EqualSortOrder_FallsBackToCreatedAt()
    {
        // Every session reports SortOrder 0 (e.g. a Director predating the field): the
        // CreatedAt tie-break must give a deterministic, stable order.
        var t = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        var sessions = new[]
        {
            S("late",  0, createdAt: t.AddMinutes(10)),
            S("early", 0, createdAt: t),
            S("mid",   0, createdAt: t.AddMinutes(5)),
        };

        var ordered = SessionOrdering.InDesktopOrder(sessions);

        Assert.Equal(new[] { "early", "mid", "late" }, ordered.Select(s => s.SessionId));
    }

    [Fact]
    public void InDesktopOrder_DoesNotMutateInput()
    {
        var sessions = new[] { S("c", 2), S("a", 0) };

        _ = SessionOrdering.InDesktopOrder(sessions);

        Assert.Equal(new[] { "c", "a" }, sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void Classify_Red_NotHeld_IsNeedsYou()
    {
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou,
            SessionOrdering.Classify(S("x", color: "red")));
    }

    [Fact]
    public void Classify_NonRed_NotHeld_IsActive()
    {
        Assert.Equal(SessionOrdering.TriageBucket.Active,
            SessionOrdering.Classify(S("x", color: "blue")));
    }

    [Fact]
    public void Classify_OnHold_TakesPrecedenceOverRed()
    {
        // A parked session sinks to the bottom even when it would otherwise be "needs you".
        Assert.Equal(SessionOrdering.TriageBucket.OnHold,
            SessionOrdering.Classify(S("x", color: "red", onHold: true)));
    }

    // ----- effective color while the wingman is reading (issue #196) -----

    [Fact]
    public void EffectiveColor_RedWhileBriefing_IsYellow()
    {
        // The Director stamps raw red at turn-end (it no longer knows about briefing,
        // #187); the Gateway stamps BriefingState=Briefing. The ONE presented color
        // must be yellow - never a red dot next to a "wingman reading..." chip.
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(S("x", color: "red", briefingState: "Briefing")));
    }

    [Fact]
    public void EffectiveColor_RedAfterBriefLands_IsRed()
    {
        Assert.Equal("red", SessionOrdering.EffectiveColor(S("x", color: "red", briefingState: "Briefed")));
    }

    [Fact]
    public void EffectiveColor_BlueWhileBriefing_StaysBlue()
    {
        // A NEW turn already running: raw activity wins, the stale in-flight brief
        // must not paint a working session yellow.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(S("x", color: "blue", briefingState: "Briefing")));
    }

    [Fact]
    public void IsBriefing_OnlyWhenBriefingAndRed()
    {
        Assert.True(SessionOrdering.IsBriefing(S("x", color: "red", briefingState: "Briefing")));
        Assert.False(SessionOrdering.IsBriefing(S("x", color: "blue", briefingState: "Briefing")));
        Assert.False(SessionOrdering.IsBriefing(S("x", color: "red", briefingState: "Briefed")));
        Assert.False(SessionOrdering.IsBriefing(S("x", color: "red", briefingState: "None")));
    }

    // ----- effective color while a user-requested deep dive runs (issue #217) -----
    //
    // THESE TESTS ARE GONE, AND THE RULE THEY COVERED IS GONE (defect 11, 2026-07-14). There were three
    // here: IsExplaining_AnyRawColor_TrueWhileExplaining, EffectiveColor_WhileExplaining_IsOrange_
    // ONLYWhenNotWorking, and (further down) EffectiveColor_GatewayDeepDiveExplaining_IsOrange_NotYellow
    // and StateLabel_GatewayDeepDive_IsExplaining. All of them fed BriefingState = "Explaining" to the fold
    // by hand and asserted orange.
    //
    // #217's roster orange has never once worked. It never fired, in any release: SessionDto.BriefingState
    // is stamped only from the Director's BriefingState enum (None / Briefing / Briefed / Failed), so
    // "Explaining" is not a producible value - and the Gateway's deep-dive request route is switched off at
    // the composition root (requestExplainAsync: null -> 503). The tests were green because they INJECTED a
    // value production cannot emit. That is this mission's most common bug shape wearing a test's clothes:
    // live consumer, no producer, a green test supplying the missing input.
    //
    // NOTE WHAT IS NOT GONE. The Director's LEGACY auto-explain yellow is a separate, WORKING feature on a
    // different field (SessionDto.IsAutoExplaining), and its test
    // (EffectiveColor_AutoExplainingAtTurnEnd_IsYellow_FromRawFact) is untouched below - it now also carries
    // the "distinct from the deep dive" note that used to live on the deleted orange test.
    //
    // There is deliberately NO replacement regression test. A test cannot fail on purpose for an unreachable
    // rule: there is no input that reaches it, which is the entire finding. The honest artefacts are this
    // note, the deletion, and the two law theory rows below (OverlaysThatMustNotBeatWorking / the
    // all-overlays-at-once case) which still pass "Explaining" as an inert input and still assert blue.

    // ---------- Defect 16: ActivityState is read case-INSENSITIVELY, everywhere in the fold ----------

    [Theory]
    [InlineData("working", "blue")]
    [InlineData("WORKING", "blue")]
    [InlineData("starting", "blue")]
    [InlineData("waitingforinput", "red")]
    [InlineData("WAITINGFORINPUT", "red")]
    [InlineData("waitingforperm", "red")]
    [InlineData("idle", "red")]
    [InlineData("exited", "grey")]
    public void EffectiveColor_ActivityState_IsCaseInsensitive(string activityState, string expected)
    {
        // Defect 16: RawActivityColor was a C# constant-pattern switch, which is ORDINAL and
        // case-SENSITIVE, while IsWorking / IsAtTurnEnd / IsVoicePreparing / the role rule all compared the
        // SAME field case-INSENSITIVELY - two readings of one field in one file, six lines apart inside
        // IsVoicePreparing.
        //
        // HONEST SCOPE: this fixes no observed bug. The only producer of ActivityState is the Director's
        // ToDto (`s.ActivityState.ToString()` over the enum), which emits exact PascalCase, so the
        // divergence cannot fire today. This pins the trap shut rather than repairing a live failure.
        //
        // The "waitingforinput" -> "red" rows are the ones that matter: under the old switch they returned
        // "unknown" (label "Idle"), silently EATING a red on a session that needs the human, while the
        // case-insensitive turn-end overlays happily fired around it.
        Assert.Equal(expected, SessionOrdering.EffectiveColor(new SessionDto
        {
            SessionId = "x",
            ActivityState = activityState,
        }));
    }

    [Fact]
    public void IsRawRed_IsCaseInsensitive_AndIgnoresTheCookedColor()
    {
        // IsRawRed is THE fold-owned answer to "is this session red?", and it is public so the Gateway's
        // enrichment pipeline can ask it BEFORE the fold runs (it replaced a gate on the Director's cooked
        // StatusColor - see the voice-window test in the aggregation suite).
        Assert.True(SessionOrdering.IsRawRed(new SessionDto { SessionId = "x", ActivityState = "waitingforinput" }));
        Assert.True(SessionOrdering.IsRawRed(new SessionDto { SessionId = "x", ActivityState = "Idle" }));
        Assert.False(SessionOrdering.IsRawRed(new SessionDto { SessionId = "x", ActivityState = "Working" }));

        // The cooked colour gets NO vote, in either direction. A working session whose Director cooked a
        // sticky red (TransientErrorAutoResume does exactly this) is NOT raw red...
        Assert.False(SessionOrdering.IsRawRed(new SessionDto
        {
            SessionId = "x",
            ActivityState = "Working",
            StatusColor = "red",
        }));
        // ...and a waiting session whose cooked colour is stale/absent still IS.
        Assert.True(SessionOrdering.IsRawRed(new SessionDto
        {
            SessionId = "x",
            ActivityState = "WaitingForInput",
            StatusColor = "",
        }));
    }

    // ---------- Transcribing "orange while a dictated utterance is being transcribed" ----------

    [Fact]
    public void EffectiveColor_WhileTranscribing_IsOrange_RegardlessOfRawColor()
    {
        // The phone released the Speak dialog and the audio is uploading/transcribing in the
        // background: orange no matter what the session is doing underneath, so nobody else grabs it.
        Assert.Equal("orange", SessionOrdering.EffectiveColor(new SessionDto { SessionId = "x", StatusColor = "blue", Transcribing = true }));
        Assert.Equal("orange", SessionOrdering.EffectiveColor(new SessionDto { SessionId = "x", StatusColor = "green", Transcribing = true }));
    }

    [Fact]
    public void EffectiveColor_OnHold_WinsOverTranscribing()
    {
        // A parked session stays grey; transcribing does not override the user's explicit hold.
        Assert.Equal("grey", SessionOrdering.EffectiveColor(new SessionDto { SessionId = "x", StatusColor = "blue", OnHold = true, Transcribing = true }));
    }

    [Fact]
    public void Classify_Transcribing_IsActive_NotNeedsYou()
    {
        // Orange is not red, so a transcribing session sits in Active - it must never jump into the
        // needs-you group just because it is busy transcribing.
        Assert.Equal(SessionOrdering.TriageBucket.Active,
            SessionOrdering.Classify(new SessionDto { SessionId = "x", StatusColor = "red", Transcribing = true }));
    }

    // ---------- Voice-mode "yellow until audio ready" (issue #553) ----------

    private static SessionDto Voice(string color, string activityState, bool generating, bool audioReady) => new()
    {
        SessionId = "v",
        StatusColor = color,
        ActivityState = activityState,
        VoiceMode = true,
        VoiceGenerating = generating,
        VoiceAudioReady = audioReady,
    };

    [Fact]
    public void EffectiveColor_VoiceWaiting_NoAudio_NotGenerating_IsYellow_UntilAudioReady()
    {
        // Owner's ruling (2026-07-19): a voice-mode waiting session with no audio yet is YELLOW
        // "preparing voice", held across the gaps between generation attempts - NOT red. In voice
        // mode the user must never see red until the voice is available. (This deliberately reverses
        // the 2026-07-08 "red when not generating" narrowing; the wedge that change feared is now a
        // voice-reliability concern, not a color rule - see IsVoicePreparing's summary.)
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(
            Voice("red", "WaitingForInput", generating: false, audioReady: false)));
    }

    [Fact]
    public void EffectiveColor_VoiceWaiting_Generating_IsYellow_EvenWithStaleAudio()
    {
        // A new turn is being summarized: yellow regardless of any stale cached audio.
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(
            Voice("red", "WaitingForPerm", generating: true, audioReady: true)));
    }

    [Fact]
    public void EffectiveColor_VoiceWaiting_AudioReady_IsRed()
    {
        // Playable audio exists and nothing is generating -> red "needs you".
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Voice("red", "WaitingForInput", generating: false, audioReady: true)));
    }

    [Fact]
    public void EffectiveColor_VoiceWorking_StaysBlue()
    {
        // While the agent is working the dot stays blue even in voice mode with no audio.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            Voice("blue", "Working", generating: true, audioReady: false)));
    }

    [Fact]
    public void EffectiveColor_NonVoiceWaiting_NoAudio_StaysRed()
    {
        // A non-voice session is untouched by the voice rule: waiting + red stays red.
        var s = Voice("red", "WaitingForInput", generating: true, audioReady: false);
        s.VoiceMode = false;
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
    }

    [Fact]
    public void Classify_VoiceWaiting_StillGenerating_IsActive_NotNeedsYou()
    {
        // While voice is ACTIVELY generating the session must not sit in NEEDS YOU (it is preparing).
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(
            Voice("red", "WaitingForInput", generating: true, audioReady: false)));
    }

    [Fact]
    public void Classify_VoiceWaiting_NoAudio_NotGenerating_IsActive_PreparingVoice()
    {
        // Owner's ruling (2026-07-19): a voice-mode waiting session with no audio yet is "preparing
        // voice" (yellow), so it triages Active, not NeedsYou - there is nothing to act on until the
        // voice is ready. It only enters NeedsYou once audio is ready (see the AudioReady test below).
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(
            Voice("red", "WaitingForInput", generating: false, audioReady: false)));
    }

    [Fact]
    public void Classify_VoiceWaiting_AudioReady_IsNeedsYou()
    {
        // Voice is ready - now there is something to play and act on, so the session genuinely needs
        // the user and surfaces in NEEDS YOU. This is the terminal exit from the yellow hold.
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(
            Voice("red", "WaitingForInput", generating: false, audioReady: true)));
    }

    // Classify_RedWhileExplaining_IsActive_NotNeedsYou lived here and is deleted with the rule it covered
    // (defect 11) - it asserted a triage bucket for a BriefingState the wire cannot carry. The briefing twin
    // below covers the same #196 rule for the state that IS producible.

    [Fact]
    public void Classify_RedWhileBriefing_IsActive_NotNeedsYou()
    {
        // The triage regression in issue #196: a mid-brief session must NOT enter the
        // NEEDS YOU bucket (and then flop back out when the brief lands or refutes).
        Assert.Equal(SessionOrdering.TriageBucket.Active,
            SessionOrdering.Classify(S("x", color: "red", briefingState: "Briefing")));
    }

    [Fact]
    public void Classify_RedAfterBriefLands_IsNeedsYou()
    {
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou,
            SessionOrdering.Classify(S("x", color: "red", briefingState: "Briefed")));
    }

    [Fact]
    public void Classify_OnHold_TakesPrecedenceOverBriefing()
    {
        Assert.Equal(SessionOrdering.TriageBucket.OnHold,
            SessionOrdering.Classify(S("x", color: "red", briefingState: "Briefing", onHold: true)));
    }

    [Fact]
    public void InBucket_FiltersToBucket_AndKeepsDesktopOrder()
    {
        var sessions = new[]
        {
            S("active1", sortOrder: 1, color: "blue"),
            S("needs1",  sortOrder: 3, color: "red"),
            S("held1",   sortOrder: 0, color: "red", onHold: true),
            S("needs0",  sortOrder: 2, color: "red"),
        };

        var needs = SessionOrdering.InBucket(sessions, SessionOrdering.TriageBucket.NeedsYou);
        var active = SessionOrdering.InBucket(sessions, SessionOrdering.TriageBucket.OnHold);

        // Only the two non-held red sessions, in SortOrder order (2 before 3).
        Assert.Equal(new[] { "needs0", "needs1" }, needs.Select(s => s.SessionId));
        // The held-red session lands in OnHold, not NeedsYou.
        Assert.Equal(new[] { "held1" }, active.Select(s => s.SessionId));
    }

    // ===== Issue #1177 (Phase 2): the Gateway fold now computes EVERY color from RAW facts (migrated
    // from SessionStatusWingmanTests, rebuilt against the raw-fact inputs the Director reports). =====

    /// <summary>A session built from the RAW facts the Director reports (no cooked StatusColor).</summary>
    private static SessionDto Raw(string activityState, bool wingmanEnabled = false, bool brandNew = false,
        bool backgroundRunning = false, bool controlled = false, string? controllerId = null,
        bool transcribing = false, bool autoExplaining = false, string briefingState = "None",
        string? sessionRole = null, string? originKind = null, bool hasLiveSupervisor = false) => new()
    {
        SessionId = "raw",
        ActivityState = activityState,
        // DEFAULTS FALSE - nobody is holding this session - which is the raw shape of every case in this
        // file except the few that say otherwise. It is the resolver's answer in production
        // (FleetRoleResolver stamps it across the whole fleet); here it is stated per case, because what
        // these tests are about is the FOLD, and the resolver has its own guard.
        HasLiveSupervisor = hasLiveSupervisor,
        WingmanEnabled = wingmanEnabled,
        IsBrandNew = brandNew,
        IsBackgroundRunning = backgroundRunning,
        IsControlled = controlled,
        ControllerSessionId = controllerId,
        IsTranscribing = transcribing,
        IsAutoExplaining = autoExplaining,
        BriefingState = briefingState,
        SessionRole = sessionRole,
        OriginKind = originKind,
        // StatusColor deliberately left at its default "unknown" to PROVE the fold never reads it.
    };

    [Fact]
    public void EffectiveColor_Working_IsBlue_FromRawFacts()
    {
        Assert.Equal("blue", SessionOrdering.EffectiveColor(Raw("Working")));
    }

    [Fact]
    public void EffectiveColor_Waiting_IsRed_FromRawFacts()
    {
        Assert.Equal("red", SessionOrdering.EffectiveColor(Raw("WaitingForInput")));
        Assert.Equal("red", SessionOrdering.EffectiveColor(Raw("WaitingForPerm")));
        Assert.Equal("red", SessionOrdering.EffectiveColor(Raw("Idle")));
    }

    [Fact]
    public void EffectiveColor_Exited_IsGrey()
    {
        // Phase 2.3 (owner-approved behavior change): an exited session shows the SAME grey string as an
        // OnHold session, so clients render the two identically. No longer byte-identical for exited.
        Assert.Equal("grey", SessionOrdering.EffectiveColor(Raw("Exited")));
    }

    [Fact]
    public void EffectiveColor_BrandNewAtTurnEnd_IsGreen_FromRawFacts()
    {
        // A brand-new session sitting at its prompt is "ready" (green), not red "needs you".
        Assert.Equal("green", SessionOrdering.EffectiveColor(Raw("WaitingForInput", brandNew: true)));
    }

    [Fact]
    public void EffectiveColor_BrandNewWhileWorking_IsBlue_NotGreen()
    {
        // Green only applies at a turn-end; a brand-new session that is working is blue.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(Raw("Working", brandNew: true)));
    }

    // ===================================================================================
    // A BRAND-NEW SESSION IS GREEN **IN VOICE MODE TOO** (owner's ruling, 2026-07-27).
    //
    // The test above proves green only for the voice-OFF case, because the Raw(...) helper
    // leaves VoiceMode false. That blind spot hid a live defect: green lives in BaseColor,
    // the LAST arm of EffectiveColor, BELOW IsVoicePreparing - so with voice mode ON every
    // freshly-spawned session folded to yellow "Preparing voice" instead, and STAYED yellow,
    // because a session that has taken no turn has no reply to narrate and so never gets
    // audio. `|| !VoiceAudioReady` then holds forever.
    //
    // These are the voice-mode twins. If one goes yellow, the brand-new guard in
    // IsVoicePreparing was removed or an arm was added above the green.
    // ===================================================================================

    /// <summary>A brand-new session as it actually arrives with the gateway in voice mode: voice on,
    /// parked at its first prompt, and (necessarily) with no audio ever generated.</summary>
    private static SessionDto BrandNewVoiceSession(string activityState = "WaitingForInput")
    {
        var s = Raw(activityState, brandNew: true);
        s.VoiceMode = true;
        s.VoiceGenerating = false;
        s.VoiceAudioReady = false;
        return s;
    }

    [Fact]
    public void EffectiveColor_BrandNewInVoiceMode_IsGreen_NotPreparingVoiceYellow()
    {
        Assert.Equal("green", SessionOrdering.EffectiveColor(BrandNewVoiceSession()));
    }

    [Fact]
    public void StateLabel_BrandNewInVoiceMode_IsReady_NotPreparingVoice()
    {
        // The label folds from the same inputs in the same order, so it must move with the dot.
        Assert.Equal("Ready", SessionOrdering.StateLabel(BrandNewVoiceSession()));
    }

    [Fact]
    public void IsVoicePreparing_BrandNew_IsFalse()
    {
        // The rule itself: there is no turn to narrate, so nothing is being prepared.
        Assert.False(SessionOrdering.IsVoicePreparing(BrandNewVoiceSession()));
    }

    [Fact]
    public void EffectiveColor_BrandNewInVoiceMode_WhileGenerating_IsStillGreen()
    {
        // Even if a generation is somehow marked in flight for a session that has taken no turn,
        // "brand new" is the truer statement - it has produced nothing to read back.
        var s = BrandNewVoiceSession();
        s.VoiceGenerating = true;
        Assert.Equal("green", SessionOrdering.EffectiveColor(s));
    }

    [Fact]
    public void EffectiveColor_VoiceModeAfterFirstTurn_StillHoldsYellow()
    {
        // The NEGATIVE control: the brand-new guard must not disarm the voice hold generally.
        // Once the session has taken a turn (IsBrandNew false), a waiting voice session with no
        // audio yet is still yellow "Preparing voice" - the behaviour issue #553 asked for.
        var s = BrandNewVoiceSession();
        s.IsBrandNew = false;
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Preparing voice", SessionOrdering.StateLabel(s));
    }

    // ---------- The voice hold's WORDS tell the truth (issue #2576) ----------

    /// <summary>A waiting voice session with no audio, carrying the Gateway's folded voice verdict.</summary>
    private static SessionDto VoiceHoldingSession(string kind, string label)
    {
        var s = BrandNewVoiceSession();
        s.IsBrandNew = false;
        s.VoiceDisplay = new VoiceDisplay { Kind = kind, Label = label };
        return s;
    }

    /// <summary>
    /// THE DEFECT: "Preparing voice" was the only thing the label could say about a session with no audio,
    /// so a state that would NEVER become audio wore it indefinitely. On 11 August a session sat on it for
    /// 48 minutes while the Gateway's own verdict on the same row read "Nothing to read aloud".
    ///
    /// The dot is unchanged - the hold is still yellow, by the 2026-07-19 ruling. Only the words move, and
    /// they are the words VoiceDisplayFold already chose, so there is one answer and not two.
    /// </summary>
    [Theory]
    [InlineData("nothingToNarrate", "Nothing to read aloud")]
    [InlineData("serviceDown", "Voice service down")]
    [InlineData("blocked", "Voice needs credit")]
    [InlineData("retrying", "Voice on its way")]
    [InlineData("notReady", "No narration yet")]
    public void StateLabel_VoiceHoldWithAReason_SaysTheReason_NotPreparingVoice(string kind, string label)
    {
        var s = VoiceHoldingSession(kind, label);
        Assert.True(SessionOrdering.IsVoicePreparing(s));           // the hold itself is untouched...
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(s));  // ...and so is the dot
        Assert.Equal(label, SessionOrdering.StateLabel(s));         // only the words changed
    }

    [Fact]
    public void StateLabel_VoiceHoldWhileGenuinelyPreparing_StillSaysPreparingVoice()
    {
        // The positive case the words were always right about: something IS being made. With no wait worth
        // reporting (under a minute), the label is exactly what it always was.
        var s = VoiceHoldingSession("preparing", "Voice on its way");
        s.VoiceGenerating = true;
        Assert.Equal("Preparing voice", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void StateLabel_PreparingWithAWaitWorthReporting_SaysHowLong()
    {
        // Issue #2576. "Preparing voice" reads identically at two seconds and at forty-eight minutes, and a
        // session really did sit at forty-eight with nobody able to tell. The Gateway has already turned
        // its own clock into words; the rail renders them.
        var s = VoiceHoldingSession("preparing", "Voice on its way");
        s.VoiceGenerating = true;
        s.VoiceDisplay!.WaitedLabel = "44m";
        Assert.Equal("Preparing voice (44m)", SessionOrdering.StateLabel(s));
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(s));   // the dot is untouched
    }

    [Fact]
    public void StateLabel_GaveUp_ReachesTheRailWithItsOwnWords()
    {
        // The terminal verdict needs no new arm here - the deny-list added for the earlier half of #2576
        // renders any kind that is not preparing/ready/off/working.
        //
        // HONEST NOTE: this passes on the parent commit too, because that deny-list already renders any
        // unknown kind. It is a REGRESSION GUARD on that behaviour, not evidence for this change - review
        // caught it being presented as the latter. What this change actually adds to the rail is the
        // preparing-with-a-wait case asserted above, which does fail without it.
        var s = VoiceHoldingSession("gaveUp", "Voice did not arrive after 48m");
        Assert.Equal("Voice did not arrive after 48m", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void StateLabel_VoiceHoldWithNoVerdictOnTheRow_FallsBackToPreparingVoice()
    {
        // A row carrying no folded verdict (an older client, or a surface that does not stamp one) keeps the
        // existing words. The label renders a verdict; it never invents one.
        var s = BrandNewVoiceSession();
        s.IsBrandNew = false;
        s.VoiceDisplay = null;
        Assert.Equal("Preparing voice", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void StateLabel_VoiceHoldWithAVerdictKindAddedLater_RendersItsWords()
    {
        // A verdict added to VoiceDisplayFold later reaches the rail WITHOUT a second edit here. An earlier
        // version of this rule kept an allow-list of known-good kinds and defaulted everything else back to
        // "Preparing voice", which meant adding a voice state to the fold and having the rail quietly go on
        // claiming a narration was in flight about it - a second rule wearing the shape of a default.
        var s = VoiceHoldingSession("somethingAddedLater", "Some new words");
        Assert.Equal("Some new words", SessionOrdering.StateLabel(s));
    }

    [Theory]
    [InlineData("ready", "Voice ready")]
    [InlineData("off", "Voice off")]
    [InlineData("working", "Agent is working")]
    public void StateLabel_VoiceHoldWithAVerdictThatCannotHonestlyReachHere_KeepsTheOldWords(string kind, string label)
    {
        // These three cannot honestly co-occur with the yellow hold (it runs only for a voice-mode session
        // with no audio that is WAITING, and StateLabel returns "Working" above this arm for anything
        // actually working). If one ever does arrive, the safe answer is the old behaviour - never a rail
        // reading "Voice ready" or "Agent is working" beside a yellow dot on a session doing neither, which
        // is a row contradicting itself.
        //
        // HONEST NOTE ON WHAT THESE PROVE: the "ready" and "off" rows also pass on the parent commit, so
        // they are REGRESSION GUARDS on retained behaviour rather than evidence for the inversion - the
        // inversion itself is proved by the future-kind test above. The "working" row is the discriminating
        // one: the first version of the inverted list omitted it, so it renders "Agent is working" there.
        var s = VoiceHoldingSession(kind, label);
        Assert.Equal("Preparing voice", SessionOrdering.StateLabel(s));
    }

    // EffectiveColor_BackgroundRunningAtTurnEnd_IsPurple_FromRawFacts and StateLabel_BackgroundRunning_IsBackground
    // lived here. Deleted with the arm they pinned (the Wingman-on-every-turn mission, ruling 3): the Director's
    // background purple had one feed, a switched-off service, and the calm verdict arm now owns purple. The test
    // below is what goes red if the arm is restored.
    [Fact]
    public void EffectiveColor_BackgroundRunning_WithTheWingmanOn_IsRed_BecausePurpleHasOneProducer()
    {
        var s = Raw("WaitingForInput", wingmanEnabled: true, backgroundRunning: true);

        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(s));
    }

    [Fact]
    public void EffectiveColor_BackgroundRunning_NoWingman_IsRed_NotPurple()
    {
        // Purple is gated on WingmanEnabled (matching the Director) - without it, the base red shows.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", wingmanEnabled: false, backgroundRunning: true)));
    }

    [Fact]
    public void EffectiveColor_ControlledAndWorking_IsBlue_NothingOutranksWorking()
    {
        // Owner's ruling, 2026-07-14: if a session is working, it is BLUE - no matter what. Being driven
        // by another session is NOT a reason to hide that it is working. This REPLACES the 2026-07-10
        // decision in issue #1286 (a controlled session that was not red returned "supporting", which threw
        // the real activity state away and painted a busy sub-agent gray "Sub-agent"). That rule is void.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            Raw("Working", controlled: true, controllerId: Guid.NewGuid().ToString())));
    }

    [Fact]
    public void EffectiveColor_ControlledWorker_WhileWorking_IsStillBlue()
    {
        // The exact shape that failed: a controlled sub-agent with the Worker role, mid-turn. Red
        // suppression must not leak into a working session - only a Worker's RED recedes.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            Raw("Working", controlled: true, controllerId: Guid.NewGuid().ToString(), sessionRole: "Worker")));
    }

    [Fact]
    public void EffectiveColor_ControlledButRed_BreaksThroughSupporting()
    {
        // Red "needs you" breaks through the slate overlay so a blocked sub-agent still surfaces.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString())));
    }

    [Fact]
    public void EffectiveColor_LiveWorker_SuppressesRed_RecedesToSupporting()
    {
        // A LIVE-supervised session's red is SUPPRESSED - it recedes to slate and never nags the owner (its
        // supervisor sees it via the rail).
        //
        // "LIVE" USED TO BE CARRIED BY THE SEAT and is now stated outright. The resolver stamped
        // SessionRole=Worker only when the controller was alive, so the seat was read as a proxy for
        // liveness - a proxy that broke the moment a seat was stamped by hand and outlived the supervisor it
        // stood for. The fold now reads the liveness answer itself, so this test says it.
        Assert.Equal("supporting", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString(),
                sessionRole: "Worker", hasLiveSupervisor: true)));
    }

    [Fact]
    public void EffectiveColor_ManagerRed_IsAllowed_HumanFacing()
    {
        // A Manager is human-facing: its red always surfaces.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", sessionRole: "Manager")));
    }

    [Fact]
    public void EffectiveColor_StandaloneRed_IsAllowed_HumanFacing()
    {
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", sessionRole: "Standalone")));
    }

    [Fact]
    public void EffectiveColor_DeadControllerWorkerRed_IsAllowed_EscapeHatch()
    {
        // A Worker whose controller has DIED is role Standalone (not "Worker"), so its red is NOT suppressed
        // and surfaces to the human - the escape hatch, so a stranded worker is never lost. The controller
        // id may still be on the DTO, but the role (not the id) drives the suppression.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString(), sessionRole: "Standalone")));
    }

    [Fact]
    public void EffectiveColor_Architect_Stopped_IsRed_EvenWithAController()
    {
        // THE ARCHITECT IS THE SEAT THE OWNER TALKS TO, so its red reaches him - even when it happens to
        // carry a controller, because the explicit role wins over derivation and resolves it to Architect
        // rather than Worker.
        //
        // THIS ASSERTION HAS NOW BEEN WRITTEN THREE TIMES, IN TWO DIRECTIONS, AND THE ROUND TRIP IS THE
        // POINT OF THE COMMENT. It began as EffectiveColor_Architect_RedAllowed_EvenWithAController
        // asserting "red"; on 2026-09-03 it became EffectiveColor_Architect_Stopped_IsSupervised_NotRed
        // asserting "supporting", implementing an amendment the owner made on 2026-07-09 - "the Architect
        // does NOT push needs-you or status to the human... Like a Worker, the Architect never surfaces to
        // the human". The owner watched that ship and overturned it on 2026-09-06:
        //
        //   "parking the architect seat is wrong. the architect is always the session i talk to."
        //   "no the architect should push to me it is what i talk to."
        //
        // What went wrong for two months was NOT that this assertion was written in the wrong direction. It
        // was that the written rule and the code disagreed and NOTHING COULD SEE IT: every test asserted the
        // shipped behaviour in the present tense, so a document saying the opposite made nothing go red.
        // A test of behaviour cannot close that on its own, whichever way it points, and this comment is not
        // the guard. The guard is SupervisionRuleMatchesTheDesignDocumentTests, which reads the attention
        // table out of docs/new_architecture/sessions.html and fails when the document and
        // SessionOrdering.IsSupervised disagree about any seat.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString(), sessionRole: SessionRoles.Architect)));
    }

    [Fact]
    public void EffectiveColor_Architect_Working_IsStillBlue()
    {
        // The law: nothing outranks working. It held while an Architect was supervised and it holds now that
        // it is not - this seat's colour is decided by what it is DOING, never by whose it is.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            Raw("Working", sessionRole: SessionRoles.Architect)));
    }

    [Fact]
    public void Architect_Stopped_IsRed_NeedsYou_AndNotParked()
    {
        // ALL THREE FOLD OUTPUTS TOGETHER, and that is deliberate: the colour alone is not what the owner
        // asked for. "The architect should push to me" means it goes red on the dot, it says it needs him in
        // words, AND it sits in his queue rather than at the bottom of the roster. Asserting the colour and
        // letting the bucket drift is exactly how the "blue dot labelled Snoozed" defect happened.
        //
        // This is the direct inverse of Supervised_Architect_Stopped_IsSlate_Snoozed_AndParked, which stood
        // here from 2026-09-03 to 2026-09-06.
        var s = Raw("WaitingForInput", sessionRole: SessionRoles.Architect);
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(s));
    }

    [Fact]
    public void Architect_StartedByASchedule_ReachesTheOwner_TheReversalOf13September2026()
    {
        // THIS TEST USED TO ASSERT THE OPPOSITE, and the round trip is the point of the comment.
        //
        // It stood as Architect_StartedByASchedule_IsStillSupervised_TheOriginOutranksTheSeat, pinning the
        // schedule arm as a deliberate exception: "an Architect a cron fired has nobody it can report to by
        // construction, and the owner's standing rule for scheduled runs is that they escalate by email
        // rather than sit red on his roster."
        //
        // The owner overturned it on 2026-09-13, having watched it run: "I think it is wrong that somebody
        // without a parent can automatically be put on snooze because nobody knows they existed." Having
        // nobody to report to is not a reason to go quiet - it is the reason to go RED. The email path was
        // the justification for the silence, and nothing on the roster could attest that it had ever fired.
        //
        // So the origin does not outrank the seat any more, because neither of them decides this at all.
        var s = Raw("WaitingForInput", sessionRole: SessionRoles.Architect, originKind: "schedule");

        Assert.False(SessionOrdering.IsSupervised(s));
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(s));
    }

    // ===================================================================================================
    // SUPERVISED SESSIONS DO NOT ASK THE OWNER (owner's ruling, 2026-09-02):
    //   "supervised still show up in Director and Cockpit, session should go to onhold when not working"
    //
    // Two kinds are supervised for two different reasons - a live supervisor and a schedule - so each gets
    // its own case rather than one parameterised sweep that would still pass if one of them silently
    // stopped working. It was three until 2026-09-06, when the owner took the Architect back out; the
    // Architect's cases now live above, asserting the opposite.
    //
    // Every case below asserts all THREE fold outputs together. The colour alone is not the behaviour the
    // owner asked for: a slate dot still sitting in the middle of his list is the thing he complained
    // about. Asserting the colour and letting the bucket drift is how the "blue dot labelled Snoozed"
    // defect happened, and this file exists partly to record that.
    // ===================================================================================================

    [Fact]
    public void Supervised_Worker_Stopped_IsSlate_Snoozed_AndParked()
    {
        // The one case that was ever really supervised, and the only one left: somebody is alive holding it.
        var s = Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString(),
            sessionRole: SessionRoles.Worker, hasLiveSupervisor: true);
        Assert.Equal("supporting", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(s));
    }

    [Fact]
    public void ScheduledRun_Stopped_ReachesTheOwner_TheReversalOf13September2026()
    {
        // REVERSED ON 2026-09-13, AND THE OLD REASONING IS KEPT HERE BECAUSE IT WAS PERSUASIVE AND WRONG.
        // It read: "a cron firing has no supervisor to report to and nobody was at a keyboard... this is the
        // largest single source of roster noise on a fleet with recurring jobs, and it matches the owner's
        // standing rule that scheduled runs escalate by email rather than by sitting red on the roster."
        //
        // Having nobody to report to is not a licence to go quiet - it is the whole reason to surface. The
        // silence was sold against an email path, and nothing in the session state could attest that the
        // email had ever been sent. Noise on the roster is a real cost; work nobody knows finished is a
        // larger one, and it is the one the owner was actually paying.
        var s = Raw("WaitingForInput", sessionRole: SessionRoles.Standalone, originKind: "schedule");
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(s));
    }

    [Theory]
    [InlineData(SessionRoles.Worker, null)]
    [InlineData(SessionRoles.Standalone, "schedule")]
    [InlineData(SessionRoles.Architect, "schedule")]
    public void Supervised_Working_IsStillBlue_NothingOutranksWorking(string role, string? origin)
    {
        // THE LAW, asserted against the new rule for every kind it added. Widening an attention rule is
        // exactly when the 2026-07-14 defect comes back - every rule that has ever sat above the working
        // check has been a defect, and each one cost the owner a day.
        var isWorker = role == SessionRoles.Worker;
        var s = Raw("Working", sessionRole: role, originKind: origin, hasLiveSupervisor: true,
            controlled: isWorker, controllerId: isWorker ? Guid.NewGuid().ToString() : null);
        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Working", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(s));
    }

    [Fact]
    public void Supervised_Exited_ReadsExited_NotSnoozed()
    {
        // The owner already ruled this exact question for his own snooze (defect 21): a dead session must
        // never hide behind a Snoozed label. The same reasoning binds a supervised one - otherwise a
        // worker that DIED would be indistinguishable from one that finished politely.
        var s = Raw("Exited", controlled: true, controllerId: Guid.NewGuid().ToString(),
            sessionRole: SessionRoles.Worker, hasLiveSupervisor: true);
        Assert.Equal("grey", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Exited", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void Supervised_Crashed_ReadsCrashed_TheOneThingNeverQuietened()
    {
        // A crash is the one signal on this whole ladder that must survive every suppression rule. Had
        // widening the rule swallowed this, a worker that DIED would look like a worker that finished -
        // silently, on a roster the owner has just been told he can stop watching.
        var s = Raw("Exited", controlled: true, controllerId: Guid.NewGuid().ToString(),
            sessionRole: SessionRoles.Worker, hasLiveSupervisor: true);
        s.Crashed = true;
        Assert.Equal("error", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Crashed", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void Supervised_OwnerIsDictatingIntoIt_StaysOrange()
    {
        // An utterance in flight means the OWNER is speaking into this session right now. He is driving
        // it, so the row must say so - the same reasoning that lets his own words supersede his own
        // snooze. This is why the supervised arm sits BELOW dictation orange rather than above it.
        var s = Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString(),
            sessionRole: SessionRoles.Worker, transcribing: true, hasLiveSupervisor: true);
        Assert.Equal("orange", SessionOrdering.EffectiveColor(s));
    }

    [Fact]
    public void Supervised_WithAnInFlightBrief_IsSnoozed_NeverWingmanReading()
    {
        // The supervised arm sits ABOVE the two yellows, and this proves it is true on its own rather
        // than by trusting the wingman never to have started. The fold's correctness must not rest on a
        // producer's good behaviour - that assumption is what left the desktop stuck on "Preparing voice"
        // for the whole of #1843.
        var s = Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString(),
            sessionRole: SessionRoles.Worker, briefingState: "Briefing", hasLiveSupervisor: true);
        Assert.Equal("supporting", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void Supervised_OrphanedWorker_SurfacesRed_EscapeHatchIntact()
    {
        // THE NEGATIVE CONTROL FOR THE WHOLE RULE. A worker whose supervisor died resolves to Standalone,
        // so it is NOT supervised and its red reaches the owner - the owner's 2026-07-09 orphan ruling
        // ("ALWAYS involve the operator" on an exception). If this ever goes quiet, the rule has stopped
        // being "route attention elsewhere" and started being "lose it".
        var s = Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString(),
            sessionRole: SessionRoles.Standalone);
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(s));
    }

    [Fact]
    public void Supervised_ManagerAndStandalone_StillReachTheOwner()
    {
        // The other half of the negative control: the two human-facing seats are untouched. The whole
        // design rests on the owner still hearing from a Manager - quietening the workers is only safe
        // because their manager consolidates and reports.
        foreach (var role in new[] { SessionRoles.Manager, SessionRoles.Standalone })
        {
            var s = Raw("WaitingForInput", sessionRole: role);
            Assert.Equal("red", SessionOrdering.EffectiveColor(s));
            Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(s));
        }
    }

    [Fact]
    public void NoOriginQuietensASessionNobodyIsHolding()
    {
        // THE ORIGIN HAS NO VOTE AT ALL SINCE 2026-09-13, and "schedule" is in this list to prove it rather
        // than to be excluded from it. This test used to guard an over-broad match on the one token the rule
        // read; there is no token now, and a regression that put one back would be invisible any other way -
        // it would simply make sessions stop asking.
        foreach (var origin in new string?[] { "human", "agent", "unknown", "schedule", null })
        {
            var s = Raw("WaitingForInput", sessionRole: SessionRoles.Standalone, originKind: origin);
            Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        }
    }

    // ===================================================================================================
    // THE ORPHAN'S EXPLANATION (owner's orphan ruling, 2026-07-09).
    //
    // The hand-back to the owner already worked - a session whose supervisor died resolves to Standalone,
    // so its red surfaces. What it never carried was the REASON, so an orphan was indistinguishable from an
    // ordinary session at a prompt and the owner had to open it to find out it was wreckage rather than
    // work. These prove the sentence appears exactly when it should and stays silent the rest of the time.
    // ===================================================================================================

    private static SessionDto Orphan(string activityState = "WaitingForInput", string role = SessionRoles.Standalone)
    {
        var s = Raw(activityState, controlled: true, controllerId: "the-dead-one", sessionRole: role);
        return s;
    }

    [Fact]
    public void SupervisorLost_StoppedOrphan_IsToldWhatHappened_AndNamesTheSupervisor()
    {
        var gone = new SessionDto { SessionId = "the-dead-one", Name = "Release - Manager", ActivityState = "Exited" };

        var notice = SessionOrdering.SupervisorLostNotice(Orphan(), gone);

        Assert.NotNull(notice);
        Assert.Contains("Release - Manager", notice);
        // The owner must be told what to DO, not merely what happened - the ruling's words were "here is the
        // task; here is where you are stuck", i.e. enough to act on without opening it first.
        Assert.Contains("resume it", notice);
        Assert.Contains("stop it", notice);
    }

    [Fact]
    public void SupervisorLost_SupervisorGoneFromTheRosterEntirely_StillExplainsItself()
    {
        // The COMMONEST case, and the one a name-first design would have got wrong: a supervisor that exited
        // long enough ago has left the roster, so the lookup returns nothing. A missing name is not a missing
        // explanation - it drops the name and still says what happened and what to do.
        var notice = SessionOrdering.SupervisorLostNotice(Orphan(), supervisor: null);

        Assert.NotNull(notice);
        Assert.Contains("has gone", notice);
        Assert.Contains("resume it", notice);
    }

    [Fact]
    public void SupervisorLost_SaysNothingWhileTheOrphanIsStillWorking()
    {
        // Mid-turn it is getting on with the work it was given and there is nothing to decide. The notice is
        // for the moment it STOPS - which is the moment it would otherwise sit red and unexplained.
        Assert.Null(SessionOrdering.SupervisorLostNotice(Orphan("Working"), supervisor: null));
    }

    [Fact]
    public void SupervisorLost_SaysNothingWhenTheSupervisorIsAlive()
    {
        // A live supervisor means role Worker, which is the resolver's way of saying "controlled AND the
        // controller is alive". Nothing is stranded, so there is nothing to say.
        var alive = new SessionDto { SessionId = "the-dead-one", Name = "still here", ActivityState = "Working" };

        Assert.Null(SessionOrdering.SupervisorLostNotice(Orphan(role: SessionRoles.Worker), alive));
    }

    [Fact]
    public void SupervisorLost_SaysNothingForAnOrdinarySessionThatNeverHadASupervisor()
    {
        // The negative control that matters most: almost every session on the fleet is this one, and a notice
        // leaking onto it would put a sentence about abandonment on rows that were never supervised at all.
        Assert.Null(SessionOrdering.SupervisorLostNotice(
            Raw("WaitingForInput", sessionRole: SessionRoles.Standalone), supervisor: null));
    }

    [Fact]
    public void SupervisorLost_SaysNothingAboutAnOrphanThatHasItselfExited()
    {
        // It is not stranded, it is over. Telling the owner to "resume it, hand it over, or stop it" about a
        // session that has already ended would send him to look at wreckage twice.
        Assert.Null(SessionOrdering.SupervisorLostNotice(Orphan("Exited"), supervisor: null));
    }

    [Fact]
    public void SupervisorLost_SaysNothingAboutAnArchitectThatCarriesAController()
    {
        // THE ARM SURVIVES THE 2026-09-06 RULING, AND ITS REASON DOES NOT. It used to be justified by "an
        // Architect is supervised BY DECLARATION, so losing a spawner strands nothing" - and an Architect is
        // not supervised at all any more. It still says nothing, for a reason that never depended on
        // supervision: this notice explains a session that was QUIET WHILE SUPERVISED and has suddenly
        // started asking. An Architect never went quiet. It takes its direction from the owner, not from
        // whoever opened it, so it cannot be stranded by that session ending - and "the session driving this
        // one has gone, hand it over or stop it" would be false about the seat he is talking to.
        Assert.Null(SessionOrdering.SupervisorLostNotice(Orphan(role: SessionRoles.Architect), supervisor: null));
    }

    [Fact]
    public void SupervisorLost_ArchitectStaysSilentEvenThoughItIsRedAndNoLongerSupervised()
    {
        // THE CONTROL FOR THE TEST ABOVE, and the one that would have caught it being kept for the wrong
        // reason. An Architect that carries a dead controller is now RED and in the owner's queue - exactly
        // the shape the orphan notice was built to explain - so "it is quiet, so there is nothing to say" is
        // no longer available as the argument. Prove BOTH halves in one place: the row surfaces, and it still
        // carries no abandonment sentence.
        var arch = Orphan(role: SessionRoles.Architect);

        Assert.Equal("red", SessionOrdering.EffectiveColor(arch));
        Assert.False(SessionOrdering.IsSupervised(arch));
        Assert.Null(SessionOrdering.SupervisorLostNotice(arch, supervisor: null));
    }

    [Fact]
    public void SupervisorLost_ChangesNothingAboutTheDot_TheLabel_OrTheBucket()
    {
        // A NOTICE, NEVER A COLOUR - the same rule the delivery notice follows. Recolouring would say
        // something false about what the agent is doing in order to say something true about its
        // supervision. The orphan is red and in the owner's queue because it has STOPPED, which is exactly
        // what it would be without this sentence; the sentence only explains it.
        var orphan = Orphan();

        Assert.NotNull(SessionOrdering.SupervisorLostNotice(orphan, supervisor: null));
        Assert.Equal("red", SessionOrdering.EffectiveColor(orphan));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(orphan));
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(orphan));
    }

    [Fact]
    public void EffectiveColor_ControlledWithNoControllerId_IsNotSupporting()
    {
        // Without a controller id present the slate overlay does not apply (it paints its normal color).
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            Raw("Working", controlled: true, controllerId: null)));
    }

    [Fact]
    public void EffectiveColor_DesktopTranscribing_IsOrange_WhenNotWorking()
    {
        // Issue #1177 audit: the desktop-dictation transcribing fact (Session.IsTranscribing) paints
        // orange exactly as the mobile Speak flag does. Gated on NOT working since 2026-07-14 - orange
        // marks "dictation in flight, do not grab this session", which is a statement about a session
        // sitting at a prompt. A working session is blue.
        Assert.Equal("orange", SessionOrdering.EffectiveColor(Raw("WaitingForInput", transcribing: true)));
        Assert.Equal("blue", SessionOrdering.EffectiveColor(Raw("Working", transcribing: true)));
    }

    [Fact]
    public void EffectiveColor_AutoExplainingAtTurnEnd_IsYellow_FromRawFact()
    {
        // Newly covered (issue #1177 audit): the legacy auto-explain (ProactiveExplainService,
        // Session.IsExplaining) must paint yellow while WingmanEnabled and at a turn-end - previously this
        // survived ONLY via the cooked StatusColor fall-through and was untested.
        //
        // THIS IS THE AUTO-EXPLAIN THAT WORKS, and it is why the gating below matters. This note used to say
        // "Distinct from the Gateway deep-dive overlay (BriefingState==\"Explaining\"), which is ORANGE",
        // and it lived on a test asserting that orange. The distinction was real; the orange was not. That
        // rule never fired in any release and is deleted (defect 11) - see the note at the top of this file.
        // The distinction is preserved here because it explains the shape of THIS rule: the yellow rides on
        // the RAW FACT SessionDto.IsAutoExplaining, which the Director actually stamps, and it is gated on
        // WingmanEnabled + turn-end. Two different fields, and only one of them was ever populated.
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", wingmanEnabled: true, autoExplaining: true)));
    }

    [Fact]
    public void EffectiveColor_AutoExplaining_NoWingman_IsRed_NotYellow()
    {
        // Auto-explain yellow is gated on WingmanEnabled (matching the Director) - without it, base red shows.
        Assert.Equal("red", SessionOrdering.EffectiveColor(
            Raw("WaitingForInput", wingmanEnabled: false, autoExplaining: true)));
    }

    // EffectiveColor_GatewayDeepDiveExplaining_IsOrange_NotYellow lived here. Deleted with the rule
    // (defect 11); its surviving half - that the auto-explain yellow is a DIFFERENT feature on a DIFFERENT
    // field - is now recorded on the yellow test above, which is the one that covers reachable behaviour.

    // ----- StateLabel: one per color / overlay (issue #1177, Phase 2) -----

    [Fact]
    public void StateLabel_OnHold_IsSnoozed()
    {
        var s = Raw("WaitingForInput");
        s.OnHold = true;
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void StateLabel_Transcribing_IsTranscribing_WhenNotWorking()
    {
        // Gated on NOT working since 2026-07-14, mirroring EffectiveColor so the dot and the label
        // are folded from the same inputs in the same order and cannot disagree.
        Assert.Equal("Transcribing", SessionOrdering.StateLabel(Raw("WaitingForInput", transcribing: true)));
        var mobile = Raw("WaitingForInput");
        mobile.Transcribing = true;
        Assert.Equal("Transcribing", SessionOrdering.StateLabel(mobile));

        // A working session reads "Working", whatever else is in flight.
        Assert.Equal("Working", SessionOrdering.StateLabel(Raw("Working", transcribing: true)));
    }

    // StateLabel_GatewayDeepDive_IsExplaining lived here. Deleted with the rule (defect 11): no session has
    // ever read "Explaining", because nothing could produce the state that label was folded from.

    [Fact]
    public void StateLabel_Briefing_IsWingmanReading()
    {
        Assert.Equal("Wingman reading", SessionOrdering.StateLabel(Raw("WaitingForInput", briefingState: "Briefing")));
    }

    [Fact]
    public void StateLabel_AutoExplain_IsWingmanReading()
    {
        Assert.Equal("Wingman reading", SessionOrdering.StateLabel(
            Raw("WaitingForInput", wingmanEnabled: true, autoExplaining: true)));
    }

    [Fact]
    public void StateLabel_VoicePreparing_IsPreparingVoice()
    {
        var s = Raw("WaitingForInput");
        s.VoiceMode = true;
        s.VoiceGenerating = true;
        Assert.Equal("Preparing voice", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void StateLabel_ControlledAndWorking_ReadsWorking_NotSubAgent()
    {
        // The label must agree with the blue dot: a controlled sub-agent that is working reads "Working"
        // like any other working session. It used to read "Sub-agent" (the void issue #1286 rule), which
        // is what let a session 23 minutes into real work look parked.
        Assert.Equal("Working", SessionOrdering.StateLabel(
            Raw("Working", controlled: true, controllerId: Guid.NewGuid().ToString())));
    }

    [Fact]
    public void StateLabel_LiveWorker_Stopped_IsSnoozed()
    {
        // Was StateLabel_LiveWorker_WhoseRedIsSuppressed_IsSubAgent, asserting "Sub-agent". The owner's
        // ruling of 2026-09-02 - "session should go to onhold when not working" - makes this a STATE, so it
        // takes the owner's own word for that state. "Sub-agent" also described the row's parentage rather
        // than what it was doing, and it was plainly wrong for the two kinds the rule now covers (an
        // Architect is not a sub-agent; neither is a cron firing). The slate dot still says "not yours".
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(
            Raw("WaitingForInput", controlled: true, controllerId: Guid.NewGuid().ToString(),
                sessionRole: "Worker", hasLiveSupervisor: true)));
    }

    [Fact]
    public void StateLabel_BrandNew_IsReady()
    {
        Assert.Equal("Ready", SessionOrdering.StateLabel(Raw("WaitingForInput", brandNew: true)));
    }

    [Fact]
    public void StateLabel_Working_IsWorking()
    {
        Assert.Equal("Working", SessionOrdering.StateLabel(Raw("Working")));
    }

    [Fact]
    public void StateLabel_Waiting_IsNeedsYou()
    {
        Assert.Equal("Needs you", SessionOrdering.StateLabel(Raw("WaitingForInput")));
    }

    [Fact]
    public void StateLabel_Exited_IsExited()
    {
        Assert.Equal("Exited", SessionOrdering.StateLabel(Raw("Exited")));
    }

    // ===== by-repo grouping (issue #219) =====

    /// <summary>Builds a session with the fields the repo grouping reads. RemoteRepo wins over
    /// RepoPath when present; MachineName/DirectorId are carried to prove they do NOT affect the
    /// group key.</summary>
    private static SessionDto R(string id, string repoPath = "", string remoteRepo = "",
        int sortOrder = 0, string machine = "", string directorId = "") => new()
    {
        SessionId = id,
        RepoPath = repoPath,
        RemoteRepo = remoteRepo,
        SortOrder = sortOrder,
        MachineName = machine,
        DirectorId = directorId,
        StatusColor = "blue",
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void RepoName_PrefersNormalizedRemote_LeafCaseInsensitiveDotGitStripped()
    {
        Assert.Equal("devthrottle", SessionOrdering.RepoName(R("x", remoteRepo: "example-org/devthrottle.git")));
        Assert.Equal("devthrottle", SessionOrdering.RepoName(R("x", remoteRepo: "  example-org/devthrottle  ")));
    }

    [Fact]
    public void RepoName_FallsBackToRepoPathLeaf_WhenNoRemote()
    {
        Assert.Equal("cc-director", SessionOrdering.RepoName(R("x", repoPath: @"C:\repos\cc-director")));
        Assert.Equal("cc-director", SessionOrdering.RepoName(R("x", repoPath: "/home/user/src/cc-director/")));
    }

    [Fact]
    public void RepoName_NoRemoteNoPath_IsNull()
    {
        Assert.Null(SessionOrdering.RepoName(R("x")));
    }

    [Fact]
    public void InRepoGroups_HeadersAreAlphabetical_CaseInsensitive()
    {
        var sessions = new[]
        {
            R("z", repoPath: @"D:\zebra"),
            R("a", repoPath: @"D:\Apple"),
            R("b", repoPath: @"D:\banana"),
        };

        var groups = SessionOrdering.InRepoGroups(sessions);

        // "Apple" sorts before "banana" before "zebra" ignoring case.
        Assert.Equal(new[] { "Apple", "banana", "zebra" }, groups.Select(g => g.Name));
    }

    [Fact]
    public void InRepoGroups_NoRepoGroup_IsPlacedLast()
    {
        var sessions = new[]
        {
            R("none", repoPath: ""),
            R("named", repoPath: @"D:\alpha"),
        };

        var groups = SessionOrdering.InRepoGroups(sessions);

        Assert.Equal(2, groups.Count);
        Assert.Equal("alpha", groups[0].Name);
        Assert.False(groups[0].IsNoRepo);
        Assert.Equal(SessionOrdering.NoRepoGroup, groups[^1].Name);
        Assert.True(groups[^1].IsNoRepo);
    }

    [Fact]
    public void InRepoGroups_WithinGroup_UsesDesktopOrder()
    {
        // Two sessions in the same repo; lower SortOrder must render first regardless of input order.
        var sessions = new[]
        {
            R("second", repoPath: @"D:\repo", sortOrder: 2),
            R("first",  repoPath: @"D:\repo", sortOrder: 1),
        };

        var groups = SessionOrdering.InRepoGroups(sessions);

        var repo = Assert.Single(groups);
        Assert.Equal(new[] { "first", "second" }, repo.Sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void InRepoGroups_SameRepoAcrossMachines_CoalescesIntoOneGroup()
    {
        // Same repo (same RemoteRepo) on two different machines / Directors must land under ONE header.
        var sessions = new[]
        {
            R("onA", remoteRepo: "example-org/devthrottle.git", machine: "MACHINE_A", directorId: "dirA"),
            R("onB", remoteRepo: "example-org/devthrottle",     machine: "MACHINE_B", directorId: "dirB"),
        };

        var groups = SessionOrdering.InRepoGroups(sessions);

        var repo = Assert.Single(groups);
        Assert.Equal("devthrottle", repo.Name);
        Assert.Equal(new[] { "onA", "onB" }, repo.Sessions.Select(s => s.SessionId).OrderBy(x => x));
    }

    [Fact]
    public void InRepoGroups_DoesNotMutateInput()
    {
        var sessions = new[]
        {
            R("b", repoPath: @"D:\repo", sortOrder: 2),
            R("a", repoPath: @"D:\repo", sortOrder: 1),
        };

        _ = SessionOrdering.InRepoGroups(sessions);

        // Original array order preserved (grouping snapshots, never sorts in place).
        Assert.Equal(new[] { "b", "a" }, sessions.Select(s => s.SessionId));
    }

    // ===================================================================================
    // THE LAW: a working session is BLUE, always. Nothing outranks working.
    // (Owner's ruling, 2026-07-14. See docs/new_architecture/sessions.html.)
    //
    // These tests exist to make the law UNBREAKABLE. Every colour that has ever been put
    // above working in the ladder gets its own case below. If you are here because one of
    // these went red, you have re-introduced a defect the owner has personally reported
    // more than once - do not "fix" the test. Fix the code, or the law changed and the
    // owner said so out loud.
    // ===================================================================================

    /// <summary>Every overlay that has ever outranked working, asserted one at a time.</summary>
    public static TheoryData<string, SessionDto> OverlaysThatMustNotBeatWorking() => new()
    {
        { "snoozed (OnHold)",        new SessionDto { SessionId = "w", ActivityState = "Working", OnHold = true } },
        { "mobile dictation phase",  new SessionDto { SessionId = "w", ActivityState = "Working", DictationStatus = "Uploading from phone" } },
        { "gateway transcribing",    new SessionDto { SessionId = "w", ActivityState = "Working", Transcribing = true } },
        { "director transcribing",   new SessionDto { SessionId = "w", ActivityState = "Working", IsTranscribing = true } },
        { "explaining (deep dive)",  new SessionDto { SessionId = "w", ActivityState = "Working", BriefingState = "Explaining" } },
        { "wingman briefing",        new SessionDto { SessionId = "w", ActivityState = "Working", BriefingState = "Briefing" } },
        { "voice preparing",         new SessionDto { SessionId = "w", ActivityState = "Working", VoiceMode = true, VoiceGenerating = true } },
        { "controlled sub-agent",    new SessionDto { SessionId = "w", ActivityState = "Working", SessionRole = SessionRoles.Worker, ControllerSessionId = "mgr" } },
        // Defect 23: a session flagged for deletion may still be WORKING - the Director's reaper
        // explicitly waits out a running final turn (SessionManager.ReapPendingDeletions). Pending
        // deletion is a BADGE, never a colour, so it cannot beat working - or anything else.
        { "pending deletion",        new SessionDto { SessionId = "w", ActivityState = "Working", PendingDeletion = true, DeletionReason = "jobs-auto: nothing to report" } },
    };

    [Theory]
    [MemberData(nameof(OverlaysThatMustNotBeatWorking))]
    public void EffectiveColor_Working_IsAlwaysBlue_NoMatterWhatElseIsTrue(string overlay, SessionDto s)
    {
        var actual = SessionOrdering.EffectiveColor(s);
        Assert.True(actual == "blue",
            $"A WORKING session rendered '{actual}' because {overlay} was true. Working is BLUE, always - " +
            $"nothing outranks it. Remove the rule you put above the working check in EffectiveColor.");
    }

    [Theory]
    [MemberData(nameof(OverlaysThatMustNotBeatWorking))]
    public void StateLabel_Working_AlwaysReadsWorking_NoMatterWhatElseIsTrue(string overlay, SessionDto s)
    {
        // The dot and its label are folded from the same inputs in the same order: a blue dot
        // labelled "Snoozed" is the contradiction this pins shut.
        var actual = SessionOrdering.StateLabel(s);
        Assert.True(actual == "Working",
            $"A WORKING session was labelled '{actual}' because {overlay} was true. The label must " +
            $"match the dot: a working session is blue and reads 'Working'.");
    }

    [Theory]
    [MemberData(nameof(OverlaysThatMustNotBeatWorking))]
    public void Classify_Working_IsAlwaysActive_NeverParked(string overlay, SessionDto s)
    {
        // A working session never sinks into the parked bucket at the bottom of the roster.
        var actual = SessionOrdering.Classify(s);
        Assert.True(actual == SessionOrdering.TriageBucket.Active,
            $"A WORKING session was triaged '{actual}' because {overlay} was true. A running session " +
            $"is Active - it cannot be parked while it is working.");
    }

    [Fact]
    public void EffectiveColor_EverySignalAtOnce_StillBlue()
    {
        // The pathological case: every overlay in the ladder is true at the same instant, and the
        // session is working. Working still wins. "Nothing outranks working" means nothing.
        var s = new SessionDto
        {
            SessionId = "w",
            ActivityState = "Working",
            OnHold = true,
            DictationStatus = "Transcribing",
            Transcribing = true,
            IsTranscribing = true,
            BriefingState = "Explaining",
            VoiceMode = true,
            VoiceGenerating = true,
            WingmanEnabled = true,
            IsAutoExplaining = true,
            IsBackgroundRunning = true,
            IsBrandNew = true,
            SessionRole = SessionRoles.Worker,
            ControllerSessionId = "mgr",
            PendingDeletion = true,
        };

        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Working", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(s));
    }

    // ===================================================================================
    // Defect 23: PENDING DELETION IS A BADGE, NEVER A COLOUR (owner's ruling, 2026-07-14).
    //
    // The law tests above pin "a flagged WORKING session is blue". These pin the whole
    // ruling: the flag changes NOTHING about the fold, in ANY state. If one of these went
    // red, someone added a PendingDeletion branch to EffectiveColor / StateLabel / Classify.
    // Don't. It would spend the dot - which says what a session is DOING - on saying the
    // session is scheduled to go, which is the same mistake as the "Supporting" grey that
    // hid 23 minutes of real work. The fact rides on SessionDto.PendingDeletion, beside the
    // dot, and the rail renders it as a badge.
    // ===================================================================================

    public static TheoryData<string, string> EveryActivityState() => new()
    {
        { "Working",         "blue" },
        { "Starting",        "blue" },
        { "WaitingForInput", "red" },
        { "WaitingForPerm",  "red" },
        { "Idle",            "red" },
        { "Exited",          "grey" },
    };

    [Theory]
    [MemberData(nameof(EveryActivityState))]
    public void EffectiveColor_PendingDeletion_ChangesNothing_InAnyState(string activityState, string expected)
    {
        var flagged = new SessionDto
        {
            SessionId = "d",
            ActivityState = activityState,
            PendingDeletion = true,
            DeletionReason = "jobs-auto: nothing to report",
        };
        var notFlagged = new SessionDto { SessionId = "d", ActivityState = activityState };

        Assert.Equal(expected, SessionOrdering.EffectiveColor(flagged));
        // The flag is invisible to the fold: flagged and unflagged fold identically, everywhere.
        Assert.Equal(SessionOrdering.EffectiveColor(notFlagged), SessionOrdering.EffectiveColor(flagged));
        Assert.Equal(SessionOrdering.StateLabel(notFlagged), SessionOrdering.StateLabel(flagged));
        Assert.Equal(SessionOrdering.Classify(notFlagged), SessionOrdering.Classify(flagged));
    }

    [Fact]
    public void EffectiveColor_PendingDeletion_NeverPaintsTheWindingDownGrey_TheDtoOncePromised()
    {
        // SessionDto.PendingDeletion's comment used to claim the row "paints as a winding-down grey".
        // It never did on any Gateway-backed screen - the fold has never read the field - and under the
        // ruling it never will: a flagged session that needs the user is still RED "Needs you", and a
        // flagged session that is working is still BLUE. The grey was a promise no code kept, which is
        // exactly how a lying comment becomes the next agent's specification.
        var needsUser = new SessionDto { SessionId = "d", ActivityState = "WaitingForInput", PendingDeletion = true };
        var working = new SessionDto { SessionId = "d", ActivityState = "Working", PendingDeletion = true };

        Assert.Equal("red", SessionOrdering.EffectiveColor(needsUser));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(needsUser));
        Assert.Equal("blue", SessionOrdering.EffectiveColor(working));
        Assert.Equal("Working", SessionOrdering.StateLabel(working));
    }

    [Fact]
    public void EffectiveColor_Starting_IsAlsoBlue()
    {
        // "Starting" is the sensor's first working byte - the session is running, so it is blue.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            new SessionDto { SessionId = "s", ActivityState = "Starting", OnHold = true }));
    }

    [Fact]
    public void EffectiveColor_WorkingIsCaseInsensitive()
    {
        // Defect 16: RawActivityColor was a case-sensitive switch while the role rules compared
        // case-insensitively on the same field. The working check must never be the fold's weak link.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(
            new SessionDto { SessionId = "w", ActivityState = "working", OnHold = true }));
    }

    // ===== The other half of the law: the overlays STILL work for a session that has stopped. =====
    // Hoisting working to the top must not weaken any rule below it - each one keeps its meaning
    // for the case it was actually built for, which is a session that is NOT running.

    [Fact]
    public void EffectiveColor_NotWorking_SnoozeStillWins()
    {
        Assert.Equal("grey", SessionOrdering.EffectiveColor(
            new SessionDto { SessionId = "q", ActivityState = "WaitingForInput", OnHold = true }));
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(
            new SessionDto { SessionId = "q", ActivityState = "WaitingForInput", OnHold = true }));
    }

    [Fact]
    public void EffectiveColor_NotWorking_TranscribingStillOrange()
    {
        // The case orange exists for: dictation in flight at a PROMPT, so nobody else grabs it.
        Assert.Equal("orange", SessionOrdering.EffectiveColor(
            new SessionDto { SessionId = "q", ActivityState = "WaitingForInput", Transcribing = true }));
    }

    [Fact]
    public void Classify_NotWorking_SnoozedRedStillSinksToOnHold()
    {
        // The deferral the snooze was built for: a red session the user parked stays parked.
        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(
            new SessionDto { SessionId = "q", ActivityState = "WaitingForInput", OnHold = true }));
    }
}
