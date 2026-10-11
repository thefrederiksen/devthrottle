using System.Diagnostics;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE SEND PATH WAITS ON THE SESSION'S CLOCK. Every allowance between typing a text and calling it delivered - the
/// echo deadline, the paste taken in, the arrival window on the agent's records, the composer release watch, the late
/// arrival watch and the notices that say what is being waited for - runs on <see cref="Session.Clock"/>. A test
/// hands the session and its terminal a fake clock and the minutes those allowances add up to are advanced, not
/// waited out: the scripted-terminal classes in this folder went from two minutes of real time to about one second.
/// </summary>
public sealed class SendPathClockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-send-clock-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _cleanup = new();
    private readonly List<string> _lines = new();
    private readonly string _tag = "SendClock-" + Guid.NewGuid().ToString("N")[..8];

    public SendPathClockTests()
    {
        Directory.CreateDirectory(_dir);
        SendWaitNotice.LineObserver += Observe;
    }

    public void Dispose()
    {
        SendWaitNotice.LineObserver -= Observe;
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private void Observe(string line) { lock (_lines) if (line.Contains(_tag, StringComparison.Ordinal)) _lines.Add(line); }

    [Fact]
    public async Task SendTextAsync_RecordsNeverArrive_TheWindowsEndWhenTheClockIsAdvancedNotWhenRealTimePasses()
    {
        // Arrange: a working Claude Code that takes the Enter but never records the prompt, and a session whose
        // records window and late watch add up to a full minute - on a fake clock shared with the terminal.
        var clock = VirtualTime.NewClock();
        var transcript = Path.Combine(_dir, "t.jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(AgentKind.ClaudeCode, transcript, _dir) { Working = true, NeverRecord = true, Clock = clock };
        _cleanup.Add(terminal);
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, terminal, SessionBackendType.ConPty) { AgentKind = AgentKind.ClaudeCode, Clock = clock };
        _cleanup.Add(session);
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        session.ApplyTerminalActivityState(ActivityState.Working);
        session.ArrivalWindow = TimeSpan.FromSeconds(30);
        session.LateArrivalLimitForTests = TimeSpan.FromSeconds(30);
        var text = "Token " + _tag + ". Reply with exactly: ACK";
        var startedAt = clock.GetUtcNow();
        var realTime = Stopwatch.StartNew();

        // Act
        var outcome = await VirtualTime.RunAsync(clock, () => session.SendTextAsync(text, SessionTestDoors.TestDoor));
        var watchEnded = await VirtualTime.RunAsync(clock, () => outcome.LateProof!);
        realTime.Stop();

        // Assert: the whole minute passed on the send's clock and the watch ended at its limit - while the real
        // clock saw a fraction of it. The bound is loose on purpose: it tells a path that waits on the real clock
        // (which would take the full minute) from one that waits on the session's, nothing finer.
        Assert.False(outcome.Confirmed);
        Assert.Equal(LateArrival.LimitEnded, watchEnded.Ended);
        var virtualElapsed = clock.GetUtcNow() - startedAt;
        Assert.True(virtualElapsed >= TimeSpan.FromSeconds(60), $"the send's clock advanced only {virtualElapsed.TotalSeconds:F1}s");
        Assert.True(realTime.Elapsed < TimeSpan.FromSeconds(20),
            $"the send took {realTime.Elapsed.TotalSeconds:F1}s of real time for {virtualElapsed.TotalSeconds:F0}s of its own clock: something waits on the real clock");
        Assert.Equal(1, terminal.EntersAccepted);
    }

    [Fact]
    public void SendWaitNotice_OnAClock_MeasuresItsWaitOnThatClock()
    {
        // Arrange: a notice measuring on a fake clock, not on a stopwatch.
        var clock = new FakeTimeProvider();
        var notice = new SendWaitNotice(_tag, "the agent to take in a paste", "120s", clock);

        // Act: two seconds is under the notice; seven is over it; the end reads the same clock.
        clock.Advance(TimeSpan.FromSeconds(2));
        notice.Check();
        var linesBeforeTheNotice = Lines().Count;
        clock.Advance(TimeSpan.FromSeconds(5));
        notice.Check();
        clock.Advance(TimeSpan.FromSeconds(2));
        notice.End("the composer shows the paste");

        // Assert
        Assert.Equal(0, linesBeforeTheNotice);
        var lines = Lines();
        Assert.Equal(2, lines.Count);
        Assert.Contains("WAITING", lines[0]);
        Assert.Contains("limit 120s", lines[0]);
        Assert.Contains("WAIT ENDED after 9.0s", lines[1]);
    }

    [Fact]
    public async Task WatchAsync_OnAClock_AnnouncesTheWaitWhenTheClockPassesTheNotice()
    {
        // Arrange: a wait that never ends on its own, watched on a fake clock.
        var clock = new FakeTimeProvider();
        var waiting = new TaskCompletionSource();
        var watch = SendWaitNotice.WatchAsync(waiting.Task, _tag, "the send in front to finish", "none", clock);

        // Act: under the notice nothing is said; past it, the wait is announced; then the wait ends.
        clock.Advance(TimeSpan.FromSeconds(4));
        var linesUnderTheNotice = Lines().Count;
        clock.Advance(TimeSpan.FromSeconds(2));
        waiting.SetResult();
        await watch;

        // Assert
        Assert.Equal(0, linesUnderTheNotice);
        var lines = Lines();
        Assert.Contains(lines, l => l.Contains("WAITING") && l.Contains("the send in front to finish"));
        Assert.Contains(lines, l => l.Contains("WAIT ENDED"));
    }

    private List<string> Lines() { lock (_lines) return _lines.ToList(); }
}
