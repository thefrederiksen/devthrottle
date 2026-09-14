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
/// The Director window's Stop control, after issue internal#1992 turned it from an interrogation back into
/// a confirmation.
///
/// THE DEFECT THESE NOW PIN AGAINST. The window demanded a written reason before it would enable its own
/// button, explained the audit trail in four sentences, and then held a monospace panel open reporting what
/// had happened to a session the owner had just watched leave the rail. Ruling 4's reason requirement binds
/// the REST API - an AGENT key may stop any OTHER session and the trail must say why - not the owner's own
/// hand.
///
/// WHAT THESE PIN, and each one fails on its own if its guard is removed:
///   - the note is OPTIONAL: the button is live from the moment the window opens, and a stop with an empty
///     box still reaches the Gateway;
///   - the audit row is never blank: an empty box sends the derived reason naming this surface, and a
///     written note is sent as written;
///   - a success is SILENT and closes the window, on every verdict word including one this build has never
///     heard of - no branch on the verdict here;
///   - a failure SPEAKS, keeps the window open with the note intact, and says only what this window knows,
///     never what became of the session.
///
/// The stop is injected, so all of this runs with no Gateway, no Director and no network. What these do
/// NOT cover: they drive the dialog's own logic, not the pixels, and nothing here proves the rail row
/// disappears - that is MainWindow.OnExternalSessionRemoved, reached by the Gateway sending the stop back
/// down the tunnel, and it needs a real Director and a real Gateway to observe.
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
    /// THE HEADLINE CHANGE. Nothing has been typed and the control is offered anyway: the owner's click is
    /// the authorisation, so there is nothing to fill in first. This is the exact assertion that was
    /// inverted before.
    /// </summary>
    [AvaloniaFact]
    public void WithNothingTyped_TheStopButtonIsAlreadyOn()
    {
        var dialog = DialogAnswering((_, _) => throw new InvalidOperationException("must not be called"));

        Assert.True(dialog.CanStop);
    }

    /// <summary>
    /// The decisive one: with the box empty, the stop is actually SENT. A test that only checked the
    /// button's state would still pass if the click path refused a blank note.
    /// </summary>
    [AvaloniaFact]
    public async Task WithAnEmptyNote_TheStopIsStillSent()
    {
        var calls = 0;
        var dialog = DialogAnswering((_, _) =>
        {
            calls++;
            return Task.FromResult(Answer("stopped"));
        });

        await dialog.StopNowAsync();

        Assert.Equal(1, calls);
    }

    /// <summary>
    /// An empty box records the DERIVED reason, so the Gateway's requirement is met and the audit row says
    /// where the stop came from. A window that sent an empty string would have its stop refused.
    /// </summary>
    [AvaloniaFact]
    public async Task WithAnEmptyNote_TheReasonRecordedNamesThisSurface()
    {
        string? sent = null;
        var dialog = DialogAnswering((reason, _) =>
        {
            sent = reason;
            return Task.FromResult(Answer("stopped 9c41e7a2"));
        });

        await dialog.StopNowAsync();

        Assert.Equal(StopSessionDialog.ReasonFromTheDirector, sent);
    }

    /// <summary>
    /// Whitespace is not a note. Spaces must fall through to the derived reason - this is the case a naive
    /// "is the box empty" check gets wrong, and the Gateway trims and refuses a blank one.
    /// </summary>
    [AvaloniaFact]
    public async Task WithOnlyWhitespaceTyped_TheDerivedReasonIsRecorded()
    {
        string? sent = null;
        var dialog = DialogAnswering((reason, _) =>
        {
            sent = reason;
            return Task.FromResult(Answer("stopped 9c41e7a2"));
        });

        dialog.ReasonText = "    ";
        await dialog.StopNowAsync();

        Assert.Equal(StopSessionDialog.ReasonFromTheDirector, sent);
    }

    /// <summary>A note the owner did write reaches the route trimmed, exactly as it was written, and the
    /// derived reason does not overwrite it.</summary>
    [AvaloniaFact]
    public async Task AWrittenNoteIsSentAsItWasWritten()
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

    /// <summary>The same rule stated where it is decided, for both directions at once.</summary>
    [AvaloniaTheory]
    [InlineData(null, StopSessionDialog.ReasonFromTheDirector)]
    [InlineData("", StopSessionDialog.ReasonFromTheDirector)]
    [InlineData("   ", StopSessionDialog.ReasonFromTheDirector)]
    [InlineData("  it was wedged  ", "it was wedged")]
    public void TheReasonToRecordIsTheNoteOrTheDerivedOne(string? note, string expected)
        => Assert.Equal(expected, StopSessionDialog.ReasonToRecord(note));

    /// <summary>
    /// A SUCCESS IS SILENT. The session leaves the rail, which is the answer; there is no report to read
    /// and nothing to dismiss. The window closing is the whole of it.
    /// </summary>
    [AvaloniaFact]
    public async Task OnSuccess_TheWindowCloses_AndSaysNothing()
    {
        var dialog = DialogAnswering((_, _) => Task.FromResult(Answer(
            "stopped 9c41e7a2 - process 51884 ended and the row was removed",
            "Reason recorded: spawned into the wrong mode")));

        var closed = false;
        dialog.Closed += (_, _) => closed = true;

        dialog.ReasonText = "spawned into the wrong mode";
        await dialog.StopNowAsync();

        Assert.True(closed);
        Assert.Equal("", dialog.FailureText);
    }

    /// <summary>
    /// A verdict word this build has never heard of closes the window like any other, because the window
    /// never looks at the verdict at all. There are four words today; a fifth is one edit on the Gateway,
    /// and this is the test that says so. A window that branched on the verdict would stall on an unknown
    /// one (CLAUDE.md rule 7).
    /// </summary>
    [AvaloniaFact]
    public async Task AVerdictWordItHasNeverSeen_ClosesTheWindowLikeAnyOther()
    {
        var dialog = DialogAnswering((_, _) => Task.FromResult(new SessionStopResponse
        {
            Verdict = "somethingTheGatewayAddedLater",
            Headline = "a sentence written on the Gateway",
            Details = new List<string> { "and a second one" },
        }));

        var closed = false;
        dialog.Closed += (_, _) => closed = true;

        await dialog.StopNowAsync();

        Assert.True(closed);
    }

    /// <summary>
    /// A failure SAYS SO, carrying the sentence that came with it, and the window STAYS OPEN. Closing
    /// silently is the defect: the operator would read a vanished window as "it worked", when the session
    /// may well still be running.
    /// </summary>
    [AvaloniaFact]
    public async Task AFailureIsShownInWords_AndTheWindowStaysOpen()
    {
        var dialog = DialogAnswering((_, _) => throw new InvalidOperationException(
            "the process would not die: process 51884 is still running after the stop"));

        dialog.ReasonText = "spawned into the wrong mode";
        await dialog.StopNowAsync();

        Assert.Contains(StopSessionDialog.OutcomeUnknownPrefix, dialog.FailureText);
        Assert.Contains("the process would not die: process 51884 is still running after the stop",
            dialog.FailureText);
    }

    /// <summary>
    /// A failure leaves the note in the box, so a retry does not begin by making the owner write his
    /// sentence again.
    /// </summary>
    [AvaloniaFact]
    public async Task AfterAFailure_TheNoteIsStillInTheBox_AndTheStopCanBeTriedAgain()
    {
        var dialog = DialogAnswering((_, _) => throw new InvalidOperationException("the Gateway is unreachable"));

        dialog.ReasonText = "spawned into the wrong mode";
        await dialog.StopNowAsync();

        Assert.Equal("spawned into the wrong mode", dialog.ReasonText);
        Assert.True(dialog.CanStop);
    }

    /// <summary>
    /// THE FINDING (inspection 1, I5), STILL PINNED. When the Gateway says it does not know what happened,
    /// this window must not say that it does.
    ///
    /// The sentence is the router's own, word for word: a timeout proves only that the GATEWAY stopped
    /// waiting, and the Director may have ended the session and answered late. An operator who believes a
    /// window claiming otherwise goes looking for a session that is already gone, or presses Stop again on
    /// one that never stopped. The assertion is therefore on what is NOT there.
    /// </summary>
    [AvaloniaFact]
    public async Task WhenTheGatewaySaysItDoesNotKnow_TheWindowDoesNotSayThatItDoes()
    {
        const string uncertain =
            "The Director on SORENLAPTOP did not answer within 30 seconds. "
            + "It is not known whether the command was carried out.";
        var dialog = DialogAnswering((_, _) => throw new InvalidOperationException(uncertain));

        await dialog.StopNowAsync();

        // The Gateway's sentence, intact.
        Assert.Contains(uncertain, dialog.FailureText);
        // And nothing of this window's own claiming an outcome over the top of it, in either direction.
        Assert.DoesNotContain("was not stopped", dialog.FailureText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("was stopped", dialog.FailureText, StringComparison.OrdinalIgnoreCase);
        // What it does say is what it knows: that it cannot tell.
        Assert.Contains("cannot say whether the session is still running", dialog.FailureText);
    }

    /// <summary>
    /// A second failure replaces the first rather than stacking, and a retry clears the stale failure while
    /// it is in flight - a window showing an old error beside an outstanding request describes two states
    /// at once.
    /// </summary>
    [AvaloniaFact]
    public async Task ARetryClearsTheFailureWhileItIsInFlight()
    {
        var release = new TaskCompletionSource<SessionStopResponse>();
        var attempt = 0;
        var dialog = DialogAnswering((_, _) =>
        {
            attempt++;
            if (attempt == 1) throw new InvalidOperationException("the Gateway is unreachable");
            return release.Task;
        });

        await dialog.StopNowAsync();
        Assert.NotEqual("", dialog.FailureText);

        var retry = dialog.StopNowAsync();
        Assert.Equal("", dialog.FailureText);

        release.SetResult(Answer("stopped 9c41e7a2"));
        await retry;
    }

    /// <summary>
    /// While the stop is in flight the button says so and is off (CLAUDE.md rule 1 - the round trip is not
    /// always loopback, so it is a real wait).
    /// </summary>
    [AvaloniaFact]
    public async Task WhileTheStopIsInFlight_TheButtonSaysSoAndIsOff()
    {
        var release = new TaskCompletionSource<SessionStopResponse>();
        var dialog = DialogAnswering((_, _) => release.Task);

        var inFlight = dialog.StopNowAsync();

        Assert.True(dialog.IsStopping);
        Assert.False(dialog.CanStop);

        release.SetResult(Answer("stopped 9c41e7a2"));
        await inFlight;

        Assert.False(dialog.IsStopping);
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
    /// Before anything is asked the window shows no failure. An error panel visible at rest reads as
    /// something having already gone wrong.
    /// </summary>
    [AvaloniaFact]
    public void BeforeAnythingIsAsked_NoFailureIsShowing()
    {
        var dialog = DialogAnswering((_, _) => throw new InvalidOperationException("must not be called"));

        Assert.Equal("", dialog.FailureText);
    }
}
