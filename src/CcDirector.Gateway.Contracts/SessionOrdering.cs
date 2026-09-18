namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Shared client-side policy for how a roster of <see cref="SessionDto"/> is ordered and
/// triaged. Lives next to the DTO so every client (Cockpit today, others later) agrees on
/// the rules instead of each re-implementing them, and so the rules are unit-testable
/// without spinning up a UI.
/// </summary>
public static class SessionOrdering
{
    /// <summary>
    /// The stable "desktop order": honor the owning Director's <see cref="SessionDto.SortOrder"/>
    /// (the user-controlled, drag-to-reorder, persisted order), then <see cref="SessionDto.CreatedAt"/>
    /// as a deterministic tie-break. The tie-break is also the only signal when a Director predates
    /// SortOrder (every session reports 0). This is what keeps a session in a fixed slot instead of
    /// reshuffling as its name or activity state changes.
    /// </summary>
    public static IReadOnlyList<SessionDto> InDesktopOrder(IEnumerable<SessionDto> sessions) =>
        sessions.OrderBy(s => s.SortOrder).ThenBy(s => s.CreatedAt).ToList();

    /// <summary>Triage priority bucket for the "needs-you-first" view.</summary>
    public enum TriageBucket
    {
        /// <summary>Wants the user now (effective color "red"), and not parked.</summary>
        NeedsYou = 0,
        /// <summary>Anything else that isn't parked.</summary>
        Active = 1,
        /// <summary>Parked by the user or the agent (<see cref="SessionDto.OnHold"/>) - sinks to the bottom.</summary>
        OnHold = 2,
    }

    /// <summary>
    /// True while the session must present as "the wingman is reading": the Gateway's brief
    /// agent has the finished turn queued or in flight (<see cref="SessionDto.BriefingState"/>
    /// "Briefing") AND the RAW activity color is red (issue #1177, Phase 2: gated on the raw
    /// <see cref="SessionDto.ActivityState"/>, no longer the Director's cooked StatusColor). While a
    /// NEW turn is already running (blue) the stale in-flight brief is irrelevant - raw activity wins.
    /// </summary>
    public static bool IsBriefing(SessionDto s) =>
        s.BriefingState == "Briefing" && IsRawRed(s);

    /// <summary>The verdict word for a stop that was a report of finished work.</summary>
    public const string VerdictFinished = "finished";

    /// <summary>The verdict word for a stop where the agent said it would carry on by itself.</summary>
    public const string VerdictContinuesAlone = "continues-alone";

    /// <summary>The one confidence word that may calm a red row. "ambiguous" is accepted and stored, and it
    /// never demotes red.</summary>
    public const string ConfidenceHigh = "high";

    // The three words above are LITERAL because this assembly references nothing. The product's one vocabulary is
    // TurnVerdictVocabulary in Core, and SessionOrderingVerdictWordsTests pins these to it, so the fold cannot
    // come to calm a word the contract never emits.

    /// <summary>The words a calm row reads when its verdict carries no label of its own: cyan is "Done",
    /// purple is "Carrying on" (ruling 1). The Wingman's own label is used whenever there is one.</summary>
    public const string CalmFinishedLabel = "Done";

    /// <inheritdoc cref="CalmFinishedLabel"/>
    public const string CalmContinuesLabel = "Carrying on";

    /// <summary>
    /// True while the Gateway's turn-verdict seat is forming a verdict about this session's stop
    /// (<see cref="SessionDto.VerdictState"/> is "reading") AND the raw activity colour is red: the Wingman is
    /// reading the finished turn, so it is not yet known whether the stop needs the owner.
    ///
    /// The Gateway stamps "reading" only for an account whose colour switch is on, so an account in shadow never
    /// sees this yellow. Gated on raw red for the same reason <see cref="IsBriefing"/> is: a session that went
    /// back to work is blue, and a read in flight is about a screen that is gone.
    /// </summary>
    public static bool IsVerdictReading(SessionDto s) =>
        string.Equals(s.VerdictState, VerdictStates.Reading, StringComparison.Ordinal) && IsRawRed(s);

