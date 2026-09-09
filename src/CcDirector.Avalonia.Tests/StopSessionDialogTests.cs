using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using CcDirector.Avalonia;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The Director window's Stop control (mission "Stop a session", Rulings 4 and 5).
///
/// THE DEFECT THIS REPLACED. The rail's Close killed the process in-process and removed the session: no
/// reason was asked for, none was recorded, and the operator was told nothing about what happened. "A
/// control that accepts a click and says nothing" is the mission's own description of it.
///
/// WHAT THESE PIN, and each one fails on its own if its guard is removed:
///   - the reason gate: the Gateway requires a reason, so the control must not offer a click that can only
///     be refused;
///   - the rendering: the headline and every detail line, in the Gateway's order, VERBATIM - no sentence
///     composed here and no branch on the verdict word;
///   - the failure: it is shown in words, in the same place, carrying the sentence it came with - and
///     it says only what this window knows, never what became of the session.
///
/// The stop is injected, so all of this runs with no Gateway, no Director and no network. What these do
/// NOT cover is written down in missions/stop-a-session/worker-e-notes.md: they drive the dialog's own
/// logic, not the pixels, and nothing here proves the rail row disappears - that is
/// MainWindow.OnExternalSessionRemoved, reached by the Gateway sending the stop back down the tunnel, and
/// it needs a real Director and a real Gateway to observe.
/// </summary>
public class StopSessionDialogTests
{
    private const string SessionId = "9c41e7a2-1111-2222-3333-444455556666";

    private static SessionStopResponse Answer(string headline, params string[] details) =>
        new()
        {
            Verdict = SessionStopVerdict.Stopped,
            Headline = headline,
            Details = new List<string>(details),
            SessionId = SessionId,
            ShortId = "9c41e7a2",
        };

    private static StopSessionDialog DialogAnswering(
        Func<string, CancellationToken, Task<SessionStopResponse>> stop) =>
        new(SessionId, "throwaway", stop);

    /// <summary>
    /// Nothing typed, nothing to click. The Gateway will refuse a stop with no reason (Ruling 4), so a
    /// button that offered the click would be offering a refusal.
    /// </summary>
    [AvaloniaFact]
    public void WithNoReasonTyped_TheStopButtonIsOff()
    {
        var dialog = DialogAnswering((_, _) => throw new InvalidOperationException("must not be called"));

        Assert.False(dialog.CanStop);
    }

    /// <summary>
    /// Whitespace is not a reason. The Gateway trims and refuses a blank one, so spaces must not turn the
    /// button on - this is the case a naive "is the box empty" check gets wrong.
    /// </summary>
    [AvaloniaFact]
    public void WithOnlyWhitespaceTyped_TheStopButtonIsStillOff()
    {
        var dialog = DialogAnswering((_, _) => throw new InvalidOperationException("must not be called"));

        dialog.ReasonText = "    ";

        Assert.False(dialog.CanStop);
    }

    /// <summary>With a reason written, the control is offered.</summary>
    [AvaloniaFact]
    public void WithAReasonTyped_TheStopButtonComesOn()
    {
        var dialog = DialogAnswering((_, _) => throw new InvalidOperationException("must not be called"));

        dialog.ReasonText = "spawned into the wrong mode";

        Assert.True(dialog.CanStop);
    }

    /// <summary>
    /// The decisive one for the gate: with a blank reason, NOTHING is sent. A test that only checked the
    /// button's state would still pass if the click path ignored it.
    /// </summary>
    [AvaloniaFact]
    public async Task WithABlankReason_NothingIsSent()
    {
        var calls = 0;
        var dialog = DialogAnswering((_, _) =>
        {
            calls++;
            return Task.FromResult(Answer("stopped"));
        });

        dialog.ReasonText = "   ";
        await dialog.StopNowAsync();

        Assert.Equal(0, calls);
    }

    /// <summary>The reason reaches the route trimmed, exactly as it was written.</summary>
    [AvaloniaFact]
    public async Task TheReasonIsSentAsItWasWritten()
    {
        string? sent = null;
        var dialog = DialogAnswering((reason, _) =>
        {
            sent = reason;
            return Task.FromResult(Answer("stopped 9c41e7a2"));
        });

        dialog.ReasonText = "  spawned into the wrong mode  ";
        await dialog.StopNowAsync();

        Assert.Equal("spawned into the wrong mode", sent);
    }

