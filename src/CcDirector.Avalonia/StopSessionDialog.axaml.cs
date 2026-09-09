using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// The Director window's Stop control (mission "Stop a session", Ruling 5). It asks for the reason, sends
/// the stop through the Gateway's one stop route, and then shows what the Gateway said happened - and
/// stays up until it is dismissed, because a control that accepts a click and says nothing is the exact
/// defect this mission exists to remove.
///
/// THIS WINDOW COMPOSES NO SENTENCE ABOUT A STOP. The Gateway folded the words; this renders
/// <see cref="SessionStopResponse.Headline"/> and then each line of <see cref="SessionStopResponse.Details"/>,
/// in the order they were given, verbatim. It does not branch on the verdict word and does not decide what
/// a verdict MEANS - there are four verdict words today and a fifth would be one edit on the Gateway and
/// none here. That is house rule 7 in CLAUDE.md, applied to a new verb.
///
/// The only thing it does decide is failure against success, and that distinction never comes from a
/// verdict: an answer is a success, and an exception - a Gateway that could not be reached, a refusal, a
/// process that would not die - is a failure. The failure is shown in the same place, in words, and the
/// session is left on the rail, because saying "it is gone" when it is not is the worse of the two
/// mistakes.
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

    /// <summary>What the answer area says before anything has been asked of the Gateway.</summary>
    internal const string NothingAskedYet =
        "Nothing has happened yet. Say why this session is being stopped, then press \"Stop the session\".";

    /// <summary>
    /// What is shown while the stop is in flight. The round trip is not always loopback - the Gateway can
    /// be on another machine - so this is a real wait and it has to say so (CLAUDE.md rule 1).
    /// </summary>
    internal const string StopInFlight = "Stopping - asking the Gateway, and waiting for it to answer...";

    /// <summary>
    /// The one line this window writes about an outcome, and it is written only for a failure the Gateway
    /// never folded - the same shape and the same reason as the command line's "Not stopped:" prefix. The
    /// message that follows it is carried through untouched.
    /// </summary>
    internal const string NotStoppedPrefix = "The session was not stopped:";

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
    /// The stop itself: it takes the reason and returns the Gateway's folded answer, and it throws when
    /// the stop did not happen. Injected rather than reached for, so the whole dialog - the reason gate,
    /// the rendering, the failure path - is driven in a headless test with no Gateway and no network.
    /// </param>
    public StopSessionDialog(
        string sessionId, string displayName, Func<string, CancellationToken, Task<SessionStopResponse>> stop)
    {
        InitializeComponent();
        _sessionId = sessionId ?? "";
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));

        var name = string.IsNullOrWhiteSpace(displayName) ? "this session" : displayName.Trim();
        Title = $"Stop {name}";
        TxtHeading.Text = $"Stop {name}";
        TxtAnswer.Text = NothingAskedYet;

        TxtReason.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) UpdateStopAvailability();
        };
    }

    /// <summary>The reason as it currently stands in the box. Settable so a test can type into it.</summary>
    internal string ReasonText
    {
        get => TxtReason.Text ?? "";
        set => TxtReason.Text = value;
    }

    /// <summary>Whether the Stop button is currently offering a click.</summary>
    internal bool CanStop => BtnStop.IsEnabled;

    /// <summary>Everything the answer area is showing, headline and detail lines included.</summary>
    internal string AnswerText => TxtAnswer.Text ?? "";

    /// <summary>The in-flight line, empty when nothing is in flight.</summary>
    internal string PhaseText => TxtPhase.Text ?? "";

    /// <summary>
    /// The Gateway requires a reason (Ruling 4), so the control must not offer a click that can only be
    /// refused: the button is off while the box is empty or only whitespace. This is the whole of the gate
    /// and it is deliberately not a validation message - there is nothing to complain about yet.
    ///
    /// Hung on the TEXT PROPERTY rather than on the TextChanged event, so it holds however the text
    /// arrives. The event does not fire for a programmatic set in a headless test, which would have left
    /// the gate untested against the one thing it exists to prevent.
    /// </summary>
    private void UpdateStopAvailability()
        => BtnStop.IsEnabled = !_running && !string.IsNullOrWhiteSpace(TxtReason.Text);

    private async void BtnStop_Click(object? sender, RoutedEventArgs e) => await StopNowAsync();

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Send the stop and show what came back. This is the Stop button's whole body, pulled out of the
    /// handler so it can be awaited in a test - the same shape as SessionActionBar's confirmed clear. The
    /// try/catch is here for that reason and this is the entry point it belongs to (CLAUDE.md rule 4);
    /// nothing it calls catches anything.
    /// </summary>
    internal async Task StopNowAsync()
    {
        if (_running) return;

        var reason = (TxtReason.Text ?? "").Trim();
        if (reason.Length == 0) return;   // the button is off in this state; belt as well as braces

        _running = true;
        BtnStop.IsEnabled = false;
        TxtPhase.Text = StopInFlight;
        TxtAnswer.Text = "";

        FileLog.Write($"[StopSessionDialog] StopNowAsync: session={_sessionId}");
        try
        {
            var answer = await _stop(reason, _cts.Token);
            if (_closed) return;   // the window went away mid-flight; there is nothing to render onto

            FileLog.Write(
                $"[StopSessionDialog] StopNowAsync: session={_sessionId} answered verdict={answer.Verdict}, "
                + $"detailLines={answer.Details?.Count ?? 0}");

            // The answer, verbatim and in the Gateway's order. The reason box and the button stay off from
            // here: the stop has been asked for and answered, and a second press would be a second stop.
            TxtAnswer.Text = Render(answer);
            TxtReason.IsEnabled = false;
        }
        catch (Exception ex)
        {
            if (_closed) return;
            FileLog.Write($"[StopSessionDialog] StopNowAsync FAILED: session={_sessionId}: {ex.Message}");

            // The failure, in the same place the answer would have been, carrying whatever sentence came
            // with it. The rail row is untouched - nothing here removes it, and on a failure that is
            // exactly right: the session may well still be running.
            TxtAnswer.Text = $"{NotStoppedPrefix}{Environment.NewLine}{Environment.NewLine}{ex.Message}";
            BtnStop.IsEnabled = !string.IsNullOrWhiteSpace(TxtReason.Text);
        }
        finally
        {
            _running = false;
            if (!_closed) TxtPhase.Text = "";
        }
    }

    /// <summary>
    /// The headline, then each detail line, in the order the Gateway gave them. Blank entries are dropped
    /// the way the command line drops them; the order is part of the answer, so nothing here sorts,
    /// filters by meaning, or re-words anything.
    /// </summary>
    internal static string Render(SessionStopResponse answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var lines = new List<string> { answer.Headline };
        if (answer.Details is not null)
            lines.AddRange(answer.Details.Where(line => !string.IsNullOrWhiteSpace(line)));
        return string.Join(Environment.NewLine, lines);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _cts.Cancel();
        _cts.Dispose();
        base.OnClosed(e);
    }
}
