using CcDirector.ControlApi;
using CcDirector.ControlApi.Drain;
using CcDirector.ControlApi.Restart;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.UnitTests.Drain;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Restart;

/// <summary>
/// The restart cycle over the REAL drain - issue #3169. The cycle was wired to a stand-in that refused, so
/// a restart asked for through it never drained anything.
///
/// Nothing here hands the cycle a finished outcome. Every test that reaches a verdict runs the real
/// <see cref="DirectorRestartCycle"/> over the real <see cref="DirectorDrainRestartStep"/> over the real
/// <see cref="DirectorDrain"/>; only the two ends are faked - the live sessions and the Gateway - exactly
/// as the drain's own tests fake them. The last two tests watch the HOST, because a step nobody wires is a
/// step that proves nothing about a restart.
/// </summary>
[Collection(DirectorGatesCollection.Name)]
public sealed class DirectorDrainRestartStepTests
{
    private DateTime _now = new(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StartedLocal = new(2026, 9, 19, 11, 5, 0, DateTimeKind.Local);

    /// <summary>The fake Director, writing what happened to its sessions into a list the Gateway fake
    /// writes into too - so the ORDER across the two is one list read top to bottom.</summary>
    private sealed class RecordingSessionControl : FakeSessionControl
    {
        private readonly List<string> _events;
        private readonly HashSet<string> _reportedGone = new(StringComparer.OrdinalIgnoreCase);
        public RecordingSessionControl(List<string> events) => _events = events;

        public override bool MarkForDeletion(string sessionId, string reason)
        {
            var flagged = base.MarkForDeletion(sessionId, reason);
            if (flagged) _events.Add($"asked to close: {sessionId}");
            return flagged;
        }

        public override bool IsPresent(string sessionId)
        {
            var present = base.IsPresent(sessionId);
            if (!present && Flagged.Contains(sessionId) && _reportedGone.Add(sessionId))
                _events.Add($"gone: {sessionId}");
            return present;
        }
    }

    private DirectorDrain RigDrain(FakeSessionControl sessions, FakeWorkspaceSink sink, Action<DrainProgress> progress)
        => new(sessions, sink, "director-under-test", progress,
            utcNow: () => _now,
            delay: (d, _) => { _now = _now.Add(d); return Task.CompletedTask; });

    private DirectorDrainRestartStep Step(FakeSessionControl sessions, FakeWorkspaceSink sink, string directory)
        => new(progress => RigDrain(sessions, sink, progress), "Test Director", directory, () => StartedLocal);

    private static DirectorRestartCycleOrder Order() => new()
    {
        RequestId = "req-3169",
        Machine = "TEST_MACHINE",
        Reason = "update to 2.9.0",
        RequestedBySessionId = "11111111-2222-3333-4444-555555555555",
        RequestedBySessionName = "Fleet Manager",
    };

    private static DirectorRestartCycle Cycle(IRestartCycleDrain step, DirectorRestartCycleTests.Gateway gateway)
        => new(Order(), step, gateway,
            () => new DirectorRestartEligibilityDto { Eligible = true, Reason = "it is the launcher's Director" },
            @"C:\app\cc-director.exe");

    private static WorkspaceSeat[] ALeadWithAWorkerAndAStandalone() => new[]
    {
        DrainTestRig.Seat("lead", "Restart - Tech Lead"),
        DrainTestRig.Seat("worker", "Restart - Developer", reportsTo: "lead", order: 1),
        DrainTestRig.Seat("solo", "Standalone", order: 2),
    };

    // Shows: with three sessions running, the cycle closes every one of them through the real drain, and
    // only when none is left does it ask the launcher to restart.
    [Fact]
    public async Task RunAsync_SessionsRunning_DrainsEveryOneBeforeTheLauncherIsAsked()
    {
        using var dir = new TempDir();
        var events = new List<string>();
        var sessions = new RecordingSessionControl(events);
        var seats = ALeadWithAWorkerAndAStandalone();
        foreach (var s in seats)
        {
            sessions.Live.Add(s.SessionId!);
            sessions.Handover(dir.Path, s.SessionId!, s.Name, DrainTestRig.Block());
        }
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };
        var gateway = new DirectorRestartCycleTests.Gateway();
        gateway.WhenLauncherAsked = () => events.Add($"launcher asked to restart: {sessions.Live.Count} sessions still running");

        var final = await Cycle(Step(sessions, sink, dir.Path), gateway).RunAsync();

        Assert.Equal(DirectorRestartRequestState.Completed, final);

        // THE ORDER. The launcher is the LAST thing that happened, it happened once, and at that moment
        // nothing was running. Every session was asked to close, and was seen gone, before it.
        Assert.Equal("launcher asked to restart: 0 sessions still running", events[^1]);
        Assert.Equal(1, gateway.LauncherAsks);
        Assert.Single(events, e => e.StartsWith("launcher asked", StringComparison.Ordinal));
        foreach (var id in new[] { "lead", "worker", "solo" })
        {
            Assert.Contains($"asked to close: {id}", events);
            Assert.Contains($"gone: {id}", events);
        }
        // Bottom of the chain first: the drain's own rule, still true when the cycle runs it.
        Assert.True(events.IndexOf("asked to close: worker") < events.IndexOf("asked to close: lead"));

        // The sessions were really asked - the drain's own message, not a flag set by the test.
        Assert.Contains(sessions.Sent, m => m.SessionId == "lead" && m.Text.Contains("START NOTHING NEW"));

        // The record the cycle names is the record the drain stored, and the drain stored it as ready.
        var workspaceId = DrainPaths.WorkspaceIdFor("Test Director", StartedLocal);
        Assert.Equal(workspaceId, sink.CaptureRequest!.Id);
        Assert.True(sink.Last.Integrity!.ReadyToRestart);
        Assert.Equal(workspaceId, gateway.Last.WorkspaceId);
        Assert.Contains(workspaceId, gateway.Last.Progress);
    }

