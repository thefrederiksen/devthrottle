using CcDirector.Gateway.Contracts;
using CcDirector.Core.HostedAi;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The one place the Voice screen is ruled (the client now only renders). These pin every folded state,
/// and above all the screenshot bug: a voice-mode session with no audio and nothing to narrate must NOT
/// offer a "Generate narration" button, because pressing it re-runs the same empty read and never makes
/// audio. CanGenerate is the whole defect, so it is asserted on every state.
/// </summary>
public sealed class VoiceDisplayFoldTests
{
    // ===================================================== a session a live session owns is not the user's to hear

    [Fact]
    public void HeldDisplay_OffersNothing()
    {
        // The card that replaces the phone's own "Voice mode is off for this session" + "Switch to voice mode" on a
        // held session. That button could not succeed - the enrolment sweep refuses a held session - and it was drawn
        // under a banner claiming every session narrates. Twelve of the owner's twenty sessions showed that pair.
        var d = VoiceDisplayFold.HeldDisplay();
        Assert.Equal(VoiceDisplayKinds.Held, d.Kind);
        Assert.False(d.CanPlay);
        Assert.False(d.CanGenerate);
        Assert.Null(d.Reason);
        Assert.NotEqual("", d.Label);
        Assert.NotEqual("", d.Message);
    }

    [Fact]
    public void NotVoiceMode_IsOff_NoActions()
    {
        var d = VoiceDisplayFold.Fold(voiceMode: false, agentWorking: false, hasAudio: false, generating: false, unavailable: null, nothingToNarrate: false);
        Assert.Equal("off", d.Kind);
        Assert.False(d.CanPlay);
        Assert.False(d.CanGenerate);
    }

