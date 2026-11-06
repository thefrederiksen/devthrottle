using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// THE WAKE (Factory Inbox mission, phase 2; MISSION section 7) over a throwaway database and a fake Gateway around it:
/// no real clock, no sleep, no port. Each test moves the fake clock and calls a look, exactly as the scheduled look and
/// the heartbeat do, and reads what was started and what was rung from the fake.
///
/// Proven here: three messages in one second start exactly one Secretary, and only once the gather window has closed; a
/// running Secretary is rung, once, and no second one starts; a ring is never asked while the session works and is never
/// counted when the Director refuses it for work or the owner's draft; a live Boss is rung and never doubled, a Boss's
/// child is not a Boss, and with none alive ONE Boss starts with the seat kit; a night-start Boss rung mid-run is rung
/// once, when it waits for a prompt; a Gateway restarted inside the window starts exactly one Secretary after it, and one
/// restarted during a start starts none; two writers cannot both take a start's claim; with no Director on the Boss's
/// computer nothing starts, the thread says why, and the start goes on when one connects there and never on another
/// computer; a failed start is said and tried again; a Secretary that ended holding messages is replaced; the Secretary's
/// start carries the phase 1 mark.
/// </summary>
public sealed class FactoryInboxWakeTests : IDisposable
{
    private const string TheFactory = DefinitionFixture.FactoryId;
    private const string BossMachine = "SOREN_NORTH";
    private const string BossDirector = "d-north";
    private const string UndefinedFolder = "D:/factories/devthrottle";
    private static readonly TenantId A = new("acct-a");
    private static readonly DateTime T0 = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly FactoryInboxStore _store;
    private readonly FactoryInboxWakeStore _wakes;
    private readonly FakeEnvironment _env = new();

    public FactoryInboxWakeTests()
    {
        _db = _h.Open();
        _store = new FactoryInboxStore(_db, _ => DictationDictionary.Empty);
        _wakes = new FactoryInboxWakeStore(_db);
        _env.Now = T0;
    }

    public void Dispose() => _h.Dispose();

    private FactoryInboxWake NewWake() => new(_wakes, _env, CancellationToken.None);

    private Guid Send(DateTime at, string text = "Add an export button.")
        => _store.SendAsync(A, new FactoryInboxSendInput(TheFactory, text, FactoryInboxWords.Notes.None,
            FactoryInboxWords.Sources.Typed, FactoryInboxWords.Surfaces.Phone, "owner-phone", at)).GetAwaiter().GetResult().Id;

    private Task<FactoryInboxWakeLook> LookAt(FactoryInboxWake wake, DateTime now)
    {
        _env.Now = now;
        return wake.LookAsync(A, TheFactory, CancellationToken.None);
    }

    private static (string DirectorId, SessionDto Session) Seat(string sessionId, string seat, string state = "WaitingForInput",
        string kit = FactoryKits.Seat)
        => (BossDirector, new SessionDto
        {
            SessionId = sessionId, Factory = TheFactory, FactorySeat = seat, FactoryKit = kit, FactoryDefinitionVersion = 3,
            ActivityState = state,
        });

    /// <summary>A Secretary files and hands everything it holds, as the routes let it.</summary>
    private void SecretaryHands(string secretary, DateTime at)
    {
        _store.Receive(A, TheFactory, secretary, null, [], [], [], at);
        _store.HandBundle(A, TheFactory, secretary, null, at.AddSeconds(1));
    }

    // ---------- the gather window and the one Secretary ----------

    [Fact]
    public async Task ThreeMessagesInOneSecond_StartExactlyOneSecretary_OnlyOnceTheWindowHasClosed()
    {
        Send(T0);
        Send(T0.AddMilliseconds(300));
        Send(T0.AddMilliseconds(900));
        var wake = NewWake();

        var early = await LookAt(wake, T0.AddSeconds(30));
        Assert.Equal(FactoryInboxWakeAct.WindowOpen, early.Secretary);
        Assert.Equal(T0.Add(FactoryInboxWake.Window), early.LookAgainAtUtc);
        Assert.Empty(_env.Starts);

        var closed = await LookAt(wake, T0.AddSeconds(60));
        Assert.Equal(FactoryInboxWakeAct.Started, closed.Secretary);
        // The heartbeat and the other two messages' looks come after it: still one Secretary.
        await LookAt(wake, T0.AddSeconds(61));
        await LookAt(wake, T0.AddSeconds(75));

        var start = Assert.Single(_env.Starts);
        Assert.Equal(BossMachine, start.Machine);
        Assert.Contains("3 messages wait to be filed now.", start.Request.PrePrompt);
    }

