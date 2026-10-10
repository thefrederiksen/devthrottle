using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Connection item for display in the connections list.
/// </summary>
public class ConnectionItem : INotifyPropertyChanged
{
    private string _status = "disconnected";

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Url { get; set; }
    public string? ToolBinding { get; set; }
    public string? Browser { get; set; }
    public string? CreatedAt { get; set; }

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            NotifyAllVisualProperties();
        }
    }

    public bool Connected { get; set; }
    public bool Busy { get; set; }
    public int? ChromePid { get; set; }

    private static readonly SolidColorBrush BrushAmber = new(Color.Parse("#CCA700"));
    private static readonly SolidColorBrush BrushGreen = new(Color.Parse("#22C55E"));
    private static readonly SolidColorBrush BrushGray = new(Color.Parse("#666666"));
    private static readonly SolidColorBrush BrushGrayText = new(Color.Parse("#888888"));
    private static readonly SolidColorBrush BrushDisabledBg = new(Color.Parse("#555555"));
    private static readonly SolidColorBrush BrushCloseBg = new(Color.Parse("#3C3C3C"));
    private static readonly SolidColorBrush BrushOpenBg = new(Color.Parse("#007ACC"));
    private static readonly SolidColorBrush BrushLightText = new(Color.Parse("#CCCCCC"));
    private static readonly SolidColorBrush BrushWhite = new(Colors.White);

    public IBrush StatusBrush => Busy ? BrushAmber : Connected ? BrushGreen : BrushGray;

    public string StatusDisplay => Busy
        ? (Connected ? "Closing..." : "Opening...")
        : Connected ? "Connected" : "Disconnected";

    public IBrush StatusTextBrush => Busy ? BrushAmber : Connected ? BrushGreen : BrushGrayText;

    public string ActionLabel => Busy
        ? (Connected ? "Closing..." : "Opening...")
        : Connected ? "Close" : "Open";

    public bool ActionEnabled => !Busy;

    public IBrush ActionBackground => Busy ? BrushDisabledBg : Connected ? BrushCloseBg : BrushOpenBg;

    public IBrush ActionForeground => Busy ? BrushGrayText : Connected ? BrushLightText : BrushWhite;

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public string MetaDisplay
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Url)) parts.Add(Url);
            if (!string.IsNullOrEmpty(ToolBinding)) parts.Add($"tool: {ToolBinding}");
            if (!string.IsNullOrEmpty(CreatedAt) &&
                DateTime.TryParse(CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                parts.Add($"Added {dt:MMM d, yyyy}");
            return string.Join("  |  ", parts);
        }
    }

    public void SetBusy(bool busy)
    {
        Busy = busy;
        NotifyAllVisualProperties();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void NotifyAllVisualProperties()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(StatusTextBrush));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(ActionEnabled));
        OnPropertyChanged(nameof(ActionBackground));
        OnPropertyChanged(nameof(ActionForeground));
    }
}

