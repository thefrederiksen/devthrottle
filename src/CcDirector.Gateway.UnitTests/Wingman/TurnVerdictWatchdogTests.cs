using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// THE CARRYING-ON CLOCK (the Wingman-on-every-turn mission, ruling 6). A "continues-alone" verdict expires at the
/// announced next wake-up plus two minutes, else ten minutes after it was judged; on expiry a "needed-you" verdict
/// labelled "Said it would continue and did not" is stored in its place with the original receipt; a Working
/// transition before the deadline stops the clock. Every moment below is set by an injected clock.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class TurnVerdictWatchdogTests
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string ReplyText = "I will keep watching the nightly build and report back when it finishes.";
    private static readonly DateTime JudgedAt = new(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

    private static TurnVerdictDto CarryingOn(DateTime? nextWake = null, string id = "carry-1") => new()
    {
        VerdictId = id,
        JudgedAtUtc = JudgedAt,
        TurnEndObservedAtUtc = JudgedAt.AddSeconds(-30),
        ScreenHash = "hash-1",
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v1",
        PackageKind = "agent-reply",
        Verdict = TurnVerdictVocabulary.ContinuesAlone,
        Confidence = "high",
        Evidence = ReplyText,
        Label = "Watching the nightly build",
        Summary = "It is watching the nightly build by itself.",
        AnswerVia = "reply",
        Risk = "none",
        Spoken = "The nightly build. It is watching the build and will report back.",
        NextScheduledWakeUtc = nextWake,
    };

    private static FakeTurnVerdictEnvironment ColourOnEnv(Func<DateTime> clock) => new()
    {
        Knobs = TurnVerdictSettings.Defaults with { JudgeEnabled = true, ColourEnabled = true, SettleMs = 0 },
        Clock = clock,
    };

    // ================================================================= the rule, by itself

    [Fact]
    public void DeadlineFor_NoAnnouncedWake_IsTenMinutesAfterItWasJudged()
    {
        Assert.Equal(JudgedAt.AddMinutes(10), TurnVerdictWatchdog.DeadlineFor(CarryingOn()));
    }

    [Fact]
    public void DeadlineFor_AnAnnouncedWake_IsTwoMinutesAfterTheWake_NotTenAfterTheJudging()
    {
        var verdict = CarryingOn(nextWake: JudgedAt.AddMinutes(30));

        Assert.Equal(JudgedAt.AddMinutes(32), TurnVerdictWatchdog.DeadlineFor(verdict));
        // The ten-minute rule would already have expired it; the announced wake is the one that counts.
        Assert.False(TurnVerdictWatchdog.IsExpired(verdict, JudgedAt.AddMinutes(11)));
        Assert.True(TurnVerdictWatchdog.IsExpired(verdict, JudgedAt.AddMinutes(32)));
    }

    [Fact]
    public void DeadlineFor_AVerdictThatIsNotAnAcceptedCarryingOn_HasNoClock()
    {
        var finished = CarryingOn();
        finished.Verdict = TurnVerdictVocabulary.Finished;
        var refused = CarryingOn();
        refused.Failed = true;

        Assert.Null(TurnVerdictWatchdog.DeadlineFor(finished));
        Assert.Null(TurnVerdictWatchdog.DeadlineFor(refused));
        Assert.False(TurnVerdictWatchdog.IsExpired(finished, JudgedAt.AddDays(1)));
    }

    [Fact]
    public void IsExpired_ASecondBeforeTheDeadline_IsFalse_AndAtTheDeadline_IsTrue()
    {
        Assert.False(TurnVerdictWatchdog.IsExpired(CarryingOn(), JudgedAt.AddMinutes(10).AddSeconds(-1)));
        Assert.True(TurnVerdictWatchdog.IsExpired(CarryingOn(), JudgedAt.AddMinutes(10)));
    }

    [Fact]
    public void Expire_IsANeededYouVerdictWithTheLabel_TheOriginalReceipt_TheSameStop_AndALaterMoment()
    {
        var original = CarryingOn();
        var now = JudgedAt.AddMinutes(12);

        var expired = TurnVerdictWatchdog.Expire(original, now);

        Assert.Equal(TurnVerdictVocabulary.NeededYou, expired.Verdict);
        Assert.Equal("Said it would continue and did not", expired.Label);
        Assert.Equal(ReplyText, expired.Evidence);
        Assert.Equal(original.TurnEndObservedAtUtc, expired.TurnEndObservedAtUtc);
        Assert.Equal(original.ScreenHash, expired.ScreenHash);
        Assert.Equal(now, expired.JudgedAtUtc);
        Assert.NotEqual(original.VerdictId, expired.VerdictId);
        Assert.False(expired.Failed);
        Assert.Empty(expired.Options);
        Assert.Equal(TurnVerdictWatchdog.ClockModel, expired.Model);
        // On the fold it is a red row reading the clock's words.
        var row = new SessionDto
        {
            SessionId = "s", ActivityState = "WaitingForInput", VerdictState = VerdictStates.Judged,
            TurnVerdict = expired, VerdictLabel = expired.Label,
        };
        Assert.Equal("red", SessionOrdering.EffectiveColor(row));
        Assert.Equal("Said it would continue and did not", SessionOrdering.StateLabel(row));
    }

    // ================================================================= the seat's tick, with an injected clock

    [Fact]
    public void ExpireCarryingOn_WithoutAnAnnouncedWake_WaitsTenMinutes_ThenStoresNeededYouInPlace_AndKeepsTheOriginal()
    {
        var now = JudgedAt;
        var env = ColourOnEnv(() => now);
        env.Store(Tenant, "s1", CarryingOn());
        var service = new TurnVerdictService(env);

        now = JudgedAt.AddMinutes(10).AddSeconds(-1);
        Assert.Equal(0, service.ExpireCarryingOn(Tenant));
        Assert.Equal(TurnVerdictVocabulary.ContinuesAlone, env.Latest(Tenant, "s1")!.Verdict);

        now = JudgedAt.AddMinutes(10);
        Assert.Equal(1, service.ExpireCarryingOn(Tenant));

        var latest = env.Latest(Tenant, "s1")!;
        Assert.Equal(TurnVerdictVocabulary.NeededYou, latest.Verdict);
        Assert.Equal(TurnVerdictWatchdog.ExpiredLabel, latest.Label);
        // The judge's own answer stays in the history the grading reads.
        Assert.Equal(new[] { TurnVerdictVocabulary.NeededYou, TurnVerdictVocabulary.ContinuesAlone },
            env.StoredRows(Tenant, "s1").Select(r => r.Verdict));
        Assert.Contains(env.Records, r => r.EventType == ActivityEventTypes.TurnVerdictExpired
                                          && r.Cause == ActivityCauses.CarryingOnExpired && r.SessionId == "s1");

        // A second tick finds no clock running: the latest verdict is the needed-you one.
        now = JudgedAt.AddHours(1);
        Assert.Equal(0, service.ExpireCarryingOn(Tenant));
    }

    [Fact]
    public void ExpireCarryingOn_WithAnAnnouncedWake_WaitsForTheWakePlusTwoMinutes()
    {
        var now = JudgedAt;
        var env = ColourOnEnv(() => now);
        env.Store(Tenant, "s1", CarryingOn(nextWake: JudgedAt.AddMinutes(20)));
        var service = new TurnVerdictService(env);

        now = JudgedAt.AddMinutes(15);
        Assert.Equal(0, service.ExpireCarryingOn(Tenant));
        now = JudgedAt.AddMinutes(21).AddSeconds(59);
        Assert.Equal(0, service.ExpireCarryingOn(Tenant));

        now = JudgedAt.AddMinutes(22);
        Assert.Equal(1, service.ExpireCarryingOn(Tenant));
        Assert.Equal(TurnVerdictVocabulary.NeededYou, env.Latest(Tenant, "s1")!.Verdict);
    }

    [Fact]
    public void ExpireCarryingOn_ColourSwitchOff_ReadsNothingAndExpiresNothing()
    {
        var now = JudgedAt.AddHours(2);
        var env = ColourOnEnv(() => now);
        env.Knobs = env.Knobs with { ColourEnabled = false };
        env.Store(Tenant, "s1", CarryingOn());
        var service = new TurnVerdictService(env);

        Assert.Equal(0, service.ExpireCarryingOn(Tenant));
        Assert.Equal(0, env.SnapshotReads);
        Assert.Equal(TurnVerdictVocabulary.ContinuesAlone, env.Latest(Tenant, "s1")!.Verdict);
    }

    [Fact]
    public async Task ExpireCarryingOn_AWorkingTransitionBeforeTheDeadline_StopsTheClock_WhileAnUntouchedSessionExpires()
    {
        // Judged through the seat's real path, so the verdict the clock reads is one the seat stored.
        var t0 = DateTime.UtcNow;
        var now = t0;
        var env = ColourOnEnv(() => now);
        env.Screen = () => Screen("any", ReplyText, "> ");
        env.Conversation = _ => Reply("keep an eye on the nightly build", ReplyText);
        env.Judge = (_, _) => Task.FromResult(ContinuesAloneAnswer());
        var service = new TurnVerdictService(env);

        var worked = await service.StartTurnEnd(new TurnEndSignal("worked", "dir-1", Tenant, t0, IsNewTurn: true));
        var untouched = await service.StartTurnEnd(new TurnEndSignal("untouched", "dir-1", Tenant, t0, IsNewTurn: true));
        // CONTROL: both stops really were judged carrying on, so the clock has something to act on.
        Assert.Equal(TurnVerdictOutcomeKind.Judged, worked.Kind);
        Assert.Equal(TurnVerdictOutcomeKind.Judged, untouched.Kind);
        Assert.Equal(TurnVerdictVocabulary.ContinuesAlone, env.Latest(Tenant, "worked")!.Verdict);

        service.OnSessionWorking(Tenant, "worked");
        now = t0.AddMinutes(11);

        Assert.Equal(1, service.ExpireCarryingOn(Tenant));
        Assert.Null(env.Latest(Tenant, "worked"));
        Assert.Equal(TurnVerdictVocabulary.NeededYou, env.Latest(Tenant, "untouched")!.Verdict);
    }

    // ================================================================= the owner's own sessions (owner ruling, 2026-09-15)

    private static SessionDto RowFor(TurnVerdictDto verdict) => new()
    {
        SessionId = "owner",
        ActivityState = "WaitingForInput",
        VerdictState = VerdictStates.Judged,
        TurnVerdict = verdict,
        VerdictLabel = verdict.Label,
    };

    [Fact]
    public void ExpireCarryingOn_AnOwnerWithOneWorkingChild_HoldsPurplePastTenMinutes()
    {
        var now = JudgedAt;
        var env = ColourOnEnv(() => now);
        env.Store(Tenant, "owner", CarryingOn());
        env.Owned = sid => sid == "owner" ? new OwnedSessionsFacts(Working: 1, Stopped: 0, NeedYou: 0, LastActivityAtUtc: now) : null;
        var service = new TurnVerdictService(env);

        now = JudgedAt.AddMinutes(25);
        // CONTROL: on its own clock alone this verdict ran out fifteen minutes ago.
        Assert.True(TurnVerdictWatchdog.IsExpired(CarryingOn(), now));

        Assert.Equal(0, service.ExpireCarryingOn(Tenant));
        var latest = env.Latest(Tenant, "owner")!;
        Assert.Equal(TurnVerdictVocabulary.ContinuesAlone, latest.Verdict);
        Assert.Equal("purple", SessionOrdering.EffectiveColor(RowFor(latest)));
    }

    [Fact]
    public void ExpireCarryingOn_TheSameOwner_GoesRedTenMinutesAfterTheChildStops()
    {
        var now = JudgedAt;
        DateTime? childStoppedAt = null;
        var env = ColourOnEnv(() => now);
        env.Store(Tenant, "owner", CarryingOn());
        env.Owned = sid => sid != "owner" ? null
            : childStoppedAt is { } stopped
                ? new OwnedSessionsFacts(Working: 0, Stopped: 1, NeedYou: 0, LastActivityAtUtc: stopped)
                : new OwnedSessionsFacts(Working: 1, Stopped: 0, NeedYou: 0, LastActivityAtUtc: now);
        var service = new TurnVerdictService(env);

        now = JudgedAt.AddMinutes(30);
        Assert.Equal(0, service.ExpireCarryingOn(Tenant));

        childStoppedAt = JudgedAt.AddMinutes(30);
        now = JudgedAt.AddMinutes(40).AddSeconds(-1);
        Assert.Equal(0, service.ExpireCarryingOn(Tenant));

        now = JudgedAt.AddMinutes(40);
        Assert.Equal(1, service.ExpireCarryingOn(Tenant));
        var latest = env.Latest(Tenant, "owner")!;
        Assert.Equal(TurnVerdictVocabulary.NeededYou, latest.Verdict);
        Assert.Equal("red", SessionOrdering.EffectiveColor(RowFor(latest)));
        Assert.Equal(TurnVerdictWatchdog.ExpiredLabel, SessionOrdering.StateLabel(RowFor(latest)));
    }

    [Fact]
    public void ExpireCarryingOn_AChildThatGoesRed_FlipsNothingOnTheOwner()
    {
        var now = JudgedAt;
        var childRed = false;
        var env = ColourOnEnv(() => now);
        env.Store(Tenant, "owner", CarryingOn());
        env.Owned = sid => sid != "owner" ? null
            : childRed
                ? new OwnedSessionsFacts(Working: 0, Stopped: 0, NeedYou: 1, LastActivityAtUtc: JudgedAt.AddMinutes(5))
                : new OwnedSessionsFacts(Working: 1, Stopped: 0, NeedYou: 0, LastActivityAtUtc: now);
        var service = new TurnVerdictService(env);

        now = JudgedAt.AddMinutes(4);
        Assert.Equal(0, service.ExpireCarryingOn(Tenant));

        childRed = true;
        now = JudgedAt.AddMinutes(5);
        Assert.Equal(0, service.ExpireCarryingOn(Tenant));

        // The owner is untouched: still carrying on, still purple, and nothing was written about the child - it
        // surfaces on its own row.
        var owner = env.Latest(Tenant, "owner")!;
        Assert.Equal(TurnVerdictVocabulary.ContinuesAlone, owner.Verdict);
        Assert.Equal("purple", SessionOrdering.EffectiveColor(RowFor(owner)));
        Assert.Equal(1, env.StoredCount(Tenant, "owner"));
        Assert.DoesNotContain(env.Records, r => r.EventType == ActivityEventTypes.TurnVerdictExpired);

        // The child's stop is an ordinary stop for the owner's clock: ten minutes from it, and not before.
        now = JudgedAt.AddMinutes(15).AddSeconds(-1);
        Assert.Equal(0, service.ExpireCarryingOn(Tenant));
        now = JudgedAt.AddMinutes(15);
        Assert.Equal(1, service.ExpireCarryingOn(Tenant));
    }

    private static string ContinuesAloneAnswer() => JsonSerializer.Serialize(new
    {
        verdict = "continues-alone",
        confidence = "high",
        evidence = ReplyText,
        label = "Watching the nightly build",
        summary = "It is watching the nightly build by itself and will report back.",
        agentRecommends = (string?)null,
        answerVia = "reply",
        menu = (object?)null,
        options = Array.Empty<object>(),
        risk = "none",
        spoken = "The nightly build. It is watching the build and will report back when it finishes.",
    });
}
