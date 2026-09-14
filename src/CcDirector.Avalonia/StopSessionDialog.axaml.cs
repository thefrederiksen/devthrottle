using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// The Director window's Stop control: one question, asked plainly, with an optional note.
///
/// IT USED TO INTERROGATE THE OWNER, AND THAT WAS THE DEFECT (issue internal#1992). It explained the audit
/// trail in four sentences, demanded a written reason before it would enable its own button, and then held
/// a monospace panel open afterwards reporting what had happened to a session the owner had just watched
/// leave the rail. Ruling 4's reason requirement belongs to the REST API - it exists because an AGENT key
/// may stop any OTHER session and the trail must say why. It was never meant for the owner's own hand: a
/// human pressing Stop in his own window is the strongest authorisation there is.
///
/// So the note is optional, and when it is left empty this window supplies <see cref="ReasonFromTheDirector"/>
/// instead. The Gateway's contract is untouched and every stop still writes a complete audit row - the
/// reason simply says WHERE the stop came from, which is the thing the Gateway cannot otherwise see. This
/// is the same shape the phone already uses (pull request #2816), so all three human surfaces now agree.
///
/// A SUCCESS IS SILENT AND CLOSES THE WINDOW. The session goes from the rail, which is the answer; a report
/// about it is a second thing to dismiss. The Gateway still folds its sentences and the command line still
/// prints them - nothing about the route changed - this window just stops making the owner read them.
///
/// ONLY A FAILURE SPEAKS, and it speaks here, above the button it explains, in the words it came with. A
/// window that closed on a failure would be claiming an outcome it does not have. The session is left on
/// the rail, because saying "it is gone" when it is not is the worse of the two mistakes.
///
/// It never removes the rail row itself, on success either. The Gateway sends the stop back down this
/// Director's own tunnel, the session manager removes the session, and MainWindow.OnExternalSessionRemoved
/// drops the row - the one place that teardown lives.
/// </summary>
public partial class StopSessionDialog : Window
{
    private readonly string _sessionId;
    private readonly Func<string, CancellationToken, Task<SessionStopResponse>> _stop;
    private readonly CancellationTokenSource _cts = new();

    private bool _running;
    private bool _closed;

    /// <summary>
    /// What is recorded when the owner writes no note. The Gateway requires a reason on every stop, so
    /// this window answers that requirement with what it knows rather than making the owner justify his
    /// own click - it names the surface the stop came from.
    /// </summary>
    internal const string ReasonFromTheDirector = "Stopped by the owner from the Director";

    /// <summary>The Stop button's label while the stop is in flight. The round trip is not always
    /// loopback - the Gateway can be on another machine - so it is a real wait and it has to say so
    /// (CLAUDE.md rule 1).</summary>
    internal const string StopInFlight = "Stopping...";

    /// <summary>The Stop button's label at rest.</summary>
    internal const string StopAtRest = "Stop the session";

    /// <summary>
    /// The one line this window writes for a failure, and it says what THIS WINDOW knows rather than what
    /// happened to the session. The message that follows it is carried through untouched.
    ///
    /// IT MUST NOT READ "The session was not stopped". A lost reply, a dropped connection, a request this
    /// window cancelled when it closed, or a Director that answered late can all happen AFTER the session
    /// was ended - and the Gateway says exactly that, in terms, when it does not know: "It is not known
    /// whether the command was carried out." A prefix claiming otherwise would print a contradiction of
    /// that sentence directly above it.
    ///
    /// It does not split refusals from lost replies the way the command line does, because it cannot: the
    /// client this dialog is handed raises one exception type for every failure and carries no status
    /// code, so one wording that claims nothing about the session covers both. That is the safe direction
    /// - a refusal announced as unknown is weaker than it needs to be, while a lost reply announced as
    /// "not stopped" is wrong.
    /// </summary>
    internal const string OutcomeUnknownPrefix =
        "Outcome unknown - this cannot say whether the session is still running:";

    /// <summary>Designer constructor. Never used at runtime; the stop route is not wired here.</summary>
    public StopSessionDialog()
        : this("", "this session",
            (_, _) => throw new InvalidOperationException("This dialog was built without a stop route."))
    {
    }