    [Fact]
    public async Task TheSecretarysStart_CarriesThePhaseOneMark_OnTheBossesComputer_WithItsOpening()
    {
        Send(T0);

        await LookAt(NewWake(), T0.AddSeconds(60));

        var (machine, req) = Assert.Single(_env.Starts);
        Assert.Equal(BossMachine, machine);
        // The mark the inbox routes and the start-door limit key on: the factory, the seat secretary, a seat kit.
        Assert.Equal(TheFactory, req.Factory);
        Assert.Equal(FactoryInboxSeats.Secretary, req.FactorySeat);
        Assert.Equal(FactoryKits.Seat, req.FactoryKit);
        Assert.Equal(3, req.FactoryDefinitionVersion);
        // No repository: it works in its kit's home. The Boss's agent and model.
        Assert.True(req.FactoryHome);
        Assert.Equal("", req.RepoPath);
        Assert.Equal("ClaudeCode", req.Agent);
        Assert.Equal("claude-opus-5-5", req.Model);
        // Pinned to the Director found running on that computer, an automated start that closes itself.
        Assert.Equal(BossDirector, req.Director);
        Assert.Equal(SessionOriginKinds.Schedule, req.Origin);
        Assert.Equal(SessionOriginSurfaces.Trigger, req.OriginSurface);
        Assert.False(req.AutoDismiss);
        Assert.Null(req.ParentSessionId);
        Assert.Equal("DevThrottle - Secretary - file the factory inbox", req.Name);
        Assert.Contains("cc-devthrottle factory inbox new", req.PrePrompt);
        Assert.Contains("cc-devthrottle factory inbox bundle --hand", req.PrePrompt);
        Assert.Contains("cc-devthrottle session done", req.PrePrompt);
        Assert.Contains("cc-devthrottle factory memory set", req.PrePrompt);
        Assert.Contains(FactorySeatOpening.OwnerWordIsFinal, req.PrePrompt);
        // A Secretary is not a Boss.
        Assert.DoesNotContain(FactorySeatOpening.BossNeverDoesTheWork, req.PrePrompt);
    }

    [Fact]
    public async Task ARunningSecretary_IsRungOnce_AndNoSecondOneStarts()
    {
        Send(T0);
        var wake = NewWake();
        await LookAt(wake, T0.AddSeconds(60));
        Assert.Single(_env.Starts);
        var secretary = FakeEnvironment.FirstSessionId;
        _env.RosterRows.Add(Seat(secretary, FactoryInboxSeats.Secretary));
        // It files the first message; a second arrives while it is still open.
        _store.Receive(A, TheFactory, secretary, null, [], [], [], T0.AddSeconds(90));
        Send(T0.AddSeconds(120), "And a second thing.");

        var inWindow = await LookAt(wake, T0.AddSeconds(150));
        Assert.Equal(FactoryInboxWakeAct.WindowOpen, inWindow.Secretary);
        Assert.Empty(_env.Rings);

        var rung = await LookAt(wake, T0.AddSeconds(180));
        Assert.Equal(FactoryInboxWakeAct.Rung, rung.Secretary);
        var ring = Assert.Single(_env.Rings);
        Assert.Equal(secretary, ring.SessionId);
        Assert.Equal("1 new factory inbox message - run cc-devthrottle factory inbox new", ring.Text);

        // Told once: the heartbeat does not ring it again for the same message, and nothing new starts.
        Assert.Equal(FactoryInboxWakeAct.AlreadyTold, (await LookAt(wake, T0.AddSeconds(195))).Secretary);
        Assert.Single(_env.Rings);
        Assert.Single(_env.Starts);
    }

    [Fact]
    public async Task ARing_IsNeverAskedWhileTheSessionWorks_AndARefusalForWorkOrTheOwnersDraftIsNotCounted()
    {
        Send(T0);
        _env.RosterRows.Add(Seat("sec-1", FactoryInboxSeats.Secretary, state: "Working"));
        var wake = NewWake();

        // The roster says it works: the Director is not even asked.
        var working = await LookAt(wake, T0.AddSeconds(60));
        Assert.Equal(FactoryInboxWakeAct.RingDeferred, working.Secretary);
        Assert.Empty(_env.Rings);
        Assert.Empty(_env.Starts);

        // The roster says it waits, but at the moment of typing the Director finds it working, then the owner's draft.
        _env.RosterRows[0] = Seat("sec-1", FactoryInboxSeats.Secretary, state: "WaitingForInput");
        _env.RingAnswers.Enqueue(FleetManagerPromptSend.Busy);
        _env.RingAnswers.Enqueue(FleetManagerPromptSend.OwnerDraft);
        Assert.Equal(FactoryInboxWakeAct.RingDeferred, (await LookAt(wake, T0.AddSeconds(75))).Secretary);
        Assert.Equal(FactoryInboxWakeAct.RingDeferred, (await LookAt(wake, T0.AddSeconds(90))).Secretary);

        // Typed at last, once; after that it is told.
        Assert.Equal(FactoryInboxWakeAct.Rung, (await LookAt(wake, T0.AddSeconds(105))).Secretary);
        Assert.Equal(FactoryInboxWakeAct.AlreadyTold, (await LookAt(wake, T0.AddSeconds(120))).Secretary);
        Assert.Equal(3, _env.Rings.Count);
        Assert.Equal(1, _env.AcceptedRings);
        Assert.Empty(_env.Starts);
    }

