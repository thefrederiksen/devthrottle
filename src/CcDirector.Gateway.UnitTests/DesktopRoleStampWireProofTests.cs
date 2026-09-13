using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Defect 5, proved END TO END through the REAL producer, the REAL wire and the REAL fold - a live Worker
/// reads "supporting" ON THE DESKTOP, and nothing in this file hand-sets the field under test.
///
/// WHY THIS FILE EXISTS BESIDE <see cref="DesktopGatewayFoldAgreementTests"/>, AND WHAT THAT ONE COULD NOT
/// DO. That test is named for this mission's core claim - the desktop and the Gateway agree - and it cannot
/// deliver it. It builds a <see cref="SessionDto"/> by hand, ASSIGNS <c>SessionRole = SessionRoles.Worker</c>
/// onto it, and asserts the fold returns "supporting". That is the injection shape this mission keeps
/// finding: it supplies the exact field that production never populated on the desktop, so it was GREEN
/// while the desktop showed RED, and both were true at once. Its honest ceiling is in its own comment -
/// "the only honest assertion is that one function yields one answer per session" - which proves the fold is
/// a FUNCTION. It says nothing about whether two surfaces call it with the SAME INPUTS, and that was the
/// entire defect.
///
/// It is kept, because it correctly states the answer the fold must give, and its comment names this work:
/// "Phase 2b pushes the role down so the rail reaches this same answer - this test pins what that answer
/// must be." This file is that push, and it pins the rail actually REACHING the answer. Read as a pair:
/// there is the answer the fold must give, and here is the desktop getting to it through the real wire.
///
/// The chain below is every real link, in order, with no shortcut:
///   real <see cref="Session"/> (a red, controlled Worker)
///     -> the real <c>set-resolved-role</c> verb (<see cref="FleetRoleExecutor"/>) - the Gateway stamping
///     -> real <see cref="Session.GatewayResolvedRole"/>
///     -> real <see cref="ControlEndpoints.Map"/> - the SAME mapper that feeds the desktop rail's fold
///        input (SessionViewModel.FoldInput) and the Gateway push
///     -> real <see cref="SessionOrdering.EffectiveColor"/>
///     -> "supporting".
///
/// Design: docs/new_architecture/session-state.html, defect 5.
/// </summary>
public sealed class DesktopRoleStampWireProofTests
{
    /// <summary>A backend that buffers and nothing else - enough for a real Session with no real process.</summary>
    private sealed class BufferBackend : Core.Backends.ISessionBackend
    {
        public int ProcessId => 0;
        public string Status => "Buffer-only";
        public bool IsRunning => true;
        public bool HasExited => false;
        public Core.Memory.CircularTerminalBuffer? Buffer { get; } = new(65536);

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Buffer?.Write(data);
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>A real Session in a real SessionManager, through the real factory - no DTO shortcuts.</summary>
    private static Session NewSession(SessionManager manager)
    {
        var s = manager.CreateEmbeddedSession(Path.GetTempPath(), null, new BufferBackend());
        s.IsBrandNew = false; // otherwise the fold answers "green" (brand-new) before it answers anything else
        return s;
    }

    /// <summary>
    /// Drive the REAL verb the Gateway sends down the tunnel. No field is assigned by the test.
    ///
    /// The supervision answer rides on the same command as the role, because it is the same kind of fact and
    /// has the same reason for travelling: one Director cannot see the session supervising one of its own.
    /// It defaults to FALSE here, which is the Gateway saying "nobody is holding this" - the value that
    /// surfaces the session to the owner, and the right default for a test that has not said otherwise.
    /// </summary>
    private static DirectorCommandResult StampRole(SessionManager manager, Session session, string role,
        bool hasLiveSupervisor = false)
    {
        var command = new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = "set-resolved-role",
            SessionId = session.Id.ToString(),
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new SetResolvedRoleRequest { Role = role, HasLiveSupervisor = hasLiveSupervisor },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
        };
        var context = new SessionCommandContext(manager, "director-under-test", Services: null, SendSource.Framework);
        return FleetRoleExecutor.SetResolvedRole(context, command);
    }

