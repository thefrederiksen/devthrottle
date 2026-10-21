using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director's <c>enter</c> verb (the owner's ruling, 21 October 2026): one Enter pressed in a session with nothing
/// typed before it, the phone's Enter key for an agent. Whatever sits in the composer is submitted - and never on Claude
/// Code's usage-limit dialog, where Enter confirms a paid option. Driven through the real core against a real session,
/// asserted on what the terminal RECEIVED.
/// </summary>
public sealed class EnterCommandTests
{
    /// <summary>The bottom of a real captured dialog, personal details removed (the same rows as
    /// <see cref="UsageLimitDialogPromptTests"/>).</summary>
    private static readonly string[] DialogRows =
    {
        "  You've hit your session limit - resets 10:20am (UTC)",
        "  What do you want to do?",
        "  > 1. Stop and wait for limit to reset",
        "    2. Add funds to continue with usage credits",
        "    3. Switch to Team plan",
        "  Enter to confirm - Esc to cancel",
    };

    [Fact]
    public void EnterCore_TextSitsInTheComposer_SubmitsIt()
    {
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        terminal.ShowInComposer("the owner's unsent words");

        var result = SessionCommandExecutor.EnterCore(session);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(new[] { "\r" }, terminal.Writes);
        Assert.Equal(new[] { "the owner's unsent words" }, terminal.Submitted);
    }

    [Fact]
    public void EnterCore_TheOwnersTrackedDraft_IsNoLongerUnsentAfterTheEnter()
    {
        var (session, _) = ScriptedTerminal.NewWaitingSession();
        ScriptedTerminal.OwnerTypes(session, "half a thought");
        Assert.True(session.HasUnsentOwnerDraft);

        var result = SessionCommandExecutor.EnterCore(session);

        Assert.True(result.Ok, result.Error);
        Assert.False(session.HasUnsentOwnerDraft);
    }

    [Fact]
    public void EnterCore_UsageLimitDialogShowing_RefusesAndPressesNothing()
    {
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        terminal.Buffer!.Write(Encoding.UTF8.GetBytes("\u001b[2J\u001b[H" + string.Join("\r\n", DialogRows)));

        var result = SessionCommandExecutor.EnterCore(session);

        Assert.False(result.Ok);
        Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
        Assert.Equal(SessionCommandExecutor.EnterRefusedOnUsageLimitDialog, result.Error);
        Assert.Empty(terminal.Writes);
        Assert.Empty(terminal.Submitted);
    }

    [Fact]
    public async Task EnterCore_WhileAnotherSendIsBeingTyped_RefusesAndPressesNothingInsideIt()
    {
        // An Enter pressed while a prompt is being typed would submit half of it. Pressed from inside the send's own
        // first write - the moment the send holds the session - it is refused, and the send still goes once, whole.
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        DirectorCommandResult? during = null;
        var pressed = false;
        terminal.OnWrite = _ =>
        {
            if (pressed) return;
            pressed = true;
            during = SessionCommandExecutor.EnterCore(session);
        };

        var sent = await SessionCommandExecutor.SendPromptAsync(session, new PromptRequest { Text = "carry on", AppendEnter = true });

        Assert.True(sent.Ok, sent.Error);
        Assert.NotNull(during);
        Assert.False(during!.Ok);
        Assert.Equal(DirectorCommandStatus.Conflict, during.Status);
        Assert.Equal(SessionCommandExecutor.EnterRefusedWhileSending, during.Error);
        Assert.Equal(new[] { "carry on" }, terminal.Submitted);
    }

    [Fact]
    public void EnterCore_SessionWithNoTerminal_IsRefused()
    {
        var terminal = new ScriptedTerminal();
        using var session = new Session(Guid.NewGuid(), Path.GetTempPath(), Path.GetTempPath(), null, terminal,
            Core.Backends.SessionBackendType.Embedded);

        var result = SessionCommandExecutor.EnterCore(session);

        Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
        Assert.Empty(terminal.Writes);
    }

    [Fact]
    public async Task EnterAsync_UnknownSession_IsNotFound()
    {
        var manager = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.EnterAsync(manager,
                new DirectorCommand { Verb = "enter", SessionId = Guid.NewGuid().ToString() });

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { manager.Dispose(); }
    }
}
