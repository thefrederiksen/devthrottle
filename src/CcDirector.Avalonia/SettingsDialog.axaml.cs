using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// CC Director's own settings page (distinct from the Claude Code config dialog). Edits
/// config.json - screenshots directory and gateway connection - via the single
/// round-trip-preserving writer <see cref="CcDirectorConfigService"/>, so untouched
/// sections are never dropped.
///
/// Gateway changes are applied live via the <c>reapplyGateway</c> delegate (the running
/// ControlApiHost re-registers with the gateway), so the user doesn't have to restart.
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly Func<Task>? _reapplyGateway;
    private readonly Func<Task>? _reloadScreenshots;
    private readonly int _directorPort;

    // Shared detection/test logic, identical to what the REST Control API exposes.
    private readonly SettingsDetectionService _detector = new();

    // Loaded values, so Save only writes fields the user actually changed.
    private string _loadedScreenshots = "";
    private string _loadedGatewayUrl = "";
    private string _loadedGatewayAdvertised = "";
    private string _loadedGatewayToken = "";
    private bool _loadedAlpha;

    public SettingsDialog() : this(null, 0, null) { }

    /// <param name="reapplyGateway">Re-registers the running Director with the gateway after a gateway change.</param>
    /// <param name="directorPort">This Director's live control port, used to build the advertised "public URL" on Detect.</param>
    /// <param name="reloadScreenshots">Re-points the main window's screenshots tab after the folder changes.</param>
    public SettingsDialog(Func<Task>? reapplyGateway, int directorPort, Func<Task>? reloadScreenshots)
    {
        FileLog.Write($"[SettingsDialog] Constructor: initializing (directorPort={directorPort})");
        _reapplyGateway = reapplyGateway;
        _directorPort = directorPort;
        _reloadScreenshots = reloadScreenshots;
        InitializeComponent();

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        FileLog.Write("[SettingsDialog] LoadAsync: reading config.json");
        try
        {
            var (screenshots, url, advertised, token, raw) = await Task.Run(ReadConfigSnapshot);

            _loadedScreenshots = screenshots;
            _loadedGatewayUrl = url;
            _loadedGatewayAdvertised = advertised;
            _loadedGatewayToken = token;
            _loadedAlpha = AlphaMode.IsEnabled;

            ScreenshotsDirBox.Text = screenshots;
            GatewayUrlBox.Text = url;
            GatewayAdvertisedBox.Text = advertised;
            GatewayTokenBox.Text = token;
            AlphaFeaturesCheck.IsChecked = _loadedAlpha;
            RawConfigBox.Text = raw;

            LoadingText.IsVisible = false;
            ContentPanel.IsVisible = true;
            FileLog.Write("[SettingsDialog] LoadAsync: loaded");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] LoadAsync FAILED: {ex.Message}");
            LoadingText.Text = $"Failed to read config.json: {ex.Message}";
            LoadingText.Foreground = global::Avalonia.Media.Brushes.IndianRed;
        }
    }

    /// <summary>Read config off the UI thread. Returns the current field values + pretty raw JSON.</summary>
    private static (string Screenshots, string Url, string Advertised, string Token, string Raw) ReadConfigSnapshot()
    {
        var root = CcDirectorConfigService.ReadRaw();
        var gateway = root["gateway"] as JsonObject;

        string Get(JsonObject? obj, string key) =>
            obj?[key] is JsonNode n && n is JsonValue ? n.GetValue<string>() : "";

        var screenshots = Get(root["screenshots"] as JsonObject, "source_directory");
        var url = Get(gateway, "url");
        var advertised = Get(gateway, "tailnetEndpoint");
        var token = Get(gateway, "token");
        var raw = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        return (screenshots, url, advertised, token, raw);
    }

    /// <summary>
    /// Fill the screenshots folder with the location this OS saves screenshots to (Windows
    /// Pictures\Screenshots, macOS screencapture location / Desktop). Fills the box on success;
    /// tells the user to Browse when none is found.
    /// </summary>
    private async void BtnDetectScreenshots_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnDetectScreenshots_Click");
        DetectScreenshotsButton.IsEnabled = false;
        try
        {
            var dir = (await _detector.DetectScreenshotsAsync()).Directory;
            if (string.IsNullOrEmpty(dir))
            {
                ShowScreenshotsStatus("Could not detect a screenshots folder on this machine. Use Browse to pick one.", error: true);
                return;
            }

            ScreenshotsDirBox.Text = dir;
            ShowScreenshotsStatus($"Detected {dir}. Click Save to use it.", error: false);
            FileLog.Write($"[SettingsDialog] BtnDetectScreenshots_Click: {dir}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnDetectScreenshots_Click FAILED: {ex.Message}");
            ShowScreenshotsStatus($"Detection failed: {ex.Message}", error: true);
        }
        finally
        {
            DetectScreenshotsButton.IsEnabled = true;
        }
    }

    private void ShowScreenshotsStatus(string text, bool error)
    {
        ScreenshotsStatus.Text = text;
        ScreenshotsStatus.IsVisible = true;
        ScreenshotsStatus.Foreground = error
            ? global::Avalonia.Media.Brushes.IndianRed
            : global::Avalonia.Media.Brushes.MediumSeaGreen;
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnBrowse_Click");
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select screenshots folder",
                AllowMultiple = false,
            });
            if (folders.Count > 0)
            {
                ScreenshotsDirBox.Text = folders[0].Path.LocalPath;
                FileLog.Write($"[SettingsDialog] BtnBrowse_Click: selected {ScreenshotsDirBox.Text}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnBrowse_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnSave_Click");
        SaveButton.IsEnabled = false;
        try
        {
            var screenshots = ScreenshotsDirBox.Text?.Trim() ?? "";
            var url = GatewayUrlBox.Text?.Trim() ?? "";
            var advertised = GatewayAdvertisedBox.Text?.Trim() ?? "";
            var token = GatewayTokenBox.Text?.Trim() ?? "";

            // Build a patch with ONLY the sections the user changed, so we touch nothing else.
            var patch = new JsonObject();

            var screenshotsChanged = screenshots != _loadedScreenshots;
            if (screenshotsChanged)
                patch["screenshots"] = new JsonObject { ["source_directory"] = screenshots };

            var gatewayChanged = url != _loadedGatewayUrl
                || advertised != _loadedGatewayAdvertised
                || token != _loadedGatewayToken;
            if (gatewayChanged)
            {
                patch["gateway"] = new JsonObject
                {
                    ["url"] = url,
                    ["token"] = token,
                    ["tailnetEndpoint"] = advertised,
                };
            }

            var alpha = AlphaFeaturesCheck.IsChecked == true;
            var alphaChanged = alpha != _loadedAlpha;

            if (patch.Count == 0 && !alphaChanged)
            {
                // Nothing changed - "Save and Close" just closes, same as Cancel.
                FileLog.Write("[SettingsDialog] BtnSave_Click: no changes; closing");
                Close();
                return;
            }

            if (patch.Count > 0)
                await Task.Run(() => CcDirectorConfigService.MergePatch(patch));

            // Persist the alpha flag and notify long-lived windows (MainWindow re-gates its
            // alpha buttons via AlphaMode.Changed). Persisted off the UI thread; the Changed
            // handler in MainWindow posts back to the UI thread itself.
            if (alphaChanged)
                await Task.Run(() => AlphaMode.SetEnabled(alpha));

            FileLog.Write($"[SettingsDialog] BtnSave_Click: saved sections={patch.Count}, gatewayChanged={gatewayChanged}, alphaChanged={alphaChanged}");

            // Re-register with the gateway live so a URL/endpoint/token change takes effect now.
            if (gatewayChanged && _reapplyGateway is not null)
            {
                await _reapplyGateway();
                FileLog.Write("[SettingsDialog] BtnSave_Click: gateway re-applied");
            }

            // Re-point the screenshots tab so a new folder takes effect without restarting.
            if (screenshotsChanged && _reloadScreenshots is not null)
            {
                await _reloadScreenshots();
                FileLog.Write("[SettingsDialog] BtnSave_Click: screenshots panel reloaded");
            }

            // Update the loaded baseline + raw view to reflect what's now on disk.
            _loadedScreenshots = screenshots;
            _loadedGatewayUrl = url;
            _loadedGatewayAdvertised = advertised;
            _loadedGatewayToken = token;
            _loadedAlpha = alpha;
            RawConfigBox.Text = await Task.Run(() =>
                CcDirectorConfigService.ReadRaw().ToJsonString(
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            // Saved cleanly - closing the dialog is the user's confirmation it worked.
            FileLog.Write("[SettingsDialog] BtnSave_Click: saved; closing");
            Close();
        }
        catch (Exception ex)
        {
            // On failure stay open so the user can see what went wrong and retry.
            FileLog.Write($"[SettingsDialog] BtnSave_Click FAILED: {ex.Message}");
            StatusText.Text = $"Save failed: {ex.Message}";
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Probe the entered gateway URL's /healthz and report what answered, so the user can
    /// confirm the URL is right before saving. Reachability only - it does not prove the
    /// gateway can call back to this Director (that is what the public URL is for).
    /// </summary>
    private async void BtnTestGateway_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnTestGateway_Click");
        var url = GatewayUrlBox.Text?.Trim() ?? "";
        if (url.Length == 0)
        {
            ShowGatewayStatus("Enter a gateway URL first.", error: true);
            return;
        }

        TestGatewayButton.IsEnabled = false;
        ShowGatewayStatus($"Testing {url} ...", error: false);
        try
        {
            var result = await _detector.TestGatewayAsync(url);
            ShowGatewayStatus(result.Message, error: !result.Ok);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnTestGateway_Click FAILED: {ex.Message}");
            ShowGatewayStatus($"Test failed: {ex.Message}", error: true);
        }
        finally
        {
            TestGatewayButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Find the gateway via the shared detector (Tailscale-first scan, then loopback) and fill
    /// the URL box with the first one that answers like a gateway.
    /// </summary>
    private async void BtnDetectGateway_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnDetectGateway_Click");
        DetectGatewayButton.IsEnabled = false;
        ShowGatewayStatus("Scanning the tailnet and this machine for a gateway ...", error: false);
        try
        {
            var result = await _detector.DetectGatewayAsync();
            if (result.Url is not null)
            {
                GatewayUrlBox.Text = result.Url;
                ShowGatewayStatus($"Found a gateway at {result.Url}. Click Save to connect.", error: false);
            }
            else
            {
                ShowGatewayStatus($"No gateway answered on any of the {result.Scanned.Count} address(es) scanned. Enter the gateway URL above.", error: true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnDetectGateway_Click FAILED: {ex.Message}");
            ShowGatewayStatus($"Detection failed: {ex.Message}", error: true);
        }
        finally
        {
            DetectGatewayButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Fill the Director public URL from the shared detector (Tailscale MagicDNS name + control
    /// port, else best local IP). This is the field a remote gateway calls back to.
    /// </summary>
    private async void BtnDetectPublicUrl_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnDetectPublicUrl_Click");
        DetectPublicUrlButton.IsEnabled = false;
        ShowGatewayStatus("Detecting this machine's address ...", error: false);
        try
        {
            var result = await _detector.DetectPublicUrlAsync(_directorPort);
            if (result.Url is not null)
            {
                GatewayAdvertisedBox.Text = result.Url;
                ShowGatewayStatus($"Detected {result.Url} ({result.Kind}). Click Save to apply.", error: false);
            }
            else if (_directorPort <= 0)
            {
                ShowGatewayStatus("This Director's control port is not known yet; cannot detect the public URL.", error: true);
            }
            else
            {
                ShowGatewayStatus("No Tailscale identity or reachable network address found. Enter the public URL manually.", error: true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnDetectPublicUrl_Click FAILED: {ex.Message}");
            ShowGatewayStatus($"Detection failed: {ex.Message}", error: true);
        }
        finally
        {
            DetectPublicUrlButton.IsEnabled = true;
        }
    }

    private void ShowGatewayStatus(string text, bool error)
    {
        GatewayTestStatus.Text = text;
        GatewayTestStatus.IsVisible = true;
        GatewayTestStatus.Foreground = error
            ? global::Avalonia.Media.Brushes.IndianRed
            : global::Avalonia.Media.Brushes.MediumSeaGreen;
    }

    /// <summary>The alpha-gated wake-word section follows the checkbox live (before Save).</summary>
    private void AlphaCheck_Changed(object? sender, RoutedEventArgs e)
    {
        AlphaVoicePanel.IsVisible = AlphaFeaturesCheck.IsChecked == true;
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnClose_Click: closing");
        Close();
    }

    /// <summary>
    /// Open the wake-word grammar sandbox. Resolves AgentOptions from the running App
    /// (same path the Wingman Speak button uses) so the test dialog can build a
    /// SpeakService. If no OpenAI key is configured the test dialog itself reports it.
    /// </summary>
    private async void BtnWakeWordTest_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnWakeWordTest_Click");
        try
        {
            var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options;
            if (options is null)
            {
                StatusText.Text = "Wake-word test not available: AgentOptions not loaded.";
                return;
            }
            var dlg = new global::CcDirector.Avalonia.Voice.WakeWordTestDialog(options);
            await dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnWakeWordTest_Click FAILED: {ex.Message}");
            StatusText.Text = $"Could not open wake-word test: {ex.Message}";
        }
    }
}
