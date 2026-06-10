using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia.Controls.DirectorView;

public partial class DirectorView : UserControl
{
    public const string DefaultGatewayUrl = "http://127.0.0.1:7878";

    private readonly ObservableCollection<SessionRowViewModel> _rows = new();
    private readonly HttpClient _http;
    private DispatcherTimer? _refreshTimer;
    // TODO #267-followup: _gatewayUrl defaults to the loopback DefaultGatewayUrl and is never
    // reassigned from GatewayConfig, so this sessions list talks only to localhost even when a
    // remote gateway is configured. Centralizing gateway-URL resolution here is out of scope for
    // #267 (which only fixes the Cockpit toolbar button) and tracked as a separate follow-up.
    private string _gatewayUrl = DefaultGatewayUrl;
    private string? _gatewayToken;

    public DirectorView()
    {
        InitializeComponent();
        SessionsList.ItemsSource = _rows;

        _http = new HttpClient { BaseAddress = new Uri(_gatewayUrl), Timeout = TimeSpan.FromSeconds(10) };
        _gatewayToken = TryLoadToken();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Attempts to load the gateway bearer token from the shared file written by GatewayAuth.
    /// Returns null if the file does not exist (Gateway has not run yet).
    /// </summary>
    private static string? TryLoadToken()
    {
        try
        {
            var path = Path.Combine(CcStorage.Config(), "director", "gateway-token.txt");
            if (!File.Exists(path)) return null;
            var token = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorView] TryLoadToken FAILED: {ex.Message}");
            return null;
        }
    }

    public string GatewayUrl
    {
        get => _gatewayUrl;
        set
        {
            _gatewayUrl = value;
            _http.BaseAddress = new Uri(value);
        }
    }

    public string? GatewayToken
    {
        get => _gatewayToken;
        set => _gatewayToken = value;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[DirectorView] Loaded");
        StatusText.Text = "Loading...";
        await RefreshAsync();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[DirectorView] Unloaded");
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    private async void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var sessions = await _http.GetFromJsonAsync<List<SessionDto>>("sessions");
            if (sessions is null) { StatusText.Text = "Gateway returned no data."; return; }

            // Merge into existing rows: update if present, add if new, remove if gone.
            var byId = _rows.ToDictionary(r => r.SessionId);
            var seen = new HashSet<string>();
            foreach (var s in sessions)
            {
                seen.Add(s.SessionId);
                if (byId.TryGetValue(s.SessionId, out var existing))
                    existing.Update(s);
                else
                    _rows.Add(new SessionRowViewModel(s));
            }
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                if (!seen.Contains(_rows[i].SessionId))
                    _rows.RemoveAt(i);
            }

            var idle = sessions.Count(s => s.ActivityState == "Idle");
            var working = sessions.Count(s => s.ActivityState == "Working");
            var waiting = sessions.Count(s => s.ActivityState is "WaitingForInput" or "WaitingForPerm");
            StatusText.Text = $"{sessions.Count} sessions ({idle} idle, {working} working, {waiting} waiting)";
        }
        catch (HttpRequestException ex)
        {
            StatusText.Text = $"Gateway not reachable at {_gatewayUrl} - {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Refresh error: {ex.Message}";
            FileLog.Write($"[DirectorView] RefreshAsync FAILED: {ex.Message}");
        }
    }

    private async void BtnSend_Click(object? sender, RoutedEventArgs e)
    {
        var prompt = PromptBox.Text;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            ResultsBox.Text = "Type a prompt first.";
            return;
        }

        var selected = _rows.Where(r => r.IsSelected).Select(r => r.SessionId).ToList();
        if (selected.Count == 0)
        {
            ResultsBox.Text = "Select at least one session.";
            return;
        }

        var waitForIdle = WaitForIdleCheck.IsChecked == true;
        BtnSend.IsEnabled = false;
        ResultsBox.Text = $"Sending to {selected.Count} session(s)...";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "fanout")
            {
                Content = JsonContent.Create(new FanoutRequest
                {
                    SessionIds = selected,
                    Text = prompt,
                    WaitForIdle = waitForIdle,
                    TimeoutMs = 300_000,
                }),
            };
            if (!string.IsNullOrEmpty(_gatewayToken))
                req.Headers.Add("Authorization", "Bearer " + _gatewayToken);

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                ResultsBox.Text = $"Gateway returned {(int)resp.StatusCode}: {body}";
                return;
            }

            var fanout = await resp.Content.ReadFromJsonAsync<FanoutResponse>();
            if (fanout is null) { ResultsBox.Text = "Empty fanout response."; return; }

            var sb = new StringBuilder();
            sb.AppendLine($"Fan-out complete in {(fanout.FinishedAt - fanout.StartedAt).TotalSeconds:F1}s.");
            sb.AppendLine();
            foreach (var r in fanout.Results)
            {
                sb.AppendLine($"---- session {r.SessionId[..Math.Min(8, r.SessionId.Length)]} [{r.Status}] ({r.ElapsedMs}ms) ----");
                if (!string.IsNullOrEmpty(r.Error))
                    sb.AppendLine($"ERROR: {r.Error}");
                if (!string.IsNullOrEmpty(r.Output))
                    sb.AppendLine(r.Output);
                sb.AppendLine();
            }
            ResultsBox.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            ResultsBox.Text = $"Send failed: {ex.Message}";
            FileLog.Write($"[DirectorView] BtnSend FAILED: {ex.Message}");
        }
        finally
        {
            BtnSend.IsEnabled = true;
        }
    }
}
