using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.Core.UnitTests.Drivers;

/// <summary>
/// Every long wait in the send path says what it waits for (Voice Delivery mission, phase 3). On 25 September 2026 at
/// 09:05 a send waited 122 seconds with nothing in the log; these pin the lines that make such a wait impossible to miss.
/// </summary>
public sealed class SendWaitNoticeTests : IDisposable
{
    private readonly List<string> _lines = new();
    private readonly string _tag = "NoticeTest-" + Guid.NewGuid().ToString("N")[..8];

    public SendWaitNoticeTests() => SendWaitNotice.LineObserver += Observe;

    public void Dispose() => SendWaitNotice.LineObserver -= Observe;

    private void Observe(string line)
    {
        lock (_lines)
            if (line.Contains(_tag, StringComparison.Ordinal) || line.Contains("PromptArrival", StringComparison.Ordinal))
                _lines.Add(line);
    }

    private List<string> Lines() { lock (_lines) return _lines.ToList(); }

    [Fact]
    public void Check_WaitRunsPastTheNotice_WritesWhatItWaitsForAndItsLimit()
    {
        // Arrange
        var elapsed = TimeSpan.Zero;
        var notice = new SendWaitNotice(_tag, "the agent to take in a 847-character paste", "120s", () => elapsed);

        // Act
        elapsed = TimeSpan.FromSeconds(2);
        notice.Check();
        elapsed = TimeSpan.FromSeconds(6);
        notice.Check();
        notice.Check();
        elapsed = TimeSpan.FromSeconds(9);
        notice.End("the composer shows the paste");

        // Assert: one "waiting" line naming what and the limit, then one "ended" line with the outcome.
        var lines = Lines().Where(l => l.Contains(_tag)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("WAITING", lines[0]);
        Assert.Contains("the agent to take in a 847-character paste", lines[0]);
        Assert.Contains("limit 120s", lines[0]);
        Assert.Contains("WAIT ENDED after 9.0s", lines[1]);
        Assert.Contains("the composer shows the paste", lines[1]);
    }

    [Fact]
    public void End_WaitEndsQuickly_WritesNothing()
    {
        // Arrange
        var elapsed = TimeSpan.FromMilliseconds(300);
        var notice = new SendWaitNotice(_tag, "a quick echo", "4s", () => elapsed);

        // Act
        notice.Check();
        notice.End("echoed");

        // Assert
        Assert.DoesNotContain(Lines(), l => l.Contains(_tag));
    }

    [Fact]
    public async Task SendTextAsync_PasteTakenInSlowly_TheWaitSaysWhatItWaitsFor()
    {
        // Arrange: a working Claude Code that draws the paste only after seven seconds - longer than the notice.
        var dir = Path.Combine(Path.GetTempPath(), "cc-notice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var transcript = Path.Combine(dir, "t.jsonl");
        File.WriteAllText(transcript, "");
        using var terminal = new Sessions.ScriptedAgentTerminal(Agents.AgentKind.ClaudeCode, transcript, dir)
            { Working = true, PasteDrawDelay = TimeSpan.FromSeconds(7) };
        using var session = new Core.Sessions.Session(Guid.NewGuid(), dir, dir, null, terminal, Core.Backends.SessionBackendType.ConPty);
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        session.ApplyTerminalActivityState(Core.Sessions.ActivityState.Working);
        var text = ("Token " + _tag + ". ").PadRight(777, 'x');
        var what = $"take in a {text.Length}-character paste";
        void Watch(string line) { if (line.Contains(what, StringComparison.Ordinal)) lock (_lines) _lines.Add(line); }
        SendWaitNotice.LineObserver += Watch;

        DateTime? announcedAt = null;
        void Announced(string line) { if (line.Contains(what, StringComparison.Ordinal) && line.Contains("WAITING")) announcedAt ??= DateTime.UtcNow; }
        SendWaitNotice.LineObserver += Announced;
        try
        {
            // Act
            await session.SendTextAsync(text, Core.Sessions.SessionTestDoors.TestDoor);
        }
        finally
        {
            SendWaitNotice.LineObserver -= Watch;
            SendWaitNotice.LineObserver -= Announced;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        // Assert: the wait announced what it waited for and its limit while it was waiting, and then how it ended.
        Assert.True(announcedAt is not null && terminal.PasteDrawnAt is not null && announcedAt < terminal.PasteDrawnAt,
            $"the wait said what it was waiting for at {announcedAt:HH:mm:ss.fff}, not before the paste was drawn at {terminal.PasteDrawnAt:HH:mm:ss.fff}");
        var lines = Lines().Where(l => l.Contains(what)).ToList();
        Assert.Contains(lines, l => l.Contains("[ClaudeCode] WAITING") && l.Contains("limit 120s"));
        Assert.Contains(lines, l => l.Contains("WAIT ENDED") && l.Contains("the composer shows the paste"));
    }

    [Fact]
    public async Task ConfirmAsync_RecordsNeverArrive_TheWaitSaysWhatItWaitsForAndHowItEnded()
    {
        // Arrange: a clock the test advances, and records that never hold the prompt.
        var now = new DateTime(2026, 9, 25, 13, 5, 26, DateTimeKind.Utc);
        var label = "Token " + _tag;

        // Act
        var outcome = await PromptArrival.ConfirmAsync(
            arrived: () => false, composerHoldsNothing: () => false, resend: () => Task.CompletedTask, mayResend: false,
            window: TimeSpan.FromSeconds(60), label: label,
            poll: TimeSpan.FromSeconds(1), pause: step => { now += step; return Task.CompletedTask; }, utcNow: () => now);

        // Assert
        Assert.Equal(PromptArrivalOutcome.NotArrived, outcome);
        var lines = Lines().Where(l => l.Contains(label)).ToList();
        Assert.Contains(lines, l => l.Contains("WAITING") && l.Contains("to appear in the agent's conversation records") && l.Contains("limit 60s"));
        Assert.Contains(lines, l => l.Contains("WAIT ENDED") && l.Contains("NotArrived"));
    }
}
