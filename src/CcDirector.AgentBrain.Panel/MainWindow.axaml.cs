using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.AgentBrain;

namespace CcDirector.AgentBrain.Panel;

/// <summary>
/// Control panel for one warm Claude brain: every IAgentBrain verb gets a large
/// button. Two modes share the whole body:
///
///   Hosted (default) - the panel process OWNS claude.exe via CcDirector.HostedAgent
///     (embedded ConPty). HOST PROCESS WARNING: launch the panel from a clean process
///     (Task Scheduler / desktop), never from inside a Claude Code terminal, or the
///     hosted claude dies on the nested-ConPty trap.
///   Director (REST) - remote-controls a session in a running CC Director via
///     AgentBrainClient.
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

    private const string DefaultDirectorUrl = "http://127.0.0.1:7886";

    private IAgentBrain? _brain;
    private readonly DispatcherTimer _healthTimer;
    private bool _busy;

    /// <summary>Cancellation for the in-flight AskAsync, so CANCEL TURN can abort the
    /// panel's wait as well as the agent's generation.</summary>
    private CancellationTokenSource? _askCts;

    /// <summary>Working directory for brain sessions; settable via --repo for tests.</summary>
    private readonly string _repoPath;

    private readonly string _directorUrl;

    private bool IsHostedMode => ModeCombo.SelectedIndex == 0;

    public MainWindow()
    {
        InitializeComponent();

        var args = Environment.GetCommandLineArgs();
        var repoIdx = Array.IndexOf(args, "--repo");
        _repoPath = repoIdx >= 0 && repoIdx + 1 < args.Length
            ? args[repoIdx + 1]
            : Path.Combine(Path.GetTempPath(), "agent-brain-sandbox");

        var urlIdx = Array.IndexOf(args, "--director");
        _directorUrl = urlIdx >= 0 && urlIdx + 1 < args.Length ? args[urlIdx + 1] : DefaultDirectorUrl;

        if (Array.IndexOf(args, "--mode-director") >= 0)
            ModeCombo.SelectedIndex = 1;

        ApplyModeToTopBar();

        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _healthTimer.Tick += async (_, _) => await RefreshHealthAsync();
        _healthTimer.Start();
    }

    // ------------------------------------------------------------- handlers
    // Try-catch lives HERE (entry points) per CodingStyle - the libraries throw,
    // the panel reports.

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent before the named controls exist.
        if (TargetBox is null) return;
        ApplyModeToTopBar();
    }

    private void ApplyModeToTopBar()
    {
        if (IsHostedMode)
        {
            TargetLabel.Text = "WORK DIR";
            TargetBox.Text = _repoPath;
            ConnectButton.Content = "START HOST";
        }
        else
        {
            TargetLabel.Text = "DIRECTOR";
            TargetBox.Text = _directorUrl;
            ConnectButton.Content = "CONNECT";
        }
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (IsHostedMode)
            await StartHostedAsync();
        else
            await ConnectDirectorAsync();
    }

    private async Task StartHostedAsync()
    {
        SetBusy("Spawning claude.exe (hosted)...");
        try
        {
            _brain?.Dispose();
            var workDir = TargetBox.Text?.Trim() ?? "";
            Directory.CreateDirectory(workDir);
            var hosted = new CcDirector.HostedAgent.HostedAgent(new CcDirector.HostedAgent.HostedAgentOptions
            {
                WorkingDirectory = workDir,
            });
            _brain = hosted;

            var t0 = DateTime.UtcNow;
            await hosted.StartAsync();
            DirectorInfoText.Text = $"hosted - pid {hosted.ProcessId}";
            HealthDot.Fill = DotAlive;
            AppendLog($"[hosted] claude.exe pid={hosted.ProcessId} spawned as MY child in {(DateTime.UtcNow - t0).TotalSeconds:F1}s");
            AppendLog($"[hosted] claude session {hosted.SessionId}, workdir {workDir}");
            SetStatus("Session ready.");
        }
        catch (Exception ex)
        {
            _brain?.Dispose();
            _brain = null;
            DirectorInfoText.Text = "host start failed";
            HealthDot.Fill = DotDead;
            AppendLog($"[ERROR] host start failed: {ex.Message}");
            SetStatus("Host start failed - see log.");
        }
        finally
        {
            ClearBusy();
        }
    }

    private async Task ConnectDirectorAsync()
    {
        SetBusy("Connecting...");
        try
        {
            _brain?.Dispose();
            var client = await AgentBrainClient.ConnectAsync(new AgentBrainOptions
            {
                DirectorUrl = TargetBox.Text?.Trim() ?? "",
                RepoPath = _repoPath,
            });
            _brain = client;
            var health = await client.GetDirectorHealthAsync();
            var version = health.TryGetProperty("version", out var v) ? v.GetString() : "?";
            DirectorInfoText.Text = $"connected - v{version}";
            HealthDot.Fill = DotAlive;
            AppendLog($"[connected] {TargetBox.Text} (Director v{version})");
            AppendLog($"[info] session working dir: {_repoPath}");
            SetStatus("Connected. Create a session to start.");
        }
        catch (Exception ex)
        {
            _brain = null;
            DirectorInfoText.Text = "connection failed";
            HealthDot.Fill = DotDead;
            AppendLog($"[ERROR] connect failed: {ex.Message}");
            SetStatus("Connect failed - see log.");
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
            switch (_brain)
            {
                case CcDirector.HostedAgent.HostedAgent hosted:
                    await hosted.StartAsync();
                    AppendLog($"[session created] hosted pid={hosted.ProcessId}, claude session {hosted.SessionId} in {(DateTime.UtcNow - t0).TotalSeconds:F1}s");
                    break;
                case AgentBrainClient client:
                    Directory.CreateDirectory(_repoPath);
                    await client.CreateSessionAsync();
                    AppendLog($"[session created] {client.SessionId} in {(DateTime.UtcNow - t0).TotalSeconds:F1}s");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown brain type: {_brain.GetType().Name}");
            }
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
            var pidNote = _brain is CcDirector.HostedAgent.HostedAgent h ? $" (new pid {h.ProcessId})" : "";
            AppendLog($"[restarted] {old ?? "none"} -> {_brain.SessionId}{pidNote}");
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
        ModeCombo.IsEnabled = !_busy && !connected;
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