    [Fact]
    public void HasAudio_IsReady_CanPlay_NoGenerate()
    {
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: true, generating: false, unavailable: null, nothingToNarrate: false);
        Assert.Equal("ready", d.Kind);
        Assert.Equal("green", d.Tone);
        Assert.True(d.CanPlay);
        Assert.False(d.CanGenerate);
    }

    [Fact]
    public void HasAudio_WinsOverGenerating_NeverPullsTheRugOnAListener()
    {
        // Mid-regeneration with a playable clip present: keep offering the clip (issue #1322), do not
        // flip to a "preparing" state that would drop a listener out of playback.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: true, generating: true, unavailable: null, nothingToNarrate: false);
        Assert.Equal("ready", d.Kind);
        Assert.True(d.CanPlay);
    }

    [Fact]
    public void Generating_IsPreparing_NoButton()
    {
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: true, unavailable: null, nothingToNarrate: false);
        Assert.Equal("preparing", d.Kind);
        Assert.Equal("yellow", d.Tone);
        Assert.False(d.CanGenerate);
    }

    [Fact]
    public void Retrying_IsYellowOnItsWay_NoButton_SharedCopy()
    {
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: HostedAiState.Retrying, nothingToNarrate: false);
        Assert.Equal("retrying", d.Kind);
        Assert.Equal("yellow", d.Tone);
        Assert.False(d.CanGenerate);
        // Reuses the single-source copy, never a hand-written string.
        Assert.Equal(HostedAiMessages.For(HostedAiState.Retrying).Text, d.Message);
    }

    [Fact]
    public void ServiceDown_IsRed_NoButton_SharedCopy()
    {
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: HostedAiState.ServiceDown, nothingToNarrate: false);
        Assert.Equal("serviceDown", d.Kind);
        Assert.Equal("red", d.Tone);
        Assert.False(d.CanGenerate);
        Assert.Equal(HostedAiMessages.For(HostedAiState.ServiceDown).Text, d.Message);
    }

    [Theory]
    [InlineData(HostedAiState.NeedsCredits)]
    [InlineData(HostedAiState.CapReached)]
    [InlineData(HostedAiState.NeedsKey)]
    [InlineData(HostedAiState.SubscriptionRequired)]
    [InlineData(HostedAiState.FairUseLimitReached)]
    [InlineData(HostedAiState.Unavailable)]
    public void Blocked_CarriesTheSharedCallToAction_NoGenerateButton(HostedAiState state)
    {
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: state, nothingToNarrate: false);
        Assert.Equal("blocked", d.Kind);
        Assert.NotNull(d.Reason);                 // the shared CTA (where one exists) rides here
        Assert.Equal(HostedAiMessages.For(state).Text, d.Message);
        Assert.False(d.CanGenerate);              // a generate button would hit the same wall
    }

    [Theory]
    [InlineData(HostedAiState.SubscriptionRequired, "Not included with this account")]
    [InlineData(HostedAiState.FairUseLimitReached, "Monthly fair-use limit reached")]
    [InlineData(HostedAiState.Unavailable, "Voice unavailable")]
    public void IncludedAiRefusals_HaveNoCostWordsInTheirLabelsOrCopy(HostedAiState state, string expectedLabel)
    {
        // Issue #1360: the two Included AI refusals and the neutral unknown state must never put credit
        // or money words on the voice screen.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: state, nothingToNarrate: false);
        Assert.Equal(expectedLabel, d.Label);
        Assert.DoesNotContain("credit", d.Label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credit", d.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingToNarrate_IsHonestState_WithNoDeadEndButton_THE_SCREENSHOT_BUG()
    {
        // The exact defect in the screenshot: voice on, no audio, and the session is waiting on a prompt
        // (no text reply). It must be its OWN honest state with NO Generate button - not a red "unavailable"
        // badge next to a button that can never succeed.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: null, nothingToNarrate: true);
        Assert.Equal("nothingToNarrate", d.Kind);
        Assert.False(d.CanGenerate);
        Assert.False(d.CanPlay);
        Assert.False(string.IsNullOrWhiteSpace(d.Label));    // it SAYS something
        Assert.False(string.IsNullOrWhiteSpace(d.Message));  // and explains why
    }

    [Fact]
    public void AnsweredFailure_WinsOverNothingToNarrate()
    {
        // If both are somehow set, a real hosted-AI failure reason is more informative than "nothing to
        // narrate" and takes precedence.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: HostedAiState.ServiceDown, nothingToNarrate: true);
        Assert.Equal("serviceDown", d.Kind);
    }

    [Fact]
    public void AgentWorking_IsWorkingState_NoPlay_NoButton_DominatesEverything()
    {
        // The agent is mid-turn: the finished-turn narration is stale. No play, no Generate - and it
        // dominates even a lingering reason or nothing-to-narrate marker, matching the pre-fold client
        // rule where agent-working suppressed every other voice affordance.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: true, hasAudio: false, generating: false, unavailable: HostedAiState.Retrying, nothingToNarrate: true);
        Assert.Equal("working", d.Kind);
        Assert.False(d.CanPlay);
        Assert.False(d.CanGenerate);
    }

    [Fact]
    public void NoAudioNoReasonNotEmpty_IsNotReady_AndOnlyHereMayGenerate()
    {
        // The one legitimate "you can make one" window: voice on, nothing yet, but there may be a text
        // reply to narrate. This is the ONLY state that offers Generate.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: null, nothingToNarrate: false);
        Assert.Equal("notReady", d.Kind);
        Assert.True(d.CanGenerate);
    }

    // --- TTS fallback: the generic backup-voice notice (mission Phase 2) ------------------------------

    [Fact]
    public void ServedViaFallback_IsStillReady_WithTheGenericNotice_NoProviderNamed()
    {
        // A backup-served clip is a SUCCESS-with-a-note: a normal green, playable "ready", plus the one
        // generic notice line. It is NOT an outage state and names no provider.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: true, generating: false, unavailable: null, nothingToNarrate: false, servedViaFallback: true);
        Assert.Equal("ready", d.Kind);
        Assert.Equal("green", d.Tone);
        Assert.True(d.CanPlay);
        Assert.False(d.CanGenerate);
        Assert.Equal(VoiceDisplayFold.BackupVoiceNotice, d.VoiceFallbackNotice);
        // Host non-disclosure has no carve-out: the notice must never name the backup provider.
        Assert.DoesNotContain("openai", d.VoiceFallbackNotice!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("backup voice", d.VoiceFallbackNotice!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotServedViaFallback_HasNoNotice()
    {
        // The normal case: a ready clip made by the primary provider carries no notice.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: true, generating: false, unavailable: null, nothingToNarrate: false, servedViaFallback: false);
        Assert.Equal("ready", d.Kind);
        Assert.Null(d.VoiceFallbackNotice);
    }

    [Fact]
    public void ServedViaFallback_NeverRidesAnOutageState()
    {
        // Defensive: a fallback flag with no playable audio must NEVER turn a real outage into a
        // "ready + notice". The notice only ever attaches to the green ready verdict; an answered
        // ServiceDown stays ServiceDown with no notice (a fallback SUCCESS never becomes an outage).
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: HostedAiState.ServiceDown, nothingToNarrate: false, servedViaFallback: true);
        Assert.Equal("serviceDown", d.Kind);
        Assert.Null(d.VoiceFallbackNotice);
    }

    // ------------------------------------------------------------------------------------------------
    // "That computer cannot send its conversation" (2026-09-02). The Gateway knew this the whole time -
    // Chat was already saying it in plain English - while the voice path said "Voice did not arrive after
    // 22m" and the colour fold held those sessions yellow. These pin the SENTENCE the reader gets, not
    // just the branch taken, because the sentence is what was wrong.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void DirectorCannotSend_SaysUpdateThatComputer_AndOffersNoDeadEndButton()
    {
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
            unavailable: null, nothingToNarrate: false, directorCannotSendConversation: true);
        Assert.Equal("directorTooOld", d.Kind);
        Assert.Equal("red", d.Tone);
        Assert.Equal("Update DevThrottle", d.Label);
        // The WORDS, verbatim. A test that only pinned the Kind would have passed while the screen said
        // anything at all, including the "be patient" sentence this whole arm exists to stop.
        Assert.Equal(VoiceDisplayFold.DirectorTooOldText, d.Message);
        Assert.Contains("Update it", d.Message);
        Assert.False(d.CanGenerate);   // the action is on the other machine
        Assert.False(d.CanPlay);
    }

    [Fact]
    public void DirectorCannotSend_ReplacesTheGaveUpSentence_THE_2026_09_02_DEFECT()
    {
        // The exact shape of the incident: voice on, no audio, and a wait long past the give-up boundary.
        // Before this arm the reader was told "Voice did not arrive after 22m" - a symptom, and a promise
        // that the Gateway was still trying at something that could never work.
        var waitingSince = new DateTime(2026, 9, 2, 18, 35, 0, DateTimeKind.Utc);
        var now = waitingSince.AddMinutes(22);

        var before = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
            unavailable: null, nothingToNarrate: false, waitingSince: waitingSince, utcNow: now,
            directorCannotSendConversation: false);
        Assert.Equal("gaveUp", before.Kind);                         // NEGATIVE CONTROL: this is what it used to say
        Assert.Contains("did not arrive", before.Label);

        var after = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
            unavailable: null, nothingToNarrate: false, waitingSince: waitingSince, utcNow: now,
            directorCannotSendConversation: true);
        Assert.Equal("directorTooOld", after.Kind);
        Assert.Equal(VoiceDisplayFold.DirectorTooOldText, after.Message);
        Assert.DoesNotContain("did not arrive", after.Label);
        Assert.DoesNotContain("still trying", after.Message);        // no promise it cannot keep
    }

    [Fact]
    public void DirectorCannotSend_BeatsNothingToNarrate_BecauseNobodyReadTheConversation()
    {
        // "This session is waiting for you on a prompt" is a claim about a conversation this Gateway has
        // never seen. It must not be made on a session whose words never arrived.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
            unavailable: null, nothingToNarrate: true, directorCannotSendConversation: true);
        Assert.Equal("directorTooOld", d.Kind);
    }

    [Theory]
    [InlineData(HostedAiState.NeedsCredits)]
    [InlineData(HostedAiState.NeedsKey)]
    [InlineData(HostedAiState.SubscriptionRequired)]
    [InlineData(HostedAiState.ServiceDown)]
    public void AccountAndServiceConditions_StillOutrank_DirectorCannotSend(HostedAiState state)
    {
        // The deliberate ordering, asserted so it cannot drift: an account-level condition is what the rest
        // of the product is already telling this member, and it is answered by a real call-to-action. The
        // too-old machine is the narrower fact and waits its turn.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
            unavailable: state, nothingToNarrate: false, directorCannotSendConversation: true);
        Assert.NotEqual("directorTooOld", d.Kind);
    }

    [Fact]
    public void PlayableAudio_AndAWorkingAgent_BothStillWin_OverDirectorCannotSend()
    {
        // A clip in hand is never hidden behind a verdict about the machine that produced it, and a session
        // mid-turn says so. Both sit above every reason arm; this pins that the new one did not jump them.
        var ready = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: true, generating: false,
            unavailable: null, nothingToNarrate: false, directorCannotSendConversation: true);
        Assert.Equal("ready", ready.Kind);
        Assert.True(ready.CanPlay);

        var working = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: true, hasAudio: false, generating: false,
            unavailable: null, nothingToNarrate: false, directorCannotSendConversation: true);
        Assert.Equal("working", working.Kind);
    }

    [Fact]
    public void VoiceOff_SaysNothingAboutAnybodysBuild()
    {
        var d = VoiceDisplayFold.Fold(voiceMode: false, agentWorking: false, hasAudio: false, generating: false,
            unavailable: null, nothingToNarrate: false, directorCannotSendConversation: true);
        Assert.Equal("off", d.Kind);
    }

    // ------------------------------------------------------------------------------------------------
    // THE WINGMAN ERROR ON THE VOICE SCREEN (mission "Wingman error and retry", 2026-09-19). It replaced the
    // "Turn not narrated" card, which said "nothing further is scheduled" from a flag held in memory, and it
    // fixed the "Voice did not arrive" card, which said "the Gateway is still trying" without being told what
    // was booked - false on seven sessions at once on 19 September. These pin the SENTENCES, and above all the
    // one rule: a card says an attempt is coming only while one is booked.
    // ------------------------------------------------------------------------------------------------

    private static readonly DateTime Booked = new(2026, 9, 19, 20, 0, 0, DateTimeKind.Utc);

    private static WingmanErrorDisplay BookedSpeechError(int retriesMade = 1)
        => WingmanErrorFold.ForSchedule(WingmanErrorFold.SpeechFailedReason, retriesMade, Booked);

    private static WingmanErrorDisplay SpentSpeechError()
        => WingmanErrorFold.ForSchedule(WingmanErrorFold.SpeechFailedReason, WingmanRetrySchedule.Total, null);

    [Fact]
    public void ASpeechError_ReplacesTheCalmRetryingSentence_WithWhichRetryIsBooked()
    {
        // NEGATIVE CONTROL: with no speech error known, Retrying still says what it always said.
        var calm = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
            unavailable: HostedAiState.Retrying, nothingToNarrate: false);
        Assert.Equal("retrying", calm.Kind);

        var error = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
            unavailable: HostedAiState.Retrying, nothingToNarrate: false, speechError: BookedSpeechError());
        Assert.Equal(VoiceDisplayKinds.WingmanError, error.Kind);
        Assert.Equal("red", error.Tone);
        Assert.Equal("Wingman error", error.Label);
        Assert.Contains("will try again by itself", error.Message);
        Assert.DoesNotContain("Nothing more is scheduled", error.Message);
        Assert.Equal("retry 2 of 8", error.WingmanError!.RetryLabel);
        Assert.Equal(Booked, error.WingmanError.NextRetryAtUtc);
        // The button is there from the first failure: a person asking makes one attempt now.
        Assert.True(error.CanGenerate);
        Assert.False(error.CanPlay);
    }

    [Fact]
    public void AUsedUpSchedule_SaysNothingMoreIsScheduled_AndNeverThatAnAttemptIsComing()
    {
        var spent = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
            unavailable: HostedAiState.Retrying, nothingToNarrate: false, speechError: SpentSpeechError());
        Assert.Equal(VoiceDisplayKinds.WingmanError, spent.Kind);
        Assert.Contains("Nothing more is scheduled", spent.Message);
        Assert.DoesNotContain("try again by itself", spent.Message);
        Assert.True(spent.WingmanError!.Exhausted);
        Assert.Null(spent.WingmanError.NextRetryAtUtc);
        Assert.True(spent.CanGenerate);   // asking is the one thing left that can still work
    }

    [Fact]
    public void GaveUp_NoLongerClaimsTheGatewayIsStillTrying_AndOffersTheButton()
    {
        // This fold is not told what is booked, so it may not say anything is. It said "The Gateway is still
        // trying" for seven sessions that had nothing booked.
        var waitingSince = new DateTime(2026, 9, 4, 19, 45, 0, DateTimeKind.Utc);
        var gaveUp = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
            unavailable: HostedAiState.Retrying, nothingToNarrate: false,
            waitingSince: waitingSince, utcNow: waitingSince.AddMinutes(18));
        Assert.Equal("gaveUp", gaveUp.Kind);
        Assert.DoesNotContain("still trying", gaveUp.Message);
        Assert.True(gaveUp.CanGenerate);
        Assert.Equal("18m", gaveUp.WaitedLabel);
    }

    [Fact]
    public void ASpeechError_NeverHidesAnActionableAccountCondition()
    {
        foreach (var state in new[] { HostedAiState.NeedsCredits, HostedAiState.CapReached, HostedAiState.NeedsKey })
        {
            var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
                unavailable: state, nothingToNarrate: false, speechError: BookedSpeechError());
            Assert.Equal("blocked", d.Kind);
        }

        var tooOld = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
            unavailable: null, nothingToNarrate: false, directorCannotSendConversation: true, speechError: BookedSpeechError());
        Assert.Equal("directorTooOld", tooOld.Kind);
    }

    [Fact]
    public void ASpeechError_NeverHidesPlayableAudioOrALiveAttempt()
    {
        var ready = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: true, generating: false,
            unavailable: HostedAiState.Retrying, nothingToNarrate: false, speechError: BookedSpeechError());
        Assert.Equal("ready", ready.Kind);
        Assert.True(ready.CanPlay);

        var generating = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: true,
            unavailable: HostedAiState.Retrying, nothingToNarrate: false, speechError: BookedSpeechError());
        Assert.Equal("preparing", generating.Kind);

        var working = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: true, hasAudio: false, generating: false,
            unavailable: HostedAiState.Retrying, nothingToNarrate: false, speechError: BookedSpeechError());
        Assert.Equal("working", working.Kind);
    }

    [Theory]
    [InlineData("notReady", true)]
    [InlineData("retrying", true)]
    [InlineData("gaveUp", true)]
    [InlineData("nothingToNarrate", true)]
    [InlineData("off", false)]
    [InlineData("working", false)]
    [InlineData("ready", false)]
    [InlineData("preparing", false)]
    [InlineData("held", false)]
    [InlineData("blocked", false)]
    [InlineData("serviceDown", false)]
    [InlineData("directorTooOld", false)]
    public void AFailedReading_ReplacesOnlyTheVerdictsThatSayThereIsNoAudioYet(string kind, bool replaced)
    {
        // The reading's failure is known after the voice fold has run, so it is applied afterwards - and only
        // over the verdicts whose whole content is "no audio for this turn yet". Everything more specific stands.
        var current = new VoiceDisplay { Kind = kind, Label = "before" };
        var readingError = WingmanErrorFold.ForSchedule("The model did not answer in time.", 0, Booked);

        var shown = VoiceDisplayFold.WithReadingError(current, readingError);

        Assert.Equal(replaced ? VoiceDisplayKinds.WingmanError : kind, shown.Kind);
        if (replaced) Assert.StartsWith("The model did not answer in time.", shown.Message);
        // And with no error at all nothing is replaced.
        Assert.Same(current, VoiceDisplayFold.WithReadingError(current, null));
    }
}