    /// <summary>
    /// THE RULE OF THIS WHOLE PHASE. The Gateway folded the words; the window shows the headline and then
    /// each detail line, in the Gateway's order, and NOTHING ELSE. The assertion is on the whole rendered
    /// string rather than on "contains", so a window that added a sentence of its own - a "Success!"
    /// banner, a re-worded verdict - fails here.
    /// </summary>
    [AvaloniaFact]
    public async Task ItShowsTheHeadlineAndEveryDetailLineVerbatim_AndAddsNothing()
    {
        var dialog = DialogAnswering((_, _) => Task.FromResult(Answer(
            "stopped 9c41e7a2 - process 51884 ended and the row was removed",
            "Its working tree at C:\\repo has uncommitted changes. They were left exactly as they were.",
            "Reason recorded: spawned into the wrong mode")));

        dialog.ReasonText = "spawned into the wrong mode";
        await dialog.StopNowAsync();

        Assert.Equal(
            "stopped 9c41e7a2 - process 51884 ended and the row was removed" + Environment.NewLine
            + "Its working tree at C:\\repo has uncommitted changes. They were left exactly as they were."
            + Environment.NewLine
            + "Reason recorded: spawned into the wrong mode",
            dialog.AnswerText);
    }

    /// <summary>
    /// A verdict word this build has never heard of renders like any other, because the window never looks
    /// at the verdict at all. There are four words today; a fifth is one edit on the Gateway, and this is
    /// the test that says so. A window that branched on the verdict to choose its wording would show
    /// nothing, or a default, for an unknown one.
    /// </summary>
    [AvaloniaFact]
    public async Task AVerdictWordItHasNeverSeen_RendersLikeAnyOther()
    {
        var dialog = DialogAnswering((_, _) => Task.FromResult(new SessionStopResponse
        {
            Verdict = "somethingTheGatewayAddedLater",
            Headline = "a sentence written on the Gateway",
            Details = new List<string> { "and a second one" },
        }));

        dialog.ReasonText = "why";
        await dialog.StopNowAsync();

        Assert.Equal(
            "a sentence written on the Gateway" + Environment.NewLine + "and a second one",
            dialog.AnswerText);
    }

    /// <summary>
    /// An answer with no detail lines shows the headline and stops there. The headline is always present;
    /// the details are not, and a window that assumed one would print a stray blank line.
    /// </summary>
    [AvaloniaFact]
    public async Task AnAnswerWithNoDetailLines_ShowsTheHeadlineAlone()
    {
        var dialog = DialogAnswering((_, _) => Task.FromResult(Answer("nothing in this account carries that id")));

        dialog.ReasonText = "why";
        await dialog.StopNowAsync();

        Assert.Equal("nothing in this account carries that id", dialog.AnswerText);
    }

    /// <summary>
    /// A failure SAYS SO, in the same place the answer would have been, carrying the sentence that came
    /// with it. Silence is the defect: the operator would read an unchanged window as "it worked".
    /// </summary>
    [AvaloniaFact]
    public async Task AFailureIsShownInWords_CarryingTheSentenceItCameWith()
    {
        var dialog = DialogAnswering((_, _) => throw new InvalidOperationException(
            "the process would not die: process 51884 is still running after the stop"));

        dialog.ReasonText = "spawned into the wrong mode";
        await dialog.StopNowAsync();

        Assert.Contains(StopSessionDialog.OutcomeUnknownPrefix, dialog.AnswerText);
        Assert.Contains("the process would not die: process 51884 is still running after the stop",
            dialog.AnswerText);
    }

