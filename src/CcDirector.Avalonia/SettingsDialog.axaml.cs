using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CcDirector.Core.Configuration;
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

    // Loaded values, so Save only writes fields the user actually changed.
    private string _loadedScreenshots = "";
    private string _loadedGatewayUrl = "";
    private string _loadedGatewayAdvertised = "";
    private string _loadedGatewayToken = "";

    public SettingsDialog() : this(null) { }

    public SettingsDialog(Func<Task>? reapplyGateway)
    {
        FileLog.Write("[SettingsDialog] Constructor: initializing");
        _reapplyGateway = reapplyGateway;
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

            ScreenshotsDirBox.Text = screenshots;
            GatewayUrlBox.Text = url;
            GatewayAdvertisedBox.Text = advertised;
            GatewayTokenBox.Text = token;
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

            if (screenshots != _loadedScreenshots)
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

            if (patch.Count == 0)
            {
                StatusText.Text = "No changes to save.";
                SaveButton.IsEnabled = true;
                return;
            }

            await Task.Run(() => CcDirectorConfigService.MergePatch(patch));
            FileLog.Write($"[SettingsDialog] BtnSave_Click: saved sections={patch.Count}, gatewayChanged={gatewayChanged}");

            // Re-register with the gateway live so a URL/endpoint/token change takes effect now.
            if (gatewayChanged && _reapplyGateway is not null)
            {
                await _reapplyGateway();
                FileLog.Write("[SettingsDialog] BtnSave_Click: gateway re-applied");
            }

            // Update the loaded baseline + raw view to reflect what's now on disk.
            _loadedScreenshots = screenshots;
            _loadedGatewayUrl = url;
            _loadedGatewayAdvertised = advertised;
            _loadedGatewayToken = token;
            RawConfigBox.Text = await Task.Run(() =>
                CcDirectorConfigService.ReadRaw().ToJsonString(
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            StatusText.Text = gatewayChanged
                ? "Saved. Gateway re-applied."
                : "Saved.";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnSave_Click FAILED: {ex.Message}");
            StatusText.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnClose_Click: closing");
        Close();
    }
}