    /// <summary>
    /// THE DEFECT, AND ITS FIX, MEASURED IN ONE TEST. A red Worker with a live controller reads "red" on the
    /// desktop before the Gateway's role reaches it, and "supporting" after - through the real mapper and the
    /// real fold, with the role never assigned by this test.
    ///
    /// The first half is the defect reproduced: this is precisely what the rail rendered - red "Needs you" -
    /// while the phone rendered slate "Sub-agent" for the same session at the same instant, because
    /// ControlEndpoints.Map never carried the role and SessionOrdering's Worker arm could not fire.
    /// </summary>
    [Fact]
    public void ALiveWorker_ReadsRedOnTheDesktopUntilTheGatewayStampsItsRole_ThenSupporting()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var worker = NewSession(manager);

        // A real red turn-end, driven through the real state setter - not a DTO field.
        worker.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        // A real controller link, the raw fact the Director genuinely reports.
        worker.ControllerSessionId = Guid.NewGuid();

        // ===== BEFORE: the desktop's fold input, built by the SAME mapper the rail uses. =====
        var before = ControlEndpoints.Map(worker, directorId: "");
        Assert.True(before.IsControlled);
        Assert.Null(before.SessionRole); // nobody has told this Director anything - the Gateway owns the fact
        Assert.Equal("red", SessionOrdering.EffectiveColor(before));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(before));

        // ===== THE GATEWAY STAMPS. The real verb, over the real command shape. =====
        var result = StampRole(manager, worker, SessionRoles.Worker, hasLiveSupervisor: true);
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);

        // ===== AFTER: the same mapper, the same fold, a different answer - because the fact arrived. =====
        var after = ControlEndpoints.Map(worker, directorId: "");
        Assert.Equal(SessionRoles.Worker, after.SessionRole);
        Assert.True(after.HasLiveSupervisor);
        Assert.Equal("supporting", SessionOrdering.EffectiveColor(after));
        // The label is "Snoozed" (was "Sub-agent") since the owner ruled on 2026-09-02 that a supervised
        // session goes to on-hold when it is not working; the slate dot is unchanged. See the supervised arm
        // in SessionOrdering.EffectiveColor.
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(after));

        // The law: the role changed the ANSWER, not the underlying activity fact. The session is still
        // genuinely at a turn end - ownership travels on the role, and the dot says what it is DOING.
        Assert.Equal("WaitingForInput", after.ActivityState);
    }

    /// <summary>
    /// THE AGREEMENT ASSERTION, which is the mission's actual claim: for the SAME session, the desktop's
    /// answer equals the Gateway's answer. The desktop's input comes from the real mapper; the Gateway's
    /// comes from the real fleet resolver over a real two-session fleet. Neither role is hand-set.
    /// </summary>
    [Fact]
    public void DesktopAndGateway_GiveTheSameAnswer_ForTheSameWorker()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var controller = NewSession(manager);
        var worker = NewSession(manager);
        worker.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        worker.ControllerSessionId = controller.Id;

        // The GATEWAY's answer: its real fleet pass over the real pushed DTOs, then its real fold.
        var fleet = new List<SessionDto>
        {
            ControlEndpoints.Map(controller, directorId: "d1"),
            ControlEndpoints.Map(worker, directorId: "d1"),
        };
        CcDirector.Gateway.Fleet.FleetRoleResolver.Stamp(fleet);
        var gatewayWorker = fleet.Single(s => s.SessionId == worker.Id.ToString());
        var gatewayAnswer = SessionOrdering.EffectiveColor(gatewayWorker);
        Assert.Equal("supporting", gatewayAnswer);

        // The Gateway stamps what it resolved back down - the real verb again.
        Assert.Equal(DirectorCommandStatus.Ok,
            StampRole(manager, worker, gatewayWorker.SessionRole!, gatewayWorker.HasLiveSupervisor).Status);

        // The DESKTOP's answer: the real mapper, the real fold. THIS is the assertion defect 5 was blocking.
        var desktopAnswer = SessionOrdering.EffectiveColor(ControlEndpoints.Map(worker, directorId: ""));
        Assert.Equal(gatewayAnswer, desktopAnswer);
    }

    /// <summary>
    /// The escape hatch still works, end to end: when the controller DIES the fleet resolver stops calling
    /// the session a Worker, the Gateway stamps the new role down, and the red surfaces on the desktop
    /// again. This is the rule that stops the suppression hiding a genuinely stuck sub-agent, and it must
    /// survive the round trip - not just the fold.
    /// </summary>
    [Fact]
    public void WhenTheControllerDies_TheStampChanges_AndTheWorkersRedSurfacesOnTheDesktop()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var controller = NewSession(manager);
        var worker = NewSession(manager);
        worker.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        worker.ControllerSessionId = controller.Id;

        // The controller dies. Its own real state change - nothing hand-set.
        controller.ApplyTerminalActivityState(ActivityState.Exited);

        var fleet = new List<SessionDto>
        {
            ControlEndpoints.Map(controller, directorId: "d1"),
            ControlEndpoints.Map(worker, directorId: "d1"),
        };
        CcDirector.Gateway.Fleet.FleetRoleResolver.Stamp(fleet);
        var gatewayWorker = fleet.Single(s => s.SessionId == worker.Id.ToString());

        // No live controller -> not a Worker -> the suppression cannot fire.
        Assert.Equal(SessionRoles.Standalone, gatewayWorker.SessionRole);
        Assert.Equal("red", SessionOrdering.EffectiveColor(gatewayWorker));

        Assert.False(gatewayWorker.HasLiveSupervisor);
        Assert.Equal(DirectorCommandStatus.Ok,
            StampRole(manager, worker, gatewayWorker.SessionRole!, gatewayWorker.HasLiveSupervisor).Status);
        Assert.Equal("red", SessionOrdering.EffectiveColor(ControlEndpoints.Map(worker, directorId: "")));
    }

    /// <summary>
    /// A blank role CLEARS the stamp back to "no answer". The Gateway must be able to retract a role it can
    /// no longer resolve, and the Director must report null rather than the last thing it was told - a stale
    /// role outliving its truth is how this defect class starts.
    /// </summary>
    [Fact]
    public void ABlankRole_ClearsTheStamp_SoTheDirectorReportsNoAnswer()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var worker = NewSession(manager);
        worker.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        worker.ControllerSessionId = Guid.NewGuid();

        Assert.Equal(DirectorCommandStatus.Ok,
            StampRole(manager, worker, SessionRoles.Worker, hasLiveSupervisor: true).Status);
        Assert.Equal("supporting", SessionOrdering.EffectiveColor(ControlEndpoints.Map(worker, directorId: "")));

        // Retracting the role retracts the supervision answer with it. A cleared seat that left "somebody is
        // holding this" behind would be the stale-fact-outliving-its-truth defect wearing a different field.
        Assert.Equal(DirectorCommandStatus.Ok, StampRole(manager, worker, "").Status);
        Assert.Null(ControlEndpoints.Map(worker, directorId: "").SessionRole);
        Assert.False(ControlEndpoints.Map(worker, directorId: "").HasLiveSupervisor);
        Assert.Equal("red", SessionOrdering.EffectiveColor(ControlEndpoints.Map(worker, directorId: "")));
    }

    /// <summary>The Director never invents a role: an unknown session is a NotFound, not a guess.</summary>
    [Fact]
    public void StampingAnUnknownSession_IsNotFound()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        using var other = new SessionManager(new Core.Configuration.AgentOptions());
        var orphan = NewSession(other); // a real session, but not in the manager the verb addresses
        Assert.Equal(DirectorCommandStatus.NotFound, StampRole(manager, orphan, SessionRoles.Worker).Status);
    }
}
