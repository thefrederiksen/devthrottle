using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Supervision;
using Xunit;

namespace CcDirector.Gateway.Tests;

// ============================================================================
// Issue #915: the session supervisor. Overnight on 2026-07-21 a session printed
// "API Error: Unable to connect to API (ENOTFOUND)" at 06:56 and sat dead until 09:32 - two hours
// thirty-six minutes lost to a name-resolution blip that would have cleared itself in seconds.
//
// These tests walk the acceptance list on the issue, in order, with no real clock: the engine's only
// wait is ISupervisorEnvironment.DelayAsync, so a two-hour escalation ladder is provable in
// milliseconds AND the delay it chose for each attempt is an assertion rather than a stopwatch.
//
// The invariant under test throughout: NON-INTERRUPTIVE BY CONSTRUCTION. A clean turn end, a working
// session, and a menu on the screen must all come back with zero keystrokes sent.
// ============================================================================
public sealed class SessionSupervisorTests
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Director = "dir-1";
    private const string Session = "sid-1";

    /// <summary>The composer box and mode footer every agent screen ends with.</summary>
    private static readonly string[] Composer =
    {
        "╭────────────────────────────────╮",
        "│ >                              │",
        "╰────────────────────────────────╯",
        "  ? for shortcuts",
    };

    private static string[] TransientFaultScreen() => new[]
    {
        "* Running gh pr checks...",
        "API Error: Unable to connect to API (ENOTFOUND)",
    }.Concat(Composer).ToArray();

    private static string[] HealthyScreen() => new[]
    {
        "I have pushed the branch and opened the pull request. Anything else?",
    }.Concat(Composer).ToArray();

    private static string[] OutOfAllowanceScreen() => new[]
    {
        "Claude usage limit reached. Your limit will reset at 5pm.",
    }.Concat(Composer).ToArray();

    private static string[] UnknownFaultScreen() => new[]
    {
        "API Error: the upstream widget refused to reticulate",
    }.Concat(Composer).ToArray();

    /// <summary>
    /// The supervisor's whole world, faked and recording. No clock, no tunnel, no model - so every test
    /// asserts on what the engine DECIDED rather than on what a real Director happened to do.
    /// </summary>
    private sealed class FakeEnvironment : ISupervisorEnvironment
    {
        public SupervisorSettings Knobs = SupervisorSettings.Defaults;
        public string[]? Screen;
        public string? ActivityState = "WaitingForInput";
        public bool MenuOnScreen;
        public bool SendSucceeds = true;
        public string? ModelReply;
        public int ModelCalls;

        public readonly List<TimeSpan> Waits = new();
        public readonly List<string> ContinuesSent = new();
        public readonly List<SupervisorRecord> Records = new();
        public readonly List<SupervisorRecord> Escalations = new();

        /// <summary>Runs at the start of each wait, so a test can make the world change mid-ladder exactly
        /// as it does in production (the session starts working, the screen changes, the session exits).</summary>
        public Action<int>? OnWait;

        public SupervisorSettings Settings(TenantId tenant) => Knobs;

        public Task<IReadOnlyList<string>?> ReadScreenRowsAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>?>(Screen);

        public string? ReadActivityState(TenantId tenant, string sessionId) => ActivityState;

        public Task<bool> IsMenuOnScreenAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct)
            => Task.FromResult(MenuOnScreen);

        public Task<bool> SendContinueAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct)
        {
            ContinuesSent.Add(sessionId);
            return Task.FromResult(SendSucceeds);
        }

        public Task<string?> AskModelVerdictAsync(TenantId tenant, IReadOnlyList<string> rows, CancellationToken ct)
        {
            ModelCalls++;
            return Task.FromResult(ModelReply);
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Waits.Add(delay);
            OnWait?.Invoke(Waits.Count);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public void Record(SupervisorRecord record) => Records.Add(record);

        public Task EscalateAsync(SupervisorRecord record, CancellationToken ct)
        {
            Records.Add(record);
            Escalations.Add(record);
            return Task.CompletedTask;
        }

        public IEnumerable<SupervisorRecord> OfType(string eventType) => Records.Where(r => r.EventType == eventType);
    }

    // ---- acceptance 1: a transient connect failure recovers with no human input ---------------------------

    [Fact]
    public async Task ATransientConnectFailure_WaitsTheShortDelay_ThenSendsContinue()
    {
        var env = new FakeEnvironment
        {
            Screen = TransientFaultScreen(),
            Knobs = SupervisorSettings.Defaults with { MaxLongRetries = 0 },
        };
        using var supervisor = new SessionSupervisor(env);

        // The first send happens, then the ceiling of zero long retries ends the ladder - so this test reads
        // the FIRST attempt precisely without a second one muddying it.
        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Single(env.ContinuesSent);
        Assert.Equal(TimeSpan.FromSeconds(SupervisorSettings.DefaultFirstRetrySeconds), Assert.Single(env.Waits));
        var detected = Assert.Single(env.OfType(ActivityEventTypes.SupervisorFaultDetected));
        Assert.Equal(ActivityCauses.TransientTransport, detected.Cause);
        Assert.Contains("enotfound", detected.Detail);
    }

    // ---- acceptance 2: the error persisting escalates to the 15-minute cadence, and keeps going -----------

    [Fact]
    public async Task WhenTheFaultPersists_TheSecondAttemptOnwardsWaitsFifteenMinutes()
    {
        var env = new FakeEnvironment
        {
            Screen = TransientFaultScreen(),
            Knobs = SupervisorSettings.Defaults with { MaxLongRetries = 3 },
        };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        // Four sends: the short one, then three on the long cadence. Then the ceiling.
        Assert.Equal(4, env.ContinuesSent.Count);
        Assert.Equal(
            new[]
            {
                TimeSpan.FromSeconds(45),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(15),
            },
            env.Waits);
    }

    // ---- acceptance 3: a non-transient stop is never auto-continued ---------------------------------------

    [Fact]
    public async Task RunningOutOfAllowance_SendsNothing_AndEscalates()
    {
        var env = new FakeEnvironment { Screen = OutOfAllowanceScreen() };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Empty(env.ContinuesSent);
        Assert.Empty(env.Waits);
        var escalation = Assert.Single(env.Escalations);
        Assert.Equal(ActivityCauses.NonRecoverable, escalation.Cause);
    }

    [Fact]
    public async Task AFullContextWindow_SendsNothing_BecauseADeafSessionWouldSwallowIt()
    {
        var env = new FakeEnvironment
        {
            Screen = new[] { "Prompt is too long. Try /compact." }.Concat(Composer).ToArray(),
        };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Empty(env.ContinuesSent);
        Assert.Equal(ActivityCauses.ContextFull, Assert.Single(env.Escalations).Cause);
    }

    // ---- acceptance 4: a clean turn end and a working session are left alone ------------------------------

    [Fact]
    public async Task ACleanlyFinishedSession_IsLeftAlone_AndCostsOneScreenRead()
    {
        var env = new FakeEnvironment { Screen = HealthyScreen() };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Empty(env.ContinuesSent);
        Assert.Empty(env.Waits);
        Assert.Empty(env.Records);          // nothing happened, so nothing is claimed to have happened
        Assert.Equal(0, env.ModelCalls);    // and no model was asked about a healthy turn
    }

    [Fact]
    public async Task ASessionThatStartedWorkingDuringTheWait_IsNeverSentAnything()
    {
        // Gate 3: the activity state is re-read immediately before every send. This is the hand-rolled
        // watcher's failure mode - it interrupted a healthy mission a dozen times in one night - made
        // unreachable rather than merely discouraged.
        var env = new FakeEnvironment { Screen = TransientFaultScreen() };
        env.OnWait = _ => env.ActivityState = "Working";
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Empty(env.ContinuesSent);
        var recovered = Assert.Single(env.OfType(ActivityEventTypes.SupervisorRecovered));
        Assert.Equal(ActivityCauses.WorkingObservation, recovered.Cause);
        Assert.Empty(env.Escalations);
    }

    [Fact]
    public async Task ASessionSittingOnAPermissionPrompt_IsNeverSteamRolled()
    {
        var env = new FakeEnvironment { Screen = TransientFaultScreen() };
        env.OnWait = _ => env.ActivityState = "WaitingForPerm";
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Empty(env.ContinuesSent);
        Assert.Single(env.OfType(ActivityEventTypes.SupervisorStoodDown));
    }

    [Fact]
    public async Task AMenuOwningTheScreen_IsNeverAnswered_ItEscalates()
    {
        // Gate 4: "continue" typed at a menu picks an option. Refuse and raise a hand instead.
        var env = new FakeEnvironment { Screen = TransientFaultScreen(), MenuOnScreen = true };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Empty(env.ContinuesSent);
        Assert.Equal(ActivityCauses.MenuOwnsScreen, Assert.Single(env.Escalations).Cause);
    }

    [Fact]
    public async Task AnUnreadableScreen_ProducesNoVerdictAndNoAction()
    {
        var env = new FakeEnvironment { Screen = null };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Empty(env.ContinuesSent);
        Assert.Empty(env.Records);
        Assert.Empty(env.Escalations);
    }

    // ---- acceptance 5: the model fallback, on and off -----------------------------------------------------

    [Fact]
    public async Task AnUnrecognizedFault_WithTheModelFallbackOn_ActsOnTheVerdict()
    {
        var env = new FakeEnvironment
        {
            Screen = UnknownFaultScreen(),
            ModelReply = SupervisorVerdict.TransientRecoverable,
            Knobs = SupervisorSettings.Defaults with { MaxLongRetries = 0 },
        };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Equal(1, env.ModelCalls);
        Assert.Single(env.ContinuesSent);
        Assert.Contains("model verdict", Assert.Single(env.OfType(ActivityEventTypes.SupervisorFaultDetected)).Detail);
    }

    [Fact]
    public async Task AnUnrecognizedFault_WithTheModelFallbackOff_EscalatesAsUnknown()
    {
        var env = new FakeEnvironment
        {
            Screen = UnknownFaultScreen(),
            Knobs = SupervisorSettings.Defaults with { ModelFallbackEnabled = false },
        };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Equal(0, env.ModelCalls);
        Assert.Empty(env.ContinuesSent);
        Assert.Equal(ActivityCauses.UnclassifiedFault, Assert.Single(env.Escalations).Cause);
    }

    [Fact]
    public async Task AModelThatSaysTheSessionNeedsAHuman_IsNeverContinued()
    {
        var env = new FakeEnvironment { Screen = UnknownFaultScreen(), ModelReply = SupervisorVerdict.NeedsHuman };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Empty(env.ContinuesSent);
        Assert.Equal(ActivityCauses.NonRecoverable, Assert.Single(env.Escalations).Cause);
    }

    [Fact]
    public async Task AModelThatMumbles_EscalatesRatherThanTyping()
    {
        // An unparsable answer is not a verdict. A model that mumbles must never be read as permission to
        // type into somebody's session.
        var env = new FakeEnvironment { Screen = UnknownFaultScreen(), ModelReply = "well, it might be either really" };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Empty(env.ContinuesSent);
        Assert.Equal(ActivityCauses.UnclassifiedFault, Assert.Single(env.Escalations).Cause);
    }

    [Fact]
    public async Task AModelThatSaysTheTurnFinishedNormally_StopsQuietly()
    {
        var env = new FakeEnvironment { Screen = UnknownFaultScreen(), ModelReply = SupervisorVerdict.HealthyDone };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Empty(env.ContinuesSent);
        Assert.Empty(env.Escalations);
    }

    // ---- acceptance 6: the ceiling raises a hand instead of retrying forever ------------------------------

    [Fact]
    public async Task TheRetryCeiling_EscalatesInsteadOfLoopingForever()
    {
        var env = new FakeEnvironment
        {
            Screen = TransientFaultScreen(),
            Knobs = SupervisorSettings.Defaults with { MaxLongRetries = 2 },
        };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Equal(3, env.ContinuesSent.Count);       // the short attempt plus two long ones
        var escalation = Assert.Single(env.Escalations);
        Assert.Equal(ActivityCauses.RetryCeiling, escalation.Cause);
    }

    [Fact]
    public async Task TheCeilingBelongsToTheEpisode_NotToOneIdleTransition()
    {
        // THE GUARDRAIL AGAINST THE INFINITE BLIND LOOP. In a real outage a "continue" does produce a brief
        // Working flicker before failing again, so a per-transition counter would reset forever and the
        // ceiling would never fire. The count belongs to the fault episode: two idle transitions in one
        // episode may not send more than the ceiling allows between them.
        var env = new FakeEnvironment
        {
            Screen = TransientFaultScreen(),
            Knobs = SupervisorSettings.Defaults with { MaxLongRetries = 1 },
        };
        using var supervisor = new SessionSupervisor(env);

        // First idle transition: one send lands, the session flickers Working (a turn that starts and dies on
        // the same broken network), and this pass ends as recovered.
        await RunOnePassInterruptedByTheSessionWorking(supervisor, env);
        Assert.Single(env.ContinuesSent);
        Assert.Single(env.OfType(ActivityEventTypes.SupervisorRecovered));
        Assert.Equal(1, supervisor.EpisodeAttempts(Tenant, Session));

        // Second idle transition on the SAME fault: it RESUMES the count, so exactly one more send is left
        // before the ceiling. Without episode continuity this pass would start from one and send forever.
        env.OnWait = null;
        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Equal(2, env.ContinuesSent.Count);       // 1 + MaxLongRetries, across both transitions
        Assert.Equal(ActivityCauses.RetryCeiling, Assert.Single(env.Escalations).Cause);
    }

    [Fact]
    public async Task AHealthyTurnEnd_EndsTheEpisode_SoTheNextFaultStartsFromTheShortWait()
    {
        var env = new FakeEnvironment
        {
            Screen = TransientFaultScreen(),
            Knobs = SupervisorSettings.Defaults with { MaxLongRetries = 1 },
        };
        using var supervisor = new SessionSupervisor(env);

        await RunOnePassInterruptedByTheSessionWorking(supervisor, env);
        Assert.Equal(1, supervisor.EpisodeAttempts(Tenant, Session));

        // The session recovers for real and finishes a turn cleanly - the only evidence that closes an episode.
        env.OnWait = null;
        env.Screen = HealthyScreen();
        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);
        Assert.Equal(0, supervisor.EpisodeAttempts(Tenant, Session));

        // A LATER, unrelated blip therefore gets the short wait again, not the long cadence.
        env.Screen = TransientFaultScreen();
        env.Waits.Clear();
        await RunOnePassInterruptedByTheSessionWorking(supervisor, env);
        Assert.Equal(TimeSpan.FromSeconds(SupervisorSettings.DefaultFirstRetrySeconds), env.Waits[0]);
    }

    // ---- acceptance 7: everything the supervisor does is in the recovery log ------------------------------

    [Fact]
    public async Task EveryDecision_LandsInTheRecoveryLog_WithClassAttemptDelayAndResult()
    {
        var env = new FakeEnvironment
        {
            Screen = TransientFaultScreen(),
            Knobs = SupervisorSettings.Defaults with { MaxLongRetries = 1 },
        };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        // detected -> waiting -> sent -> waiting -> sent -> escalated: nothing happens invisibly.
        Assert.Equal(
            new[]
            {
                ActivityEventTypes.SupervisorFaultDetected,
                ActivityEventTypes.SupervisorWaiting,
                ActivityEventTypes.SupervisorContinueSent,
                ActivityEventTypes.SupervisorWaiting,
                ActivityEventTypes.SupervisorContinueSent,
                ActivityEventTypes.SupervisorEscalated,
            },
            env.Records.Select(r => r.EventType));

        var waits = env.OfType(ActivityEventTypes.SupervisorWaiting).ToList();
        Assert.Contains("attempt 1", waits[0].Detail);
        Assert.Contains("45 seconds", waits[0].Detail);
        Assert.Contains("attempt 2", waits[1].Detail);
        Assert.Contains("15 minutes", waits[1].Detail);

        var sends = env.OfType(ActivityEventTypes.SupervisorContinueSent).ToList();
        Assert.Contains("delivered", sends[0].Detail);
        Assert.All(env.Records, r => Assert.Equal(Session, r.SessionId));
        Assert.All(env.Records, r => Assert.Equal(Director, r.DirectorId));
    }

    [Fact]
    public async Task ASendThatDoesNotLand_IsRecordedHonestly()
    {
        var env = new FakeEnvironment
        {
            Screen = TransientFaultScreen(),
            SendSucceeds = false,
            Knobs = SupervisorSettings.Defaults with { MaxLongRetries = 0 },
        };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Contains("did not land",
            Assert.Single(env.OfType(ActivityEventTypes.SupervisorContinueSent)).Detail);
    }

    [Fact]
    public async Task ASessionThatDisappearedDuringTheWait_IsNotChased()
    {
        var env = new FakeEnvironment { Screen = TransientFaultScreen() };
        env.OnWait = _ => env.ActivityState = null;
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Empty(env.ContinuesSent);
        // It stood down - it did not raise a hand, because a session that is gone needs nobody's attention.
        Assert.Equal(ActivityCauses.SessionNotLive,
            Assert.Single(env.OfType(ActivityEventTypes.SupervisorStoodDown)).Cause);
        Assert.Empty(env.Escalations);
    }

    // ---- the master switch --------------------------------------------------------------------------------

    [Fact]
    public async Task WithTheSupervisorSwitchedOff_NothingIsEvenRead()
    {
        var env = new FakeEnvironment
        {
            Screen = TransientFaultScreen(),
            Knobs = SupervisorSettings.Defaults with { Enabled = false },
        };
        using var supervisor = new SessionSupervisor(env);

        await supervisor.SuperviseAsync(Tenant, Director, Session, CancellationToken.None);

        Assert.Empty(env.ContinuesSent);
        Assert.Empty(env.Records);
    }

    [Fact]
    public void TheShippedDefaults_AreTheOnesTheOwnerAskedFor()
    {
        var defaults = SupervisorSettings.Defaults;
        Assert.True(defaults.Enabled);                                  // default ON - the product decision
        Assert.Equal(TimeSpan.FromSeconds(45), defaults.FirstRetry);
        Assert.Equal(TimeSpan.FromMinutes(15), defaults.RetryCadence);
        Assert.Equal(8, defaults.MaxLongRetries);                       // roughly two hours, then a hand up
        Assert.True(defaults.ModelFallbackEnabled);
    }

    // ---- the live entry point: the Working -> idle event -------------------------------------------------

    [Fact]
    public async Task TheWorkingTransition_CancelsAWaitInFlight_AndEndsTheEpisodeAsRecovered()
    {
        // The real public path: a turn-end signal starts the ladder on a background task, and the Working
        // transition (which the turn-end watcher also fires) cancels it. This is the second, independent
        // guarantee that a working session is never sent anything.
        var gate = new SemaphoreSlim(0, 1);
        var env = new FakeEnvironment { Screen = TransientFaultScreen() };
        using var supervisor = new SessionSupervisor(env);
        env.OnWait = _ =>
        {
            // The session came back on its own while we were waiting.
            supervisor.OnSessionWorking(Tenant, Session);
            gate.Release();
        };

        supervisor.OnTurnEnd(new TurnEndSignal(Session, Director, Tenant, DateTime.UtcNow, IsNewTurn: true));

        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)), "the supervisor never reached its first wait");
        await WaitUntil(() => env.OfType(ActivityEventTypes.SupervisorRecovered).Any());
        Assert.Empty(env.ContinuesSent);
    }

    [Fact]
    public async Task ASecondTurnEndWhileAnEpisodeIsLive_DoesNotStackASecondLadder()
    {
        var reached = new SemaphoreSlim(0, 1);
        var release = new SemaphoreSlim(0, 1);
        var env = new FakeEnvironment
        {
            Screen = TransientFaultScreen(),
            Knobs = SupervisorSettings.Defaults with { MaxLongRetries = 0 },
        };
        env.OnWait = _ =>
        {
            reached.Release();
            release.Wait(TimeSpan.FromSeconds(5));
        };
        using var supervisor = new SessionSupervisor(env);

        supervisor.OnTurnEnd(new TurnEndSignal(Session, Director, Tenant, DateTime.UtcNow, IsNewTurn: true));
        Assert.True(await reached.WaitAsync(TimeSpan.FromSeconds(5)), "the first ladder never started");

        // A second signal for the same session arrives while the first ladder is mid-wait.
        supervisor.OnTurnEnd(new TurnEndSignal(Session, Director, Tenant, DateTime.UtcNow, IsNewTurn: true));
        await Task.Delay(50);

        // Still exactly one ladder: one detection, one wait.
        Assert.Single(env.OfType(ActivityEventTypes.SupervisorFaultDetected));
        Assert.Single(env.Waits);
        release.Release();
        supervisor.OnSessionWorking(Tenant, Session);
    }

    /// <summary>
    /// One pass of the ladder that sends its first "continue" and is then interrupted by the session going
    /// Working - the ordinary production sequence when a continue reaches a still-broken network. The
    /// interruption arrives the way it really does: as a cancellation of the wait.
    /// </summary>
    private static async Task RunOnePassInterruptedByTheSessionWorking(SessionSupervisor supervisor, FakeEnvironment env)
    {
        using var pass = new CancellationTokenSource();
        env.OnWait = waitNumber =>
        {
            // Wait 1 elapses and the continue is sent; on wait 2 the session is Working, which cancels.
            if (waitNumber >= 2) pass.Cancel();
        };
        await supervisor.SuperviseAsync(Tenant, Director, Session, pass.Token);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "the supervisor never reached the expected state");
    }
}
