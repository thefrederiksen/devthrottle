using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.AgentBrain;
using HostedBrain = CcDirector.HostedAgent.HostedAgent;
using HostedBrainOptions = CcDirector.HostedAgent.HostedAgentOptions;

namespace CcDirector.AgentBrain.Panel;

/// <summary>
/// Control panel for one warm in-process Claude brain: every IAgentBrain verb gets a
/// large button. The panel process OWNS claude.exe via CcDirector.HostedAgent
/// (embedded ConPty) - the same hosting the Gateway tray app uses for its brain, so
/// this window is the test harness for that path (issue #184; the Director-REST
/// transport is retired).
///
/// HOST PROCESS WARNING: launch the panel from a clean process (Task Scheduler /
/// desktop), never from inside a Claude Code terminal, or the hosted claude dies on
/// the nested-ConPty trap.
///
/// All I/O is async (responsive-UI rule: immediate feedback, never block the UI
/// thread); a background timer keeps the health strip live.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly IBrush DotDisconnected = new SolidColorBrush(Color.Parse("#666666"));
    private static readonly IBrush DotAlive = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush DotBusy = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush DotDead = new SolidColorBrush(Color.Parse("#E5484D"));

    private HostedBrain? _brain;
    private readonly DispatcherTimer _healthTimer;
    private bool _busy;

    /// <summary>Cancellation for the in-flight AskAsync, so CANCEL TURN can abort the
    /// panel's wait as well as the agent's generation.</summary>
    private CancellationTokenSource? _askCts;

    /// <summary>Working directory for brain sessions; settable via --repo for tests.</summary>
    private readonly string _repoPath;

    public MainWindow()
    {
        InitializeComponent();

        var args = Environment.GetCommandLineArgs();
        var repoIdx = Array.IndexOf(args, "--repo");
        _repoPath = repoIdx >= 0 && repoIdx + 1 < args.Length
            ? args[repoIdx + 1]
            : Path.Combine(Path.GetTempPath(), "agent-brain-sandbox");
        TargetBox.Text = _repoPath;

        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _healthTimer.Tick += async (_, _) => await RefreshHealthAsync();
        _healthTimer.Start();
    }

    // ------------------------------------------------------------- handlers
    // Try-catch lives HERE (entry points) per CodingStyle - the libraries throw,
    // the panel reports.

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await StartHostedAsync();
    }

    private async Task StartHostedAsync()
    {
        SetBusy("Spawning claude.exe (hosted)...");
        try
        {
            _brain?.Dispose();
            var workDir = TargetBox.Text?.Trim() ?? "";
            Directory.CreateDirectory(workDir);
            var hosted = new HostedBrain(new HostedBrainOptions
            {
                WorkingDirectory = workDir,
            });
            _brain = hosted;

            var t0 = DateTime.UtcNow;
            await hosted.StartAsync();
            HostInfoText.Text = $"hosted - pid {hosted.ProcessId}";
            HealthDot.Fill = DotAlive;
            AppendLog($"[hosted] claude.exe pid={hosted.ProcessId} spawned as MY child in {(DateTime.UtcNow - t0).TotalSeconds:F1}s");
            AppendLog($"[hosted] claude session {hosted.SessionId}, workdir {workDir}");
            SetStatus("Session ready.");
        }
        catch (Exception ex)
        {
            _brain?.Dispose();
            _brain = null;
            HostInfoText.Text = "host start failed";
            HealthDot.Fill = DotDead;
            AppendLog($"[ERROR] host start failed: {ex.Message}");
            SetStatus("Host start failed - see log.");
        }
        finally
        {
            ClearBusy();
        }
    }

    private async void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _brain is null) return;
        SetBusy("Creating session (waiting for the agent to come up)...");
        try
        {
            var t0 = DateTime.UtcNow;
            await _brain.StartAsync();
            AppendLog($"[session created] hosted pid={_brain.ProcessId}, claude session {_brain.SessionId} in {(DateTime.UtcNow - t0).TotalSeconds:F1}s");
            SetStatus("Session ready.");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] create failed: {ex.Message}");
            SetStatus("Create failed - see log.");
        }
        finally
        {
            ClearBusy();
        }
    }

    private async void OnAskClick(object? sender, RoutedEventArgs e) => await AskAsync();

    private async void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter makes a newline.
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            await AskAsync();
        }
    }

    private async Task AskAsync()
    {
        if (_busy || _brain?.SessionId is null) return;
        var prompt = PromptBox.Text?.Trim();
        if (string.IsNullOrEmpty(prompt)) return;

        SetBusy("Asking...");
        AppendLog($"\n>>> YOU: {prompt}");
        PromptBox.Text = "";
        _askCts = new CancellationTokenSource();
        try
        {
            var result = await _brain.AskAsync(prompt, _askCts.Token);
            AppendLog($"<<< AGENT ({result.ReplySeconds:F1}s, context {result.ContextTokens:N0} tokens):");
            AppendLog(result.Text);

            if (AutoClearCheck.IsChecked == true)
            {
                SetStatus("Auto-clearing context...");
                var clear = await _brain.ClearAsync();
                AppendLog($"[auto-clear] context reset in {clear.Seconds:F1}s " +
                          $"(transcript {Shorten(clear.OldClaudeSessionId)} -> {Shorten(clear.NewClaudeSessionId)})");
            }
            SetStatus("Reply received.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("[cancelled] turn aborted by CANCEL TURN");
            SetStatus("Turn cancelled - session stays usable.");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] ask failed: {ex.Message}");
            SetStatus("Ask failed - see log. RESTART recovers a stuck session.");
        }
        finally
        {
            _askCts?.Dispose();
            _askCts = null;
            ClearBusy();
        }
    }

    private async void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        // Deliberately NOT gated on _busy: cancelling an in-flight turn is the whole point.
        if (_brain?.SessionId is null) return;
        try
        {
            AppendLog("[cancel] sending the driver's cancel keystroke (Esc)");
            await _brain.CancelAsync();
            _askCts?.Cancel();
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] cancel failed: {ex.Message}");
            SetStatus("Cancel failed - see log.");
        }
    }

    private async void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _brain?.SessionId is null) return;
        SetBusy("Clearing context...");
        try
        {
            var result = await _brain.ClearAsync();
            AppendLog($"[cleared] context reset in {result.Seconds:F1}s " +
                      $"(transcript {Shorten(result.OldClaudeSessionId)} -> {Shorten(result.NewClaudeSessionId)})");
            SetStatus("Context cleared - the agent remembers nothing.");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] clear failed: {ex.Message}");
            SetStatus("Clear failed - see log.");
        }
        finally
        {
            ClearBusy();
        }
    }

    private async void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _brain is null) return;
        SetBusy("Restarting (kill + fresh session)...");
        try
        {
            var old = _brain.SessionId;
            await _brain.RestartAsync();
            AppendLog($"[restarted] {old ?? "none"} -> {_brain.SessionId} (new pid {_brain.ProcessId})");
            SetStatus("Fresh session ready.");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] restart failed: {ex.Message}");
            SetStatus("Restart failed - see log.");
        }
        finally
        {
            ClearBusy();
        }
    }

    private async void OnKillClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _brain?.SessionId is null) return;
        SetBusy("Killing session...");
        try
        {
            var old = _brain.SessionId;
            await _brain.KillAsync();
            AppendLog($"[killed] {old}");
            SetStatus("Session terminated.");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] kill failed: {ex.Message}");
            SetStatus("Kill failed - see log.");
        }
        finally
        {
            ClearBusy();
        }
    }

    private async void OnHealthClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _brain is null) return;
        SetBusy("Checking health...");
        try
        {
            var h = await _brain.GetHealthAsync();
            AppendLog($"[health] alive={h.IsAlive} status={h.Status} state={h.ActivityState} " +
                      $"idle={h.IdleSeconds:F1}s context={h.ContextTokens:N0} turns={h.TurnCount}");
            SetStatus(h.IsAlive ? "Session is healthy." : "Session is DEAD - use RESTART.");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] health failed: {ex.Message}");
            SetStatus("Health check failed - see log.");
        }
        finally
        {
            ClearBusy();
        }
    }

    // ------------------------------------------------------------- plumbing

    private async Task RefreshHealthAsync()
    {
        if (_brain is null || _brain.SessionId is null)
        {
            ActivityText.Text = "-";
            IdleText.Text = "-";
            TokensText.Text = "-";
            SessionIdText.Text = "none";
            if (_brain is null) HealthDot.Fill = DotDisconnected;
            UpdateButtonStates();
            return;
        }

        try
        {
            var h = await _brain.GetHealthAsync();
            SessionIdText.Text = _brain.SessionId ?? "none";
            ActivityText.Text = h.ActivityState;
            IdleText.Text = $"idle {h.IdleSeconds:F0}s";
            TokensText.Text = $"context {h.ContextTokens:N0} tokens";
            HealthDot.Fill = !h.IsAlive ? DotDead
                : h.ActivityState is "Working" or "Active" ? DotBusy
                : DotAlive;
        }
        catch
        {
            // Background poll only - a blip here must not spam the log. Real
            // operations surface their own errors.
            HealthDot.Fill = DotDead;
        }
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        var connected = _brain is not null;
        var hasSession = _brain?.SessionId is not null;
        ConnectButton.IsEnabled = !_busy;
        CreateButton.IsEnabled = !_busy && connected && !hasSession;
        CancelButton.IsEnabled = hasSession;   // usable WHILE busy - that is its job
        AskButton.IsEnabled = !_busy && hasSession;
        ClearButton.IsEnabled = !_busy && hasSession;
        RestartButton.IsEnabled = !_busy && connected;
        KillButton.IsEnabled = !_busy && hasSession;
        HealthButton.IsEnabled = !_busy && connected;
    }

    private void SetBusy(string status)
    {
        _busy = true;
        SetStatus(status);
        UpdateButtonStates();
    }

    private void ClearBusy()
    {
        _busy = false;
        UpdateButtonStates();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void AppendLog(string line)
    {
        LogText.Text += line + Environment.NewLine;
        LogScroll.ScrollToEnd();
    }

    private static string Shorten(string id) => id.Length > 8 ? id[..8] : id;
}