    /// <summary>
    /// THE CALM ARM (the Wingman-on-every-turn mission, slice D): the Wingman judged this stop a REPORT rather
    /// than an ask, so the row drops from red to a calm colour, keeps its row everywhere, and is not counted in
    /// "needs you".
    ///
    /// ALL FOUR GATES, and each one alone keeps the row red:
    ///  - RAW RED. The detector says the session is stopped. A verdict never recolours a working, exited or
    ///    crashed session.
    ///  - STATE JUDGED. An accepted verdict is on the row. "reading", "failed" and "none" never calm anything,
    ///    and the Gateway stamps "none" on every row of an account whose colour switch is off.
    ///  - THE STATE IS ONE OF THE THREE CALM WORDS - finished-done, finished-report or carrying-on. Every other
    ///    word is an ask, a stuck session, or cannot-tell.
    ///
    /// THERE WAS A FOURTH GATE UNTIL CONTRACT v3 (owner ruling, 2026-09-18): the judge's own "confidence" word had
    /// to be "high". That field is cut, so a v3 record carries no confidence at all and gating every row on it
    /// would have left EVERY row red for ever - the failure mode this note exists to stop a later reader from
    /// re-creating by "restoring" the gate unconditionally. It is not a loss: the two fields said one thing. A
    /// judge that could not judge the stop answers cannot-tell, which is not calm, and the code that read
    /// confidence already treated "ambiguous" and "cannot-tell" identically.
    ///
    /// A RECORD THAT CARRIES A CONFIDENCE IS STILL READ UNDER IT, and that is the whole of why the gate is not
    /// simply deleted. Every session's newest reading on the morning of the deploy was written under the OLD
    /// contract, so deleting the gate outright would have recoloured the live roster in the instant the Gateway
    /// swapped: a session sitting red on an "ambiguous" reading would have gone calm without anything about it
    /// changing. That is a quietened question, which this file calls the worst thing the mission can do. So a
    /// record is judged under the contract it was written with - the word if it has one, nothing to answer if it
    /// does not - and no stored reading ever changes colour because the code around it changed.
    ///
    /// It errs toward the owner. Every gate that cannot be answered answers "not calm", so the wrong answer this
    /// can give is a red row that did not need him, never a calm row that did.
    /// </summary>
    public static bool IsCalmVerdict(SessionDto s) =>
        IsRawRed(s)
        && string.Equals(s.VerdictState, VerdictStates.Judged, StringComparison.Ordinal)
        && s.TurnVerdict is { } verdict
        && (string.Equals(verdict.Verdict, VerdictFinished, StringComparison.Ordinal)
            || string.Equals(verdict.Verdict, VerdictContinuesAlone, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(verdict.Confidence)
            || string.Equals(verdict.Confidence, ConfidenceHigh, StringComparison.Ordinal));

    /// <summary>
    /// The colour a snooze that ended with nothing new comes back in (the Wingman-on-every-turn mission, slice F,
    /// ruling 10).
    ///
    /// CYAN, BY THE ARCHITECT'S RULING OF 2026-09-15, and the implementation plan's "green" is superseded. A
    /// session coming back from a snooze has TAKEN TURNS, and green is the brand-new session's "Ready" (issue
    /// #2892) - painting this row green would say the opposite of what is true about it, which is the same
    /// misreading that took green away from a finished report. So it takes the calm colour the owner chose for a
    /// row that needs nothing from him. The words are what tell the two apart.
    /// </summary>
    public const string SnoozeEndedNothingNewColor = "cyan";

    /// <summary>The words on a row that came back from a snooze with nothing new to say.</summary>
    public const string SnoozeEndedNothingNewLabel = "Snooze ended, nothing new";

    /// <summary>
    /// THE SNOOZE-EXPIRY ARM (the Wingman-on-every-turn mission, slice F, ruling 10): the owner asked for quiet,
    /// the timer ran out, and the session took no turn while it ran. A snooze expiry must not manufacture a red -
    /// nothing happened, so there is nothing to bring him.
    ///
    /// RAW RED IS A GATE, exactly as it is on <see cref="IsCalmVerdict"/>: this may quieten a session that is
    /// stopped, and never one that is working, exited or crashed. The stamp itself is written only for an account
    /// whose colour switch is on, so a shadow account never sees this colour.
    ///
    /// AN ACCEPTED VERDICT IS THE SECOND GATE, and it is not belt and braces. "Nothing was judged" and "here is
    /// what the judge said" are answers to the same question, and a row must never be able to carry both: a red
    /// judged row - a real ask the Wingman found while the snooze ran - would otherwise be painted calm by this
    /// arm, which is a quietened question and the worst thing this mission can do. The fold does not stamp both
    /// (a judged stop is case 2 of ruling 10 and stamps nothing), so this gate is what makes that a property of
    /// the ladder rather than a promise from the producer.
    /// </summary>
    public static bool IsSnoozeEndedNothingNew(SessionDto s) =>
        s.SnoozeEndedNothingNew
        && IsRawRed(s)
        && !string.Equals(s.VerdictState, VerdictStates.Judged, StringComparison.Ordinal);

    /// <summary>The finished kind for work the agent says is complete (owner ruling, 2026-09-15).</summary>
    public const string FinishedKindDone = "done";

    /// <summary>The finished kind for a stop where the agent only informs the owner and asks nothing.</summary>
    public const string FinishedKindReport = "report";

    /// <summary>
    /// The words a calm "report" row's label leads with. "Done" is <see cref="CalmFinishedLabel"/>.
    ///
    /// "TELLING YOU", NOT "REPORT", and it is one name in three places. The Wingman tab's pill said "Report",
    /// its card said "Only telling you", and this row said "Report - Review the QA report" - three names for one
    /// state on one screen. "Report" beside "QA report" also reads as a noun about the document rather than as
    /// what the session is doing, which is telling him something and asking nothing.
    /// </summary>
    public const string CalmReportLabel = "Telling you";

    /// <summary>
    /// THE WORDS ON A CALM ROW. Purple reads the Wingman's own line, or "Carrying on". Cyan LEADS WITH "Done" or
    /// "Telling you" (owner ruling, 2026-09-15: "an informational state where I'm not needed but I'm just given
    /// information"), followed by the Wingman's line when there is one. The two kinds are the same colour, the same
    /// band and equally uncounted; only the words differ.
    ///
    /// A finished verdict with no kind is one stored before the field existed - the contract refuses such an answer
    /// now - and reads the Wingman's line alone, as it did when it was judged.
    /// </summary>
    private static string CalmLabel(SessionDto s)
    {
        var line = JudgedLabel(s);
        if (CalmColor(s) == "purple") return line ?? CalmContinuesLabel;

        var lead = s.TurnVerdict?.FinishedKind switch
        {
            FinishedKindDone => CalmFinishedLabel,
            FinishedKindReport => CalmReportLabel,
            _ => null,
        };
        if (lead is null) return line ?? CalmFinishedLabel;
        return line is null ? lead : $"{lead} - {line}";
    }

    /// <summary>Cyan for finished, purple for continues-alone. Asked only after <see cref="IsCalmVerdict"/>.
    ///
    /// NOT GREEN (issue #2892). Green is the brand-new session's "Ready", and a finished row painted that same green
    /// read as a new session that had not started yet. A finished row and a new one say opposite things, so they
    /// never share a colour.</summary>
    private static string CalmColor(SessionDto s) =>
        string.Equals(s.TurnVerdict?.Verdict, VerdictContinuesAlone, StringComparison.Ordinal) ? "purple" : "cyan";

    /// <summary>
    /// The Wingman's one-line label for a row carrying an ACCEPTED verdict, verbatim, or null when the row carries
    /// none. A blank label counts as none, so no label on this ladder is ever the empty string (gap 6).
    /// </summary>
    private static string? JudgedLabel(SessionDto s) =>
        string.Equals(s.VerdictState, VerdictStates.Judged, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(s.VerdictLabel)
            ? s.VerdictLabel
            : null;

    /// <summary>
    /// The voice fold's own headline for a row whose narration is not coming, or null when this row's voice
    /// is fine, absent, or still promising one.
    ///
    /// Null-safe by necessity, not by caution: <see cref="SessionDto.VoiceDisplay"/> is null on every
    /// Director-local response, so this must answer "no voice words" there rather than assume a shape.
    /// </summary>
    private static string? VoiceGaveUpLabel(SessionDto s) =>
        IsVoiceGaveUp(s) && !string.IsNullOrWhiteSpace(s.VoiceDisplay!.Label)
            ? s.VoiceDisplay.Label
            : null;

    /// <summary>
    /// THIS ROW'S NARRATION IS NOT COMING - the voice fold's own two terminal verdicts, and nothing else. A
    /// session was promised audio and will not get it, which is a thing the owner can act on.
    ///
    /// ONE PREDICATE, THREE READERS: the voice hold below ends on it, the colour ladder ranks by it, and the
    /// words read it. It was written out three times before, and three copies of one rule is how the label
    /// came to say "Voice did not arrive after 48m" while the dot said something else for 48 minutes.
    ///
    /// Null-safe by necessity, not by caution: <see cref="SessionDto.VoiceDisplay"/> is null on every
    /// Director-local response, so this must answer "no voice verdict" there rather than assume a shape.
    /// </summary>
    public static bool IsVoiceGaveUp(SessionDto s) =>
        s.VoiceDisplay is { Kind: VoiceDisplayKinds.GaveUp or VoiceDisplayKinds.NotNarrated };

    // GAP 5: THE GATEWAY'S VOICE WINDOW NEEDS NO RULE HERE - IsVoicePreparing BELOW ALREADY IS IT.
    //
    // The Gateway used to get its voice-mode yellow by WRITING s.BriefingState = "Briefing" onto the row
    // during enrichment (GatewayEndpoints), gated on the Director's value being null/"None"/"Briefed". That
    // overwrite destroyed a field the Director owns: afterwards, BriefingState="Briefing" + VoiceGenerating
    // =true could not say WHO said it - a Director genuinely briefing (the desktop folds yellow too, so the
    // screens agree) and a Gateway that had overwritten a "None" (the desktop folds red - a real
    // disagreement) produced an identical row. The agreement check could only call that "indeterminate" and
    // refuse to grade it, which fixes the instrument rather than the product.
    //
    // The first attempt at this fix added an IsGatewayVoiceBriefing rule here, reading VoiceGenerating and
    // carrying the stamp's condition, on the theory that it preserved every pixel. THE SUITE REFUTED THAT
    // AND WAS RIGHT: it broke StateLabel_VoicePreparing_IsPreparingVoice and
    // EffectiveColor_NonVoiceWaiting_NoAudio_StaysRed, because IsVoicePreparing ALREADY folds the Gateway's
    // own VoiceGenerating fact - correctly, and more narrowly (it requires VoiceMode and an actually
    // WAITING session). A second rule for one fact is a second answer, which is this mission's entire
    // defect class.
    //
    // So the overwrite is deleted and NOTHING replaces it. That also fixes a lie nobody had noticed: by
    // hijacking BriefingState the Gateway made a voice-generating session read "Wingman reading", when its
    // own rule says the truer thing - "Preparing voice". The dot is yellow either way; the words are now
    // honest, the Director's fact survives, and the check can grade the row.

    /// <summary>
    /// True when the session's RAW activity fact reads red - it is parked at a prompt, waiting on a
    /// permission, or idle. THE fold-owned answer to "is this session red?", computed from
    /// <see cref="SessionDto.ActivityState"/> and nothing else.
    ///
    /// Public because the Gateway's own enrichment pipeline must ask this question BEFORE the fold runs
    /// (the voice-mode window stamps <see cref="SessionDto.BriefingState"/> only for a red session). That
    /// stamp used to gate on the DIRECTOR's cooked <see cref="SessionDto.StatusColor"/>, which made a
    /// Gateway-rendered colour depend on a Director-made decision - the one thing law 2 forbids. Exposing
    /// the raw question here is what let that call site stop reading the cooked colour.
    ///
    /// Do NOT read this as "cooked red and raw red are the same thing". They are not, and the difference
    /// is the whole reason this exists - see the note on <see cref="IsVoicePreparing"/>.
    /// </summary>
    public static bool IsRawRed(SessionDto s) =>
        string.Equals(RawActivityColor(s), "red", StringComparison.Ordinal);

    /// <summary>
    /// The dictation phase to paint and label, or null when no dictation should paint this session. A
    /// BLANK <see cref="SessionDto.DictationStatus"/> counts as absent, not as a dictation with no name.
    ///
    /// GAP 6 - THIS IS WHAT MAKES "StateLabel IS NEVER BLANK" A STRUCTURAL FACT RATHER THAN A HOPE.
    /// StateLabel used to return s.DictationStatus verbatim, so its non-blankness rested on a promise made
    /// somewhere else entirely: DictationPhase.For (Gateway/Transcription) only ever returns one of two
    /// non-empty constants or null. That promise is kept today - the hole was NOT reachable, and this is a
    /// hardening rather than a bug fix - but it was enforced two assemblies away from the only method that
    /// depends on it, by a producer that has no idea anything hangs on it. A future phase label read from a
    /// config file, a wire payload or a new producer would break the invariant without touching this file,
    /// and it would surface as a session labelled with the empty string.
    ///
    /// It mattered because a blank label was the ONE reachable-looking hole in Car Mode's old fallback
    /// chain, <c>StateLabel ?? (EffectiveColor ?? StatusColor)</c> - a chain that ended by SPEAKING the
    /// Director's cooked colour. Closing the hole here is what let that chain be deleted as provably dead
    /// rather than argued about: fix the producer, and the fallback has nothing left to catch.
    ///
    /// Asked by BOTH fold arms, so the dot and the label cannot disagree about whether a dictation exists.
    /// A blank reaching only one of them would paint an orange dot beside a label that had fallen through
    /// to "Idle" - a row contradicting itself, which is this mission's whole defect class.
    /// </summary>
    private static string? DictationPhaseLabel(SessionDto s) =>
        string.IsNullOrWhiteSpace(s.DictationStatus) ? null : s.DictationStatus;

    /// <summary>
    /// Issue #553: true while a VOICE-MODE waiting session does not yet have playable audio - either it is
    /// actively generating its spoken summary (<see cref="SessionDto.VoiceGenerating"/>) OR there is simply
    /// no audio ready yet (<c>!VoiceAudioReady</c>). The roster holds YELLOW ("preparing voice") the WHOLE
    /// time - across the gaps between generation attempts, not just while one is in flight - until the audio
    /// is ready.
    ///
    /// Voice mode is a FIRST-CLASS state, not an overlay on a red session. A voice session that has finished
    /// its turn does need the user, but until the voice is ready there is nothing to act on - so it presents
    /// as "needs you, preparing voice" (yellow), and you can read the text if you choose. It becomes red only
    /// once <see cref="SessionDto.VoiceAudioReady"/> is true - now there is something to play and act on.
    ///
    /// OWNER'S RULING, 2026-07-19: in voice mode the user must NEVER see red until the voice is available.
    /// This restores the <c>|| !VoiceAudioReady</c> hold that was removed on 2026-07-08. That removal narrowed
    /// the hold to <see cref="SessionDto.VoiceGenerating"/> ALONE to make it wedge-proof - the yellow could
    /// never get stuck - but it fell back to red in EVERY gap between retry attempts, so a phone in voice mode
    /// flashed red while its voice was still on the way. The wedge the 2026-07-08 change feared (a permanent
    /// text-to-speech failure sitting yellow forever, because "no audio yet" and "gave up" were the same
    /// value) is NOT re-introduced by widening this COLOR rule. It is prevented where it belongs: by making
    /// voice generation reliable (a sub-minute average; anything over three minutes is an exception to be
    /// flagged and fixed) and, separately, by giving voice a terminal "gave up" state. So do NOT re-narrow
    /// this to VoiceGenerating alone to "fix" a session stuck yellow - a stuck session is a voice-reliability
    /// bug, not a color bug, and narrowing the color only hides it behind a red flicker again.
    ///
    /// Gated on raw red and on WaitingForInput/WaitingForPerm so a working (blue) session is untouched.
    /// </summary>
    public static bool IsVoicePreparing(SessionDto s)
    {
        if (!s.VoiceMode) return false;
        // Issue #1177 (Phase 2): gate on the RAW activity color (from ActivityState), not the Director's
        // cooked StatusColor.
        //
        // This comment used to add: "Equivalent today (StatusColor=="red" iff the raw activity is
        // Waiting/Idle)". THAT IFF IS FALSE, and it was asserted rather than checked. The cooked colour has
        // a SECOND writer that never goes through the activity mapping at all: TransientErrorAutoResume
        // (Core/Wingman) writes StatusColor.Red with StatusColorSource.PositiveEvidence when auto-resume
        // gives up, described in its own comment as "sticky over the detector's plain activity-state
        // mapping until the user acts". So cooked-red can stand while the raw activity says otherwise.
        // Raw is the authority here - not because the two agree, but because the fold says raw wins.
        if (!IsRawRed(s)) return false;
        // A BRAND-NEW SESSION IS NEVER "PREPARING VOICE" - it is READY (green), and this arm must not
        // eat that (owner's ruling, 2026-07-27). A session that has taken no turn has produced no
        // assistant reply, so there is no turn to narrate: no generation will ever be attempted, no
        // audio will ever land, and the `|| !VoiceAudioReady` hold below would therefore be permanent.
        //
        // The bug this closes: green lives in BaseColor, the LAST arm of EffectiveColor, BELOW the
        // IsVoicePreparing arm. So with voice mode ON, every freshly-spawned session folded to yellow
        // "Preparing voice" and STAYED there - the green "Ready" state was unreachable for the entire
        // voice-mode fleet. "Preparing voice" was also simply false about it: nothing was being prepared.
        //
        // Not caught for the same reason it was easy to write: the brand-new-is-green tests build their
        // session with a helper that leaves VoiceMode at its default false, so green was only ever proven
        // for the voice-OFF case. The voice-mode variants now live beside them.
        if (s.IsBrandNew) return false;
        var state = s.AssessedState ?? s.ActivityState;
        var waiting = string.Equals(state, "WaitingForInput", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(state, "WaitingForPerm", StringComparison.OrdinalIgnoreCase);
        if (!waiting) return false;
        // THE HOLD ENDS WHEN THE PROMISE DOES (owner's amendment, 2026-09-15).
        //
        // The hold exists to promise a narration is coming, and it is right for as long as one is. When
        // this row's own voice verdict says none is coming, there is no promise left to keep and the
        // session falls through to its base colour - red - so it asks for the owner like any other stopped
        // session, and NeedsYouSince starts running.
        //
        // READ, NOT RE-DERIVED. VoiceDisplayFold already answered this question for this row, against a
        // real clock, under its own tests. Asking it again here - from VoiceWaitingSince and a threshold -
        // would be a second answer to one question, and this file's history is made of those. It also keeps
        // this fold CLOCK-FREE, which is why every rule in it can be tested at any instant.
        //
        // WHAT THIS AMENDS. The 2026-07-19 ruling was "in voice mode the user must NEVER see red until the
        // voice is available", and the summary above still describes the wedge it feared as prevented by
        // "giving voice a terminal gave-up state". That state was built - VoiceDisplayFold.GaveUpAfter, three
        // minutes - and it was wired to the WORDS and never to the DOT, so the sentence was true of the label
        // and false of the colour. On 2026-09-16 a session sat yellow for 48 minutes carrying the label
        // "Voice did not arrive after 48m" while its raw fact was red. The owner's amendment, that day:
        // "make sure that all sessions get close to 'need you' if transcription fails".
        //
        // NARROW. Only the two verdicts that report NOTHING IS COMING end the hold. Every calm verdict -
        // preparing, retrying, notReady - still holds yellow, so nothing flashes red in the gaps between
        // attempts, which is what the 2026-07-19 ruling was protecting. nothingToNarrate still holds, because
        // a session parked on a menu was never going to be narrated and that is not a voice failure. The
        // blocked and serviceDown verdicts still hold, because they already carry an actionable sentence of
        // their own and "needs you" would replace it with a symptom.
        if (IsVoiceGaveUp(s))
            return false;
        // Yellow while generating OR while there is simply no audio yet - held across the gaps between
        // attempts, until VoiceAudioReady flips true, or until the verdict above ends the promise.
        return s.VoiceGenerating || !s.VoiceAudioReady;
    }

    /// <summary>
    /// IS SOMETHING ALIVE HOLDING THIS SESSION? - the one question that decides whether a stopped session is
    /// allowed to ask for the owner.
    ///
    /// ONE INPUT: <see cref="SessionDto.HasLiveSupervisor"/>, resolved against the whole fleet by
    /// <c>FleetRoleResolver</c> every pass. A session is quietened because a supervisor is BREATHING, and for
    /// no other reason.
    ///
    /// THE RULE THIS REPLACED ASKED ABOUT TYPE, AND THAT IS THE OWNER'S RULING OF 2026-09-13. It read:
    ///
    ///     IsSupervised = SessionRole == Worker || OriginKind == "schedule"
    ///
    /// Both of those are stamped at BIRTH. So the fleet went quiet about a session because of the category it
    /// was born into rather than because anyone was there, and on 13 September six sessions sat grey and
    /// labelled "Snoozed" with nothing snoozed - five of the six had nobody who would ever come for them. The
    /// owner's words: "I think it is wrong that somebody without a parent can automatically be put on
    /// snooze because nobody knows they existed."
    ///
    /// The two old arms, and why neither survives as a special case:
    ///  - <b>The Worker seat.</b> It was a PROXY for live supervision, and a leaky one. The role is derived
    ///    as "controlled AND the controller is alive", so it carried the right answer - until a seat was
    ///    stamped by hand, which short-circuits the derivation entirely. An explicitly stamped Worker kept
    ///    the seat, and the quiet, after its supervisor had exited. Reading the liveness answer directly
    ///    removes the proxy rather than patching it.
    ///  - <b>The schedule origin.</b> A cron firing has no supervisor, so under this rule it SURFACES. That
    ///    reverses the older ruling that scheduled runs escalate by email and never sit red on the roster.
    ///    The owner settled it on 2026-09-13: let them go red. A session nobody is holding is the owner's,
    ///    whatever started it, and an email path that nothing on the roster can attest to is not a supervisor.
    ///
    /// NO ROLE IS SUPERVISED ANY MORE. Worker, Manager, Architect and Standalone are all just seats: they
    /// name what a session is for, they group the fleet, and they have no vote here. Neither does the origin.
    /// A Worker whose supervisor is alive is held; the same Worker an hour after its supervisor died is the
    /// owner's, and nothing has to notice or repair that - the answer is recomputed every pass.
    ///
    /// FAILS TOWARD THE OWNER. <see cref="SessionDto.HasLiveSupervisor"/> defaults to false, so a path that
    /// folds without resolving the fleet first answers "not supervised" and the session SURFACES. The wrong
    /// answer this can give is a session asking for him when somebody had it; the wrong answer it cannot give
    /// is silence over a session nobody had.
    ///
    /// Reads only a fact already on the wire. It adds no state, arms no timer and writes nothing.
    ///
    /// THE WRITTEN RULE AND THIS METHOD ARE MACHINE-CHECKED AGAINST EACH OTHER by
    /// <c>SupervisionRuleMatchesTheDesignDocumentTests</c>, which reads the attention table out of
    /// <c>docs/new_architecture/sessions.html</c> and fails when the document and this method
    /// disagree about any case. That guard exists because an earlier amendment sat in the document,
    /// unimplemented and uncontradicted, for two months: every test of the day asserted the SHIPPED
    /// behaviour in the present tense, so a document saying something else could not make anything go red.
    /// </summary>
    public static bool IsSupervised(SessionDto s) => s.HasLiveSupervisor;

    /// <summary>
    /// True when a supervised session has STOPPED and must therefore present as snoozed (owner's ruling,
    /// 2026-09-02: "supervised still show up in Director and Cockpit, session should go to onhold when not
    /// working").
    ///
    /// THIS IS THE WHOLE OF THE NEW RULE, and it is deliberately one predicate read by all three fold arms
    /// so the dot, the words and the bucket cannot disagree about it - the standing requirement in
    /// docs/new_architecture/sessions.html.
    ///
    /// EXITED IS EXCLUDED, and it is not an oversight. The owner already ruled the identical question for
    /// the owner's own snooze (defect 21): "a dead session must never hide behind a Snoozed label". A
    /// supervised session that has exited reads Exited - or Crashed, which is the one thing on this whole
    /// ladder nobody may ever quieten.
    ///
    /// WORKING IS EXCLUDED BY THE CALLERS, not here, because every caller already answers it first: nothing
    /// outranks working. Do NOT add a working check to this method - a second answer to that question is
    /// exactly the defect this file's history is made of.
    /// </summary>
    private static bool IsSupervisedAndStopped(SessionDto s) =>
        IsSupervised(s) && !Is(s.ActivityState, "Exited");

    /// <summary>Case-insensitive activity-state comparison, shared by every reader of the field in this
    /// file. Named once here because a second, ordinal copy six lines from a case-insensitive one is how
    /// this file previously came to compare the same field two different ways.</summary>
    private static bool Is(string? value, string name) =>
        string.Equals(value, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the Director's terminal sensor says bytes are flowing: the session IS WORKING.
    /// This is the top of the ladder and the only rule that cannot be overridden.
    ///
    /// Read from the RAW activity fact only. Nothing else may contribute - not hold, not
    /// dictation, not briefing, not role. Those answer "why is it NOT working?", which is a
    /// question that only means anything once this returns false.
    /// </summary>
    /// <summary>
    /// PUBLIC face of <see cref="IsWorking"/> - "is this session mid-turn?" - for the one caller outside this
    /// file that needs it: the fold pass, which lowers a raised hand the moment its worker stops (#2662).
    ///
    /// It delegates rather than re-testing the field, so there is still exactly ONE definition of working in
    /// the system. A second copy would be a second answer to the question the whole ladder is built on, and
    /// this file's history is a list of what that costs.
    /// </summary>
    public static bool IsWorkingSession(SessionDto s) => IsWorking(s);

    private static bool IsWorking(SessionDto s) =>
        string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s.ActivityState, "Starting", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The ONE effective status color every client renders and triages on (issue #196).
    ///
    /// THE LAW (owner's ruling, 2026-07-14, restated and final): IF A SESSION IS WORKING, IT IS
    /// BLUE. ALWAYS. NOTHING OUTRANKS WORKING. There are no exceptions and none may be added.
    /// If you are about to add a rule above the working check - don't. Every rule that ever sat
    /// above it has been a defect, and each one cost the owner a day.
    ///
    /// Everything below the working check answers a single question: "why is this session NOT
    /// working?" Grey = parked. Orange = its dictation is in flight.
    /// Yellow = the wingman is reading the finished turn, or voice is generating. Those are all
    /// states of a session that has STOPPED. A working session has stopped being none of them,
    /// so they cannot apply to it - that is not a policy choice, it is what the words mean.
    ///
    /// The Director stamps the raw facts; the Gateway stamps <see cref="SessionDto.BriefingState"/>
    /// on top. Folding them HERE - instead of in each view - is what keeps the dot, the
    /// "wingman reading..." chip, and the triage bucket atomic across every screen.
    /// </summary>
    public static string EffectiveColor(SessionDto s) =>
        // ===== ORDER 0: WORKING IS BLUE. NOTHING GOES ABOVE THIS LINE. =====
        IsWorking(s) ? "blue"
        // ===== Everything below applies ONLY to a session that is NOT working. =====
        : s.OnHold ? "grey"
        // Transcribing orange fires for ANY dictation source: the Task 4 phase label (mobile Speak -
        // "Uploading from phone" or "Transcribing", s.DictationStatus), the legacy Gateway flag
        // (s.Transcribing), OR the Director raw fact (desktop dictation, s.IsTranscribing). All orange.
        // It marks "a dictated utterance is in flight, do not grab this session" - which is only
        // meaningful at a prompt. Mid-turn it is invisible anyway: blue already won above.
        : (DictationPhaseLabel(s) != null || s.Transcribing || s.IsTranscribing) ? "orange"
        // SUPERVISED AND STOPPED -> SLATE (owner's ruling, 2026-09-02). A session that answers to another
        // session or to a schedule has nobody to ask when it stops, so it presents as snoozed: it keeps its
        // row on every screen, and it sinks out of the owner's queue. An Architect is NOT one of these - see
        // IsSupervised for the owner's 2026-09-06 ruling that put the design seat back in front of him.
        //
        // ABOVE the two yellows on purpose. A supervised session must never read "Wingman reading" or
        // "Preparing voice" at the owner, and this arm must be true on its own rather than by trusting the
        // wingman not to have started - the fold's correctness cannot rest on a producer's good behaviour,
        // which is the lesson of the voice-enrichment hole (#1843).
        //
        // BELOW dictation orange, also on purpose: an utterance in flight means the OWNER is speaking into
        // this session right now. He is driving it, so the row must say so - the same reasoning that lets
        // an owner's own words supersede an owner's own snooze.
        //
        // It cannot touch a working session: order 0 returned already. Slate rather than the snooze grey
        // because the two are genuinely different facts - grey is "YOU parked this, and a timer will bring
        // it back", slate is "this one is not yours to watch". Four indistinguishable greys is a defect this
        // document already carries (17); adding a fifth to save a colour would be the wrong trade.
        : IsSupervisedAndStopped(s) ? "supporting"
        // THERE IS NO "EXPLAINING" ORANGE ARM HERE, AND THERE NEVER WORKED ONE. This used to read
        // `: IsExplaining(s) ? "orange"`, gated on BriefingState == "Explaining" (issue #217's
        // user-initiated "I am lost - explain" deep dive). #217's roster orange has never once worked -
        // it never fired, in any release, because no code path could produce the value it gates on:
        //   * SessionDto.BriefingState is stamped ONLY from the Director's BriefingState enum
        //     (ControlEndpoints.ToDto, `s.BriefingState.ToString()`), and that enum declares exactly
        //     None / Briefing / Briefed / Failed. "Explaining" is not a member, so it is unreachable.
        //   * The string exists only in the TurnBriefs pane's own response family, and even THERE it is
        //     dead: the live wiring (GatewayHost, TurnBriefGatewayEndpoints.Map) passes a briefingStateFor
        //     that returns only "Briefed" or "None", and passes requestExplainAsync: null - so the deep
        //     dive's request route answers 503 and the state is switched off at the composition root.
        // Do NOT "restore" this rule. Making it fire is not a bug fix, it is a feature: a request path, a
        // state producer, and a new value on the wire, with a product decision behind it.
        //
        // The Director's LEGACY auto-explain is a SEPARATE, WORKING feature and is untouched: it rides on
        // the raw fact SessionDto.IsAutoExplaining and folds to YELLOW in ResolveActivity below. Do not
        // conflate the two - deleting this orange did not delete that yellow.
        // The DIRECTOR's own briefing (its BriefingState) and the GATEWAY's voice generation (its
        // VoiceGenerating, folded by IsVoicePreparing) are separate facts with separate owners, and each
        // has exactly one rule. The Gateway used to reach the first arm by overwriting the Director's field
        // rather than letting the second arm do its job - same yellow, destroyed evidence, wrong words
        // (gap 5). Do not add a third rule for either fact.
        : IsBriefing(s) ? "yellow"
        : IsVoicePreparing(s) ? "yellow"
        // THE WINGMAN'S VERDICT ARMS (the Wingman-on-every-turn mission, slice D).
        //
        // Reading: the Gateway's turn-verdict seat is forming a verdict about this stop. Beside IsVoicePreparing
        // because it is the same kind of fact - the Gateway is working on a stopped session and does not yet know
        // what it needs - and below the two earlier yellows so the Director's own briefing and a voice session's
        // missing audio keep the words they already had.
        : IsVerdictReading(s) ? "yellow"
        // Calm: the Wingman judged the stop a REPORT, with high confidence. Cyan "Done" for finished, purple
        // "Carrying on" for continues-alone. BELOW everything above - working, a snooze, a dictation in flight, a
        // supervised session and both yellows each describe something a verdict does not outrank - and ABOVE
        // BaseColor, whose red is the one colour a verdict may calm. The four gates are in IsCalmVerdict. Classify
        // reads this colour, so a calm row leaves the needs-you bucket, the needs-you clock and every needs-you
        // count without any of them learning a rule.
        //
        // PURPLE HAS ONE PRODUCER, and it is this line. The Director's "background running" purple in
        // ResolveActivity is deleted - see the tombstone there.
        : IsCalmVerdict(s) ? CalmColor(s)
        // THE SNOOZE-EXPIRY ARM (the Wingman-on-every-turn mission, slice F, ruling 10). The owner's quiet ran
        // out and nothing happened while it did, so the row comes back calm instead of as a red the clock
        // manufactured. BELOW the verdict arms on purpose: a stop that WAS judged while the snooze ran is ruled
        // by its verdict, and this arm only ever speaks when there is nothing to rule on. ABOVE BaseColor,
        // whose red is the colour it exists to replace.
        // THIS LADDER IS RANKED BY WHAT THE READER CAN DO, NOT BY WHEN AN ARM WAS ADDED, and this pair is
        // where that rule was first written down - read it before adding an arm below.
        //
        // A FAILED NARRATION OUTRANKS A QUIET SNOOZE. Both arms describe the same raw-red row and only one of
        // them can speak. "Snooze ended, nothing new" is TERMINAL: it is the end of the story, and there is
        // nothing for the owner to do about it. A narration that is never coming is ACTIONABLE: he was
        // promised he would HEAR this session, he did not, and he has to go and look instead. A terminal
        // verdict must never outrank an actionable one, so the voice arm goes above (the Architect's ruling,
        // 2026-09-16, on the collision found when these two changes met in a rebase).
        //
        // IT WAS NOT MERELY OUTRANKED, IT WAS UNREACHABLE. The snooze arm returned cyan here, and the voice
        // words are only consulted inside the RED branch of StateLabel - so with the cyan winning the colour,
        // "Voice did not arrive after 48m" could never be reached by any row. Ordering the arms is what makes
        // the words reachable at all, which is why this is a ladder fix and not a preference.
        //
        // The arm yields the BASE colour rather than naming red, because the base is already the honest answer
        // for such a row - it is red when the session is stopped, and it stays whatever it should be when the
        // session is working or exited. What this arm actually does is keep the snooze arm from speaking over
        // it, and a row with no expired snooze reaches the same base colour one line further down regardless.
        : IsVoiceGaveUp(s) ? BaseColor(s)
        : IsSnoozeEndedNothingNew(s) ? SnoozeEndedNothingNewColor
        // Issue #1177 (Phase 2): the base color is computed from RAW facts. NO GATEWAY-DECIDED COLOUR READS
        // THE DIRECTOR'S COOKED StatusColor - as of 2026-07-14 that is true of the pipeline as well as the
        // fold. It was NOT true before: the Gateway's voice-mode window (GatewayEndpoints, issue #531) gated
        // its BriefingState = "Briefing" stamp on s.StatusColor == "red", so a yellow rendered on the phone
        // and the Cockpit depended on a decision the Director made. That was the last Gateway consumer of
        // the cooked colour and it is now IsRawRed.
        //
        // Do NOT read that as "the cooked colour is dead" and delete the Director's colour computation. It
        // is not, and it is not ours to delete: the cooked field still crosses the wire, the ?statusColor=
        // query filter still selects on it, and several desktop surfaces still read it. What is now true is
        // narrower and is the part law 2 cares about - the Gateway decides its colours from raw facts alone.
        : BaseColor(s);

    // ===== Issue #1177 (Phase 2): the raw-fact base color (a port of the Director's SessionStatusWingman
    // ColorFromActivityState, computed from the wire's raw facts). The method named here used to be
    // "ColorFor", which does not exist and never did - the fifth copy of that wrong name in the codebase. =====

    /// <summary>
    /// The base presentation color from raw facts: the activity color with the purple (background),
    /// green (brand-new), and Director auto-explain (yellow) turn-end overlays, plus the slate
    /// "Supporting" overlay that suppresses a controlled Worker's RED. The briefing overlay is applied
    /// by <see cref="EffectiveColor"/> above (it wins before this is reached), so it is intentionally
    /// not repeated here.
    ///
    /// A WORKING session is BLUE, always - nothing outranks working (owner's ruling, 2026-07-14). This
    /// used to open with a slate overlay that returned "supporting" for ANY controlled session that was
    /// not red, which DISCARDED the real activity state: a controlled sub-agent 23 minutes into real
    /// work rendered gray and read "Sub-agent", indistinguishable from on-hold or exited. That rule
    /// implemented the 2026-07-10 decision in issue #1286 ("a controlled worker always shows the
    /// recessive Supporting colour"), which the owner has since VOIDED. Do not restore it.
    ///
    /// Ownership - who is driving a session - travels on the rail's role badge, a separate channel.
    /// Color says what a session is DOING and must never be spent saying who owns it.
    /// </summary>
    private static string BaseColor(SessionDto s)
    {
        // THE WORKER RED-SUPPRESSION ARM THAT USED TO LIVE HERE HAS MOVED UP THE LADDER, NOT BEEN DELETED.
        // It read: a live-controlled Worker that is red recedes to "supporting" instead of surfacing. The
        // owner widened that rule on 2026-09-02 - it now covers every SUPERVISED session (a Worker with a
        // live supervisor, or a scheduled run; an Architect was in that set until the owner took it out again
        // on 2026-09-06), and it puts them in the parked bucket rather than merely recolouring them. A rule
        // that answers "may this session ask the owner?" belongs beside the other rules that suppress
        // attention, above the two yellows, not at the bottom underneath them; down here it could recolour a
        // row the wingman had already claimed for a narration nobody wanted.
        //
        // So "supporting" still has exactly one producer and every consumer of it still has one - it is
        // IsSupervisedAndStopped in EffectiveColor. Do not re-add a role check here: two producers of one
        // colour, agreeing today and drifting tomorrow, is the defect class this file's history is made of.
        return ResolveActivity(s);
    }

    /// <summary>The activity color plus the turn-end overlays the Director bakes: auto-explain yellow and
    /// brand-new green. Order matches the Director's <c>ResolveActivityColor</c>.</summary>
    private static string ResolveActivity(SessionDto s)
    {
        var atTurnEnd = IsAtTurnEnd(s);
        // Legacy auto-explain (ProactiveExplainService): yellow while WingmanEnabled and at a turn-end.
        if (s.WingmanEnabled && s.IsAutoExplaining && atTurnEnd) return "yellow";
        // THE "BACKGROUND RUNNING" PURPLE THAT USED TO LIVE HERE IS DELETED (the Wingman-on-every-turn mission,
        // ruling 3). It read `WingmanEnabled && IsBackgroundRunning && atTurnEnd -> purple`, and its only feed is
        // the Director's ProactiveExplainService, which is switched off - a dead producer of the colour the calm
        // arm in EffectiveColor now owns. Two producers of one colour, one alive and one dead, is the defect class
        // this file's history is made of. IsBackgroundRunning stays on the wire as a raw fact; no colour reads it.
        // Do not restore this arm.
        //
        // Brand-new, has not yet taken a turn: green ("ready").
        if (s.IsBrandNew && atTurnEnd) return "green";
        return RawActivityColor(s);
    }

    /// <summary>The pure activity-state color. Starting/Working -&gt; blue; Waiting/Idle -&gt; red;
    /// Exited -&gt; "grey" (Phase 2.3, owner-approved: an exited session shows the SAME grey string as an
    /// OnHold one, so clients render it identically), EXCEPT a crashed one -&gt; "error" (issue #959: the
    /// deep red #B91C1C, deliberately darker than the bright "needs you" red, so a session that DIED is
    /// never mistaken for one that finished). This deliberately DIVERGES from the Director's standalone
    /// <c>ColorFromActivityState</c>, which keeps exited as "unknown" - the Gateway is the single source of
    /// truth for the fold. Any unrecognized state -&gt; "unknown".
    ///
    /// The crash arm reads <see cref="SessionDto.Crashed"/>, NOT the activity state: a crash was never
    /// modelled in ActivityState (a crashed session is "Exited" like any other), which is exactly how the
    /// deep red went missing for two releases - this fold reads raw facts, and the crash fact was not on
    /// the wire to read.
    ///
    /// Case-INSENSITIVE, matching every other reader of <see cref="SessionDto.ActivityState"/> in this
    /// file (<see cref="IsWorking"/>, <see cref="IsAtTurnEnd"/>, <see cref="IsVoicePreparing"/>, and the
    /// role rule in <see cref="BaseColor"/>). This used to be a C# constant-pattern switch, which is
    /// ORDINAL and case-SENSITIVE - so one file compared the same field both ways, six lines apart inside
    /// <see cref="IsVoicePreparing"/>. It could not fire today: the sole producer of the field
    /// (the Director's ToDto, `s.ActivityState.ToString()` over the ActivityState enum) emits exact
    /// PascalCase. This change therefore fixes NO observed bug - it removes a trap. Had a second producer
    /// ever emitted "waitingforinput", the failure would have been silent and would have eaten a red: the
    /// turn-end overlays would fire (they are case-insensitive) while this returned "unknown", rendering a
    /// session that needs the human as "Idle" with no red at all.</summary>
    private static string RawActivityColor(SessionDto s)
    {
        if (Is(s.ActivityState, "Starting") || Is(s.ActivityState, "Working")) return "blue";
        if (Is(s.ActivityState, "WaitingForInput") || Is(s.ActivityState, "WaitingForPerm")
            || Is(s.ActivityState, "Idle")) return "red";
        if (Is(s.ActivityState, "Exited")) return s.Crashed ? "error" : "grey";
        return "unknown";
    }

    /// <summary>True when the session is parked at a turn-end (WaitingForInput / WaitingForPerm), the
    /// gate the Director uses for its purple/green/auto-explain overlays.</summary>
    private static bool IsAtTurnEnd(SessionDto s) =>
        string.Equals(s.ActivityState, "WaitingForInput", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s.ActivityState, "WaitingForPerm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Issue #1177 (Phase 2): the ONE human-readable state label every client renders, computed by the
    /// Gateway from the same fold inputs as <see cref="EffectiveColor"/> (so the dot color and its label
    /// never disagree). Consolidates the label logic each client used to hand-roll.
    /// </summary>
    public static string StateLabel(SessionDto s)
    {
        // ORDER 0: WORKING. Mirrors EffectiveColor exactly - the label and the dot are folded from the
        // same inputs in the same order, so they cannot disagree. A snoozed session that starts working
        // is blue AND reads "Working"; it must never be a blue dot labelled "Snoozed".
        if (IsWorking(s)) return "Working";
        if (s.OnHold) return "Snoozed";
        // Issue #1181, Task 4: the honest phase label wins - "Uploading from phone" while the phone is still
        // sending the audio, "Transcribing" while the server turns it into text. Falls back to the blanket
        // "Transcribing" for the legacy flag / the desktop's own dictation.
        if (DictationPhaseLabel(s) is { } dictationPhase) return dictationPhase;
        if (s.Transcribing || s.IsTranscribing) return "Transcribing";
        // Mirrors EffectiveColor's supervised arm, in the same position, so the dot and the words are folded
        // from the same inputs in the same order. "Snoozed" is the owner's own word for this state and it is
        // true of both kinds; the row's role badge says WHOSE it is.
        //
        // This retires the label "Sub-agent", which was the answer here while the rule fired only for a
        // Worker. It described the session's PARENTAGE where every other label on this ladder describes its
        // STATE, and it was plainly wrong for the scheduled run this arm also covers - a cron firing is not a
        // sub-agent. The slate dot still carries "not yours to watch"; the word now carries what the session
        // is actually doing, which is nothing.
        if (IsSupervisedAndStopped(s)) return "Snoozed";
        // No "Explaining" arm: BriefingState can never be "Explaining" - see the tombstone in
        // EffectiveColor above. The label and the dot are folded from the same inputs in the same
        // order, so this deletion keeps them in lockstep.
        // Mirrors EffectiveColor's arms exactly, in the same order, so the label and the dot cannot
        // disagree. "Wingman reading" is the DIRECTOR's briefing; "Preparing voice" is the GATEWAY's voice
        // generation. The Gateway's old BriefingState overwrite made a voice-generating session take the
        // first arm and read "Wingman reading" - the wrong words, on top of a destroyed fact (gap 5).
        if (IsBriefing(s)) return "Wingman reading";
        if (IsVoicePreparing(s)) return VoiceHoldLabel(s);
        // Mirrors EffectiveColor's two verdict arms, in the same order. A calm row reads the Wingman's own line
        // verbatim - the report - and "Done" or "Carrying on" only when the verdict carries no line at all.
        if (IsVerdictReading(s)) return "Wingman reading";
        if (IsCalmVerdict(s)) return CalmLabel(s);
        // Mirrors EffectiveColor's voice-gave-up arm, in the same position and for the same reason: a session
        // that was promised a narration and did not get one has something the owner can act on, and the snooze
        // words would say the opposite. It falls to the base switch below, where VoiceGaveUpLabel tells him WHY
        // he is being asked by a session he expected to hear instead.
        if (IsVoiceGaveUp(s)) return BaseStateLabel(s);
        // Mirrors EffectiveColor's snooze-expiry arm, in the same position, so the dot and the words are folded
        // from the same inputs in the same order.
        if (IsSnoozeEndedNothingNew(s)) return SnoozeEndedNothingNewLabel;
        return BaseStateLabel(s);
    }

    /// <summary>
    /// The words for a row that reached the bottom of the ladder, folded from its BASE colour. Split out so the
    /// voice-gave-up arm can reach it without repeating it: two copies of this switch would be two answers to
    /// one question, which is the defect this file keeps being repaired for.
    /// </summary>
    private static string BaseStateLabel(SessionDto s)
    {
        return BaseColor(s) switch
        {
            // NO "supporting" ARM. It is not missing - it is unreachable, and saying so here is cheaper than
            // the next reader re-deriving it. BaseColor no longer produces that colour (see the tombstone in
            // it), and the only thing that does - the supervised arm - already returned "Snoozed" above this
            // switch. A "supporting" => something mapping here would be a second answer to a question that
            // has already been answered, which is how this file previously came to label a blue dot "Snoozed".
            //
            // NO "purple" ARM EITHER. BaseColor no longer produces purple (the background arm is deleted), and
            // the calm arm that does has already returned its label above.
            "green" => "Ready",
            "yellow" => "Wingman reading",   // Director auto-explain base yellow
            "blue" => "Working",
            // A red row the Wingman has judged carries the Wingman's own line where "Needs you" was (ruling 3):
            // the ask, an ambiguous report that stayed red, or the carrying-on clock's "Said it would continue
            // and did not". A row with no accepted verdict - never judged, refused, or an account whose colour
            // switch is off - still reads "Needs you".
            // A red row whose VOICE gave up keeps the fold's own words - "Voice did not arrive after 48m",
            // "Turn not narrated" - rather than a bare "Needs you". Going red is what makes the owner look;
            // these words are what tell him WHY he is being asked by a session he was promised he would hear
            // instead. Below JudgedLabel on purpose: when the Wingman managed to say what the session needs,
            // that is the more useful sentence, and a failed narration is the reason he is reading it at all.
            "red" => JudgedLabel(s) ?? VoiceGaveUpLabel(s) ?? "Needs you",
            "grey" => "Exited",              // Phase 2.3: an exited session's grey base (see RawActivityColor)
            "error" => "Crashed",            // issue #959: died, not finished - never reads as a clean "Exited"
            _ => "Idle",
        };
    }

    /// <summary>
    /// The WORDS for a session the voice hold is keeping yellow - "Preparing voice" only when something is
    /// genuinely being prepared, and the honest reason otherwise.
    ///
    /// WHY THIS IS NOT A SECOND RULE. <see cref="IsVoicePreparing"/> - the COLOR - reads two booleans, and
    /// deliberately so: yellow is held across the gaps between attempts and must never flash red (owner's
    /// ruling, 2026-07-19, see that method). But those two booleans are also the ONLY thing the label had,
    /// so every reason a session has no audio came out as one sentence claiming work was in flight. On
    /// 11 August a session sat on "Preparing voice" for 48 minutes while the Gateway's OWN verdict for it,
    /// on the same row, was "Nothing to read aloud" - a state that would never become audio. Another row
    /// carried the label "Preparing voice" beside a "No voice" chip, because the chip reads a fact the label
    /// could not see. See issue #2576.
    ///
    /// The verdict already exists and is already on the row: <see cref="SessionDto.VoiceDisplay"/>, folded
    /// by the Gateway's VoiceDisplayFold from all six voice facts. So this does not compute anything - it
    /// RENDERS the words that fold already chose, which is what keeps this from becoming a second answer to
    /// the same question. The dot is unchanged; only the words are.
    ///
    /// Deliberately narrow: it defers ONLY for the verdicts that mean "no audio, and here is why", and
    /// falls back to "Preparing voice" for everything else - including a row carrying no verdict at all
    /// (the display-push seam does not stamp one, and an older client may not send one either).
    /// </summary>
    private static string VoiceHoldLabel(SessionDto s)
    {
        var display = s.VoiceDisplay;
        if (display is null || string.IsNullOrWhiteSpace(display.Label)) return "Preparing voice";

        // THE WAIT, ON THE RAIL (issue #2576). A row that says "Preparing voice" reads identically at two
        // seconds and at forty-eight minutes, and a session really did sit at forty-eight with nobody able
        // to tell. The Gateway already holds when the wait began and has already turned it into words
        // (VoiceDisplay.WaitedLabel, null under a minute), so this only renders them - the healthy case
        // still reads plain "Preparing voice", and a wait worth noticing carries its own age.
        if (string.Equals(display.Kind, "preparing", StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(display.WaitedLabel)
                ? "Preparing voice"
                : $"Preparing voice ({display.WaitedLabel})";
        return display.Kind switch
        {
            // The THREE verdicts whose own words would be wrong ON THE RAIL, named explicitly:
            //  - "preparing" is the case the existing words are already right about.
            //  - "ready", "off" and "working" cannot honestly reach here at all: this arm runs only for a
            //    session the voice hold is keeping yellow, which requires voice mode, no audio, and a
            //    WAITING activity - and StateLabel has already returned "Working" above for anything
            //    actually working. So if one of them ever does arrive on a row, the safe answer is the old
            //    behaviour rather than a rail reading "Voice ready" or "Agent is working" beside a yellow
            //    dot on a session that is doing neither. ("working" was missed by the first version of this
            //    list and found in review - it is a kind VoiceDisplayFold really does emit.)
            "preparing" or "ready" or "off" or "working" => "Preparing voice",
            // EVERYTHING ELSE renders the fold's own words - including a verdict added later. The list runs
            // this way round deliberately: an allow-list of known-good kinds would mean adding a state to
            // VoiceDisplayFold and having the rail quietly keep saying "Preparing voice" about it, which is
            // a second rule wearing the shape of a default (found in review). A new state now reaches the
            // rail by being added ONCE, in the fold.
            _ => display.Label,
        };
    }

    /// <summary>
    /// THE ONE PLACE that turns "a prompt to this session did not go" into words (issue internal#811),
    /// or null when there is nothing to say. Every client renders the returned string VERBATIM - no client
    /// re-derives it, counts anything, or decides what a delivery failure means (CLAUDE.md rule 7).
    ///
    /// It speaks ONLY while the failure is UNRESOLVED - the last send threw and nothing has landed since,
    /// so the user's words are gone right now. Once a later prompt gets through, the alarm has nothing to
    /// warn about and goes quiet; the COUNTS stay on the row, because "this happened four times today" is a
    /// fact worth keeping and a lucky retry must not erase it.
    ///
    /// Why this is a notice and not a colour: the session's colour says what the AGENT is doing, and the
    /// agent is doing exactly what it was doing before - it never heard anything. Recolouring it would say
    /// something false about the agent to say something true about the delivery. The notice says the true
    /// thing in its own words.
    /// </summary>
    public static string? PromptDeliveryNotice(SessionDto s)
    {
        if (!s.PromptDeliveryUnresolved) return null;

        var reason = (s.LastPromptDeliveryFailureReason ?? "").Trim();
        return reason.Length == 0
            ? "Your last prompt was not delivered - the agent never received it."
            : $"Your last prompt was not delivered - the agent never received it. {reason}";
    }

    /// <summary>
    /// THE WORDS FOR AN ORPHAN (owner's orphan ruling, 2026-07-09), or null when there is nothing to say.
    ///
    /// A session that was being driven by another session, whose driver has now gone, is the ONE case where
    /// a session the fleet was handling lands back on the owner. That hand-back already works - the role
    /// resolver answers Standalone rather than Worker once the supervisor is not alive, so the suppression
    /// stops and the red surfaces. What was missing is the ruling's other half: he was told THAT something
    /// wants him and never WHY, so an orphan looked exactly like an ordinary session at a prompt.
    ///
    /// WHY IT TAKES THE SUPERVISOR RATHER THAN LOOKING IT UP. This method is pure and per-session like every
    /// other rule in this file; "which session is id X, and is it alive?" is a question about the whole
    /// fleet, and the fleet is something only the Gateway's fold pass holds. So the caller does the lookup
    /// and passes what it found - including NULL, which is itself an answer and the commonest one (a
    /// supervisor that exited long enough ago to have left the roster entirely).
    ///
    /// SILENT WHILE IT IS WORKING, and that is deliberate rather than an oversight: a session mid-turn is
    /// getting on with the work it was given and there is nothing for anyone to decide yet. It speaks when
    /// the session STOPS, which is the moment it would otherwise sit red and unexplained.
    ///
    /// It is a NOTICE, NOT A COLOUR, for the same reason the delivery notice is: recolouring would say
    /// something false about what the agent is doing in order to say something true about its supervision.
    /// The dot keeps telling the truth and this rides beside it.
    /// </summary>
    /// <param name="s">The possibly-orphaned session.</param>
    /// <param name="supervisor">The session named by its <see cref="SessionDto.ControllerSessionId"/> as the
    /// caller found it in the fleet, or null when the fleet no longer contains it at all.</param>
    public static string? SupervisorLostNotice(SessionDto s, SessionDto? supervisor)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));

        // Never had a supervisor: an ordinary session, nothing to explain.
        if (!s.IsControlled || string.IsNullOrEmpty(s.ControllerSessionId)) return null;

        // STILL SUPERVISED. Read from the RESOLVED ROLE rather than re-deciding it here, because "Worker"
        // already means "controlled AND the controller is alive" - that is the single fact the resolver
        // exists to own, and asking it a second way here would be a second answer to it.
        if (string.Equals(s.SessionRole, SessionRoles.Worker, StringComparison.OrdinalIgnoreCase)) return null;

        // AN EXPLICIT ARCHITECT THAT HAPPENS TO CARRY A CONTROLLER IS A DESIGN SEAT, NOT AN ABANDONED WORKER,
        // and this arm is CHECKED AND KEPT under the owner's 2026-09-06 ruling rather than carried over
        // unread. Its old justification died with that ruling: it used to say "it is supervised by
        // declaration, so losing a spawner strands nothing", and an Architect is no longer supervised at all.
        //
        // The behaviour is right for a reason that never depended on supervision. This notice exists to
        // explain a session that was QUIET WHILE SUPERVISED and has suddenly started asking - "nothing is
        // supervising it now, so it came back to you". An Architect never went away: it surfaces at the owner
        // the whole time, by declaration, whoever spawned it and whether or not that spawner is still alive.
        // It takes its direction from him, not from the session that happened to open it, so there is nothing
        // to be stranded from. Saying "the session that was driving this one has gone" about the seat the
        // owner talks to would be false about the seat AND would tell him to hand off or stop the one thing
        // he is holding the conversation with.
        //
        // THE ARGUMENT ABOVE RESTS ON A PRODUCT FACT, NOT ON GOOD INTENTIONS, because "the controller means
        // nothing to an Architect" is exactly the kind of claim that is true when written and quietly false
        // a year later. The fact: an Architect is the TOP of a mission. It settles the design, writes the
        // brief and hires the Manager, and the resolver enforces the direction - Manager-derivation
        // EXCLUDES Architect, so the session on the other end of that controller id is a session that
        // OPENED this one, never a seat this one answers to. That is what makes losing it provenance
        // rather than supervision.
        //
        // If that ever stops being true - if an Architect comes to take direction from the session that
        // spawned it - this arm is wrong and the answer is a notice written for the seat, not this generic
        // one. The generic sentence would still be unusable: "it came back to you" is false about a session
        // that was never away.
        //
        // The role is EXPLICIT and sticky, so this arm cannot be reached by accident: a session is only ever
        // an Architect because somebody said so.
        if (string.Equals(s.SessionRole, SessionRoles.Architect, StringComparison.OrdinalIgnoreCase)) return null;

        // Working: getting on with it. Nothing to decide until it stops.
        if (IsWorking(s)) return null;

        // Dead: it is not stranded, it is over. Saying "nothing is supervising this" about a session that
        // has itself exited would send the owner to look at wreckage twice.
        if (Is(s.ActivityState, "Exited")) return null;

        var name = (supervisor?.Name ?? "").Trim();
        var who = name.Length > 0
            ? $"The session that was driving this one (\"{name}\") has gone."
            : "The session that was driving this one has gone.";

        return who + " Nothing is supervising it now, so it came back to you - read it, then resume it, "
             + "hand it to another session, or stop it.";
    }

    /// <summary>
    /// Classify a session for triage, folded in the same order as <see cref="EffectiveColor"/> and
    /// <see cref="StateLabel"/> so all three always agree.
    ///
    /// ORDER 0: a WORKING session is Active. Snooze is a statement about a session that has STOPPED
    /// ("do not nag me about this when it finishes"); it cannot park a session that is running right
    /// now. This used to read <c>s.OnHold ? OnHold</c> first, which sank a working session into the
    /// parked bucket at the bottom of the roster while its dot was blue - the colour said working, the
    /// list said parked. Snooze still wins for a session that is NOT working, which is the case it was
    /// built for and the only case it means anything in.
    ///
    /// Uses <see cref="EffectiveColor"/>, NOT the raw Director color: a session the wingman is still
    /// reading stays in Active until the brief lands, instead of flopping into NEEDS YOU mid-brief and
    /// possibly back out (issue #196).
    /// </summary>
    public static TriageBucket Classify(SessionDto s) =>
        IsWorking(s) ? TriageBucket.Active
        : s.OnHold ? TriageBucket.OnHold
        // Supervised and stopped sinks to the parked bucket beside the owner's own snoozes (owner's ruling,
        // 2026-09-02). This is the half of the rule that does the work he actually asked for: the colour
        // stops it going red, and THIS stops it sitting in the middle of his list. Folded in the same order
        // as the colour and the label above.
        //
        // It is what keeps every downstream count honest without any of them learning a new rule: the "N
        // need you" header, the phone's web-push badge and Car Mode all select on this bucket, so all three
        // go quiet from this one line.
        : IsSupervisedAndStopped(s) ? TriageBucket.OnHold
        : EffectiveColor(s) == "red" ? TriageBucket.NeedsYou
        : TriageBucket.Active;

    /// <summary>All sessions in a given triage bucket, in desktop order.</summary>
    public static IReadOnlyList<SessionDto> InBucket(IEnumerable<SessionDto> sessions, TriageBucket bucket) =>
        InDesktopOrder(sessions.Where(s => Classify(s) == bucket));

    /// <summary>
    /// THE WAITING LINE for the needs-you group. The session that has been asking for you the LONGEST
    /// sits at the top; a session that only just started needing you drops in at the BOTTOM. This keeps
    /// the list from reshuffling under you as new work arrives and makes it a first-in, first-handled
    /// queue when you work down from the top.
    ///
    /// Ordered by <see cref="SessionDto.NeedsYouSince"/> ascending - the earliest stamp is the oldest
    /// wait - then <see cref="SessionDto.CreatedAt"/> and finally the session id, so equal waits never
    /// jitter between polls. A session with no stamp sorts to the BOTTOM: we cannot place it in the line,
    /// so it never jumps ahead of a session with a real wait time.
    ///
    /// IT DELIBERATELY IGNORES <see cref="SessionDto.SortOrder"/>. The needs-you group is a queue by wait
    /// time, not the owner's manual arrangement - which is the whole difference between the attention
    /// order and my order.
    ///
    /// The port of <c>inWaitingOrder</c> in packages/client-core/src/sessions/ordering.ts, and the tie
    /// breaks are ORDINAL where the TypeScript's are locale-aware. Session ids are hexadecimal or numeric
    /// and timestamps are ISO 8601, so the two agree on every value this field can carry - and ordinal is
    /// the one that cannot change answer with the machine's locale.
    /// </summary>
    ///
    /// THE CALM BAND (the Wingman-on-every-turn mission, slice D). After every needs-you row come the rows in
    /// <see cref="IsInCalmBand"/>, ordered by the same rule. They are listed and not counted: their bucket is
    /// Active, so nothing that counts needs-you counts them.
    /// </summary>
    public static IReadOnlyList<SessionDto> InWaitingOrder(IEnumerable<SessionDto> sessions)
    {
        var list = sessions as IReadOnlyCollection<SessionDto> ?? sessions.ToList();
        return WaitingLine(list.Where(s => Classify(s) == TriageBucket.NeedsYou))
            .Concat(WaitingLine(list.Where(IsInCalmBand)))
            .ToList();
    }

    private static IEnumerable<SessionDto> WaitingLine(IEnumerable<SessionDto> rows) =>
        rows.OrderBy(s => s.NeedsYouSince ?? DateTime.MaxValue)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.SessionId, StringComparer.Ordinal);

    /// <summary>
    /// Is this row in the calm band - a calm colour AND an accepted verdict? The port of <c>isInCalmBand</c> in
    /// packages/client-core/src/sessions/ordering.ts, which selects on the same two stamped strings, and
    /// tree-agreement.json holds the two to the same answers. A brand-new session is green, which is not a calm
    /// colour, so it is not in the band; a snoozed row is grey, so a snooze keeps a row out of it.
    /// </summary>
    public static bool IsInCalmBand(SessionDto s)
    {
        var color = EffectiveColor(s);
        return (string.Equals(color, "cyan", StringComparison.Ordinal) || string.Equals(color, "purple", StringComparison.Ordinal))
               && string.Equals(s.VerdictState, VerdictStates.Judged, StringComparison.Ordinal);
    }

    /// <summary>
    /// The display label for the "(no repo)" group: sessions whose <see cref="SessionDto.RepoPath"/>
    /// is empty (and that carry no <see cref="SessionDto.RemoteRepo"/>). Rendered last in the
    /// by-repo view (issue #219).
    /// </summary>
    public const string NoRepoGroup = "(no repo)";

    /// <summary>
    /// One repository's group in the by-repo rail view (issue #219): the display name (the repo's
    /// short name) plus its sessions in desktop order. <see cref="IsNoRepo"/> marks the trailing
    /// catch-all group for repo-less sessions.
    /// </summary>
    public sealed record RepoGroup(string Name, IReadOnlyList<SessionDto> Sessions, bool IsNoRepo);

    /// <summary>
    /// The repo-identity decision for the by-repo view (issue #219). Same repo regardless of where
    /// it is checked out: prefer the remote (<see cref="SessionDto.RemoteRepo"/>, normalized - trimmed,
    /// trailing ".git" dropped) so the SAME repo on two machines coalesces under one header; fall back
    /// to the leaf folder name of <see cref="SessionDto.RepoPath"/> when there is no remote. Returns
    /// null when the session has neither (it belongs in the "(no repo)" group). The returned value is
    /// the human-facing group name; grouping is case-insensitive (see <see cref="InRepoGroups"/>).
    /// </summary>
    public static string? RepoName(SessionDto s)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));

        var remote = NormalizeRemote(s.RemoteRepo);
        if (!string.IsNullOrEmpty(remote))
            return LeafName(remote);

        if (!string.IsNullOrWhiteSpace(s.RepoPath))
            return LeafName(s.RepoPath.Trim());

        return null;
    }

    /// <summary>
    /// Group a session roster by repository for the by-repo rail view (issue #219): one group per
    /// distinct repo (case-insensitive on the <see cref="RepoName"/>), named-repo groups sorted
    /// alphabetically (case-insensitive), then a single "(no repo)" group last for sessions with no
    /// repo. Sessions within each group are in <see cref="InDesktopOrder"/> so a row holds its slot
    /// and never reshuffles when only its status color changes. Sessions for the same repo on
    /// different machines/Directors land in ONE group (the key ignores machine/Director identity).
    /// </summary>
    public static IReadOnlyList<RepoGroup> InRepoGroups(IEnumerable<SessionDto> sessions)
    {
        if (sessions is null) throw new ArgumentNullException(nameof(sessions));

        var named = sessions
            .Where(s => RepoName(s) is not null)
            // GroupBy on the case-insensitive name so "cc-director" and "CC-Director" coalesce; the
            // group's display name is the first session's RepoName (stable under desktop order).
            .GroupBy(s => RepoName(s), StringComparer.OrdinalIgnoreCase)
            .Select(g => new RepoGroup(
                RepoName(InDesktopOrder(g)[0]) ?? g.Key ?? "",
                InDesktopOrder(g),
                IsNoRepo: false))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var noRepo = InDesktopOrder(sessions.Where(s => RepoName(s) is null));
        if (noRepo.Count > 0)
            named.Add(new RepoGroup(NoRepoGroup, noRepo, IsNoRepo: true));

        return named;
    }

    /// <summary>Normalize a remote-repo slug for grouping: trim, then drop a single trailing ".git".
    /// Empty/whitespace yields "".</summary>
    private static string NormalizeRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return "";
        var trimmed = remote.Trim();
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        return trimmed;
    }

    /// <summary>The leaf segment of a repo identifier: the last non-empty part after splitting on
    /// both path separators (so "owner/repo" -> "repo" and "C:\repos\cc-director" -> "cc-director").
    /// Returns the whole input when it has no separators.</summary>
    private static string LeafName(string value)
    {
        var parts = value.Split('/', '\\');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(parts[i]))
                return parts[i];
        }
        return value;
    }
}