    // Shows: when one session never writes a handover the drain ends not ready, the cycle stops on the
    // drain's own reason and names the record, the launcher is never asked, and that session is left
    // running with nothing forced.
    [Fact]
    public async Task RunAsync_DrainEndsNotReady_StopsTheCycleAndTheLauncherIsNeverAsked()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl();
        var seats = new[]
        {
            DrainTestRig.Seat("done", "Finishes cleanly"),
            DrainTestRig.Seat("silent", "Never answers", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        sessions.Handover(dir.Path, "done", "Finishes cleanly", DrainTestRig.Block());
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };
        var gateway = new DirectorRestartCycleTests.Gateway();

        var final = await Cycle(Step(sessions, sink, dir.Path), gateway).RunAsync();

        Assert.Equal(DirectorRestartRequestState.Abandoned, final);
        Assert.Equal(0, gateway.LauncherAsks);
        Assert.Equal(0, gateway.CapabilityChecks);

        // Nothing was forced: the silent session was never asked to close and is still running.
        Assert.DoesNotContain("silent", sessions.Flagged);
        Assert.Contains("silent", sessions.Live);

        // The drain's own verdict and reason, not one made up by the step.
        Assert.False(sink.Last.Integrity!.ReadyToRestart);
        var reason = sink.Last.Integrity.NotReadyReason;
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Equal(DirectorRestartRequestState.Abandoned, gateway.Last.State);
        Assert.Contains(reason!, gateway.Last.Progress);

        // The record holding the session that DID close is named, so it can be found.
        var workspaceId = DrainPaths.WorkspaceIdFor("Test Director", StartedLocal);
        Assert.Equal(workspaceId, gateway.Last.WorkspaceId);
        Assert.Contains(workspaceId, gateway.Last.Progress);
    }

    // Shows: a Director with no Gateway client (the factory builds no drain) abandons the cycle at the drain
    // step saying so in plain words, and neither the machine check nor the launcher is ever reached.
    [Fact]
    public async Task RunAsync_NoGatewayClient_IsUnavailableAndNothingIsTouched()
    {
        var built = 0;
        var step = new DirectorDrainRestartStep(_ => { built++; return null; }, "Test Director");
        var gateway = new DirectorRestartCycleTests.Gateway();

        var final = await Cycle(step, gateway).RunAsync();

        Assert.Equal(DirectorRestartRequestState.Abandoned, final);
        Assert.Equal(1, built);
        Assert.Contains("not connected to a Gateway", gateway.Last.Progress);
        Assert.Null(gateway.Last.WorkspaceId);
        Assert.Equal(0, gateway.CapabilityChecks);
        Assert.Equal(0, gateway.LauncherAsks);
        Assert.Null(DirectorDrain.Running);
    }

