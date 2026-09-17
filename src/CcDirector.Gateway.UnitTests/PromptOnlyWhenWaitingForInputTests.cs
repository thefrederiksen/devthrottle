using System.Text;
using System.Text.Json;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director's own idle check for text the product sends by itself (the Fleet Manager mission, step 4): with
/// <see cref="PromptRequest.OnlyWhenWaitingForInput"/>, the Director types only when the session is waiting for a
/// prompt at the moment it would type. Driven through the real prompt core against a real session.
/// </summary>
public sealed class PromptOnlyWhenWaitingForInputTests
{
    private static (SessionManager Manager, Session Session) NewSession()
    {
        var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        return (manager, session);
    }

    private static PromptRequest EventPrompt(bool onlyWhenWaiting = true) => new()
    {
        Text = "[Fleet Manager events] 1 stop",
        AppendEnter = true,
        AgentDriven = true,
        OnlyWhenWaitingForInput = onlyWhenWaiting,
    };

    private static PromptResponse Body(DirectorCommandResult result)
    {
        Assert.True(result.Ok, result.Error);
        return JsonSerializer.Deserialize<PromptResponse>(result.BodyJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Theory]
    [InlineData(ActivityState.Working)]
    [InlineData(ActivityState.WaitingForPerm)]
    [InlineData(ActivityState.Starting)]
    public async Task SendPromptAsync_SessionNotWaitingForAPrompt_TypesNothingAndSaysSo(ActivityState state)
    {
        var (manager, session) = NewSession();
        try
        {
            session.ApplyTerminalActivityState(state);

            var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

            Assert.False(response.Accepted);
            Assert.True(response.RefusedBusy);
            Assert.True(response.IdleChecked);
            Assert.Equal(state.ToString(), response.ActivityState);
            Assert.Contains("nothing was typed", response.Error);
            // Nothing was submitted: no turn counted, and the state is what the owner's own turn made it.
            Assert.Equal(0, session.InputStats.Snapshot().AgentDrivenTurns);
            Assert.Equal(state, session.ActivityState);
        }
        finally { manager.Dispose(); }
    }

    [Theory]
    [InlineData(ActivityState.WaitingForInput)]
    [InlineData(ActivityState.Idle)]
    public async Task SendPromptAsync_SessionWaitingForAPrompt_TypesItAndConfirmsTheCheck(ActivityState state)
    {
        var (session, _) = NewTerminalSession();
        session.ApplyTerminalActivityState(state);

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.True(response.Accepted);
        Assert.False(response.RefusedBusy);
        Assert.True(response.IdleChecked);
        Assert.Equal(1, session.InputStats.Snapshot().AgentDrivenTurns);
    }

    /// <summary>
    /// INSPECTION ROUND 2, FINDING 2: a session whose terminal submits a whole turn in one call (an embedded, pipe or
    /// studio session) cannot have that call taken back, so the input bound could not be kept. Such a session is never
    /// sent a guarded prompt: it is refused before anything is sent, and the answer says why.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_TerminalSubmitsInOneCall_RefusedBeforeAnythingIsSent()
    {
        var (manager, session) = NewSession();
        try
        {
            session.ApplyTerminalActivityState(ActivityState.Working);
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

            var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

            Assert.False(response.Accepted);
            Assert.True(response.RefusedBusy);
            Assert.True(response.IdleChecked);
            Assert.Equal(PromptResponse.RefusedForOneCallSubmit, response.RefusedFor);
            Assert.Contains("cannot be taken back", response.Error);
            Assert.Equal(0, session.InputStats.Snapshot().AgentDrivenTurns);
            Assert.Equal(ActivityState.WaitingForInput, session.ActivityState);
        }
        finally { manager.Dispose(); }
    }

    /// <summary>Every other caller is unchanged: without the field a working session is typed into, and the answer
    /// says no check was made.</summary>
    [Fact]
    public async Task SendPromptAsync_FieldNotSet_TypesIntoAWorkingSession_AndReportsNoCheck()
    {
        var (manager, session) = NewSession();
        try
        {
            session.ApplyTerminalActivityState(ActivityState.Working);

            var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt(onlyWhenWaiting: false)));

            Assert.True(response.Accepted);
            Assert.False(response.IdleChecked);
            Assert.Equal(1, session.InputStats.Snapshot().AgentDrivenTurns);
        }
        finally { manager.Dispose(); }
    }

    // ===== The owner typing between the check and the Enter (inspection round 2, finding 1) =====

    private const string EventText = "[Fleet Manager events] 1 stop";

    private static (Session Session, ScriptedTerminal Terminal) NewTerminalSession() => ScriptedTerminal.NewWaitingSession();

    private static void OwnerTypes(Session session, string keys) => ScriptedTerminal.OwnerTypes(session, keys);

    /// <summary>
    /// Text in the composer that the Director does not know is there: put straight into the scripted composer, as an
    /// agent's own history recall or a mis-tracked edit could. The owner-draft check cannot see it, so a guarded send
    /// goes ahead - and its rollback must still leave that text exactly as it was.
    /// </summary>
    private static void TextTheDirectorDoesNotKnowAbout(Session session, ScriptedTerminal terminal, string text)
    {
        terminal.Composer.Append(text);
        Assert.False(session.HasUnsentOwnerDraft);
    }

    [Fact]
    public async Task SendPromptAsync_TheOwnerTypesAfterTheCheck_TheEventGoesFirst_ThenTheirKeys()
    {
        var (session, terminal) = NewTerminalSession();
        // The owner presses Enter on their own prompt in the gap between the idle check and the first typed byte.
        session.AfterInputCheckForTests = () => OwnerTypes(session, "carry on\r");

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.True(response.Accepted);
        Assert.True(response.IdleChecked);
        // The event holds the input: its text and its Enter, then the owner's keys, never welded together.
        Assert.Equal(new[] { EventText, "\r", "carry on\r" }, terminal.Writes);
        Assert.Equal(new[] { EventText, "carry on" }, terminal.Submitted);
        Assert.Equal(1, session.InputStats.Snapshot().AgentDrivenTurns);
    }

    /// <summary>
    /// INSPECTION FINDING 1 (steps 1 and 4): the owner pressing Enter after the event text is in the composer and before
    /// the event's own Enter must never submit the event text as the owner's turn. Their Enter waits for the section.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_TheOwnerPressesEnterBetweenTheTextAndTheEnter_TheirEnterNeverSubmitsTheEvent()
    {
        var (session, terminal) = NewTerminalSession();
        var ownersEnterReachedTheTerminalAtOnce = true;
        terminal.OnWrite = written =>
        {
            if (!written.Contains(EventText, StringComparison.Ordinal)) return;
            var before = terminal.Writes.Count;
            OwnerTypes(session, "\r");
            ownersEnterReachedTheTerminalAtOnce = terminal.Writes.Count != before;
        };

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.True(response.Accepted);
        // While the event text sat in the composer, the owner's Enter did not reach the terminal.
        Assert.False(ownersEnterReachedTheTerminalAtOnce);
        // The event's own Enter submitted it; the owner's Enter came after, on an empty composer.
        Assert.Equal(new[] { EventText, "\r", "\r" }, terminal.Writes);
        Assert.Equal(new[] { EventText, "" }, terminal.Submitted);
        Assert.Equal(1, session.InputStats.Snapshot().AgentDrivenTurns);
    }

    [Fact]
    public async Task SendPromptAsync_TheOwnerTypesDuringTheSend_TheirKeysArriveAfterIt_InOrder()
    {
        var (session, terminal) = NewTerminalSession();
        session.AfterInputCheckForTests = () => OwnerTypes(session, "a");
        terminal.OnWrite = written =>
        {
            if (written.Contains(EventText, StringComparison.Ordinal))
            {
                OwnerTypes(session, "b");
                OwnerTypes(session, "c\r");
            }
        };

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.True(response.Accepted);
        Assert.Equal(new[] { EventText, "\r", "a", "b", "c\r" }, terminal.Writes);
        Assert.Equal(new[] { EventText, "abc" }, terminal.Submitted);
    }

    /// <summary>
    /// A send that cannot finish inside its bound (here the composer never echoes) is abandoned: the text it typed is
    /// removed, so the composer is as the owner had it - their draft, plus what they typed while it was held - and the
    /// send is reported refused. Nothing was submitted and no Escape was pressed over the owner's draft.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_AbandonedAtItsBound_LeavesTheComposerAsTheOwnerHadIt()
    {
        var (session, terminal) = NewTerminalSession();
        TextTheDirectorDoesNotKnowAbout(session, terminal, "my draft ");
        terminal.Echo = false;
        session.GuardedSendLimitForTests = TimeSpan.FromMilliseconds(300);
        terminal.OnWrite = written =>
        {
            if (written.Contains(EventText, StringComparison.Ordinal)) OwnerTypes(session, "x");
        };

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.False(response.Accepted);
        Assert.True(response.RefusedBusy);
        Assert.Contains("removed from the composer", response.Error);
        Assert.Equal("my draft x", terminal.Composer.ToString());
        Assert.Empty(terminal.Submitted);
        Assert.DoesNotContain(terminal.Writes, w => w.Contains('\x1b'));
        Assert.Equal(0, session.InputStats.Snapshot().AgentDrivenTurns);

        // The owner's input flows normally once the send is over.
        OwnerTypes(session, "y\r");
        Assert.Equal(new[] { "my draft xy" }, terminal.Submitted);
    }

    /// <summary>
    /// A composer that does not echo within the ordinary echo wait would be cleared with Escape and retyped by an
    /// ordinary send. A guarded send never presses Escape over a composer that may hold the owner's draft: it removes
    /// its own text instead and is reported refused.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_ComposerDoesNotEcho_NoEscape_ItsOwnTextIsRemoved()
    {
        var (session, terminal) = NewTerminalSession();
        TextTheDirectorDoesNotKnowAbout(session, terminal, "my draft ");
        terminal.Echo = false;
        session.GuardedSendLimitForTests = TimeSpan.FromSeconds(30);

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.False(response.Accepted);
        Assert.True(response.RefusedBusy);
        Assert.Contains("removed from the composer", response.Error);
        Assert.Equal("my draft ", terminal.Composer.ToString());
        Assert.Empty(terminal.Submitted);
        Assert.DoesNotContain(terminal.Writes, w => w.Contains('\x1b'));
    }

    // ===== The owner's unsent draft (inspection round 2, finding 1) =====

    /// <summary>
    /// INSPECTION ROUND 2, FINDING 1: the owner typed a draft and paused without sending it. The session is waiting and
    /// no input is in flight, but the composer holds the owner's words. The event must not be appended to them and
    /// submitted as one turn: the send is refused before anything is typed, and the draft is left as it is.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_TheOwnerHasAnUnsentDraft_SubmitsNothing_AndLeavesTheDraft()
    {
        var (session, terminal) = NewTerminalSession();
        OwnerTypes(session, "my draft ");

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.False(response.Accepted);
        Assert.True(response.RefusedBusy);
        Assert.True(response.IdleChecked);
        Assert.Equal(PromptResponse.RefusedForOwnerDraft, response.RefusedFor);
        Assert.Contains("unsent text", response.Error);
        Assert.Equal(new[] { "my draft " }, terminal.Writes);
        Assert.Equal("my draft ", terminal.Composer.ToString());
        Assert.Empty(terminal.Submitted);
        Assert.Equal(0, session.InputStats.Snapshot().AgentDrivenTurns);
    }

    /// <summary>Once the owner sends their draft, and the turn it started ends, the event is delivered on its own.</summary>
    [Fact]
    public async Task SendPromptAsync_AfterTheOwnerSubmitsTheirDraft_TheEventIsDeliveredAlone()
    {
        var (session, terminal) = NewTerminalSession();
        OwnerTypes(session, "my draft ");
        var refused = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));
        Assert.False(refused.Accepted);

        OwnerTypes(session, "\r");
        Assert.False(session.HasUnsentOwnerDraft);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.True(response.Accepted);
        Assert.Null(response.RefusedFor);
        Assert.Equal(new[] { "my draft ", EventText }, terminal.Submitted);
        Assert.Equal(1, session.InputStats.Snapshot().AgentDrivenTurns);
    }

    /// <summary>
    /// The owner typing in the Cockpit's terminal reaches the Director with no input origin, through the Gateway's
    /// terminal door. It is still the owner's draft.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_TheOwnerTypedInTheCockpitTerminal_SubmitsNothing()
    {
        var (session, terminal) = NewTerminalSession();
        session.SendInput(Encoding.UTF8.GetBytes("from the browser "), null,
            SubmissionProvenance.Typed(SubmissionRoutes.GatewayTerminal, "device-key"));

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.False(response.Accepted);
        Assert.Equal(PromptResponse.RefusedForOwnerDraft, response.RefusedFor);
        Assert.Empty(terminal.Submitted);
    }

    /// <summary>
    /// Bytes that put no text in the composer are not a draft: a terminal's own reports (focus, mouse) and the
    /// product's own framework keys (a Wingman menu answer).
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_OnlyTerminalReportsAndFrameworkKeysWereWritten_TheEventIsDelivered()
    {
        var (session, terminal) = NewTerminalSession();
        ScriptedTerminal.OwnerTypes(session, "\x1b[I");
        ScriptedTerminal.OwnerTypes(session, "\x1b[<64;10;5M");
        ScriptedTerminal.OwnerTypes(session, "\x1b[M`!!");
        session.SendInput(Encoding.UTF8.GetBytes("2"), null, SubmissionProvenance.FrameworkText());
        terminal.Composer.Clear();
        Assert.False(session.HasUnsentOwnerDraft);

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.True(response.Accepted);
        Assert.Equal(new[] { EventText }, terminal.Submitted);
    }

    /// <summary>Arrow up or down recalls a line from history into the composer: that is a draft too.</summary>
    [Fact]
    public async Task SendPromptAsync_TheOwnerRecalledALineFromHistory_SubmitsNothing()
    {
        var (session, terminal) = NewTerminalSession();
        ScriptedTerminal.OwnerTypes(session, "\x1b[A");

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.False(response.Accepted);
        Assert.Equal(PromptResponse.RefusedForOwnerDraft, response.RefusedFor);
        Assert.Empty(terminal.Submitted);
    }

    /// <summary>
    /// When the Director cannot be sure the composer is empty it waits: a draft rubbed out with Backspace may leave
    /// text the Director cannot see (a wrapped line, an autocomplete), so the send stays refused until the next submit.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_TheOwnerRubsOutTheirDraft_StillRefusedUntilTheNextSubmit()
    {
        var (session, terminal) = NewTerminalSession();
        OwnerTypes(session, "ab");
        OwnerTypes(session, "\x7f\x7f");

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.False(response.Accepted);
        Assert.Equal(PromptResponse.RefusedForOwnerDraft, response.RefusedFor);
        Assert.Empty(terminal.Submitted);
    }

    /// <summary>
    /// A draft typed while the agent works is still there when the turn ends. A Working state read from the terminal
    /// does not clear it: only a submission the Director saw does.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_TheOwnerTypedWhileTheAgentWorked_RefusedWhenTheTurnEnds()
    {
        var (session, terminal) = NewTerminalSession();
        session.ApplyTerminalActivityState(ActivityState.Working);
        OwnerTypes(session, "next ");
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        session.ApplyTerminalActivityState(ActivityState.Working);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.False(response.Accepted);
        Assert.Equal(PromptResponse.RefusedForOwnerDraft, response.RefusedFor);
        Assert.Equal("next ", terminal.Composer.ToString());
    }

    /// <summary>
    /// Keys the owner typed while the event was being sent are written after it, into an empty composer - and they are
    /// now the owner's unsent draft, so the next event is refused. The event's own submit does not clear them.
    /// </summary>
    [Fact]
    public async Task SendPromptAsync_TheOwnerTypedDuringTheSend_TheirKeysAreADraftForTheNextEvent()
    {
        var (session, terminal) = NewTerminalSession();
        session.AfterInputCheckForTests = () => OwnerTypes(session, "a");

        var first = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));
        Assert.True(first.Accepted);
        Assert.True(session.HasUnsentOwnerDraft);
        session.AfterInputCheckForTests = null;
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        var second = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.False(second.Accepted);
        Assert.Equal(PromptResponse.RefusedForOwnerDraft, second.RefusedFor);
        Assert.Equal("a", terminal.Composer.ToString());
        Assert.Equal(new[] { EventText }, terminal.Submitted);
    }

    [Fact]
    public async Task SendPromptAsync_NobodyTypes_TheTerminalGetsTheTextThenTheEnter()
    {
        var (session, terminal) = NewTerminalSession();

        var response = Body(await ControlApi.SessionCommandExecutor.SendPromptAsync(session, EventPrompt()));

        Assert.True(response.Accepted);
        Assert.True(response.IdleChecked);
        Assert.Equal(new[] { EventText, "\r" }, terminal.Writes);
        Assert.Equal(1, session.InputStats.Snapshot().AgentDrivenTurns);
    }
}
