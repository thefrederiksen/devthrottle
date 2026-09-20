using CcDirector.Core.HostedAi;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2576: the terminal "gave up" verdict, and the wait it carries.
///
/// Before this, every no-audio verdict was either a calm "still coming" or a specific fault, so a
/// narration that simply never arrived matched the calm one FOREVER. A session sat that way for
/// forty-eight minutes. <c>SessionOrdering.IsVoicePreparing</c>'s own note named a terminal gave-up state
/// as the correct answer to that wedge and it was never built; this is it.
/// </summary>
public sealed class VoiceGaveUpFoldTests
{
    private static readonly DateTime Now = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);

    private static VoiceDisplay Fold(DateTime? waitingSince, bool generating = false, bool hasAudio = false,
        HostedAiState? unavailable = null, bool nothingToNarrate = false)
        => VoiceDisplayFold.Fold(
            voiceMode: true, agentWorking: false, hasAudio: hasAudio, generating: generating,
            unavailable: unavailable, nothingToNarrate: nothingToNarrate,
            waitingSince: waitingSince, utcNow: Now);

    [Fact]
    public void PastTheThreshold_TheVerdictIsGaveUp_AndSaysHowLong()
    {
        var v = Fold(Now - TimeSpan.FromMinutes(48));

        Assert.Equal("gaveUp", v.Kind);
        Assert.Equal("Voice did not arrive after 48m", v.Label);
        Assert.Equal("48m", v.WaitedLabel);
        Assert.False(v.CanPlay);
        // THE BUTTON IS OFFERED NOW (mission "Wingman error and retry", 2026-09-19). It was withheld on the ground that
        // "automatic attempts continue, so a button would only duplicate them" - and this fold is not told what is
        // booked, so it could not know that, and for seven sessions it was false. Asking is the one action on this
        // card that can still produce the turn's audio.
        Assert.True(v.CanGenerate);
        Assert.DoesNotContain("still trying", v.Message);
    }

    [Fact]
    public void JustUnderTheThreshold_IsStillTheCalmNotReady_NotGaveUp()
    {
        // The boundary matters in this direction: declaring defeat early would put a red "did not arrive"
        // on the ordinary case, which is how a real signal gets trained out of a reader.
        var v = Fold(Now - VoiceDisplayFold.GaveUpAfter + TimeSpan.FromSeconds(1));
        Assert.NotEqual("gaveUp", v.Kind);
    }

    [Fact]
    public void ExactlyAtTheThreshold_HasGivenUp()
    {
        Assert.Equal("gaveUp", Fold(Now - VoiceDisplayFold.GaveUpAfter).Kind);
    }

    [Fact]
    public void PlayableAudio_BeatsGaveUp_HoweverLongTheWaitWas()
    {
        // Audio arriving is the episode ENDING. A clip the reader can play must never be hidden behind a
        // verdict about how long it took to make.
        var v = Fold(Now - TimeSpan.FromHours(2), hasAudio: true);
        Assert.Equal("ready", v.Kind);
        Assert.True(v.CanPlay);
    }

    [Fact]
    public void ALiveAttempt_BeatsGaveUp()
    {
        // Something IS happening right now, so saying it did not arrive would be false at the moment it is
        // read - and the attempt in flight may well be the one that lands.
        var v = Fold(Now - TimeSpan.FromHours(2), generating: true);
        Assert.Equal("preparing", v.Kind);
    }

    [Fact]
    public void GaveUp_OutranksARetryingState_BecauseTheRetryHasHadLongEnough()
    {
        // "Voice on its way" past the threshold is the exact sentence this issue exists to stop. A retry
        // that has been going for 48 minutes is not news of progress.
        var v = Fold(Now - TimeSpan.FromMinutes(48), unavailable: HostedAiState.Retrying);
        Assert.Equal("gaveUp", v.Kind);
    }

    /// <summary>
    /// EVERY ACTIONABLE REASON OUTRANKS THE GIVE-UP VERDICT, and the first draft had this backwards for all
    /// of them. A member who needs to add credit, finish setup, or wait out a speech outage was told "voice
    /// did not arrive" - the symptom, in place of the one sentence that tells them what to do about it.
    /// Same defect as a terminal read failure hiding a standing account condition, which this codebase
    /// fixed once already the same day.
    /// </summary>
    [Theory]
    [InlineData(HostedAiState.ServiceDown, "serviceDown")]
    [InlineData(HostedAiState.NeedsCredits, "blocked")]
    [InlineData(HostedAiState.NeedsKey, "blocked")]
    [InlineData(HostedAiState.CapReached, "blocked")]
    [InlineData(HostedAiState.SubscriptionRequired, "blocked")]
    public void AnActionableReason_OutranksGaveUp_HoweverLongTheWait(HostedAiState state, string expectedKind)
    {
        var v = Fold(Now - TimeSpan.FromMinutes(48), unavailable: state);
        Assert.Equal(expectedKind, v.Kind);
    }

    [Fact]
    public void WaitingOnAPromptForHours_IsNotAccusedOfLosingANarration()
    {
        // THE FALSE ALARM this ordering exists to prevent, and the first draft got it wrong. A session
        // parked on a menu has no text reply to read aloud, so no narration was ever coming - saying "it
        // did not arrive" reports a failure that never happened, and points the reader at the voice
        // pipeline instead of at the prompt actually waiting for them.
        var v = Fold(Now - TimeSpan.FromHours(2), nothingToNarrate: true);

        Assert.Equal("nothingToNarrate", v.Kind);
        Assert.Equal("Nothing to read aloud", v.Label);
    }

    [Fact]
    public void NotWaitingAtAll_IsUnchanged()
    {
        // A null clock must not manufacture a verdict - this is the ordinary path for every session that
        // has its audio, and for every caller that does not supply the clock at all.
        var v = Fold(waitingSince: null, nothingToNarrate: true);
        Assert.Equal("nothingToNarrate", v.Kind);
        Assert.Null(v.WaitedLabel);
    }

    [Theory]
    [InlineData(0, 30, null)]          // under a minute: silent, so an ordinary turn shows no number
    [InlineData(0, 61, "1m")]
    [InlineData(0, 44 * 60, "44m")]
    [InlineData(1, 4 * 60, "1h 4m")]
    [InlineData(50, 0, "2d 2h")]
    public void TheWaitLadder_MatchesTheOneTheRosterAlreadyUses(int hours, int seconds, string? expected)
    {
        var since = Now - TimeSpan.FromHours(hours) - TimeSpan.FromSeconds(seconds);
        Assert.Equal(expected, VoiceDisplayFold.WaitedLabelFor(since, Now));
    }

    [Fact]
    public void APlainNotReadyWindow_AlsoGivesUpEventually()
    {
        // The other sentence that was still being said at forty-eight minutes. "No narration yet" is honest
        // for a moment and misleading for an hour, so it takes the same threshold the retrying arm does.
        var v = Fold(Now - TimeSpan.FromMinutes(48));
        Assert.Equal("gaveUp", v.Kind);
    }

    [Fact]
    public void TheWaitingPredicate_IsTheOneBothCallersUse()
    {
        // It lived TWICE, hand-written, in the roster aggregation and the display-push enrichment. They
        // agreed character for character, which is the condition under which two copies stay wrong together
        // and then quietly diverge. This is the single definition both now call.
        Assert.True(VoiceDisplayFold.IsWaitingForVoice(voiceMode: true, hasAudio: false, agentWorking: false));
        Assert.False(VoiceDisplayFold.IsWaitingForVoice(voiceMode: false, hasAudio: false, agentWorking: false));
        Assert.False(VoiceDisplayFold.IsWaitingForVoice(voiceMode: true, hasAudio: true, agentWorking: false));
        Assert.False(VoiceDisplayFold.IsWaitingForVoice(voiceMode: true, hasAudio: false, agentWorking: true));
    }

    [Fact]
    public void APreparingSessionCarriesItsWait_SoTheRailCanShowIt()
    {
        // The calm state carries the number too: the rail reads "Preparing voice (2m)", so a wait that is
        // growing is visible BEFORE it reaches the point of giving up.
        var v = Fold(Now - TimeSpan.FromMinutes(2), generating: true);
        Assert.Equal("preparing", v.Kind);
        Assert.Equal("2m", v.WaitedLabel);
    }
}
