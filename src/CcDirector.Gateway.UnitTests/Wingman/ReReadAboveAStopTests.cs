using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// A SESSION IS CYAN ONLY WHILE SESSIONS UNDER IT ARE WORKING (the owner's ruling, 30 September 2026; issue 3499). A calm
/// reading made while work ran under a parent - the model's, or way-back's - is read again, by code, when a session stops,
/// exits or is removed and nothing is working under that parent any more. Nothing else asks about the parent then.
///
/// Every screen and conversation below is written from scratch. Not a byte of a real session is here.
/// </summary>
public sealed class ReReadAboveAStopTests
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Parent = "sid-parent";
    private const string Child = "sid-child";
    private static readonly DateTime ObservedAt = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private const string Reply = "The worker is building the image; I will pick it up when it lands.";

    private static TurnEndSignal Signal(string sid) => new(sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true);

    private static StoredConversation Conversation(params (string Tool, string Input)[] lastTurnTools)
    {
        var widgets = new List<TurnWidgetDto>
        {
            new() { Kind = StoredConversationWidgets.UserTextKind, Content = "Get the worker to build the image." },
        };
        foreach (var (tool, input) in lastTurnTools)
            widgets.Add(new TurnWidgetDto { Kind = TurnVerdictPackageBuilder.ToolUseKind, Header = tool, Content = input });
        widgets.Add(new TurnWidgetDto { Kind = StoredConversationWidgets.AgentTextKind, Content = Reply });
        return new StoredConversation(true, widgets);
    }

    /// <summary>A parent whose stop the model reads as carrying on while one session works under it.</summary>
    private static FakeTurnVerdictEnvironment ParentWithWorkUnderIt(
        params (string Tool, string Input)[] lastTurnTools)
    {
        var under = new OwnedSessionCounts(Working: 1, Stopped: 0, NeedYou: 0);
        var env = new FakeTurnVerdictEnvironment
        {
            Screen = () => Screen(Parent, "  " + Reply, "", "> "),
            Conversation = _ => Conversation(lastTurnTools),
            Judge = (_, _) => Task.FromResult("carrying-on"),
        };
        env.Owned = sid => sid == Parent ? under : new OwnedSessionCounts(0, 0, 0);
        return env;
    }

    [Fact]
    public async Task AModelReading_IsReadAgainByCode_WhenTheLastSessionUnderItStops()
    {
        var env = ParentWithWorkUnderIt();
        var service = new TurnVerdictService(env);
        await service.StartTurnEnd(Signal(Parent));
        Assert.Equal(CallACodeSteps.ModelStep, env.Latest(Tenant, Parent)!.DecidedBy);
        Assert.Equal("continues-alone", env.Latest(Tenant, Parent)!.Verdict);

        env.Owned = sid => sid == Parent ? new OwnedSessionCounts(Working: 0, Stopped: 1, NeedYou: 0) : new OwnedSessionCounts(0, 0, 0);
        var reread = await service.ReReadAboveAStopAsync(Tenant, Child);

        Assert.Equal(1, reread);
        Assert.Equal(1, env.JudgeCalls);                     // the re-decision is code, not a second model call
        var stored = env.Latest(Tenant, Parent)!;
        Assert.Equal("needed-you", stored.Verdict);
        Assert.Equal(CallACodeSteps.AllUnderItStoppedStep, stored.DecidedBy);
    }

    [Fact]
    public async Task AWayBackReading_IsReadAgainByCode_WhenTheSessionUnderItIsRemoved()
    {
        var env = ParentWithWorkUnderIt(("Monitor", "{\"until\":\"image built\"}"));
        var service = new TurnVerdictService(env);
        await service.StartTurnEnd(Signal(Parent));
        Assert.Equal(CallACodeSteps.WayBackStep, env.Latest(Tenant, Parent)!.DecidedBy);

        env.Owned = _ => new OwnedSessionCounts(0, 0, 0);    // the only session under it is gone from the roster
        var reread = await service.ReReadAboveAStopAsync(Tenant, Child);

        Assert.Equal(1, reread);
        Assert.Equal(0, env.JudgeCalls);
        var stored = env.Latest(Tenant, Parent)!;
        Assert.Equal("needed-you", stored.Verdict);
        Assert.Equal(CallACodeSteps.NothingUnderItStep, stored.DecidedBy);
    }

    [Fact]
    public async Task Control_WorkStillRunningUnderIt_TheParentIsNotRead()
    {
        var env = ParentWithWorkUnderIt();
        var service = new TurnVerdictService(env);
        await service.StartTurnEnd(Signal(Parent));
        var reading = env.Latest(Tenant, Parent)!.VerdictId;
        var screens = env.ScreenReads;

        var reread = await service.ReReadAboveAStopAsync(Tenant, Child);

        Assert.Equal(0, reread);
        Assert.Equal(screens, env.ScreenReads);
        Assert.Equal(reading, env.Latest(Tenant, Parent)!.VerdictId);
    }

    [Fact]
    public async Task ARedACodeStepMade_IsNeverAskedAboutAgain_NotEvenItsRoster()
    {
        var env = new FakeTurnVerdictEnvironment { Screen = () => Screen(Parent, "  Done.", "", "> ") };
        env.Owned = _ => new OwnedSessionCounts(0, 0, 0);
        var service = new TurnVerdictService(env);
        await service.StartTurnEnd(Signal(Parent));
        Assert.Equal(CallACodeSteps.NothingUnderItStep, env.Latest(Tenant, Parent)!.DecidedBy);
        var states = env.StateReads;
        var screens = env.ScreenReads;

        var reread = await service.ReReadAboveAStopAsync(Tenant, Child);

        Assert.Equal(0, reread);
        Assert.Equal(states, env.StateReads);
        Assert.Equal(screens, env.ScreenReads);
    }

    [Fact]
    public async Task AHeldParent_IsNotRead()
    {
        var env = ParentWithWorkUnderIt();
        var service = new TurnVerdictService(env);
        await service.StartTurnEnd(Signal(Parent));
        var screens = env.ScreenReads;

        env.Held = sid => sid == Parent;
        env.Owned = _ => new OwnedSessionCounts(0, 1, 0);
        var reread = await service.ReReadAboveAStopAsync(Tenant, Child);

        Assert.Equal(0, reread);
        Assert.Equal(screens, env.ScreenReads);
        Assert.Equal(CallACodeSteps.ModelStep, env.Latest(Tenant, Parent)!.DecidedBy);
    }

    [Fact]
    public async Task TheStoppedSessionItself_IsLeftToItsOwnTurnEnd()
    {
        var env = ParentWithWorkUnderIt();
        var service = new TurnVerdictService(env);
        await service.StartTurnEnd(Signal(Parent));
        env.Owned = _ => new OwnedSessionCounts(0, 1, 0);

        Assert.Equal(0, await service.ReReadAboveAStopAsync(Tenant, Parent));
    }

    [Fact]
    public async Task WorkStartedUnderItAgainBeforeTheRead_KeepsTheReading_AndNeverAsksTheModel()
    {
        var env = ParentWithWorkUnderIt();
        var service = new TurnVerdictService(env);
        await service.StartTurnEnd(Signal(Parent));
        var reading = env.Latest(Tenant, Parent)!.VerdictId;
        env.Screen = () => Screen(Parent, "  a status line ticked", "", "> ");   // the screen was redrawn

        await service.VerdictForCurrentScreenAsync(Tenant, "dir-1", Parent, TurnVerdictTrigger.UnderItStopped);

        Assert.Equal(1, env.JudgeCalls);
        Assert.Equal(reading, env.Latest(Tenant, Parent)!.VerdictId);
    }

    [Fact]
    public async Task TheReRead_WaitsOutTheSettle_BeforeItReadsTheRoster()
    {
        var env = ParentWithWorkUnderIt();
        var service = new TurnVerdictService(env);
        await service.StartTurnEnd(Signal(Parent));
        env.Knobs = env.Knobs with { SettleMs = 750 };
        env.Owned = _ => new OwnedSessionCounts(0, 1, 0);
        while (env.Steps.TryDequeue(out _)) { }

        await service.ReReadAboveAStopAsync(Tenant, Child);

        Assert.Equal("settle:750", env.Steps.First());
    }

    [Fact]
    public async Task AParentStillBeingRead_WhenTheLastSessionUnderItStops_IsWaitedFor_AndThenReadAgain()
    {
        // Review of pull request 3501: the parent went to the model with work under it, the last session under it stopped
        // while the model was answering, and the calm answer was stored after the pass had looked.
        var env = ParentWithWorkUnderIt();
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Judge = (_, _) => { asked.TrySetResult(); return answer.Task; };
        var service = new TurnVerdictService(env);

        var turn = service.StartTurnEnd(Signal(Parent));
        await asked.Task;                                      // the parent's reading is now waiting on the model
        env.Owned = _ => new OwnedSessionCounts(0, 1, 0);      // the last session under it stops meanwhile
        var pass = service.ReReadAboveAStopAsync(Tenant, Child);
        answer.SetResult("carrying-on");
        await turn;
        var reread = await pass;

        Assert.Equal(1, reread);
        Assert.Equal(1, env.JudgeCalls);
        var stored = env.Latest(Tenant, Parent)!;
        Assert.Equal("needed-you", stored.Verdict);
        Assert.Equal(CallACodeSteps.AllUnderItStoppedStep, stored.DecidedBy);
    }

    [Fact]
    public async Task TheReRead_IsNotStoodDownByTheJudgeSwitchOrTheCeiling_BecauseItAsksNoJudge()
    {
        // Review of pull request 3501: both ration paid judge calls; a re-read either stood down stayed cyan for good.
        var env = ParentWithWorkUnderIt();
        var service = new TurnVerdictService(env);
        await service.StartTurnEnd(Signal(Parent));
        env.Knobs = env.Knobs with { JudgeEnabled = false, MaxInFlight = 0 };
        env.Owned = _ => new OwnedSessionCounts(0, 1, 0);

        var reread = await service.ReReadAboveAStopAsync(Tenant, Child);

        Assert.Equal(1, reread);
        Assert.Equal(CallACodeSteps.AllUnderItStoppedStep, env.Latest(Tenant, Parent)!.DecidedBy);
    }

    [Fact]
    public void AHandOver_TellsTheReReadWhichSessionMoved()
    {
        // A working session handed to another owner leaves its old owner with nothing under it (review of 3501).
        (TenantId Tenant, string Sid)? moved = null;
        var handOver = new GatewayFleetManagerHandOverEnvironment
        {
            Pushed = null!, StaleAfter = TimeSpan.Zero, Directors = null!, Capabilities = null!, SendCommand = null!,
            Mark = _ => null, AuditLog = null!, Events = () => null, EnterTenantScope = _ => null!,
            OwnerMoved = (tenant, sid) => moved = (tenant, sid),
        };

        handOver.OwnerChanged(Tenant, "dir-1", new SessionDto { SessionId = Child });

        Assert.Equal((Tenant, Child), moved);
    }

    [Fact]
    public void TheTrigger_IsNamedForAPersonInTheStopsView()
    {
        Assert.Equal("A session under it stopped", WingmanStopsFold.TriggerText(TurnVerdictService.UnderItStoppedTriggerWord));
    }
}