    [Fact]
    public async Task ASecretaryThatEndedHoldingMessages_IsReplacedByOne()
    {
        Send(T0);
        var wake = NewWake();
        await LookAt(wake, T0.AddSeconds(60));
        var first = FakeEnvironment.FirstSessionId;
        _store.Receive(A, TheFactory, first, null, [], [], [], T0.AddSeconds(70));
        // It is killed mid-claim: the roster shows it exited.
        _env.RosterRows.Add(Seat(first, FactoryInboxSeats.Secretary, state: "Exited"));
        _env.Ended.Add(first);

        var look = await LookAt(wake, T0.AddSeconds(120));

        Assert.Equal(FactoryInboxWakeAct.Started, look.Secretary);
        Assert.Equal(2, _env.Starts.Count);
    }

    // ---------- the Boss ----------

    [Fact]
    public async Task ALiveBoss_IsRung_NeverDoubled()
    {
        Send(T0);
        SecretaryHands("sec-1", T0.AddSeconds(70));
        _env.RosterRows.Add(Seat("sec-1", FactoryInboxSeats.Secretary));
        _env.RosterRows.Add(Seat("boss-1", "boss", state: "Idle"));
        var wake = NewWake();

        var look = await LookAt(wake, T0.AddSeconds(72));

        Assert.Equal(FactoryInboxWakeAct.Rung, look.Boss);
        var ring = Assert.Single(_env.Rings);
        Assert.Equal("boss-1", ring.SessionId);
        Assert.Equal("1 new factory inbox bundle from your Secretary - run cc-devthrottle factory inbox bundle", ring.Text);
        Assert.Equal(FactoryInboxWakeAct.AlreadyTold, (await LookAt(wake, T0.AddSeconds(90))).Boss);
        Assert.Empty(_env.Starts);
    }

    [Fact]
    public async Task ABossTheOwnersAgentStartedThroughTheBossStartDoor_WithNoRecord_IsNotTheInboxsBoss_SoOneSeatKitBossStartsBesideIt()
    {
        // A start-door Boss the Gateway holds NO record for (started before issue #4598, or a child) carries the
        // rules-only kit and is still not the inbox's Boss. With a record it is: see the test below.
        Send(T0);
        SecretaryHands("sec-1", T0.AddSeconds(70));
        _env.RosterRows.Add(Seat("door-boss", "boss", kit: FactoryKits.RulesOnly));
        var wake = NewWake();

        var look = await LookAt(wake, T0.AddSeconds(72));
        await LookAt(wake, T0.AddSeconds(73));

        Assert.Equal(FactoryInboxWakeAct.Started, look.Boss);
        var (_, req) = Assert.Single(_env.Starts);
        Assert.Equal("boss", req.FactorySeat);
        Assert.Equal(FactoryKits.Seat, req.FactoryKit);
        // Nothing is typed into the owner's conversation with it.
        Assert.DoesNotContain(_env.Rings, r => r.SessionId == "door-boss");
    }

    [Fact]
    public async Task ABossTheGatewayRecordedAsStartedByASessionInNoFactory_IsTheInboxsBoss_AndIsRungNotDoubled()
    {
        // The owner's ruling of 6 November 2026 ("Yes, I also want to allow those."), which overturned the Tech Lead's
        // ruling of 5 November 2026 for this case: the Boss the owner talks to is the Boss the wake rings.
        Send(T0);
        SecretaryHands("sec-1", T0.AddSeconds(70));
        _env.RosterRows.Add(Seat("door-boss", "boss", kit: FactoryKits.RulesOnly));
        _env.RecordedBosses.Add("door-boss");
        var wake = NewWake();

        var look = await LookAt(wake, T0.AddSeconds(72));
        await LookAt(wake, T0.AddSeconds(73));

        Assert.NotEqual(FactoryInboxWakeAct.Started, look.Boss);
        Assert.DoesNotContain(_env.Starts, s => s.Request.FactorySeat == "boss");
        Assert.Contains(_env.Rings, r => r.SessionId == "door-boss");
    }

    [Fact]
    public async Task AnUnrecordedBossChildWithTheBossesRulesOnlyKit_IsNotTheInboxsBoss()
    {
        // A Boss's child carries the same seat and kit and has no record: it is never rung as the Boss.
        Send(T0);
        SecretaryHands("sec-1", T0.AddSeconds(70));
        _env.RosterRows.Add(Seat("boss-child", "boss", kit: FactoryKits.RulesOnly));
        var wake = NewWake();

        var look = await LookAt(wake, T0.AddSeconds(72));

        Assert.Equal(FactoryInboxWakeAct.Started, look.Boss);
        Assert.DoesNotContain(_env.Rings, r => r.SessionId == "boss-child");
    }

