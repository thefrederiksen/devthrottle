using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE CLEAR NEVER ERASES WORDS TYPED WHILE IT WAITS (review of pull request 4396, finding 1). A send that finds its own
/// earlier text in the composer clears it first, after waiting for the terminal to go quiet. The person at the terminal
/// can type in that wait. Codex's End then Ctrl+U, or Claude Code's End and Backspaces, would erase their words with the
/// line. The clear is written only if no text reached the session since the send began, decided under the input lock;
/// otherwise nothing is cleared and the send types after what is there (the owner's ruling, 21 October 2026).
/// </summary>
public sealed class ClearKeysOwnerWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-clear-window-" + Guid.NewGuid().ToString("N"));
    private readonly VirtualClock _clock = VirtualTime.NewClock();
    private readonly List<IDisposable> _cleanup = new();

    public ClearKeysOwnerWindowTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private const string Leftover = "an earlier unsent prompt about the deploy";
    private const string OwnerWords = " and my own words";

    /// <summary>A waiting session whose composer holds this product's own earlier text, marked as such.</summary>
    private (Session Session, ScriptedAgentTerminal Terminal) NewSessionHoldingALeftover(AgentKind agent)
    {
        var transcript = Path.Combine(_dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(agent, transcript, _dir) { Clock = _clock };
        terminal.SetComposer(Leftover);
        _cleanup.Add(terminal);
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, terminal, SessionBackendType.ConPty)
        {
            AgentKind = agent,
            Clock = _clock,
        };
        _cleanup.Add(session);
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        ComposerRetention.MarkMayHoldText(terminal, agent.ToString(), Leftover);
        terminal.Redraw();
        return (session, terminal);
    }

    [Theory]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.ClaudeCode)]
    public async Task SendTextAsync_OwnerTypesWhileTheClearWaits_NoClearKeyIsPressedAndTheirWordsStay(AgentKind agent)
    {
        // Arrange: the owner types at the desktop terminal at the last moment before the clear is decided.
        var (session, terminal) = NewSessionHoldingALeftover(agent);
        session.BeforeClearKeysForTests = () =>
            session.SendInput(System.Text.Encoding.ASCII.GetBytes(OwnerWords), InputOrigin.DesktopTyped, SessionTestDoors.TestDoor);
        const string prompt = "Token CLEAR1. Reply with exactly: ACK";

        // Act: whatever the send's outcome, the owner's words must survive it.
        await Record.ExceptionAsync(() => VirtualTime.RunAsync(_clock, () => session.SendTextAsync(prompt, SessionTestDoors.TestDoor)));

        // Assert: no clear key reached the terminal, and the owner's words are still there - in the composer, or submitted
        // with the text typed after them.
        Assert.Equal(0, terminal.ClearKeysPressed);
        var everything = terminal.Composer + "|" + string.Join("|", terminal.Recorded);
        Assert.Contains(OwnerWords.Trim(), everything);
        Assert.Contains(Leftover, everything);
    }

    [Fact]
    public async Task SendTextAsync_NobodyTypesWhileTheClearWaits_TheLeftoverIsClearedAndThePromptGoesAlone()
    {
        // The control: with nobody typing, the clear is pressed as before and the prompt goes alone.
        var (session, terminal) = NewSessionHoldingALeftover(AgentKind.ClaudeCode);
        const string prompt = "Token CLEAR2. Reply with exactly: ACK";

        await VirtualTime.RunAsync(_clock, () => session.SendTextAsync(prompt, SessionTestDoors.TestDoor));

        Assert.True(terminal.ClearKeysPressed > 0);
        Assert.Equal(new[] { prompt }, terminal.Recorded);
    }
}
