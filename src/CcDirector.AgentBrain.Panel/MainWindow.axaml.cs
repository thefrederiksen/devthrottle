using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace CcDirector.AgentBrain.Panel;

/// <summary>
/// Control panel for one warm headless Claude session: every AgentBrainClient verb
/// gets a large button. All I/O is async (responsive-UI rule: immediate feedback,
/// never block the UI thread); a background timer keeps the health strip live.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly IBrush DotDisconnected = new SolidColorBrush(Color.Parse("#666666"));
    private static readonly IBrush DotAlive = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush DotBusy = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush DotDead = new SolidColorBrush(Color.Parse("#E5484D"));

    private AgentBrainClient? _brain;
    private readonly DispatcherTimer _healthTimer;
    private bool _busy;

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

        var urlIdx = Array.IndexOf(args, "--director");
        if (urlIdx >= 0 && urlIdx + 1 < args.Length)
            DirectorUrlBox.Text = args[urlIdx + 1];

        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _healthTimer.Tick += async (_, _) => await RefreshHealthAsync();
        _healthTimer.Start();
    }

    // ------------------------------------------------------------- handlers
    // Try-catch lives HERE (entry points) per CodingStyle - the library throws,
    // the panel reports.

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy("Connecting...");
        try
        {
            _brain?.Dispose();
            _brain = await AgentBrainClient.ConnectAsync(new AgentBrainOptions
            {
                DirectorUrl = DirectorUrlBox.Text?.Trim() ?? "",
                RepoPath = _repoPath,
            });
            var health = await _brain.GetDirectorHealthAsync();
            var version = health.TryGetProperty("version", out var v) ? v.GetString() : "?";
            DirectorInfoText.Text = $"connected - v{version}";
            HealthDot.Fill = DotAlive;
            AppendLog($"[connected] {DirectorUrlBox.Text} (Director v{version})");
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
            Directory.CreateDirectory(_repoPath);
            var t0 = DateTime.UtcNow;
            await _brain.CreateSessionAsync();
            AppendLog($"[session created] {_brain.SessionId} in {(DateTime.UtcNow - t0).TotalSeconds:F1}s");
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
        try
        {
            var result = await _brain.AskAsync(prompt);
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
        catch (Exception ex)
        {
            AppendLog($"[ERROR] ask failed: {ex.Message}");
            SetStatus("Ask failed - see log. RESTART recovers a stuck session.");
        }
        finally
        {
            ClearBusy();
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
            AppendLog($"[restarted] {old ?? "none"} -> {_brain.SessionId}");
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
        if (_busy || _brain?.SessionId is null) return;
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
        if (_brain?.SessionId is null)
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
                : h.ActivityState == "Working" ? DotBusy
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
        AskButton.IsEnabled = !_busy && hasSession;
        ClearButton.IsEnabled = !_busy && hasSession;
        RestartButton.IsEnabled = !_busy && connected;
        KillButton.IsEnabled = !_busy && hasSession;
        HealthButton.IsEnabled = !_busy && hasSession;
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