/// <summary>
/// Full-page overlay view for managing browser connections.
/// Polls daemon for status updates; saves directly to connections.json for CRUD.
/// </summary>
public partial class ConnectionsView : UserControl
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ObservableCollection<ConnectionItem> _connections = new();
    private DispatcherTimer? _pollTimer;

    public ConnectionsView()
    {
        InitializeComponent();
        ConnectionList.ItemsSource = _connections;

        Loaded += async (_, _) =>
        {
            FileLog.Write("[ConnectionsView] Loaded");
            LoadingText.IsVisible = true;

            await Task.Run(() => LoadConnectionsFromFile());

            LoadingText.IsVisible = false;
            UpdateEmptyState();
        };
    }

    // -----------------------------------------------------------------------
    // Public Polling Control (called by MainWindow)
    // -----------------------------------------------------------------------

    public void StartPolling()
    {
        FileLog.Write("[ConnectionsView] StartPolling");
        if (_pollTimer != null) return;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await PollDaemonStatus();
        _pollTimer.Start();

        // Immediate poll
        _ = PollDaemonStatus();
    }

    public void StopPolling()
    {
        FileLog.Write("[ConnectionsView] StopPolling");
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    // -----------------------------------------------------------------------
    // Connections File (direct read/write, no daemon needed)
    // -----------------------------------------------------------------------

    private void LoadConnectionsFromFile()
    {
        FileLog.Write("[ConnectionsView] LoadConnectionsFromFile");
        try
        {
            var registryPath = CcStorage.ConnectionsRegistry();
            if (!File.Exists(registryPath))
            {
                FileLog.Write("[ConnectionsView] No connections.json found");
                return;
            }

            var json = File.ReadAllText(registryPath);
            var items = JsonSerializer.Deserialize<JsonElement>(json);

            if (items.ValueKind != JsonValueKind.Array) return;

            Dispatcher.UIThread.Post(() =>
            {
                _connections.Clear();
                foreach (var item in items.EnumerateArray())
                {
                    _connections.Add(new ConnectionItem
                    {
                        Name = item.GetProperty("name").GetString() ?? "",
                        Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                        Url = item.TryGetProperty("url", out var u) ? u.GetString() : null,
                        ToolBinding = item.TryGetProperty("toolBinding", out var t) ? t.GetString() : null,
                        Browser = item.TryGetProperty("browser", out var b) ? b.GetString() : "chrome",
                        CreatedAt = item.TryGetProperty("createdAt", out var ca) ? ca.GetString() : null,
                        Status = item.TryGetProperty("status", out var s) ? s.GetString() ?? "disconnected" : "disconnected",
                    });
                }
                FileLog.Write($"[ConnectionsView] Loaded {_connections.Count} connections");
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] LoadConnectionsFromFile FAILED: {ex.Message}");
        }
    }

    private void SaveConnectionToFile(ConnectionItem item)
    {
        FileLog.Write($"[ConnectionsView] SaveConnectionToFile: {item.Name}");
        try
        {
            var registryPath = CcStorage.ConnectionsRegistry();
            var dir = Path.GetDirectoryName(registryPath)
                ?? throw new InvalidOperationException($"Cannot determine directory for {registryPath}");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            List<Dictionary<string, object?>> existing = new();
            if (File.Exists(registryPath))
            {
                var json = File.ReadAllText(registryPath);
                var parsed = JsonSerializer.Deserialize<JsonElement>(json);
                if (parsed.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in parsed.EnumerateArray())
                    {
                        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(el.GetRawText());
                        if (dict != null) existing.Add(dict);
                    }
                }
            }

            var newEntry = new Dictionary<string, object?>
            {
                ["name"] = item.Name,
                ["description"] = item.Description,
                ["url"] = item.Url,
                ["toolBinding"] = item.ToolBinding,
                ["browser"] = item.Browser ?? "chrome",
                ["createdAt"] = item.CreatedAt,
                ["status"] = "disconnected",
            };

            existing.Add(newEntry);
            File.WriteAllText(registryPath, JsonSerializer.Serialize(existing,
                new JsonSerializerOptions { WriteIndented = true }));

            FileLog.Write($"[ConnectionsView] Saved connection to file: {item.Name}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] SaveConnectionToFile FAILED: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Daemon Polling (status updates)
    // -----------------------------------------------------------------------

    private async Task PollDaemonStatus()
    {
        try
        {
            var port = GetDaemonPort();
            var response = await _http.GetAsync($"http://127.0.0.1:{port}/connections");
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(json);

            if (!data.TryGetProperty("connections", out var conns)) return;

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var item in conns.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString();
                    var connected = item.TryGetProperty("connected", out var c) && c.GetBoolean();

                    var existing = _connections.FirstOrDefault(ci => ci.Name == name);
                    if (existing != null)
                    {
                        existing.Connected = connected;
                        existing.Status = connected ? "connected" : "disconnected";
                    }
                    else
                    {
                        _connections.Add(new ConnectionItem
                        {
                            Name = name ?? "",
                            Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                            Url = item.TryGetProperty("url", out var u) ? u.GetString() : null,
                            ToolBinding = item.TryGetProperty("toolBinding", out var t) ? t.GetString() : null,
                            CreatedAt = item.TryGetProperty("createdAt", out var ca) ? ca.GetString() : null,
                            Connected = connected,
                            Status = connected ? "connected" : "disconnected",
                        });
                    }
                }
                UpdateEmptyState();
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Daemon not running or request timed out - set all to disconnected
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var item in _connections)
                {
                    item.Connected = false;
                    item.Status = "disconnected";
                }
            });
        }
    }

    private int GetDaemonPort()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var lockPath = Path.Combine(localAppData, "cc-browser", "daemon.lock");

        if (File.Exists(lockPath))
        {
            try
            {
                var data = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(lockPath));
                if (data.TryGetProperty("port", out var port)) return port.GetInt32();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                FileLog.Write($"[ConnectionsView] GetDaemonPort FAILED: failed to read lock file: {ex.Message}");
            }
        }
        return 9280;
    }

    /// <summary>
    /// Checks if the cc-browser daemon is reachable. If not, starts it
    /// via "cc-browser connections status" and waits for it to become available.
    /// </summary>
    public async Task EnsureDaemonRunningAsync()
    {
        FileLog.Write("[ConnectionsView] EnsureDaemonRunningAsync: checking daemon");

        var port = GetDaemonPort();
        if (await IsDaemonReachableAsync(port))
        {
            FileLog.Write("[ConnectionsView] EnsureDaemonRunningAsync: daemon already running");
            return;
        }

        FileLog.Write("[ConnectionsView] EnsureDaemonRunningAsync: daemon not running, starting...");

        var ccBrowserPath = Path.Combine(CcStorage.Bin(), "cc-browser.cmd");

        if (!File.Exists(ccBrowserPath))
        {
            FileLog.Write($"[ConnectionsView] EnsureDaemonRunningAsync FAILED: cc-browser not found at {ccBrowserPath}");
            throw new FileNotFoundException(
                $"cc-browser not found at {ccBrowserPath}. Run the setup wizard to install tools.");
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ccBrowserPath,
            Arguments = "connections status",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null)
        {
            FileLog.Write("[ConnectionsView] EnsureDaemonRunningAsync FAILED: Process.Start returned null");
            throw new InvalidOperationException("Failed to start cc-browser process");
        }

        // Don't block on the process -- just wait for daemon to become reachable
        _ = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            port = GetDaemonPort();
            if (await IsDaemonReachableAsync(port))
            {
                FileLog.Write($"[ConnectionsView] EnsureDaemonRunningAsync: daemon started on port {port}");
                return;
            }
        }

        FileLog.Write("[ConnectionsView] EnsureDaemonRunningAsync FAILED: daemon did not start within 5 seconds");
        throw new TimeoutException("cc-browser daemon did not start within 5 seconds");
    }

    private async Task<bool> IsDaemonReachableAsync(int port)
    {
        try
        {
            var response = await _http.GetAsync($"http://127.0.0.1:{port}/connections");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // UI Helpers
    // -----------------------------------------------------------------------

    private void UpdateEmptyState()
    {
        EmptyText.IsVisible = _connections.Count == 0;
    }

    // -----------------------------------------------------------------------
    // Event Handlers
    // -----------------------------------------------------------------------

    private async void BtnAddConnection_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ConnectionsView] BtnAddConnection_Click");
        var parentWindow = TopLevel.GetTopLevel(this) as Window;
        if (parentWindow == null) return;

        var dialog = new AddConnectionDialog();
        var result = await dialog.ShowDialog<bool?>(parentWindow);
        if (result == true)
        {
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var item = new ConnectionItem
            {
                Name = dialog.ConnectionName,
                Description = dialog.ConnectionDescription,
                Url = dialog.ConnectionUrl,
                ToolBinding = dialog.ConnectionTool,
                Browser = "chrome",
                CreatedAt = now,
            };

            _connections.Add(item);
            UpdateEmptyState();

            // Save directly to file (no daemon needed)
            _ = Task.Run(() => SaveConnectionToFile(item));

            FileLog.Write($"[ConnectionsView] Connection added: {dialog.ConnectionName}");
        }
    }

    private async void BtnConnectionAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string name) return;

        var item = _connections.FirstOrDefault(c => c.Name == name);
        if (item == null) return;

        FileLog.Write($"[ConnectionsView] BtnConnectionAction_Click: name={name}, connected={item.Connected}, busy={item.Busy}");

        if (item.Busy)
        {
            FileLog.Write("[ConnectionsView] BtnConnectionAction_Click: ignored, item is busy");
            return;
        }

        if (item.Connected)
        {
            await CloseConnection(item);
        }
        else
        {
            await OpenConnection(item);
        }
    }

    private async Task OpenConnection(ConnectionItem item)
    {
        FileLog.Write($"[ConnectionsView] OpenConnection: name={item.Name}, connected={item.Connected}");

        if (item.Connected)
        {
            FileLog.Write("[ConnectionsView] OpenConnection: already connected, ignoring double-open");
            return;
        }

        item.SetBusy(true);

        try
        {
            await EnsureDaemonRunningAsync();

            var port = GetDaemonPort();
            var payload = JsonSerializer.Serialize(new { name = item.Name });
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"http://127.0.0.1:{port}/connections/open", content);
            var json = await response.Content.ReadAsStringAsync();

            FileLog.Write($"[ConnectionsView] OpenConnection: daemon response status={response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                FileLog.Write($"[ConnectionsView] OpenConnection FAILED: {json}");
                item.SetBusy(false);
                await ShowError($"Failed to open connection \"{item.Name}\".\n\n{json}");
                return;
            }

            var result = JsonSerializer.Deserialize<JsonElement>(json);
            if (result.TryGetProperty("pid", out var pidEl))
                item.ChromePid = pidEl.GetInt32();

            item.Connected = true;
            item.SetBusy(false);
            FileLog.Write($"[ConnectionsView] OpenConnection: opened pid={item.ChromePid}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] OpenConnection FAILED: {ex.Message}");
            item.SetBusy(false);
            await ShowError($"Failed to open connection \"{item.Name}\".\n\n{ex.Message}");
        }
    }

    private async Task CloseConnection(ConnectionItem item)
    {
        FileLog.Write($"[ConnectionsView] CloseConnection: name={item.Name}, pid={item.ChromePid}");
        item.SetBusy(true);

        try
        {
            var port = GetDaemonPort();
            var payload = JsonSerializer.Serialize(new { name = item.Name });
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"http://127.0.0.1:{port}/connections/close", content);
            FileLog.Write($"[ConnectionsView] CloseConnection: daemon response status={response.StatusCode}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] CloseConnection: daemon call FAILED: {ex.Message}");
        }

        item.ChromePid = null;
        item.Connected = false;
        item.SetBusy(false);
    }

    private async void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string name) return;

        var item = _connections.FirstOrDefault(c => c.Name == name);
        if (item == null) return;

        FileLog.Write($"[ConnectionsView] BtnDelete_Click: {item.Name}");

        var parentWindow = TopLevel.GetTopLevel(this) as Window;
        if (parentWindow == null) return;

        var confirmDialog = new InputDialog("Confirm Delete",
            $"Type \"{item.Name}\" to confirm deletion:", "");
        var confirmed = await confirmDialog.ShowDialog<bool?>(parentWindow);

        if (confirmed != true || confirmDialog.InputText != item.Name)
        {
            FileLog.Write("[ConnectionsView] BtnDelete_Click: user cancelled or name mismatch");
            return;
        }

        // Close if connected
        if (item.Connected)
            await CloseConnection(item);

        // Remove from file directly
        _ = Task.Run(() => DeleteConnectionFromFile(item.Name));

        _connections.Remove(item);
        UpdateEmptyState();
        FileLog.Write($"[ConnectionsView] Connection deleted: {item.Name}");
    }

    private void DeleteConnectionFromFile(string name)
    {
        FileLog.Write($"[ConnectionsView] DeleteConnectionFromFile: {name}");
        try
        {
            var registryPath = CcStorage.ConnectionsRegistry();
            if (!File.Exists(registryPath)) return;

            var json = File.ReadAllText(registryPath);
            var items = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? new();
            var filtered = items.Where(i =>
                i.TryGetProperty("name", out var n) && n.GetString() != name).ToList();
            File.WriteAllText(registryPath, JsonSerializer.Serialize(filtered,
                new JsonSerializerOptions { WriteIndented = true }));

            FileLog.Write($"[ConnectionsView] Deleted from file: {name}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectionsView] DeleteConnectionFromFile FAILED: {ex.Message}");
        }
    }

    private async Task ShowError(string message)
    {
        var parentWindow = TopLevel.GetTopLevel(this) as Window;
        if (parentWindow == null) return;

        var dialog = new Window
        {
            Title = "Connection Error",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#252526")),
            Content = new StackPanel
            {
                Margin = new global::Avalonia.Thickness(16),
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
                        FontSize = 12,
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                        Margin = new global::Avalonia.Thickness(0, 0, 0, 16),
                    },
                    new Button
                    {
                        Content = "OK",
                        Width = 80,
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                        Background = new SolidColorBrush(Color.Parse("#007ACC")),
                        Foreground = new SolidColorBrush(Colors.White),
                        BorderThickness = new global::Avalonia.Thickness(0),
                    }
                }
            }
        };

        // Wire OK button to close
        var panel = (StackPanel)dialog.Content;
        var okButton = (Button)panel.Children[1];
        okButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(parentWindow);
    }
}