    [Fact]
    public async Task ARungSecretary_IsToldOnlyTheMessagesItWasNotToldOf()
    {
        Send(T0);
        _env.RosterRows.Add(Seat("sec-1", FactoryInboxSeats.Secretary));
        var wake = NewWake();
        // Told of the first by a ring once its window closed; a second arrives after.
        await LookAt(wake, T0.AddSeconds(60));
        Send(T0.AddSeconds(90));

        await LookAt(wake, T0.AddSeconds(150));

        Assert.Equal(2, _env.Rings.Count);
        Assert.Equal(FactoryInboxSecretary.RingLine(1), _env.Rings[1].Text);
    }

    [Fact]
    public async Task ABossesChild_IsNotABoss_SoOneBossStarts_WithTheSeatKitNotTheRulesOnlyKit()
    {
        Send(T0);
        SecretaryHands("sec-1", T0.AddSeconds(70));
        _env.RosterRows.Add(Seat("sec-1", FactoryInboxSeats.Secretary));
        _env.RosterRows.Add(Seat("child-1", "boss", kit: FactoryKits.RulesOnly));
        var wake = NewWake();

        var look = await LookAt(wake, T0.AddSeconds(72));
        await LookAt(wake, T0.AddSeconds(73));

        Assert.Equal(FactoryInboxWakeAct.Started, look.Boss);
        var (machine, req) = Assert.Single(_env.Starts);
        Assert.Equal(BossMachine, machine);
        // As itself: the night-start seat kit - the brief, memory and skills - never the Boss start door's rules-only kit.
        Assert.Equal("boss", req.FactorySeat);
        Assert.Equal(FactoryKits.Seat, req.FactoryKit);
        Assert.Equal(3, req.FactoryDefinitionVersion);
        Assert.Equal(BossDirector, req.Director);
        Assert.Contains("You are the boss of DevThrottle", req.PrePrompt);
        Assert.Contains("Run the factory.", req.PrePrompt);
        Assert.Contains("cc-devthrottle factory inbox bundle", req.PrePrompt);
        Assert.Contains(FactorySeatOpening.BossNeverDoesTheWork, req.PrePrompt);
        Assert.Contains(FactorySeatOpening.OwnerWordIsFinal, req.PrePrompt);
        Assert.Equal("DevThrottle - Boss - decide the factory inbox", req.Name);
    }

    /// <summary>
    /// THE OPEN ITEM (MISSION section 10): a night-start Boss rung mid-run. It is a Boss seat with a seat kit, so it is the
    /// live Boss and none is started. While it works nothing is asked; when the roster is a moment behind and the Director
    /// finds it working, nothing is typed and nothing is counted; when its turn ends and it waits for a prompt the ring
    /// lands, once. If instead it closes itself at the end of its run, ONE Boss starts for the bundle.
    /// </summary>
    [Fact]
    public async Task NightStartBoss_RungMidRun_RingWaitsUntilIdleAndLandsOnce()
    {
        Send(T0);
        SecretaryHands("sec-1", T0.AddSeconds(70));
        _env.RosterRows.Add(Seat("sec-1", FactoryInboxSeats.Secretary));
        _env.RosterRows.Add(Seat("night-boss", "boss", state: "Working"));
        var wake = NewWake();

        Assert.Equal(FactoryInboxWakeAct.RingDeferred, (await LookAt(wake, T0.AddSeconds(72))).Boss);
        Assert.Empty(_env.Rings);

        _env.RosterRows[1] = Seat("night-boss", "boss", state: "WaitingForInput");
        _env.RingAnswers.Enqueue(FleetManagerPromptSend.Busy);
        Assert.Equal(FactoryInboxWakeAct.RingDeferred, (await LookAt(wake, T0.AddSeconds(87))).Boss);

        Assert.Equal(FactoryInboxWakeAct.Rung, (await LookAt(wake, T0.AddSeconds(102))).Boss);
        Assert.Equal(FactoryInboxWakeAct.AlreadyTold, (await LookAt(wake, T0.AddSeconds(117))).Boss);
        Assert.Equal(1, _env.AcceptedRings);
        Assert.Empty(_env.Starts);
    }

    [Fact]
    public async Task NightStartBoss_ThatClosesItselfBeforeReadingTheBundle_IsReplacedByOneBoss()
    {
        Send(T0);
        SecretaryHands("sec-1", T0.AddSeconds(70));
        _env.RosterRows.Add(Seat("sec-1", FactoryInboxSeats.Secretary));
        _env.RosterRows.Add(Seat("night-boss", "boss", state: "Working"));
        var wake = NewWake();
        await LookAt(wake, T0.AddSeconds(72));

        // Its run ends and it closes itself: exited, the bundle still unread.
        _env.RosterRows[1] = Seat("night-boss", "boss", state: "Exited");
        var look = await LookAt(wake, T0.AddSeconds(90));
        await LookAt(wake, T0.AddSeconds(105));

        Assert.Equal(FactoryInboxWakeAct.Started, look.Boss);
        Assert.Single(_env.Starts);
        Assert.Empty(_env.Rings);
    }

    // ---------- a restart, and two writers ----------

