using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;
using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A KEY PRESSED FROM OUTSIDE DURING A SEND IS HELD, NOT REFUSED (issue #4380; the owner's decision 8, 23 October 2026:
/// no gate a person at the terminal does not have). The phone's Enter, pressed while a prompt is being typed, used to be
/// refused; written there it would submit half the prompt. It is now held and pressed when the send ends, as the
/// desktop's keys are held. Driven through the real Session against a terminal drawn the way Claude Code draws it, on a
/// fake clock.
/// </summary>
public sealed class HeldKeyAfterSendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-held-key-" + Guid.NewGuid().ToString("N"));
    private readonly VirtualClock _clock = VirtualTime.NewClock();
    private readonly List<IDisposable> _cleanup = new();

    public HeldKeyAfterSendTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private (Session Session, ScriptedAgentTerminal Terminal) NewWaitingClaudeSession()
    {
        var transcript = Path.Combine(_dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(AgentKind.ClaudeCode, transcript, _dir) { Clock = _clock };
        _cleanup.Add(terminal);
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, terminal, SessionBackendType.ConPty)
        {
            AgentKind = AgentKind.ClaudeCode,
            Clock = _clock,
        };
        _cleanup.Add(session);
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        return (session, terminal);
    }

    private static readonly byte[] Enter = [0x0D];

    [Fact]
    public async Task PressKeyAfterAnySend_DuringASend_IsHeldThenPressedAfterTheSendsOwnEnter()
    {
        // Arrange: the key is pressed at the send's first keystroke.
        var (session, terminal) = NewWaitingClaudeSession();
        InputAnswer? during = null;
        var writesWhenPressed = -1;
        terminal.OnWrite = _ =>
        {
            if (during is not null) return;
            during = session.PressKeyAfterAnySend(Enter, SessionTestDoors.TestDoor);
            writesWhenPressed = terminal.Writes.Count;
        };
        const string prompt = "Token HELD1. Reply with exactly: ACK";

        // Act
        await VirtualTime.RunAsync(_clock, () => session.SendTextAsync(prompt, SessionTestDoors.TestDoor));

        // Assert: held, nothing written inside the send, and pressed once the send - its proof included - had ended.
        Assert.Equal(InputAnswerKind.Held, during!.Kind);
        Assert.Equal(new[] { prompt }, terminal.Recorded);
        Assert.Equal(Enter, terminal.Writes[^1]);
        Assert.Equal(2, terminal.Writes.Count(w => w.SequenceEqual(Enter)));
        Assert.DoesNotContain(terminal.Writes.Take(writesWhenPressed - 1), w => w.SequenceEqual(Enter));
    }

    [Fact]
    public void PressKeyAfterAnySend_NoSendRunning_IsWrittenAtOnce()
    {
        var (session, terminal) = NewWaitingClaudeSession();

        var answer = session.PressKeyAfterAnySend(Enter, SessionTestDoors.TestDoor);

        Assert.Equal(InputAnswer.Written, answer);
        Assert.Equal(Enter, Assert.Single(terminal.Writes));
    }
}
