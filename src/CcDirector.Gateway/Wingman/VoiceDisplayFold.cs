using CcDirector.Core.HostedAi;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.HostedAi;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Folds one session's voice facts into the single <see cref="VoiceDisplay"/> verdict the Voice screen
/// renders verbatim. This is THE place the voice screen is ruled - the job that used to be spread across
/// the client (voiceAvailability.ts's nine-input guess plus the view's retrying / service-down / reason
/// branching). Pure and total so it is unit-tested directly, and so "add a voice state" is one edit here
/// instead of a new branch in every client.
///
/// The law it serves (docs/new_architecture/sessions.html): the client is dumb; all ruling - state,
/// colors, labels, and which actions are offered - is computed on the Gateway and pushed. The credit /
/// key / service-down / retrying copy is reused from the single-source <see cref="HostedAiMessages"/> so
/// the voice screen says exactly what every other surface says for the same condition.
/// </summary>
public static class VoiceDisplayFold
{
    /// <summary>
    /// The GENERIC, provider-neutral heads-up shown when a ready clip was served by the BACKUP voice
    /// provider (the TTS-fallback mission). Single-source and rendered verbatim by every client. It
    /// deliberately names NO provider (the host non-disclosure rule has no carve-out) and states there is
    /// no extra charge, because the member is billed the normal rate on a fallback. Owner-approved wording.
    /// </summary>
    /// <summary>
    /// How long a session may wait for its voice before the screen stops promising one and says it did
    /// not arrive.
    ///
    /// Three minutes because that is the number this product already committed to: SessionOrdering's
    /// note on IsVoicePreparing says voice generation should average under a minute and that "anything
    /// over three minutes is an exception to be flagged and fixed". This is that flag, finally built -
    /// it does not invent a new standard, it renders the one already written down.
    ///
    /// Erring long on purpose. A false "did not arrive" on a narration that lands at three minutes and
    /// one second costs a moment of doubt; a promise that never ends cost forty-eight minutes of a
    /// person believing the product was working on something.
    /// </summary>
    public static readonly TimeSpan GaveUpAfter = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The wait, in whole minutes and hours, or null under a minute. ONE ladder, matching the one the
    /// roster already uses for needs-you waits (client-core sessions/waiting.ts durationFromMs) so the two
    /// elapsed times on a card cannot describe the same kind of span two different ways.
    ///
    /// Null under a minute rather than "0m": the healthy case is a two-second synthesis, and a card that
    /// announces "0m" on every ordinary turn trains the reader to ignore the number that matters.
    /// </summary>
    internal static string? WaitedLabelFor(DateTime? waitingSince, DateTime utcNow)
    {
        if (waitingSince is not { } since) return null;
        var elapsed = utcNow - since;
        if (elapsed < TimeSpan.FromMinutes(1)) return null;
        var days = (int)elapsed.TotalDays;
        if (days >= 1) return $"{days}d {elapsed.Hours}h";
        if (elapsed.TotalHours >= 1) return $"{elapsed.Hours}h {elapsed.Minutes}m";
        return $"{(int)elapsed.TotalMinutes}m";
    }

    public const string BackupVoiceNotice =
        "Some voice providers are overloaded right now, so we switched you to a backup voice. No extra charge.";

    /// <summary>
    /// Shown when the computer that owns this session is connected but running a build that cannot send its
    /// conversation. Nothing will ever be stored to narrate until it is updated, so this says the remedy
    /// instead of asking the reader to keep waiting.
    ///
    /// It is the VOICE wording of the fact <see cref="History.SessionConversationFold.DirectorTooOldText"/>
    /// states for Chat, and the two must keep agreeing about the CAUSE while differing about the
    /// consequence - one loses the chat, the other loses the narration. They are deliberately not one
    /// shared string: a sentence that has to be true of both surfaces ends up naming neither.
    /// </summary>
    public const string DirectorTooOldText =
        "This session's computer is running an older DevThrottle that cannot send its conversation, "
      + "so there is nothing here to read aloud. Update it to hear this session.";

