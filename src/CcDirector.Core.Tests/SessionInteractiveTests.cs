using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Behavior tests for the final-build session features:
///   #3 terminal input  -> SendInput forwards raw bytes to the backend (the exact path the
///                          /stream WebSocket now drives).
///   #4 the prompt queue  -> a MANUAL holding list: it never auto-sends, whatever the activity
///                          state. The auto-drain this line used to describe was gated on a
///                          transition to Idle, which no producer has ever emitted, so it never
///                          ran in a real session and is now deleted (issue #1564).
///   #5 PTY resize       -> Resize no-ops on an unchanged size (the repaint-loop guard).
/// </summary>
public sealed class SessionInteractiveTests
{
    private static Session NewSession(RecordingBackend backend, ActivityState initial)
    {
        var s = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: "claude-test",
            activityState: initial,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);
        s.MarkRunning(); // so SendInput / drain aren't short-circuited by Exited/Failed
        return s;
    }

    // ---- #3 terminal input ----

    [Fact]
    public void SendInput_forwards_raw_bytes_to_the_backend()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);

        s.SendInput(new byte[] { 0x1b, (byte)'[', (byte)'A' }); // an Up-arrow escape sequence

        Assert.Single(backend.Writes);
        Assert.Equal(new byte[] { 0x1b, (byte)'[', (byte)'A' }, backend.Writes[0]);
    }

    // ================= the hold state machine =================
    // One field, three states: None / Held / DeferredHold. Design and diagram:
    // docs/new_architecture/sessions.html. These tests walk every cell of the
    // transition table in that document. Credit: the five deferred-hold cases come from pull request
    // #1512 (session "Gateway Cleanup - Tunnel-Only Migration"), re-pointed at the live ActivityState.

    // ---- THE RULE: a held session that starts working comes off hold, every time ----

    [Fact]
    public void ApplyGatewayHold_IsADumbMirror_ThatNoActivityCanChange()
    {
        // THE DIRECTOR'S ENTIRE HOLD CONTRACT, IN ONE TEST: it writes down what the Gateway decided, and
        // then nothing that happens in this process changes it. Every hold rule that used to live here -
        // work lifts it, exit clears it, settle lands a deferral - is now the Gateway's, driven by the
        // facts this session reports upward.
        //
        // The block of tests this replaces (RequestHold_*, RestoreHoldState_*, ~20 of them) tested a state
        // machine that no longer exists on this side. Their subject moved to SnoozeRegistry and
        // SnoozeLandingObserver on the Gateway, and so did they.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);

        s.ApplyGatewayHold(HoldState.Held);
        Assert.True(s.OnHold);

        s.ApplyTerminalActivityState(ActivityState.Working);          // a byte, a repaint, an agent's poke
        Assert.Equal(HoldState.Held, s.HoldState);                    // still exactly what the Gateway said

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);  // the turn ends
        Assert.Equal(HoldState.Held, s.HoldState);

        s.ApplyGatewayHold(HoldState.None);                           // only the Gateway changes it
        Assert.False(s.OnHold);
    }

    [Fact]
    public void ApplyGatewayHold_DeferredHold_IsNotLandedHere_EvenWhenTheWorkEnds()
    {
        // Landing a deferral is a RULING - it starts a twelve-hour clock - so it belongs to the owner of
        // the clock. This session reports that the work ended (ActivityState, which the Gateway reads via
        // SnoozeLandingObserver) and does nothing else about it.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        s.ApplyGatewayHold(HoldState.DeferredHold);
        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(HoldState.DeferredHold, s.HoldState); // untouched; the Gateway lands it
    }

    [Fact]
    public void ExitedSession_DoesNotClearItsOwnHold()
    {
        // Even exit. A dead session must not hide behind a "Snoozed" label - but that rule is enforced by
        // the Gateway, which sees the exit reported on the push seam and drops the hold there.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);

        s.ApplyGatewayHold(HoldState.Held);
        s.ApplyTerminalActivityState(ActivityState.Exited);

        Assert.Equal(HoldState.Held, s.HoldState);
    }

    [Fact]
    public async Task OwnerDrivenSend_StampsLastOwnerTurn()
    {
        // The second and last fact this Director contributes to hold. The Gateway cannot see it: desktop
        // typing never leaves this machine.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);
        Assert.Null(s.LastOwnerTurnAtUtc);

        await s.SendTextAsync("hello", SendSource.UserInput, InputOrigin.DesktopTyped);

        Assert.NotNull(s.LastOwnerTurnAtUtc);
    }

    [Fact]
    public async Task OwnersOwnVoice_ArrivingAsFrameworkTransport_StillStampsAnOwnerTurn()
    {
        // The desktop dictation path sends the owner's transcribed voice tagged Framework: the TRANSPORT
        // is the framework, the ACTOR is the human. The non-null origin is what tells them apart, which is
        // why the origin - not the source - is the primary signal.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);

        await s.SendTextAsync("what is the status", SendSource.Framework, InputOrigin.DesktopVoice);

        Assert.NotNull(s.LastOwnerTurnAtUtc);
    }

    [Fact]
    public async Task AgentAndFrameworkSends_DoNotStampAnOwnerTurn()
    {
        // A fleet message from another agent is real work, but it is not the owner coming back. If this
        // stamped, the Gateway would drop the owner's hold - which is exactly the defect that killed
        // session 8c17dc1c 93 seconds after it was held on 15 July 2026.
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);

        await s.SendTextAsync("a fleet message from another agent", SendSource.Agent);
        await s.SendTextAsync("/handover", SendSource.Framework);

        Assert.Null(s.LastOwnerTurnAtUtc);
    }

    [Fact]
    public void SendInput_StampsAnOwnerTurn_OnlyWithAHumanOrigin()
    {
        var backend = new RecordingBackend();
        using var typed = NewSession(backend, ActivityState.Idle);
        typed.SendInput(new byte[] { (byte)'h', (byte)'i', 0x0D }, InputOrigin.DesktopTyped);
        Assert.NotNull(typed.LastOwnerTurnAtUtc);

        using var agent = NewSession(new RecordingBackend(), ActivityState.Idle);
        agent.SendInput(new byte[] { (byte)'h', (byte)'i', 0x0D }); // no origin: an agent's AppendEnter=false prompt
        Assert.Null(agent.LastOwnerTurnAtUtc);
    }

    [Fact]
    public void SendInput_BareKeystroke_IsNotATurn()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);
        s.SendInput(new byte[] { (byte)'h', (byte)'i' }, InputOrigin.DesktopTyped); // composing - no CR/LF
        Assert.Null(s.LastOwnerTurnAtUtc);
    }

    [Fact]
    public void Resize_changed_size_calls_backend_unchanged_is_noop()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Idle);

        s.Resize(80, 24);
        s.Resize(80, 24); // identical -> guarded, must NOT reach the backend again
        s.Resize(120, 30);

        Assert.Equal(2, backend.Resizes.Count);
        Assert.Equal((80, 24), backend.Resizes[0]);
        Assert.Equal((120, 30), backend.Resizes[1]);
    }

    // ---- the prompt queue is a MANUAL holding list, and always has been ----

    // These replace three tests that certified an auto-drain which has never run in a real session. They
    // passed for fourteen months by calling ApplyTerminalActivityState(ActivityState.Idle) directly - a state
    // NOTHING in the product has ever assigned, so the transition they relied on could not occur outside the
    // test. The old gate is deleted (see Session.SetActivityState), and what is pinned here is the behaviour
    // users actually have and the product actually describes: a queue you send from explicitly.

    /// <summary>
    /// The queue never auto-sends, on ANY activity state. Deliberately a theory over EVERY member of the
    /// enum rather than the one state the detector happens to emit today: that makes the guarantee immune to
    /// the producer changing which state it reports at a turn end - the exact coupling that let the original
    /// drain sit dead behind a state no producer emitted, with a green test in front of it.
    /// </summary>
    [Theory]
    [InlineData(ActivityState.Starting)]
    [InlineData(ActivityState.Idle)]
    [InlineData(ActivityState.Working)]
    [InlineData(ActivityState.WaitingForInput)]
    [InlineData(ActivityState.WaitingForPerm)]
    [InlineData(ActivityState.Exited)]
    public async Task Queue_neverAutoSends_onAnyActivityState(ActivityState state)
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.PromptQueue.Enqueue("queued work");

        s.ApplyTerminalActivityState(state);

        await Task.Delay(250);
        Assert.Empty(backend.SentTexts);          // nothing was fired at the session
        Assert.Equal(1, s.PromptQueue.Count);     // and the item is still there, waiting to be sent
    }

    /// <summary>
    /// The turn end specifically. This is the moment a queued prompt would have gone out, and the moment it
    /// must not: the detector reports WaitingForInput for BOTH "finished cleanly" and "blocked on a question"
    /// and explicitly refuses to tell them apart, so an auto-send here could answer a question the agent
    /// asked with unrelated text - a silent wrong action. Auto-send needs a real turn-end classifier first
    /// (issue #1564).
    /// </summary>
    [Fact]
    public async Task Queue_doesNotAutoSend_atATurnEnd_whichCouldAnswerTheAgentsOwnQuestion()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.PromptQueue.Enqueue("also update the README");

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        await Task.Delay(250);
        Assert.Empty(backend.SentTexts);
        Assert.Equal(1, s.PromptQueue.Count);
    }

    /// <summary>The queue still does what it is for: the user sends an item, explicitly, whenever they want.</summary>
    [Fact]
    public async Task Queue_itemIsSentWhenTheUserSendsIt()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        var item = s.PromptQueue.Enqueue("first");
        s.PromptQueue.Enqueue("second");

        await s.SendTextAsync(item.Text);
        s.PromptQueue.Remove(item.Id);

        Assert.Equal("first", backend.SentTexts[0]);
        Assert.Equal(1, s.PromptQueue.Count);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met within timeout");
    }

    /// <summary>An ISessionBackend that records what the Session sends it.</summary>
    private sealed class RecordingBackend : ISessionBackend
    {
        public List<byte[]> Writes { get; } = new();
        public List<string> SentTexts { get; } = new();
        public List<(short cols, short rows)> Resizes { get; } = new();

        public int ProcessId => 1234;
        public string Status => "Recording";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Writes.Add(data);
        public Task SendTextAsync(string text) { SentTexts.Add(text); return Task.CompletedTask; }
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) => Resizes.Add((cols, rows));
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
