using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// <see cref="VoiceRowStamp"/>, the ONE place a row is stamped with its voice verdict.
///
/// WHY THIS SUITE EXISTS AND WHY IT IS NOT A RESTATEMENT OF THE FOLD. There were two stamps - the roster
/// route and the Director push - each calling <c>VoiceDisplayFold</c> with its own argument list, and they
/// had already drifted: the push path's list is missing one of the facts the route's carries. Nothing
/// detected that, because nothing compared them. What is tested here is the GATHERING, not the ruling:
/// that each fact reaches the parameter it belongs to (a swapped pair changes the answer), that a fact
/// nobody can see is not invented, and that the waiting clock is told exactly what the fold reads.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class VoiceRowStampTests
{
    private static SessionDto Row() => new()
    {
        SessionId = "11111111-1111-1111-1111-111111111111",
        ActivityState = "WaitingForInput",
        VoiceMode = true,
    };

    /// <summary>The two readiness booleans are the Gateway's own - the Director never sets them - and a row
    /// carrying audio reads back as ready to play.</summary>
    [Fact]
    public void The_readiness_booleans_are_stamped_and_reach_the_fold()
    {
        var row = Row();

        VoiceRowStamp.Apply(row, new VoiceRowStamp.VoiceFacts(AudioReady: _ => true));

        Assert.True(row.VoiceAudioReady);
        Assert.Equal("ready", row.VoiceDisplay!.Kind);
        Assert.True(row.VoiceDisplay.CanPlay);
    }

    /// <summary>Being made right now is its own answer, and it is not "there is no voice".</summary>
    [Fact]
    public void Generating_reaches_the_fold_as_its_own_verdict()
    {
        var row = Row();

        VoiceRowStamp.Apply(row, new VoiceRowStamp.VoiceFacts(Generating: _ => true));

        Assert.True(row.VoiceGenerating);
        Assert.Equal("preparing", row.VoiceDisplay!.Kind);
    }

    /// <summary>
    /// EACH REASON REACHES ITS OWN PARAMETER. These are the facts that tell the owner WHY there is no
    /// audio, and they are the difference between a remedy and a shrug. Two of them swapped in the call
    /// would still produce a plausible verdict, which is exactly why they are each named here.
    /// </summary>
    [Theory]
    [InlineData("nothing-to-narrate", "nothingToNarrate")]
    [InlineData("director-too-old", "directorTooOld")]
    [InlineData("speech-error", "wingmanError")]
    public void Each_reason_reaches_the_parameter_it_belongs_to(string fact, string expectedKind)
    {
        var row = Row();

        VoiceRowStamp.Apply(row, new VoiceRowStamp.VoiceFacts(
            NothingToNarrate: _ => fact == "nothing-to-narrate",
            DirectorCannotSendConversation: _ => fact == "director-too-old",
            SpeechError: _ => fact == "speech-error"
                ? CcDirector.Gateway.Wingman.WingmanErrorFold.ForSchedule(CcDirector.Gateway.Wingman.WingmanErrorFold.SpeechFailedReason, 0, DateTime.UtcNow.AddMinutes(1))
                : null));

        Assert.Equal(expectedKind, row.VoiceDisplay!.Kind);
    }

    /// <summary>
    /// THE BACKUP-VOICE NOTICE, which is the fact the push path does not carry and the roster route does.
    /// It rides a normal playable clip and says so in one line - it is a success with a note, never an
    /// outage - and it is stamped only when the caller can actually see it.
    /// </summary>
    [Fact]
    public void The_backup_voice_notice_is_stamped_when_the_caller_can_see_it_and_never_invented()
    {
        var withNotice = Row();
        VoiceRowStamp.Apply(withNotice, new VoiceRowStamp.VoiceFacts(
            AudioReady: _ => true, ServedViaFallback: _ => true));

        var withoutTheFact = Row();
        VoiceRowStamp.Apply(withoutTheFact, new VoiceRowStamp.VoiceFacts(AudioReady: _ => true));

        Assert.False(string.IsNullOrWhiteSpace(withNotice.VoiceDisplay!.VoiceFallbackNotice));
        Assert.Equal("ready", withNotice.VoiceDisplay.Kind);
        Assert.Null(withoutTheFact.VoiceDisplay!.VoiceFallbackNotice);
    }

    /// <summary>
    /// A FACT THIS CALLER CANNOT SEE IS NOT OVERWRITTEN WITH A CONFIDENT FALSE. A caller with no voice
    /// service hands no delegate, and what the row already carries stands - which is what keeps a path that
    /// gathers fewer facts from actively destroying the ones already on the row.
    /// </summary>
    [Fact]
    public void A_fact_the_caller_cannot_see_leaves_the_rows_own_value_alone()
    {
        var row = Row();
        row.VoiceAudioReady = true;
        row.VoiceGenerating = true;

        VoiceRowStamp.Apply(row, new VoiceRowStamp.VoiceFacts());

        Assert.True(row.VoiceAudioReady);
        Assert.True(row.VoiceGenerating);
        Assert.Equal("ready", row.VoiceDisplay!.Kind);
    }

    /// <summary>Handed no facts at all, nothing is stamped - the row is left exactly as it arrived.</summary>
    [Fact]
    public void No_facts_at_all_stamps_nothing()
    {
        var row = Row();

        VoiceRowStamp.Apply(row, null);

        Assert.Null(row.VoiceDisplay);
        Assert.Null(row.VoiceWaitingSince);
    }

    /// <summary>
    /// THE CLOCK IS TOLD WHAT THE FOLD READS. It is a second clock beside the needs-you one, and its whole
    /// job is to say how long a session has been waiting for its voice - so if it is told "waiting" while
    /// the fold sees a session that is not, the elapsed time on a row and the words on that row describe
    /// different sessions. The readiness stamp happens FIRST for this reason: told from the row as it
    /// arrived, the clock would answer about a stale fact.
    /// </summary>
    [Fact]
    public void The_waiting_clock_is_told_what_the_fold_reads_after_the_readiness_stamp()
    {
        var waitingAnswers = new List<bool>();
        var stamped = new DateTime(2026, 9, 17, 11, 0, 0, DateTimeKind.Utc);

        var waiting = Row();
        VoiceRowStamp.Apply(waiting, new VoiceRowStamp.VoiceFacts(
            AudioReady: _ => false,
            WaitingStamp: (_, isWaiting) => { waitingAnswers.Add(isWaiting); return isWaiting ? stamped : null; }));

        // A session with voice on, no audio and not working IS waiting - and the clock was told so.
        Assert.Equal(new[] { true }, waitingAnswers);
        Assert.Equal(stamped, waiting.VoiceWaitingSince);

        // The same row with audio ready is NOT waiting, and the clock is told THAT - read from the boolean
        // this stamp had just written, not from the row as it arrived.
        waitingAnswers.Clear();
        var ready = Row();
        ready.VoiceAudioReady = false;
        VoiceRowStamp.Apply(ready, new VoiceRowStamp.VoiceFacts(
            AudioReady: _ => true,
            WaitingStamp: (_, isWaiting) => { waitingAnswers.Add(isWaiting); return isWaiting ? stamped : null; }));

        Assert.Equal(new[] { false }, waitingAnswers);
        Assert.Null(ready.VoiceWaitingSince);
    }

    /// <summary>A working session's finished-turn narration is stale, so the fold's own working arm answers -
    /// and it is reached from the row's activity state, which is where both callers read it from.</summary>
    [Fact]
    public void A_working_session_is_read_off_the_rows_own_activity_state()
    {
        var row = Row();
        row.ActivityState = "Working";

        VoiceRowStamp.Apply(row, new VoiceRowStamp.VoiceFacts(AudioReady: _ => true));

        Assert.Equal("working", row.VoiceDisplay!.Kind);
    }
}