    /// <param name="voiceMode">The session is in voice mode (the Director's authoritative flag).</param>
    /// <param name="agentWorking">The agent is mid-turn (a blue / working activity state). The
    /// finished-turn narration is stale while it works, so this dominates: no play, no Generate button,
    /// just "the agent is working; the next completed turn will be narrated". Matches the pre-fold client
    /// rule where agent-working suppressed every other voice affordance.</param>
    /// <param name="hasAudio">The Gateway holds fetchable, playable audio for this turn (HasVoice).</param>
    /// <param name="generating">The wingman is producing this turn's narration right now (IsGenerating).</param>
    /// <param name="unavailable">Why the hosted leg could not make audio, or null - the recorded
    /// <see cref="HostedAiState"/> (Retrying / ServiceDown / NeedsCredits / CapReached / NeedsKey).</param>
    /// <param name="nothingToNarrate">The last turn has no text reply to read aloud - the session is
    /// waiting on a prompt / menu, so there is genuinely nothing to narrate (a NON-failure, distinct from
    /// "not made yet"). This is the fact that used to reach the client as a bare null and got rendered as
    /// a dead-end Generate button.</param>
    /// <param name="servedViaFallback">This turn's ready clip was made by the BACKUP voice provider (the
    /// primary was overloaded and the cloud proxy quietly failed over). A SUCCESS-with-a-note: it only ever
    /// rides the green <c>ready</c> verdict and adds the generic <see cref="BackupVoiceNotice"/> - it is not
    /// an unavailable/outage state and changes nothing else. Ignored unless there is playable audio.</param>
    /// <param name="waitingSince">When this session's wait for voice began (SessionDto.VoiceWaitingSince),
    /// or null when it is not waiting. Past <see cref="GaveUpAfter"/> the verdict becomes a terminal
    /// "gave up" instead of another calm "on its way" - see that field for why a promise with no end is
    /// worse than an admission.</param>
    /// <param name="utcNow">Now, injected so the give-up boundary is testable without waiting for it.</param>
    /// <param name="directorCannotSendConversation">The computer that owns this session is connected but
    /// running a build that cannot send its conversation, so the Gateway will never hold words to narrate
    /// for it. A SPECIFIC, ACTIONABLE reason with a one-line remedy - which is why it outranks every
    /// "be patient" verdict below it. See <see cref="DirectorTooOldText"/>.</param>
    /// <param name="speechError">This stop's reading succeeded and making its AUDIO failed: where that is on the
    /// retry schedule, as the card draws it (<see cref="WingmanVoiceService.SpeechErrorFor"/>), or null. It
    /// displaces the calm retrying sentence, because it says exactly which retry is booked and when, or that none
    /// is. A FAILED READING is not an input here: it is known only after this fold has run, and is applied by
    /// <see cref="WithReadingError"/>.</param>
    public static VoiceDisplay Fold(bool voiceMode, bool agentWorking, bool hasAudio, bool generating, HostedAiState? unavailable, bool nothingToNarrate, bool servedViaFallback = false, DateTime? waitingSince = null, DateTime? utcNow = null, bool directorCannotSendConversation = false, WingmanErrorDisplay? speechError = null)
    {
        // Computed once, up front, because more than one verdict below carries it: the calm "on its way"
        // wants it so a healthy wait can be seen climbing, and the give-up verdict wants it in its own
        // sentence. Null under a minute - see WaitedLabelFor.
        var waited = WaitedLabelFor(waitingSince, utcNow ?? DateTime.UtcNow);

        // Not a voice session: the screen shows its own "off" card; there is no verdict to render.
        if (!voiceMode)
            return new VoiceDisplay { Kind = "off", Tone = "neutral", Label = "Voice off", Message = "" };

        // The agent is working: the finished-turn narration is stale, so offer nothing (no play, no
        // Generate) and say what is true - the next completed turn will be narrated. Dominates the rest.
        if (agentWorking)
            return new VoiceDisplay
            {
                Kind = "working",
                Tone = "yellow",
                Label = "Agent is working",
                Message = "The agent is working on the next step. The next completed turn will be narrated.",
            };

        // Playable audio wins over everything below. Even mid-regeneration we keep offering the existing
        // clip (issue #1322: never pull the rug on a listener), so has-audio is checked before generating.
        if (hasAudio)
            return new VoiceDisplay
            {
                Kind = "ready",
                Tone = "green",
                Label = "Voice ready",
                Message = "",
                CanPlay = true,
                // A backup-served clip is still a normal, playable "ready" - just with a generic heads-up.
                // The failover is never surfaced as an outage; it only adds this one verbatim line.
                VoiceFallbackNotice = servedViaFallback ? BackupVoiceNotice : null,
            };

        // Being made right now: a calm "on its way", no button (it is already happening).
        if (generating)
            return new VoiceDisplay
            {
                Kind = "preparing",
                Tone = "yellow",
                Label = "Voice on its way",
                Message = "The wingman is preparing this turn's narration.",
                WaitedLabel = waited,
            };

        // GAVE UP. The one state the voice screen could never reach before, and the reason a session
        // could sit on "voice on its way" for forty-eight minutes: every arm below is either a calm
        // "still coming" or a specific fault, and a narration that simply never arrives matches the
        // calm one forever. IsVoicePreparing's own comment named a terminal gave-up state as the
        // correct answer to that wedge; this is it.
        //
        // Keyed on ELAPSED TIME rather than on a count of attempts, deliberately. The attempts are made
        // by two different loops (the turn-end path and the idle sweep) at rates neither controls, so a
        // count means different things on a busy Gateway and a quiet one, while a clock means the same
        // thing to the person reading it - which is the only place this number is used. It is therefore
        // "nothing has arrived in three minutes", NOT "three attempts have failed", and the sweep's
        // three-per-cycle global cap means those are genuinely different claims.
        //
        // WHERE IT SITS, and the two versions of this that were wrong before review caught them:
        //
        //   above it  - off, working, hasAudio, generating. A playable clip or a live attempt must never
        //               be hidden behind a verdict about how long the wait has been.
        //   also above - EVERY SPECIFIC, ACTIONABLE REASON: service down, out of credits, cap reached,
        //               finish setup. The first draft put gaveUp ahead of these, so a member who needed
        //               to add credit was told "voice did not arrive" instead - the actionable sentence
        //               replaced by a symptom. That is the same defect as a terminal read hiding a
        //               standing account condition, which this codebase fixed once already today.
        //   also above - nothingToNarrate. A session parked on a menu was never going to be narrated, so
        //               "it did not arrive" reports a failure that never happened.
        //
        // What gaveUp DOES displace is Retrying and notReady - the two states whose whole content is "be
        // patient". Those are the sentences that were still being said at forty-eight minutes.
        //
        // Nothing here stops the work: the sweep keeps trying and a success replaces this on the next
        // fold. What ends is the PROMISE, not the effort.
        var gaveUp = !nothingToNarrate
                     && waitingSince is { } since
                     && (utcNow ?? DateTime.UtcNow) - since >= GaveUpAfter;

        // An ANSWERED or in-progress hosted-AI condition. Reuse the single-source copy so the voice screen
        // matches the roster and every other surface for the same state - never a hand-written string here.
        switch (unavailable)
        {
            case HostedAiState.Retrying:
                // THE SPEECH FAILED AND ITS RETRY SCHEDULE IS KNOWN, so say that instead of "on its way": which
                // retry is booked and when, or that nothing more is. Checked before the elapsed-time give-up
                // because it is the stronger and more exact claim.
                if (speechError is not null) return WingmanErrorDisplayFor(speechError, waited);
                // "Voice on its way" is true for a minute and a lie at forty-eight. Past the threshold the
                // retry stops being news and becomes the thing being reported.
                if (gaveUp) return GaveUpDisplay(waited);
                return new VoiceDisplay
                {
                    Kind = "retrying",
                    Tone = "yellow",
                    Label = "Voice on its way",
                    Message = HostedAiMessages.For(HostedAiState.Retrying).Text,
                };
            case HostedAiState.ServiceDown:
                // An answered failure is retried on the same schedule as every other kind (owner ruling,
                // 2026-09-19: one schedule for every kind of failure), so the card says which retry is next.
                if (speechError is not null) return WingmanErrorDisplayFor(speechError, waited);
                return new VoiceDisplay
                {
                    Kind = "serviceDown",
                    Tone = "red",
                    Label = "Voice service down",
                    Message = HostedAiMessages.For(HostedAiState.ServiceDown).Text,
                };
            case HostedAiState.NeedsCredits:
            case HostedAiState.CapReached:
            case HostedAiState.NeedsKey:
            case HostedAiState.SubscriptionRequired:
            case HostedAiState.FairUseLimitReached:
            case HostedAiState.Unavailable:
                // Carries the shared call-to-action where one exists (add credit / raise the cap /
                // finish setup / view plans - the fair-use and unknown states have none). The CTA
                // IS a real action, so it rides in Reason; a generate button is still wrong (it would hit
                // the same wall), so CanGenerate stays false.
                return new VoiceDisplay
                {
                    Kind = "blocked",
                    Tone = "red",
                    Label = BlockedLabel(unavailable.Value),
                    Message = HostedAiMessages.For(unavailable.Value).Text,
                    Reason = HostedAiHttp.Dto(unavailable.Value),
                };
        }

        // THE OWNING COMPUTER CANNOT SEND ITS CONVERSATION. One more member of the category the comment above
        // already names - a SPECIFIC, ACTIONABLE reason - so it sits with those, above every sentence whose
        // content is "be patient". It is placed BELOW the hosted-AI switch on purpose: an account that is out
        // of credits or needs setup must still be told that first, because those conditions are what the rest
        // of the product is already saying about the account and this one is about a single machine.
        //
        // Above nothingToNarrate as well as above gaveUp. Both would be FALSE CLAIMS here and they are false
        // in different directions: "nothing to read aloud" asserts the session is parked on a prompt, which
        // is a statement about the conversation nobody here has read, and "voice did not arrive" reports a
        // narration that was never attempted. The wingman clears nothingToNarrate on this path for the same
        // reason, so in practice they do not collide - the ordering is here because a defence that depends on
        // another file's clearing discipline is not a defence.
        //
        // The remedy is a single sentence and the recovery needs nobody: updating that computer flips its
        // next Hello, the wingman's marker clears on the following pass, and narration resumes.
        if (directorCannotSendConversation)
            return new VoiceDisplay
            {
                Kind = "directorTooOld",
                Tone = "red",
                Label = "Update DevThrottle",
                Message = DirectorTooOldText,
                // No Generate button: pressing it would re-read the same empty store. The action is on the
                // other machine, and the message names it.
            };

        // THE SPEECH FAILED, reached here when the cause was never written into the hosted-AI slot above (an empty
        // body, or a slot a new turn cleared). Below the actionable account conditions and below "update that
        // computer", for the same reason gaveUp is: a remedy the reader can carry out beats a report of what did
        // not happen.
        if (speechError is not null) return WingmanErrorDisplayFor(speechError, waited);

        // Nothing to read aloud: the session needs the user, but on a prompt / menu, not a text reply. This
        // is the honest state that replaces the old "red badge next to a Generate button that can never
        // work". No button - generating cannot narrate a text reply that does not exist.
        if (nothingToNarrate)
            return new VoiceDisplay
            {
                Kind = "nothingToNarrate",
                Tone = "neutral",
                Label = "Nothing to read aloud",
                Message = "This session is waiting for you on a prompt, not a text answer, so there is nothing to read aloud yet.",
            };

        // Voice on, no audio, no reason, not being made, and not known-empty: a genuine "not made yet"
        // window (just entered voice, or a fresh turn before the sweep runs). Here - and ONLY here -
        // offering "Generate narration now" is honest, because there may be a text reply waiting to narrate.
        // The same rule for the plain "not made yet" window: honest for a moment, misleading for an hour.
        if (gaveUp) return GaveUpDisplay(waited);

        return new VoiceDisplay
        {
            Kind = "notReady",
            Tone = "neutral",
            Label = "No narration yet",
            Message = "There is no spoken summary for this turn yet.",
            CanGenerate = true,
            WaitedLabel = waited,
        };
    }