    /// <summary>
    /// Create the dialog.
    /// </summary>
    /// <param name="sessionId">The session to stop, as the Gateway knows it.</param>
    /// <param name="displayName">The session's name, for the heading and the window title.</param>
    /// <param name="stop">
    /// The stop itself: it takes the reason and returns the Gateway's answer, and it throws when the stop
    /// did not happen. Injected rather than reached for, so the whole dialog - the derived reason, the
    /// silent success, the failure path - is driven in a headless test with no Gateway and no network.
    /// </param>
    public StopSessionDialog(
        string sessionId, string displayName, Func<string, CancellationToken, Task<SessionStopResponse>> stop)
    {
        InitializeComponent();
        _sessionId = sessionId ?? "";
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));

        var name = string.IsNullOrWhiteSpace(displayName) ? "this session" : displayName.Trim();
        Title = "Stop session";
        TxtHeading.Text = $"Stop {name}?";
    }

    /// <summary>The note as it currently stands in the box. Settable so a test can type into it.</summary>
    internal string ReasonText
    {
        get => TxtReason.Text ?? "";
        set => TxtReason.Text = value;
    }

    /// <summary>
    /// Whether the Stop button is currently offering a click. It is on from the moment the window opens
    /// and goes off only while a stop is in flight - there is nothing to fill in first.
    /// </summary>
    internal bool CanStop => BtnStop.IsEnabled;

    /// <summary>The failure text, empty when no failure is showing.</summary>
    internal string FailureText => PnlFailure.IsVisible ? TxtFailure.Text ?? "" : "";

    /// <summary>Whether the stop has been sent and the window is waiting for the answer.</summary>
    internal bool IsStopping => _running;

    /// <summary>
    /// What this window will record for a stop, given whatever is in the note box. A note written by the
    /// owner is sent as written, trimmed; an empty or whitespace-only box sends the derived reason, so a
    /// stop is never refused for want of one and the audit row is never blank.
    /// </summary>
    internal static string ReasonToRecord(string? note)
        => string.IsNullOrWhiteSpace(note) ? ReasonFromTheDirector : note.Trim();

    private async void BtnStop_Click(object? sender, RoutedEventArgs e) => await StopNowAsync();

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Send the stop. This is the Stop button's whole body, pulled out of the handler so it can be awaited
    /// in a test - the same shape as SessionActionBar's confirmed clear. The try/catch is here for that
    /// reason and this is the entry point it belongs to (CLAUDE.md rule 4); nothing it calls catches
    /// anything.
    ///
    /// A second press while one stop is still outstanding sends nothing: without the guard an impatient
    /// double-click is two stops and two rows in the trail for one intention.
    /// </summary>
    internal async Task StopNowAsync()
    {
        if (_running) return;

        var reason = ReasonToRecord(TxtReason.Text);

        _running = true;
        BtnStop.IsEnabled = false;
        BtnStop.Content = StopInFlight;
        PnlFailure.IsVisible = false;

        FileLog.Write($"[StopSessionDialog] StopNowAsync: session={_sessionId}");
        try
        {
            var answer = await _stop(reason, _cts.Token);
            if (_closed) return;   // the window went away mid-flight; there is nothing to close

            FileLog.Write(
                $"[StopSessionDialog] StopNowAsync: session={_sessionId} answered verdict={answer.Verdict}");

            // Silent on every verdict. The window does not read the verdict word and does not decide what
            // one MEANS - there are four today and a fifth would be one edit on the Gateway and none here
            // (CLAUDE.md rule 7). Every one of them is a session that is no longer running, so every one
            // of them closes this window.
            Close();
        }
        catch (Exception ex)
        {
            if (_closed) return;
            FileLog.Write($"[StopSessionDialog] StopNowAsync FAILED: session={_sessionId}: {ex.Message}");

            // The failure, in words, above the button it explains, carrying whatever sentence came with
            // it. The window stays open with the note still in the box, so a retry does not begin by
            // making the owner write it again. The rail row is untouched, which on a failure is exactly
            // right: the session may well still be running.
            TxtFailure.Text = $"{OutcomeUnknownPrefix} {ex.Message}";
            PnlFailure.IsVisible = true;
        }
        finally
        {
            _running = false;
            if (!_closed)
            {
                BtnStop.IsEnabled = true;
                BtnStop.Content = StopAtRest;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _cts.Cancel();
        _cts.Dispose();
        base.OnClosed(e);
    }
}