    /// <summary>
    /// THE FINDING (inspection 1, I5), PINNED. When the Gateway says it does not know what happened, this
    /// window must not say that it does.
    ///
    /// The sentence is the router's own, word for word: a timeout proves only that the GATEWAY stopped
    /// waiting, and the Director may have ended the session and answered late. The window used to print
    /// "The session was not stopped:" directly above it - a client composing a verdict in the same breath
    /// as the server saying there is no verdict to be had. An operator who believes the window goes
    /// looking for a session that is already gone, or presses Stop again on one that never stopped.
    ///
    /// The test therefore asserts on what is NOT there. The uncertainty is the Gateway's to state, and the
    /// only thing being pinned is that this window does not contradict it.
    /// </summary>
    [AvaloniaFact]
    public async Task WhenTheGatewaySaysItDoesNotKnow_TheWindowDoesNotSayThatItDoes()
    {
        const string uncertain =
            "The Director on SORENLAPTOP did not answer within 30 seconds. "
            + "It is not known whether the command was carried out.";
        var dialog = DialogAnswering((_, _) => throw new InvalidOperationException(uncertain));

        dialog.ReasonText = "spawned into the wrong mode";
        await dialog.StopNowAsync();

        // The Gateway's sentence, intact.
        Assert.Contains(uncertain, dialog.AnswerText);
        // And nothing of this window's own claiming an outcome over the top of it, in either direction.
        Assert.DoesNotContain("was not stopped", dialog.AnswerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("was stopped", dialog.AnswerText, StringComparison.OrdinalIgnoreCase);
        // What it does say is what it knows: that it cannot tell.
        Assert.Contains("cannot say whether the session is still running", dialog.AnswerText);
    }

    /// <summary>
    /// After a failure the control is offered again, because the stop can honestly be tried again. After a
    /// success it is not: the stop has been asked for and answered.
    /// </summary>
    [AvaloniaFact]
    public async Task AfterAFailureTheStopCanBeTriedAgain_AfterAnAnswerItCannot()
    {
        var failing = DialogAnswering((_, _) => throw new InvalidOperationException("the Gateway is unreachable"));
        failing.ReasonText = "why";
        await failing.StopNowAsync();
        Assert.True(failing.CanStop);

        var answering = DialogAnswering((_, _) => Task.FromResult(Answer("stopped 9c41e7a2")));
        answering.ReasonText = "why";
        await answering.StopNowAsync();
        Assert.False(answering.CanStop);
    }

    /// <summary>
    /// While the stop is in flight the window says so and the button is off (CLAUDE.md rule 1 - the round
    /// trip is not always loopback, so it is a real wait). Once it has answered, the in-flight line is
    /// gone: a "stopping..." left on screen beside a finished answer is a window describing two states at
    /// once.
    /// </summary>
    [AvaloniaFact]
    public async Task WhileTheStopIsInFlightItSaysSo_AndTheLineGoesWhenItAnswers()
    {
        var release = new TaskCompletionSource<SessionStopResponse>();
        var dialog = DialogAnswering((_, _) => release.Task);

        dialog.ReasonText = "why";
        var inFlight = dialog.StopNowAsync();

        Assert.Equal(StopSessionDialog.StopInFlight, dialog.PhaseText);
        Assert.False(dialog.CanStop);

        release.SetResult(Answer("stopped 9c41e7a2"));
        await inFlight;

        Assert.Equal("", dialog.PhaseText);
    }

    /// <summary>
    /// A second press while one stop is still in flight sends nothing. Without the guard an impatient
    /// double-click is two stops and two rows in the audit trail for one intention.
    /// </summary>
    [AvaloniaFact]
    public async Task ASecondPressWhileOneIsInFlight_SendsNothing()
    {
        var release = new TaskCompletionSource<SessionStopResponse>();
        var calls = 0;
        var dialog = DialogAnswering((_, _) =>
        {
            calls++;
            return release.Task;
        });

        dialog.ReasonText = "why";
        var inFlight = dialog.StopNowAsync();
        // Started, NOT awaited. With the guard this returns at once having sent nothing; without it, it
        // would sit on the same outstanding answer as the first press and awaiting it here would deadlock
        // the test rather than fail it - which is a test that cannot go red, not a test that passes.
        var second = dialog.StopNowAsync();

        Assert.Equal(1, calls);

        release.SetResult(Answer("stopped 9c41e7a2"));
        await inFlight;
        await second;
    }

    /// <summary>
    /// Before anything is asked, the window says that nothing has happened yet. An empty panel beside a
    /// button reads as an answer of "nothing to report", which is a different claim entirely.
    /// </summary>
    [AvaloniaFact]
    public void BeforeAnythingIsAsked_ItSaysNothingHasHappened()
    {
        var dialog = DialogAnswering((_, _) => throw new InvalidOperationException("must not be called"));

        Assert.Equal(StopSessionDialog.NothingAskedYet, dialog.AnswerText);
    }
}
