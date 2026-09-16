using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// NO RED FRAME BEFORE THE WINGMAN READS, proved at the SEAT (the Wingman-on-every-turn mission, slice F, and
/// the owner's slice E ruling).
///
/// WHAT THIS FILE COVERS THAT THE FOLD TESTS CANNOT. <see cref="SnoozeExpiryReJudgeTests"/> proves the FOLD
/// paints the row yellow when the seat says it is reading - its seat is a fake that answers whatever the test
/// wants. That is the right shape for a fold test and it says nothing whatever about whether the REAL seat
/// answers truthfully, or answers in time. This file drives the real <see cref="TurnVerdictService"/>.
///
/// THE ORIGINAL DEFECT, in one sentence: the re-judge went through the seat's ordinary current-screen request,
/// which reads the screen BEFORE it stamps "reading" - so the fold that asked for the read returned first and
/// served the row red, and the yellow appeared only on some later poll. The decisive test is therefore to hold
/// the screen read open and look: with the old ordering nothing is stamped while the read is blocked, because
/// the stamp is on the far side of it.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class SnoozeExpiryStampsReadingBeforeItReadsTests
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-snooze-expiry";
    private const string ReplyText = "I have pushed the branch and opened the pull request.";

    private static FakeTurnVerdictEnvironment Env() => new()
    {
        Screen = () => Screen(Sid, ReplyText, "> "),
        Conversation = _ => Reply("push it", ReplyText),
        Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Finished(ReplyText, "The branch is pushed.")),
    };

    [Fact]
    public void TheStopIsStampedReading_WhileTheScreenReadIsStillOpen()
    {
        // The screen read never returns for as long as this test holds the gate. With the stamp on the far side
        // of that read - where it was - nothing would be stamped here at all.
        using var screenIsOpen = new ManualResetEventSlim(false);
        var env = Env();
        env.Screen = () =>
        {
            screenIsOpen.Wait(TimeSpan.FromSeconds(30));
            return Screen(Sid, ReplyText, "> ");
        };
        using var seat = new TurnVerdictService(env);

        var reading = seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", Sid);

        // Answered on the caller's thread - this IS the fold's thread in production, and the fold is about to
        // serve the very row it just asked about.
        Assert.True(reading);
        Assert.True(seat.IsReading(Tenant, Sid));

        screenIsOpen.Set();
    }

    [Fact]
    public void TheStopIsStampedReading_BeforeTheScreenHasBeenReadAtAll()
    {
        // The same claim, counted rather than blocked: the seat has taken its roster snapshot and stamped, and
        // has not asked for a screen, at the moment it answers. Kept beside the blocking test because a count is
        // the more direct statement of "before", and the blocking one is the more robust proof of it.
        using var screenIsOpen = new ManualResetEventSlim(false);
        var env = Env();
        env.Screen = () =>
        {
            screenIsOpen.Wait(TimeSpan.FromSeconds(30));
            return Screen(Sid, ReplyText, "> ");
        };
        using var seat = new TurnVerdictService(env);

        seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", Sid);

        Assert.True(seat.IsReading(Tenant, Sid));
        Assert.Equal(0, env.ScreenReads);

        screenIsOpen.Set();
    }

    [Fact]
    public void AnAccountWhoseJudgeSwitchIsOff_IsNotStamped_AndTheSeatSaysSo()
    {
        // RED WHEN IT WILL NOT JUDGE. Only a stop actually about to be read turns yellow; a stop nothing will
        // ever look at must keep the detector's red, or the fix trades one lie for another.
        var env = Env();
        env.Knobs = env.Knobs with { JudgeEnabled = false };
        using var seat = new TurnVerdictService(env);

        var reading = seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", Sid);

        Assert.False(reading);
        Assert.False(seat.IsReading(Tenant, Sid));
    }

    [Fact]
    public void AHeldSession_IsNotStamped_AndTheSeatSaysSo()
    {
        // A session a live owning session holds is never read, so nothing is reading it and nothing will be.
        var env = Env();
        env.Held = _ => true;
        using var seat = new TurnVerdictService(env);

        var reading = seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", Sid);

        Assert.False(reading);
        Assert.False(seat.IsReading(Tenant, Sid));
    }

    [Fact]
    public async Task TheStampIsCleared_WhenTheJudgementEnds()
    {
        // THE STAMP IS PAIRED WITH A FLIGHT, and that is why the gate is taken before it is written. A stamp made
        // without the gate could be left behind by a judgement that had already ended, and the row would sit
        // yellow for the life of the process with nothing reading it.
        var env = Env();
        using var seat = new TurnVerdictService(env);

        Assert.True(seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", Sid));

        var waitedFor = TimeSpan.FromSeconds(30);
        var until = DateTime.UtcNow + waitedFor;
        while (seat.IsReading(Tenant, Sid) && DateTime.UtcNow < until)
            await Task.Delay(10);

        Assert.False(seat.IsReading(Tenant, Sid));
        Assert.Equal(1, env.JudgeCalls);
    }

    [Fact]
    public void AJudgementAlreadyInFlight_IsNotAskedTwice_AndTheSeatReportsWhatThatOneStamped()
    {
        // ONE MODEL CALL PER STOP. A judgement already running for this session is the answer this expiry wanted,
        // so the expiry stands aside - and reports what that judgement has stamped rather than stamping anything
        // of its own.
        using var screenIsOpen = new ManualResetEventSlim(false);
        var env = Env();
        env.Screen = () =>
        {
            screenIsOpen.Wait(TimeSpan.FromSeconds(30));
            return Screen(Sid, ReplyText, "> ");
        };
        using var seat = new TurnVerdictService(env);

        Assert.True(seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", Sid));
        var second = seat.StartSnoozeExpiryReJudge(Tenant, "dir-1", Sid);

        Assert.True(second);
        Assert.Equal(1, env.StateReads);

        screenIsOpen.Set();
    }
}