    /// <summary>
    /// THE ONE definition of "this session is waiting for its voice", used by every caller that stamps the
    /// clock AND readable beside the fold that consumes it.
    ///
    /// It existed twice, hand-written, before review: once in the roster aggregation and once in the
    /// display-push enrichment. They happened to agree character for character, which is exactly the
    /// condition under which two copies stay wrong together and then quietly diverge - nothing bound them,
    /// and no test would have noticed the day one of them changed.
    ///
    /// Deliberately NARROWER than "the fold shows a no-audio verdict". A session that is out of credits or
    /// whose speech service is down is not waiting for a narration that is coming - it is blocked, and
    /// counting that as waiting would start a clock whose only use is to declare a give-up that the
    /// blocked verdict already explains better. So the clock runs for voice sessions with no audio that
    /// are not mid-turn, and the fold decides what to SAY about that; the two questions are related but
    /// they are not the same question.
    /// </summary>
    public static bool IsWaitingForVoice(bool voiceMode, bool hasAudio, bool agentWorking)
        => voiceMode && !hasAudio && !agentWorking;

    /// <summary>
    /// The card for a session a live session owns. No play, no Generate, and - the point - no "Switch to voice mode":
    /// there is no action here for the person reading it, because the session is not theirs to be read aloud.
    ///
    /// IT IS NOT AN ARM OF <see cref="Fold"/>, and that is an ordering fact rather than a preference. Both callers that
    /// fold a voice verdict do it in a loop that runs BEFORE <see cref="SessionDto.HasLiveSupervisor"/> is resolved -
    /// the push store nulls the resolved facts at ingest on purpose, so only this Gateway decides them, and the one
    /// place that has decided them is <c>GatewayEndpoints.StampFleetRolesAndFold</c>. A <c>heldByAnotherSession</c>
    /// parameter on Fold would therefore be passed a default false by every caller in the product: a branch nothing
    /// reaches, tested green and dead. So the stamp REPLACES the folded verdict with this one, at the first moment the
    /// answer exists, and that replacement is what the tests drive.
    ///
    /// Replacing rather than pre-empting also gives the precedence for free: this outranks every state Fold can
    /// produce, including a playable clip. That is the one place the voice screen deliberately takes audio away from a
    /// listener, and it is right here - a held session's narration was spent on a turn the user never asked to hear.
    ///
    /// The wording names the RULE rather than the seat. "A Worker is not narrated" would be wrong the moment its
    /// supervisor exits, which is the whole reason supervision is read from liveness and not from the role stamp
    /// (SessionOrdering.IsSupervised). The same session becomes the user's, and narrated, with nothing to repair.
    /// </summary>
    public static VoiceDisplay HeldDisplay() => new()
    {
        Kind = VoiceDisplayKinds.Held,
        Tone = "neutral",
        Label = "Another session is running this one",
        Message = "Voice mode narrates the sessions you own. This one answers to another live session, "
                + "which reads its turns - so it is not narrated to you. It starts being narrated by itself "
                + "if that session finishes or exits.",
    };