    [Fact]
    public async Task AGatewayRestartedInsideTheWindow_StartsExactlyOneSecretaryAfterIt()
    {
        Send(T0);
        Send(T0.AddMilliseconds(500));
        var before = NewWake();
        Assert.Equal(FactoryInboxWakeAct.WindowOpen, (await LookAt(before, T0.AddSeconds(20))).Secretary);

        // The Gateway restarts: a new wake with nothing in memory, over the same database. Its looks - the heartbeat
        // after the restart - start exactly one Secretary once the window closes, and the old process's scheduled look,
        // had it run, starts none.
        var after = NewWake();
        Assert.Equal(FactoryInboxWakeAct.WindowOpen, (await LookAt(after, T0.AddSeconds(45))).Secretary);
        Assert.Equal(FactoryInboxWakeAct.Started, (await LookAt(after, T0.AddSeconds(61))).Secretary);
        Assert.Equal(FactoryInboxWakeAct.Starting, (await LookAt(before, T0.AddSeconds(61))).Secretary);
        Assert.Equal(FactoryInboxWakeAct.Starting, (await LookAt(after, T0.AddSeconds(76))).Secretary);

        Assert.Single(_env.Starts);
    }

    [Fact]
    public async Task AGatewayRestartedDuringAStart_StartsNone_WhileItsClaimIsHeld()
    {
        Send(T0);
        var startGate = new TaskCompletionSource<TriggerStartAttempt>();
        _env.StartAnswer = _ => startGate.Task;
        var first = NewWake();
        var inFlight = LookAt(first, T0.AddSeconds(60));

        // A second Gateway process looks while the first one's start is still out.
        var second = NewWake();
        _env.StartAnswer = _ => Task.FromResult(TriggerStartAttempt.Started("should-not-start"));
        Assert.Equal(FactoryInboxWakeAct.Starting, (await second.LookAsync(A, TheFactory, CancellationToken.None)).Secretary);

        startGate.SetResult(TriggerStartAttempt.Started("sec-1"));
        Assert.Equal(FactoryInboxWakeAct.Started, (await inFlight).Secretary);
        Assert.Single(_env.Starts);
    }

    [Fact]
    public void TwoWritersOnOneReadVersion_OnlyOneTakesTheClaim()
    {
        var row = _wakes.Row(A, TheFactory, T0);

        var first = _wakes.TryUpdate(A, TheFactory, row.Version, w => w.SecretaryStartedAtUtc = T0, T0);
        var second = _wakes.TryUpdate(A, TheFactory, row.Version, w => w.SecretaryStartedAtUtc = T0.AddSeconds(1), T0.AddSeconds(1));

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(T0, _wakes.Find(A, TheFactory)!.SecretaryStartedAtUtc);
    }

    [Fact]
    public async Task AStartWhoseOutcomeIsUnknown_HoldsItsClaimForTheGrace_ThenLapses()
    {
        Send(T0);
        _env.StartAnswer = _ => Task.FromResult(TriggerStartAttempt.Unknown("the tunnel dropped"));
        var wake = NewWake();

        Assert.Equal(FactoryInboxWakeAct.StartOutcomeUnknown, (await LookAt(wake, T0.AddSeconds(60))).Secretary);
        Assert.Equal(FactoryInboxWakeAct.Starting, (await LookAt(wake, T0.AddMinutes(3))).Secretary);
        Assert.Single(_env.Starts);

        _env.StartAnswer = _ => Task.FromResult(TriggerStartAttempt.Started("sec-2"));
        Assert.Equal(FactoryInboxWakeAct.Started, (await LookAt(wake, T0.AddSeconds(60).Add(FactoryInboxWake.StartGrace))).Secretary);
        Assert.Equal(2, _env.Starts.Count);
    }

    // ---------- no computer, no definition, a failed start ----------

    [Fact]
    public async Task ADirectorOnTheBossesComputerThatCannotBuildAKit_StartsNothing_AndSaysToUpdateIt()
    {
        Send(T0);
        _env.CannotBuildKit.Add(BossMachine);
        var wake = NewWake();

        var waiting = await LookAt(wake, T0.AddSeconds(60));

        Assert.Equal(FactoryInboxWakeAct.Waiting, waiting.Secretary);
        Assert.Empty(_env.Starts);
        var row = _wakes.Find(A, TheFactory)!;
        Assert.Equal(FactoryInboxWords.Waiting.DirectorCannotBuildKit, row.SecretaryWaiting);
        Assert.Equal("Waiting - the Director on the Boss's computer, SOREN_NORTH, cannot build a seat kit; it goes on when that Director is updated",
            FactoryInboxFold.WaitingOf(row)!.Secretary);

        // Updated, it builds kits: the start goes on, there.
        _env.CannotBuildKit.Clear();
        Assert.Equal(FactoryInboxWakeAct.Started, (await LookAt(wake, T0.AddSeconds(75))).Secretary);
        Assert.Null(_wakes.Find(A, TheFactory)!.SecretaryWaiting);
    }

