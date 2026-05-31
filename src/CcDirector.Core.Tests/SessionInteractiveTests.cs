using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Behavior tests for the final-build session features:
///   #3 terminal input  -> SendInput forwards raw bytes to the backend (the exact path the
///                          /stream WebSocket now drives).
///   #4 queue auto-drain -> the next queued prompt is sent when the session goes Idle, gated
///                          by OnHold and never on WaitingForInput.
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

    // ---- #5 PTY resize guard ----

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

    // ---- #4 queue auto-drain ----

    [Fact]
    public async Task Queue_auto_drains_one_item_when_session_goes_idle()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.PromptQueue.Enqueue("first");
        s.PromptQueue.Enqueue("second");

        s.ApplyTerminalActivityState(ActivityState.Idle); // transition triggers the drain

        await WaitUntil(() => backend.SentTexts.Count >= 1);
        Assert.Equal("first", backend.SentTexts[0]); // FIFO
        Assert.Equal(1, s.PromptQueue.Count);          // exactly one drained per Idle
    }

    [Fact]
    public async Task Queue_does_not_drain_when_on_hold()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.OnHold = true;
        s.PromptQueue.Enqueue("held");

        s.ApplyTerminalActivityState(ActivityState.Idle);

        await Task.Delay(250);
        Assert.Empty(backend.SentTexts);
        Assert.Equal(1, s.PromptQueue.Count);
    }

    [Fact]
    public async Task Queue_does_not_drain_on_waiting_for_input()
    {
        var backend = new RecordingBackend();
        using var s = NewSession(backend, ActivityState.Working);
        s.PromptQueue.Enqueue("answer?");

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput); // Claude is asking - do NOT auto-answer

        await Task.Delay(250);
        Assert.Empty(backend.SentTexts);
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
