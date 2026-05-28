using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Small modal that asks the Wingman to read the active session's terminal and explain,
/// in plain language, what just happened and what the agent is waiting on. It runs the
/// exact same read-only briefing the FIFO conveyor uses
/// (<see cref="Core.Wingman.WingmanService.BriefingQuestion"/> over
/// <see cref="Core.Wingman.WingmanService.AnswerViaSessionAsync"/>), so honing that one
/// briefing improves both paths at once.
///
/// The Wingman call takes a few seconds. The dialog appears immediately with a "Reading
/// the session..." placeholder; Cancel aborts the in-flight call and closes at any time.
/// Once the explanation arrives the button becomes "Close".
/// </summary>
public partial class ExplainDialog : Window
{
    private readonly Session _session;
    private readonly AgentOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private bool _done;

    public ExplainDialog(Session session, AgentOptions options)
    {
        InitializeComponent();
        _session = session;
        _options = options;

        HeaderSub.Text = DescribeSession(session);

        Loaded += async (_, _) => await RunExplainAsync();
    }

    // Parameterless constructor for the XAML designer.
    public ExplainDialog() : this(null!, null!) { }

    private async Task RunExplainAsync()
    {
        try
        {
            FileLog.Write($"[ExplainDialog] explaining session {_session.Id}");
            var bytes = _session.Buffer?.DumpAll() ?? Array.Empty<byte>();
            var fullTerminal = global::CcDirector.ControlApi.AnsiCleaner.Clean(bytes);

            var result = await global::CcDirector.Core.Wingman.WingmanService.AnswerViaSessionAsync(
                global::CcDirector.Core.Wingman.WingmanService.BriefingQuestion,
                fullTerminal,
                _session.AgentKind.ToString(),
                _session.RepoPath,
                _options.ClaudePath,
                _cts.Token);

            if (_cts.IsCancellationRequested) return;

            ExplanationText.Text = string.IsNullOrWhiteSpace(result.Answer)
                ? "The Wingman had nothing to report."
                : result.Answer;
            MarkDone();
        }
        catch (OperationCanceledException)
        {
            // User hit Cancel; the dialog is already closing.
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ExplainDialog] RunExplain FAILED: {ex.Message}");
            if (!_cts.IsCancellationRequested)
            {
                ExplanationText.Text = "Could not read this session: " + ex.Message;
                MarkDone();
            }
        }
    }

    // After the explanation lands, the only remaining action is to dismiss, so the
    // Cancel button becomes a plain Close.
    private void MarkDone()
    {
        _done = true;
        CloseButton.Content = "Close";
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        if (!_done) _cts.Cancel();
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!_done) _cts.Cancel();
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _cts.Dispose();
        base.OnClosed(e);
    }

    private static string DescribeSession(Session session)
    {
        var repo = session.RepoPath ?? "";
        var name = System.IO.Path.GetFileName(repo.TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? session.AgentKind.ToString() : name;
    }
}