    [Fact]
    public async Task NoDirectorOnTheBossesComputer_StartsNothing_SaysWhy_AndGoesOnWhenOneConnectsThere()
    {
        Send(T0);
        _env.DirectorsOnMachine.Clear();
        // A Director on ANOTHER computer is never used.
        _env.DirectorsOnMachine["SOREN_SOUTH"] = "d-south";
        var wake = NewWake();

        var waiting = await LookAt(wake, T0.AddSeconds(60));

        Assert.Equal(FactoryInboxWakeAct.Waiting, waiting.Secretary);
        Assert.Empty(_env.Starts);
        var row = _wakes.Find(A, TheFactory)!;
        Assert.Equal(FactoryInboxWords.Waiting.NoDirector, row.SecretaryWaiting);
        var label = FactoryInboxFold.WaitingOf(row)!.Secretary;
        Assert.Equal("Waiting - no Director is running on the Boss's computer, SOREN_NORTH; it goes on when one connects there", label);
        // The thread prints it for the message that waits.
        var message = _store.Thread(A, TheFactory, 10).Messages[0];
        Assert.Equal(label, FactoryInboxFold.Message(message, null, null, FactoryInboxFold.WaitingOf(row)).StateLabel);

        _env.DirectorsOnMachine[BossMachine] = BossDirector;
        var resumed = await LookAt(wake, T0.AddSeconds(75));

        Assert.Equal(FactoryInboxWakeAct.Started, resumed.Secretary);
        var (machine, req) = Assert.Single(_env.Starts);
        Assert.Equal(BossMachine, machine);
        Assert.Equal(BossDirector, req.Director);
        Assert.Null(_wakes.Find(A, TheFactory)!.SecretaryWaiting);
    }

    [Fact]
    public async Task AnUndefinedFactory_StartsItsSecretaryInItsFolder_OnTheBossSeatsComputer_AndRecordsIt()
    {
        Send(T0);
        _env.PublishedOf = () => null;
        var wake = NewWake();

        var look = await LookAt(wake, T0.AddSeconds(60));

        Assert.Equal(FactoryInboxWakeAct.Started, look.Secretary);
        var (machine, req) = Assert.Single(_env.Starts);
        Assert.Equal(BossMachine, machine);
        // No definition, so no kit and no seat: a session in the factory, in its folder, as its talk starts.
        Assert.Equal(UndefinedFolder, req.RepoPath);
        Assert.Equal(TheFactory, req.Factory);
        Assert.Null(req.FactorySeat);
        Assert.Null(req.FactoryKit);
        Assert.Null(req.FactoryDefinitionVersion);
        Assert.Equal(BossDirector, req.Director);
        Assert.False(_env.LastAskedForAKit);
        Assert.Contains("cc-devthrottle factory inbox new", req.PrePrompt);
        Assert.Contains(FactorySeatOpening.OwnerWordIsFinal, req.PrePrompt);
        // The wake's row is what records it as the Secretary.
        Assert.Equal(FakeEnvironment.FirstSessionId, _wakes.Find(A, TheFactory)!.SecretarySessionId);
        // Not reported yet, it is starting: no second one.
        Assert.Equal(FactoryInboxWakeAct.Starting, (await LookAt(wake, T0.AddSeconds(75))).Secretary);
        Assert.Single(_env.Starts);
    }

    [Fact]
    public async Task AnUndefinedFactorysBoss_IsStartedInItsFolder_WithItsPublishedBrief_AndRecorded()
    {
        Send(T0);
        _env.PublishedOf = () => null;
        _env.Brief = new FactoryGivenBrief(2, "Run the invoices.", FactoryBriefVersionKinds.Brief);
        SecretaryHands("sec-1", T0.AddSeconds(70));
        var wake = NewWake();

        var look = await LookAt(wake, T0.AddSeconds(72));

        Assert.Equal(FactoryInboxWakeAct.Started, look.Boss);
        var (machine, req) = Assert.Single(_env.Starts);
        Assert.Equal(BossMachine, machine);
        Assert.Equal(UndefinedFolder, req.RepoPath);
        Assert.Null(req.FactorySeat);
        Assert.Contains("Run the invoices.", req.PrePrompt);
        Assert.Contains("cc-devthrottle factory inbox bundle", req.PrePrompt);
        Assert.Contains(FactorySeatOpening.BossNeverDoesTheWork, req.PrePrompt);
        Assert.EndsWith(FactorySeatOpening.OwnerWordIsFinal, req.PrePrompt);
        Assert.Equal(FakeEnvironment.FirstSessionId, _wakes.Find(A, TheFactory)!.BossSessionId);
    }

    [Fact]
    public async Task AnUndefinedFactorysBossWhoseBriefIsAFile_IsToldItsPath()
    {
        Send(T0);
        _env.PublishedOf = () => null;
        SecretaryHands("sec-1", T0.AddSeconds(70));

        await LookAt(NewWake(), T0.AddSeconds(72));

        var (_, req) = Assert.Single(_env.Starts);
        Assert.Contains($"Read your brief first: {UndefinedFolder}/agents/boss.yaml", req.PrePrompt);
    }