    /// <summary>
    /// THE READING FAILED, applied AFTER the fold because it is known after it: both callers fold the voice verdict
    /// before the account's stored readings are stamped onto the rows, exactly as <see cref="HeldDisplay"/> is
    /// applied after the fold for the same ordering reason. Returns the verdict to show.
    ///
    /// It replaces only the verdicts whose whole content is "there is no audio for this turn yet" - not ready,
    /// retrying, gave up. Everything more specific stands: voice off, the agent working, a playable clip, a reading
    /// in progress, a held session, and every account or computer condition the reader can act on.
    /// </summary>
    public static VoiceDisplay WithReadingError(VoiceDisplay current, WingmanErrorDisplay? readingError)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (readingError is null) return current;
        return current.Kind is "notReady" or "retrying" or VoiceDisplayKinds.GaveUp or "nothingToNarrate"
            ? WingmanErrorDisplayFor(readingError, current.WaitedLabel)
            : current;
    }

    /// <summary>
    /// The voice screen's card for a Wingman error - a failed reading, or a reading whose audio failed.
    ///
    /// THE SENTENCE ABOUT WHAT HAPPENS NEXT IS CHOSEN BY THE BOOKED TIME AND BY NOTHING ELSE. "The Gateway will try
    /// again by itself" is said only while a retry is booked; "nothing more is scheduled" only when none is. This
    /// card replaced two - "Voice did not arrive ... the Gateway is still trying" and "Turn not narrated" - that
    /// differed mainly in that sentence, chose it from flags held in memory, and on 19 September 2026 had it wrong
    /// on seven sessions at once.
    ///
    /// The button is offered from the first failure: a person asking makes one attempt now, and neither resets nor
    /// consumes the schedule.
    /// </summary>
    private static VoiceDisplay WingmanErrorDisplayFor(WingmanErrorDisplay error, string? waited) => new()
    {
        Kind = VoiceDisplayKinds.WingmanError,
        Tone = "red",
        Label = error.Tag,
        Message = error.Exhausted
            ? error.Reason + " Nothing more is scheduled. Read the turn, or ask again."
            : error.Reason + " The Gateway will try again by itself, and you can ask again now.",
        CanGenerate = true,
        WaitedLabel = waited,
        WingmanError = error,
    };

    /// <summary>
    /// Nothing has arrived inside the give-up window and nothing is known to have failed. It used to add "The
    /// Gateway is still trying", which nothing here can know - this fold is not told what is booked - and which was
    /// false for seven sessions on 19 September 2026. It now says only what is true, and offers the one action that
    /// can still produce this turn's audio.
    /// </summary>
    private static VoiceDisplay GaveUpDisplay(string? waited) => new()
    {
        Kind = VoiceDisplayKinds.GaveUp,
        Tone = "red",
        Label = waited is null ? "Voice did not arrive" : $"Voice did not arrive after {waited}",
        Message = "This turn's narration has not been produced. Read the turn, or ask for the narration again.",
        CanGenerate = true,
        WaitedLabel = waited,
    };

    /// <summary>The short badge headline for a blocked (account) state - the body text and the call-to-
    /// action still come from the shared <see cref="HostedAiMessages"/> / <see cref="HostedAiHttp.Dto"/>.</summary>
    private static string BlockedLabel(HostedAiState state) => state switch
    {
        HostedAiState.NeedsCredits => "Voice needs credit",
        HostedAiState.CapReached => "Monthly limit reached",
        HostedAiState.NeedsKey => "Finish setup",
        // The two Included AI refusals (issue #1360): no cost words, no numbers.
        HostedAiState.SubscriptionRequired => "Not included with this account",
        HostedAiState.FairUseLimitReached => "Monthly fair-use limit reached",
        _ => "Voice unavailable",
    };
}