    // Shows: asked directly, the step with no Gateway client answers Unavailable with no record - the
    // verdict the cycle is documented to stop on.
    [Fact]
    public async Task RunAsync_NoGatewayClient_ReturnsTheUnavailableVerdictWithNoRecord()
    {
        var step = new DirectorDrainRestartStep(_ => null, "Test Director");

        var outcome = await step.RunAsync(Order(), _ => { }, CancellationToken.None);

        Assert.Equal(RestartDrainVerdict.Unavailable, outcome.Verdict);
        Assert.Null(outcome.WorkspaceId);
        Assert.Contains("not connected to a Gateway", outcome.Detail);
    }

    // Shows: asking whether the drain is available, with sessions running, says yes and changes nothing -
    // no session is messaged or asked to close, nothing is captured on the Gateway, no drain holds the gate.
    [Fact]
    public void Availability_GatewayClientPresent_IsAvailableAndChangesNothing()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl();
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(DrainTestRig.Seat("solo", "Standalone")) };

        var availability = Step(sessions, sink, dir.Path).Availability;

        Assert.True(availability.Available);
        Assert.Contains("connected to a Gateway", availability.Reason);
        Assert.Empty(sessions.Sent);
        Assert.Empty(sessions.Flagged);
        Assert.Null(sink.CaptureRequest);
        Assert.Empty(sink.Saves);
        Assert.Null(DirectorDrain.Running);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    // Shows: with no Gateway client the step says it is not available, and the reason tells a person why -
    // the record must live off the machine - and what to do about it.
    [Fact]
    public void Availability_NoGatewayClient_IsNotAvailableAndSaysWhyInPlainWords()
    {
        var availability = new DirectorDrainRestartStep(_ => null, "Test Director").Availability;

        Assert.False(availability.Available);
        Assert.Contains("not connected to a Gateway", availability.Reason);
        Assert.Contains("while this machine is down", availability.Reason);
        Assert.Contains("Connect this Director to a Gateway", availability.Reason);
    }

    // Shows: the reason on the accepted order is written into the record on the Gateway and told to the
    // session being asked to stop, and the record says which request and which session asked for it.
    [Fact]
    public async Task RunAsync_OrderReason_IsWrittenIntoTheRecordAndToldToTheSession()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl();
        sessions.Live.Add("solo");
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(DrainTestRig.Seat("solo", "Standalone")) };

        var outcome = await Step(sessions, sink, dir.Path).RunAsync(Order(), _ => { }, CancellationToken.None);

        Assert.Equal(RestartDrainVerdict.Drained, outcome.Verdict);
        Assert.Equal("update to 2.9.0", sink.CaptureRequest!.Reason);
        Assert.Equal("update to 2.9.0", sink.Last.Reason);
        Assert.Equal("11111111-2222-3333-4444-555555555555", sink.CaptureRequest.DrivenBySessionId);
        Assert.Contains("req-3169", sink.CaptureRequest.DrivenByNote);
        Assert.Contains("Fleet Manager", sink.CaptureRequest.DrivenByNote);
        Assert.Contains("update to 2.9.0", sessions.Sent.First(m => m.SessionId == "solo").Text);
    }

    // Shows: while another drain holds this Director, a cycle is stopped with that drain's own sentence, its
    // sessions are never messaged, nothing is captured for it, and the launcher is never asked.
    [Fact]
    public async Task RunAsync_ADrainIsAlreadyRunning_IsBlockedWithThatReasonAndTouchesNothing()
    {
        using var firstDir = new TempDir();
        using var secondDir = new TempDir();
        var release = new TaskCompletionSource();
        var parked = new TaskCompletionSource();

        // The first drain: one session that never answers, parked inside its first wait.
        var firstSessions = new FakeSessionControl();
        firstSessions.Live.Add("first");
        var firstSink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(DrainTestRig.Seat("first", "Held by the first drain")) };
        var first = new DirectorDrain(firstSessions, firstSink, "director-under-test", null,
            utcNow: () => _now,
            delay: async (_, _) => { parked.TrySetResult(); await release.Task.ConfigureAwait(false); _now = _now.AddMinutes(5); });
        var firstRun = first.RunAsync(
            new DrainOptions { WorkspaceId = "the-first-drain", WorkspaceName = "first", HandoverDeadline = TimeSpan.FromMinutes(2) },
            firstDir.Path);
        await parked.Task;

        var sessions = new FakeSessionControl();
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(DrainTestRig.Seat("solo", "Standalone")) };
        var gateway = new DirectorRestartCycleTests.Gateway();

        var final = await Cycle(Step(sessions, sink, secondDir.Path), gateway).RunAsync();

        Assert.Equal(DirectorRestartRequestState.Abandoned, final);
        Assert.Contains("already running", gateway.Last.Progress);
        Assert.Contains("the-first-drain", gateway.Last.Progress);
        Assert.Null(gateway.Last.WorkspaceId);
        Assert.Equal(0, gateway.LauncherAsks);
        Assert.Equal(0, gateway.CapabilityChecks);
        Assert.Empty(sessions.Sent);
        Assert.Null(sink.CaptureRequest);

        release.SetResult();
        await firstRun;
        Assert.Null(DirectorDrain.Running);
    }

    // Shows: the drain reports on every poll, mostly saying the same thing again; the step passes on only
    // the changes, each as one sentence, from the first phase to the last.
    [Fact]
    public async Task RunAsync_Progress_PassesEachChangeOnOnceAsOneSentence()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 6 };
        sessions.Live.Add("solo");
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(DrainTestRig.Seat("solo", "Standalone")) };
        var raw = 0;
        var step = new DirectorDrainRestartStep(
            progress => RigDrain(sessions, sink, p => { raw++; progress(p); }),
            "Test Director", dir.Path, () => StartedLocal);
        var passedOn = new List<string>();

        var outcome = await step.RunAsync(Order(), passedOn.Add, CancellationToken.None);

        Assert.Equal(RestartDrainVerdict.Drained, outcome.Verdict);
        Assert.True(raw > passedOn.Count, $"the drain reported {raw} times and {passedOn.Count} sentences were passed on; nothing was repeated, so this test did not exercise the rule");
        for (var i = 1; i < passedOn.Count; i++)
            Assert.NotEqual(passedOn[i - 1], passedOn[i]);
        Assert.StartsWith("drain capturing:", passedOn[0]);
        Assert.StartsWith("drain finished: 1 of 1 sessions accounted for, 1 closed", passedOn[^1]);
        Assert.All(passedOn, s => Assert.DoesNotContain('\n', s));
    }

    // Shows: the HOST hands the cycle the real step, not a stand-in. Put the stand-in back in
    // ControlApiHost.RestartDrainStep and this goes red, which no test of the step alone can do.
    [Fact]
    public async Task RestartDrainStep_OnTheHost_IsTheRealStepOverTheRealDrain()
    {
        using var sessions = new SessionManager(new AgentOptions());
        using var dir = new TempDir();
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(), instancesDirectory: dir.Path);
        try
        {
            Assert.IsType<DirectorDrainRestartStep>(host.RestartDrainStep());
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    // Shows: a host that was never connected to a Gateway answers the eligibility question, before the owner
    // is shown anything, with "cannot drain" and the plain reason - the answer now comes from whether there
    // is a Gateway client, not from a sentence about the build.
    [Fact]
    public async Task JudgeRestartEligibility_HostWithNoGatewayClient_SaysItCannotDrainAndWhy()
    {
        using var sessions = new SessionManager(new AgentOptions());
        using var dir = new TempDir();
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(), instancesDirectory: dir.Path);
        try
        {
            var answer = host.JudgeRestartEligibility();

            Assert.False(answer.DrainAvailable);
            Assert.Contains("not connected to a Gateway", answer.DrainReason);
            Assert.DoesNotContain("carries no drain", answer.DrainReason);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }
}