    [Fact]
    public async Task AnUndefinedFactoryWithNoFolder_WaitsAndSaysWhy()
    {
        Send(T0);
        _env.PublishedOf = () => null;
        _env.Registered.Folder = "";

        var look = await LookAt(NewWake(), T0.AddSeconds(60));

        Assert.Equal(FactoryInboxWakeAct.Waiting, look.Secretary);
        Assert.Empty(_env.Starts);
        Assert.Equal("Waiting - the Secretary cannot be started: it is registered with no folder",
            FactoryInboxFold.WaitingOf(_wakes.Find(A, TheFactory))!.Secretary);
    }

    [Fact]
    public async Task AFailedStart_ReleasesTheClaim_SaysWhy_AndIsTriedAgain()
    {
        Send(T0);
        _env.StartAnswer = _ => Task.FromResult(TriggerStartAttempt.Failed("the Director refused: no such repository"));
        var wake = NewWake();

        Assert.Equal(FactoryInboxWakeAct.StartFailed, (await LookAt(wake, T0.AddSeconds(60))).Secretary);
        var row = _wakes.Find(A, TheFactory)!;
        Assert.Null(row.SecretaryStartedAtUtc);
        Assert.Equal("Waiting - the Secretary did not start: the Director refused: no such repository; it is tried again",
            FactoryInboxFold.WaitingOf(row)!.Secretary);

        _env.StartAnswer = _ => Task.FromResult(TriggerStartAttempt.Started("sec-2"));
        Assert.Equal(FactoryInboxWakeAct.Started, (await LookAt(wake, T0.AddSeconds(75))).Secretary);
        Assert.Equal(2, _env.Starts.Count);
        Assert.Null(_wakes.Find(A, TheFactory)!.SecretaryWaiting);
    }

    [Fact]
    public async Task AnArchivedFactory_WakesNobody()
    {
        Send(T0);
        _env.Registered.ArchivedAtUtc = T0;

        var look = await LookAt(NewWake(), T0.AddSeconds(60));

        Assert.Equal(FactoryInboxWakeAct.Nothing, look.Secretary);
        Assert.Empty(_env.Starts);
    }

    // ---------- the scheduled look and the heartbeat ----------

    [Fact]
    public async Task MessageSent_LooksWhenTheWindowCloses_FromTheSentTime()
    {
        Send(T0);
        _env.Now = T0.AddSeconds(10);
        var wake = NewWake();

        var scheduled = wake.MessageSent(A, TheFactory, T0);
        Assert.Equal(TimeSpan.FromSeconds(50), Assert.Single(_env.Delays));
        Assert.False(scheduled.IsCompleted);

        _env.Now = T0.AddSeconds(60);
        _env.ReleaseDelays();
        var look = await scheduled;

        Assert.Equal(FactoryInboxWakeAct.Started, look.Secretary);
        Assert.Single(_env.Starts);
    }

    [Fact]
    public async Task TheHeartbeat_LooksAtEveryFactoryWithSomethingWaiting()
    {
        Send(T0);
        _env.Now = T0.AddSeconds(60);

        await NewWake().SweepAsync(A, CancellationToken.None);

        Assert.Single(_env.Starts);
        Assert.Equal(new[] { TheFactory }, _wakes.FactoriesWithWork(A));
        Assert.Equal(1, _env.FactoryAgentsAsked);
    }

    [Fact]
    public async Task TheHeartbeat_OverAnEmptyInbox_NeverAsksTheFactoryAgentsSwitch()
    {
        // Production cost review (6 November 2026): every 15 seconds, for every account with a Director bound. An empty
        // inbox costs its two indexed reads and no plan read.
        _env.Now = T0.AddSeconds(60);

        await NewWake().SweepAsync(A, CancellationToken.None);

        Assert.Equal(0, _env.FactoryAgentsAsked);
        Assert.Empty(_env.Starts);
    }

    [Fact]
    public async Task TheHeartbeat_OverInboxWork_WithTheFactoryAgentsAreaOff_AsksOnce_AndWakesNobody()
    {
        Send(T0);
        _env.Now = T0.AddSeconds(60);
        _env.FactoryAgentsAreOn = false;

        await NewWake().SweepAsync(A, CancellationToken.None);

        Assert.Equal(1, _env.FactoryAgentsAsked);
        Assert.Empty(_env.Starts);
        Assert.Null(_wakes.Find(A, TheFactory));
    }

    [Fact]
    public async Task BundleHanded_ReturnsBeforeTheLookReadsAnything_AndTheLookStartsTheBossOffTheCallersThread()
    {
        // Production cost review (6 November 2026): the hand route calls this inside its request. The look's first read
        // is held shut, so a look run on the caller's thread would hold this call too.
        Send(T0);
        SecretaryHands("sec-1", T0.AddSeconds(70));
        _env.Now = T0.AddSeconds(72);
        using var hold = new ManualResetEventSlim(false);
        _env.HoldTheFactoryRead = hold;

        var look = NewWake().BundleHanded(A, TheFactory);

        Assert.False(look.IsCompleted);
        Assert.Empty(_env.Starts);
        hold.Set();
        var done = await look;
        Assert.Equal(FactoryInboxWakeAct.Started, done.Boss);
        Assert.Equal("boss", Assert.Single(_env.Starts).Request.FactorySeat);
    }

