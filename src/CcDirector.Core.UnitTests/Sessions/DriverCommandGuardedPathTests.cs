using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A DRIVER'S COMMAND GOES THROUGH THE GUARDED PATH (issue #4380). /clear and /compact used to be typed by the driver
/// itself, with the backend's one-call submit, so they skipped the look before typing every prompt gets: with Claude Code's
/// question menu open, the command went into the menu - the 15 October failure, reached by another route. The driver now
/// says what to type and the Session types it, through the same send path as a prompt. These drive the real Session
/// against a terminal drawn the way Claude Code draws it.
/// </summary>
public sealed class DriverCommandGuardedPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-driver-command-" + Guid.NewGuid().ToString("N"));
    private readonly VirtualClock _clock = VirtualTime.NewClock();
    private readonly List<IDisposable> _cleanup = new();

    public DriverCommandGuardedPathTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>A waiting Claude Code session. With <paramref name="linkTranscript"/> false it has no agent session id
    /// yet, so a clear returns as soon as the command is typed instead of watching for the new transcript.</summary>
    private (Session Session, ScriptedAgentTerminal Terminal) NewClaudeSession(bool menuOpen, bool linkTranscript = true)
    {
        var transcript = Path.Combine(_dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(AgentKind.ClaudeCode, transcript, _dir) { Clock = _clock, MenuOpen = menuOpen };
        _cleanup.Add(terminal);
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, terminal, SessionBackendType.ConPty)
        {
            AgentKind = AgentKind.ClaudeCode,
            Clock = _clock,
        };
        _cleanup.Add(session);
        if (linkTranscript)
            session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        return (session, terminal);
    }

    [Fact]
    public async Task ClearContextAsync_MenuOnTheScreen_RefusesAndWritesNothing()
    {
        // Arrange
        var (session, terminal) = NewClaudeSession(menuOpen: true);

        // Act
        var ex = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => VirtualTime.RunAsync(_clock, () => session.ClearContextAsync()));

        // Assert: refused with the plain reason, and not one byte reached the terminal by any route.
        Assert.Contains("menu or a dialog", ex.Message);
        Assert.False(ex.TerminalReacted);
        Assert.Equal(0, terminal.BytesWritten);
        Assert.Empty(terminal.SentTexts);
    }

    [Fact]
    public async Task CompactContextAsync_MenuOnTheScreen_RefusesAndWritesNothing()
    {
        // Arrange
        var (session, terminal) = NewClaudeSession(menuOpen: true);

        // Act
        var ex = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => VirtualTime.RunAsync(_clock, () => session.CompactContextAsync()));

        // Assert
        Assert.Contains("menu or a dialog", ex.Message);
        Assert.Equal(0, terminal.BytesWritten);
        Assert.Empty(terminal.SentTexts);
    }

    [Fact]
    public async Task ClearContextAsync_ComposerDrawn_TypesTheDriversCommandIntoTheComposer()
    {
        // Arrange
        var (session, terminal) = NewClaudeSession(menuOpen: false, linkTranscript: false);
        terminal.ReplyBytesAfterEnter = 4096; // the agent answers the command, as Claude Code redraws after a /clear

        // Act
        var newId = await VirtualTime.RunAsync(_clock, () => session.ClearContextAsync());

        // Assert: the Session typed "/clear" key by key and pressed Enter; the one-call submit was not used.
        Assert.Null(newId);
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Equal(new[] { "/clear" }, terminal.Recorded);
        Assert.Empty(terminal.SentTexts);
        Assert.NotNull(session.ContextClearedUtc);
    }
}