    // ---------- the fake Gateway around the wake ----------

    private sealed class FakeEnvironment : IFactoryInboxWakeEnvironment
    {
        public const string FirstSessionId = "sid-1";
        private int _started;
        private readonly List<TaskCompletionSource> _delays = new();

        public DateTime Now;
        public RegisteredFactoryDto Registered = new()
        {
            Factory = TheFactory,
            Title = "DevThrottle",
            Folder = UndefinedFolder,
            BossSeat = "boss",
            Seats = { new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Role = "Boss", Computer = BossMachine, BriefFile = "agents/boss.yaml" } },
        };
        public Func<PublishedFactoryDefinition?> PublishedOf = () =>
            new PublishedFactoryDefinition(3, FactoryDefinitionCheck.Read(DefinitionFixture.Publishable()).Definition!);
        public readonly List<(string DirectorId, SessionDto Session)> RosterRows = new();
        public readonly HashSet<string> Ended = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> DirectorsOnMachine = new(StringComparer.OrdinalIgnoreCase) { [BossMachine] = BossDirector };
        public readonly HashSet<string> CannotBuildKit = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Machine, NewSessionRequest Request)> Starts = new();
        public Func<NewSessionRequest, Task<TriggerStartAttempt>>? StartAnswer;
        public readonly List<(string DirectorId, string SessionId, string Text)> Rings = new();
        public readonly Queue<FleetManagerPromptSend> RingAnswers = new();
        public int AcceptedRings;
        public readonly List<TimeSpan> Delays = new();
        public FactoryGivenBrief? Brief;
        public bool LastAskedForAKit;
        public bool FactoryAgentsAreOn = true;
        public int FactoryAgentsAsked;
        /// <summary>When set, a look's first read waits until it is opened - how a test sees on which thread a look runs.
        /// The bound is reached only when the look holds the test's own thread, so the test fails instead of hanging.</summary>
        public ManualResetEventSlim? HoldTheFactoryRead;

        public RegisteredFactoryDto? Factory(TenantId tenant, string factory)
        {
            if (HoldTheFactoryRead is { } hold && !hold.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("the look's read was never opened: the look ran on the caller's thread");
            return string.Equals(factory, Registered.Factory, StringComparison.OrdinalIgnoreCase) ? Registered : null;
        }

        public bool FactoryAgentsOn(TenantId tenant)
        {
            FactoryAgentsAsked++;
            return FactoryAgentsAreOn;
        }

        public PublishedFactoryDefinition? Published(TenantId tenant, string factory) => PublishedOf();

        public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant) => RosterRows.ToList();

        public (string DirectorId, SessionDto Session)? LastKnown(TenantId tenant, string sessionId)
        {
            foreach (var row in RosterRows)
                if (row.Session.SessionId == sessionId) return row;
            return null;
        }

        public bool HasEnded(TenantId tenant, string sessionId) => Ended.Contains(sessionId);

        /// <summary>Sessions the Gateway recorded as started as the Boss by a session in no factory (issue #4598).</summary>
        public HashSet<string> RecordedBosses { get; } = new(StringComparer.Ordinal);

        public bool StartedAsTheBoss(TenantId tenant, string sessionId, string factory, string seat)
            => RecordedBosses.Contains(sessionId) && seat == "boss";

        public FactoryGivenBrief? PublishedBrief(TenantId tenant, string factory, string seat) => Brief;

        public string? DirectorOn(TenantId tenant, string machine, bool buildsKit)
        {
            LastAskedForAKit = buildsKit;
            if (buildsKit && CannotBuildKit.Contains(machine)) return null;
            return DirectorsOnMachine.TryGetValue(machine, out var d) ? d : null;
        }

        public Task<TriggerStartAttempt> StartAsync(TenantId tenant, string machine, NewSessionRequest request, CancellationToken ct)
        {
            Starts.Add((machine, request));
            _started++;
            return StartAnswer?.Invoke(request) ?? Task.FromResult(TriggerStartAttempt.Started($"sid-{_started}"));
        }

        public Task<FleetManagerPromptSend> RingAsync(TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct)
        {
            Rings.Add((directorId, sessionId, text));
            var answer = RingAnswers.Count > 0 ? RingAnswers.Dequeue() : FleetManagerPromptSend.Accepted;
            if (answer == FleetManagerPromptSend.Accepted) AcceptedRings++;
            return Task.FromResult(answer);
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Delays.Add(delay);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _delays.Add(gate);
            return gate.Task;
        }

        public void ReleaseDelays()
        {
            foreach (var gate in _delays) gate.TrySetResult();
            _delays.Clear();
        }

        public DateTime NowUtc() => Now;
    }
}
