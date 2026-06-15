using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using CcDirector.Core.Storage;
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
    private readonly ToolDetectionService _toolDetector = new();

    // Loaded values, so Save only writes fields the user actually changed.
    private string _loadedScreenshots = "";
    private string _loadedGatewayUrl = "";
    private string _loadedGatewayAdvertised = "";
    private string _loadedGatewayToken = "";
    private string _loadedClaudePath = "";
    private string _loadedPiPath = "";
    private string _loadedCodexPath = "";
    private string _loadedGeminiPath = "";
    private string _loadedOpenCodePath = "";
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
            var (screenshots, url, advertised, token, claudePath, piPath, codexPath, geminiPath, openCodePath) = await Task.Run(ReadConfigSnapshot);

            _loadedScreenshots = screenshots;
            _loadedGatewayUrl = url;
            _loadedGatewayAdvertised = advertised;
            _loadedGatewayToken = token;
            _loadedClaudePath = claudePath;
            _loadedPiPath = piPath;
            _loadedCodexPath = codexPath;
            _loadedGeminiPath = geminiPath;
            _loadedOpenCodePath = openCodePath;
            _loadedAlpha = AlphaMode.IsEnabled;

            ScreenshotsDirBox.Text = screenshots;
            GatewayUrlBox.Text = url;
            GatewayAdvertisedBox.Text = advertised;
            GatewayTokenBox.Text = token;
            ClaudePathBox.Text = claudePath;
            PiPathBox.Text = piPath;
            CodexPathBox.Text = codexPath;
            GeminiPathBox.Text = geminiPath;
            OpenCodePathBox.Text = openCodePath;
            AlphaFeaturesCheck.IsChecked = _loadedAlpha;

            LoadToolPresets();

            LoadingText.IsVisible = false;
            SettingsTabs.IsVisible = true;
            FileLog.Write("[SettingsDialog] LoadAsync: loaded");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] LoadAsync FAILED: {ex.Message}");
            LoadingText.Text = $"Failed to read config.json: {ex.Message}";
            LoadingText.Foreground = global::Avalonia.Media.Brushes.IndianRed;
        }
    }

    /// <summary>Read config off the UI thread. Returns the current field values.</summary>
    private static (string Screenshots, string Url, string Advertised, string Token, string ClaudePath, string PiPath, string CodexPath, string GeminiPath, string OpenCodePath) ReadConfigSnapshot()
    {
        var root = CcDirectorConfigService.ReadRaw();
        var gateway = root["gateway"] as JsonObject;
        var agent = root["agent"] as JsonObject ?? root["Agent"] as JsonObject;
        var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options
            ?? (global::Avalonia.Application.Current as App)?.Options
            ?? new AgentOptions();

        string Get(JsonObject? obj, string key) =>
            obj?[key] is JsonNode n && n is JsonValue ? n.GetValue<string>() : "";
        string GetTool(string snakeKey, string pascalKey, string fallback) =>
            Get(agent, snakeKey).Length > 0 ? Get(agent, snakeKey)
            : Get(agent, pascalKey).Length > 0 ? Get(agent, pascalKey)
            : fallback;

        var screenshots = Get(root["screenshots"] as JsonObject, "source_directory");
        var url = Get(gateway, "url");
        var advertised = Get(gateway, "tailnetEndpoint");
        var token = Get(gateway, "token");
        var claudePath = GetTool("claude_path", "ClaudePath", options.ClaudePath);
        var piPath = GetTool("pi_path", "PiPath", options.PiPath);
        var codexPath = GetTool("codex_path", "CodexPath", options.CodexPath);
        var geminiPath = GetTool("gemini_path", "GeminiPath", options.GeminiPath);
        var openCodePath = GetTool("opencode_path", "OpenCodePath", options.OpenCodePath);

        return (screenshots, url, advertised, token, claudePath, piPath, codexPath, geminiPath, openCodePath);
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
            var claudePath = ClaudePathBox.Text?.Trim() ?? "";
            var piPath = PiPathBox.Text?.Trim() ?? "";
            var codexPath = CodexPathBox.Text?.Trim() ?? "";
            var geminiPath = GeminiPathBox.Text?.Trim() ?? "";
            var openCodePath = OpenCodePathBox.Text?.Trim() ?? "";

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

            var toolsChanged = claudePath != _loadedClaudePath
                || piPath != _loadedPiPath
                || codexPath != _loadedCodexPath
                || geminiPath != _loadedGeminiPath
                || openCodePath != _loadedOpenCodePath;
            if (toolsChanged)
            {
                patch["agent"] = new JsonObject
                {
                    ["claude_path"] = claudePath,
                    ["pi_path"] = piPath,
                    ["codex_path"] = codexPath,
                    ["gemini_path"] = geminiPath,
                    ["opencode_path"] = openCodePath,
                };
            }

            var alpha = AlphaFeaturesCheck.IsChecked == true;
            var alphaChanged = alpha != _loadedAlpha;

            // Per-tool presets/model/enabled are persisted separately to config.json
            // (agent.tools.<key>) - a machine-level write, never a gateway call. The snapshot
            // is read on the UI thread; the disk write runs off it.
            var presetSnapshot = SnapshotToolPresets();
            var presetsChanged = await Task.Run(() => PersistToolPresets(presetSnapshot));

            if (patch.Count == 0 && !alphaChanged && !presetsChanged)
            {
                // Nothing changed - "Save and Close" just closes, same as Cancel.
                FileLog.Write("[SettingsDialog] BtnSave_Click: no changes; closing");
                Close();
                return;
            }

            // A Claude preset/model change must take effect for the next session without a
            // restart, so recompute the running Claude default args from the saved config.
            if (presetsChanged)
                ApplyClaudePresetToRunningOptions();

            if (patch.Count > 0)
                await Task.Run(() => CcDirectorConfigService.MergePatch(patch));

            // Persist the alpha flag and notify long-lived windows (MainWindow re-gates its
            // alpha buttons via AlphaMode.Changed). Persisted off the UI thread; the Changed
            // handler in MainWindow posts back to the UI thread itself.
            if (alphaChanged)
                await Task.Run(() => AlphaMode.SetEnabled(alpha));

            if (toolsChanged)
                ApplyToolPathsToRunningOptions(claudePath, piPath, codexPath, geminiPath, openCodePath);

            FileLog.Write($"[SettingsDialog] BtnSave_Click: saved sections={patch.Count}, gatewayChanged={gatewayChanged}, toolsChanged={toolsChanged}, alphaChanged={alphaChanged}");

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
            _loadedClaudePath = claudePath;
            _loadedPiPath = piPath;
            _loadedCodexPath = codexPath;
            _loadedGeminiPath = geminiPath;
            _loadedOpenCodePath = openCodePath;
            _loadedAlpha = alpha;

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

    private async void BtnDetectClaude_Click(object? sender, RoutedEventArgs e) =>
        await DetectToolAsync(AgentKind.ClaudeCode, ClaudePathBox, ClaudeStatus, DetectClaudeButton);

    private async void BtnDetectPi_Click(object? sender, RoutedEventArgs e) =>
        await DetectToolAsync(AgentKind.Pi, PiPathBox, PiStatus, DetectPiButton);

    private async void BtnDetectCodex_Click(object? sender, RoutedEventArgs e) =>
        await DetectToolAsync(AgentKind.Codex, CodexPathBox, CodexStatus, DetectCodexButton);

    private async void BtnDetectGemini_Click(object? sender, RoutedEventArgs e) =>
        await DetectToolAsync(AgentKind.Gemini, GeminiPathBox, GeminiStatus, DetectGeminiButton);

    private async void BtnDetectOpenCode_Click(object? sender, RoutedEventArgs e) =>
        await DetectToolAsync(AgentKind.OpenCode, OpenCodePathBox, OpenCodeStatus, DetectOpenCodeButton);

    private async void BtnTestClaude_Click(object? sender, RoutedEventArgs e) =>
        await TestToolAsync(AgentKind.ClaudeCode, ClaudePathBox, ClaudeStatus, QuickCheckClaudeButton);

    private async void BtnTestPi_Click(object? sender, RoutedEventArgs e) =>
        await TestToolAsync(AgentKind.Pi, PiPathBox, PiStatus, QuickCheckPiButton);

    private async void BtnTestCodex_Click(object? sender, RoutedEventArgs e) =>
        await TestToolAsync(AgentKind.Codex, CodexPathBox, CodexStatus, QuickCheckCodexButton);

    private async void BtnTestGemini_Click(object? sender, RoutedEventArgs e) =>
        await TestToolAsync(AgentKind.Gemini, GeminiPathBox, GeminiStatus, QuickCheckGeminiButton);

    private async void BtnTestOpenCode_Click(object? sender, RoutedEventArgs e) =>
        await TestToolAsync(AgentKind.OpenCode, OpenCodePathBox, OpenCodeStatus, QuickCheckOpenCodeButton);

    private async void BtnBrowseClaude_Click(object? sender, RoutedEventArgs e) =>
        await BrowseToolAsync("Select Claude Code executable", ClaudePathBox, ClaudeStatus);

    private async void BtnBrowsePi_Click(object? sender, RoutedEventArgs e) =>
        await BrowseToolAsync("Select Pi executable", PiPathBox, PiStatus);

    private async void BtnBrowseCodex_Click(object? sender, RoutedEventArgs e) =>
        await BrowseToolAsync("Select Codex executable", CodexPathBox, CodexStatus);

    private async void BtnBrowseGemini_Click(object? sender, RoutedEventArgs e) =>
        await BrowseToolAsync("Select Gemini executable", GeminiPathBox, GeminiStatus);

    private async void BtnBrowseOpenCode_Click(object? sender, RoutedEventArgs e) =>
        await BrowseToolAsync("Select OpenCode executable", OpenCodePathBox, OpenCodeStatus);

    private async void BtnLaunchPreviewClaude_Click(object? sender, RoutedEventArgs e) =>
        await LaunchPreviewAsync(AgentKind.ClaudeCode, ClaudeStatus);

    private async void BtnLaunchPreviewPi_Click(object? sender, RoutedEventArgs e) =>
        await LaunchPreviewAsync(AgentKind.Pi, PiStatus);

    private async void BtnLaunchPreviewCodex_Click(object? sender, RoutedEventArgs e) =>
        await LaunchPreviewAsync(AgentKind.Codex, CodexStatus);

    private async void BtnLaunchPreviewGemini_Click(object? sender, RoutedEventArgs e) =>
        await LaunchPreviewAsync(AgentKind.Gemini, GeminiStatus);

    private async void BtnLaunchPreviewOpenCode_Click(object? sender, RoutedEventArgs e) =>
        await LaunchPreviewAsync(AgentKind.OpenCode, OpenCodeStatus);

    /// <summary>
    /// Open the throwaway Launch preview popup (issue #436) for one tool: start the agent with the
    /// exact resolved command line from that card in a disposable ConPTY terminal so the user can
    /// watch it boot. The session is never saved and never added to the session roster - it lives
    /// and dies inside <see cref="LaunchPreviewDialog"/>.
    /// </summary>
    private async Task LaunchPreviewAsync(AgentKind tool, TextBlock status)
    {
        FileLog.Write($"[SettingsDialog] LaunchPreviewAsync: tool={tool}");
        var card = PresetControls().First(c => c.Tool == tool);

        var exe = card.Path.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(exe))
            exe = ToolDetectionService.GetConfiguredPath(tool, CurrentOptions());
        if (string.IsNullOrWhiteSpace(exe))
        {
            ShowToolStatus(status, "Set the executable path first, then try Launch preview again.", error: true);
            return;
        }

        var config = new AgentToolConfig
        {
            Tool = tool,
            PresetName = card.Preset.SelectedItem as string ?? "",
            DefaultModel = card.Model.Text?.Trim() ?? "",
            ArgsOverride = card.Override.Text?.Trim() ?? "",
        };
        var args = config.ResolveEffectiveCommandLineArguments();
        var workingDir = CurrentOptions().ChatSessionRepoPath ?? Environment.CurrentDirectory;

        var dialog = new LaunchPreviewDialog(exe, args, workingDir, ToolDetectionService.DisplayName(tool));
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Re-run the first-run tool-detection wizard on demand (issue #392). Opens the same wizard
    /// that auto-opens on a fresh machine; on accept it writes the selected tools to config.json,
    /// so we reload this page's path boxes and presets to show the newly-added tools.
    /// </summary>
    private async void BtnRunWizard_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnRunWizard_Click");
        RunWizardButton.IsEnabled = false;
        try
        {
            var options = CurrentOptions();
            var dialog = new ToolDetectionWizardDialog(options);
            var accepted = await dialog.ShowDialog<bool?>(this);
            if (accepted == true)
            {
                await LoadAsync();
                ShowAgentToolsStatus("Detection wizard finished. The tools it added are shown above.", error: false);
                FileLog.Write("[SettingsDialog] BtnRunWizard_Click: wizard accepted; reloaded page");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnRunWizard_Click FAILED: {ex.Message}");
            ShowAgentToolsStatus($"Detection wizard failed: {ex.Message}", error: true);
        }
        finally
        {
            RunWizardButton.IsEnabled = true;
        }
    }

    private async void BtnDetectAllTools_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnDetectAllTools_Click");
        DetectAllToolsButton.IsEnabled = false;
        ShowAgentToolsStatus("Detecting all agent tools...", error: false);
        try
        {
            await DetectToolAsync(AgentKind.ClaudeCode, ClaudePathBox, ClaudeStatus, DetectClaudeButton);
            await DetectToolAsync(AgentKind.Pi, PiPathBox, PiStatus, DetectPiButton);
            await DetectToolAsync(AgentKind.Codex, CodexPathBox, CodexStatus, DetectCodexButton);
            await DetectToolAsync(AgentKind.Gemini, GeminiPathBox, GeminiStatus, DetectGeminiButton);
            await DetectToolAsync(AgentKind.OpenCode, OpenCodePathBox, OpenCodeStatus, DetectOpenCodeButton);
            ShowAgentToolsStatus("Detect All finished. Click Test All to validate working CLIs, then Save.", error: false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnDetectAllTools_Click FAILED: {ex.Message}");
            ShowAgentToolsStatus($"Detect All failed: {ex.Message}", error: true);
        }
        finally
        {
            DetectAllToolsButton.IsEnabled = true;
        }
    }

    private async void BtnTestAllTools_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnTestAllTools_Click");
        TestAllToolsButton.IsEnabled = false;
        ShowAgentToolsStatus("Testing all configured agent tools...", error: false);
        try
        {
            var results = new[]
            {
                await TestToolAsync(AgentKind.ClaudeCode, ClaudePathBox, ClaudeStatus, QuickCheckClaudeButton),
                await TestToolAsync(AgentKind.Pi, PiPathBox, PiStatus, QuickCheckPiButton),
                await TestToolAsync(AgentKind.Codex, CodexPathBox, CodexStatus, QuickCheckCodexButton),
                await TestToolAsync(AgentKind.Gemini, GeminiPathBox, GeminiStatus, QuickCheckGeminiButton),
                await TestToolAsync(AgentKind.OpenCode, OpenCodePathBox, OpenCodeStatus, QuickCheckOpenCodeButton),
            };

            var ok = results.Count(r => r.Ok);
            ShowAgentToolsStatus($"Test All finished: {ok}/{results.Length} agent CLI(s) validated. Save to make validated tools available in New Session.", error: ok == 0);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnTestAllTools_Click FAILED: {ex.Message}");
            ShowAgentToolsStatus($"Test All failed: {ex.Message}", error: true);
        }
        finally
        {
            TestAllToolsButton.IsEnabled = true;
        }
    }

    private async Task DetectToolAsync(AgentKind tool, TextBox box, TextBlock status, Button button)
    {
        FileLog.Write($"[SettingsDialog] DetectToolAsync: tool={tool}");
        var options = CurrentOptions();
        button.IsEnabled = false;
        ShowToolStatus(status, "Detecting...", error: false);
        try
        {
            var typedPath = box.Text?.Trim();
            var result = await Task.Run(() => _toolDetector.DetectTool(tool, options, typedPath));
            if (result.ResolvedPath is not null)
            {
                box.Text = result.ResolvedPath;
                RefreshAllPreviewStrips();
            }
            ShowToolStatus(status, $"{result.Message} Source: {result.Source}.", error: !result.Found);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] DetectToolAsync FAILED: tool={tool}, error={ex.Message}");
            ShowToolStatus(status, $"Detection failed: {ex.Message}", error: true);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task<ToolTestResult> TestToolAsync(AgentKind tool, TextBox box, TextBlock status, Button button)
    {
        FileLog.Write($"[SettingsDialog] TestToolAsync: tool={tool}");
        button.IsEnabled = false;
        ShowToolStatus(status, "Testing...", error: false);
        try
        {
            var result = await _toolDetector.TestToolAsync(tool, box.Text?.Trim() ?? "");
            ShowToolStatus(status, result.Message, error: !result.Ok);
            await Task.Run(() => CcDirectorConfigService.MergePatch(ToolDetectionService.BuildValidationPatch(result)));
            FileLog.Write($"[SettingsDialog] TestToolAsync: persisted validation tool={tool}, ok={result.Ok}");
            return result;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] TestToolAsync FAILED: tool={tool}, error={ex.Message}");
            var result = new ToolTestResult(tool, ToolDetectionService.DisplayName(tool), false, box.Text?.Trim() ?? "", null, $"Test failed: {ex.Message}");
            ShowToolStatus(status, result.Message, error: true);
            return result;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task BrowseToolAsync(string title, TextBox box, TextBlock status)
    {
        FileLog.Write($"[SettingsDialog] BrowseToolAsync: title={title}");
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Executables")
                    {
                        Patterns = OperatingSystem.IsWindows()
                            ? new[] { "*.exe", "*.cmd", "*.bat" }
                            : new[] { "*" }
                    }
                }
            });
            if (files.Count == 0)
                return;

            box.Text = files[0].Path.LocalPath;
            RefreshAllPreviewStrips();
            ShowToolStatus(status, $"Selected {box.Text}. Click Quick check to verify or Save to apply.", error: false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BrowseToolAsync FAILED: {ex.Message}");
            ShowToolStatus(status, $"Browse failed: {ex.Message}", error: true);
        }
    }

    private void ShowToolStatus(TextBlock status, string text, bool error)
    {
        status.Text = text;
        status.IsVisible = true;
        status.Foreground = error
            ? global::Avalonia.Media.Brushes.IndianRed
            : global::Avalonia.Media.Brushes.MediumSeaGreen;
    }

    private void ShowAgentToolsStatus(string text, bool error)
    {
        AgentToolsStatus.Text = text;
        AgentToolsStatus.IsVisible = true;
        AgentToolsStatus.Foreground = error
            ? global::Avalonia.Media.Brushes.IndianRed
            : global::Avalonia.Media.Brushes.MediumSeaGreen;
    }

    private static AgentOptions CurrentOptions()
    {
        var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options
            ?? (global::Avalonia.Application.Current as App)?.Options;
        return options ?? throw new InvalidOperationException("AgentOptions not loaded.");
    }

    private static void ApplyToolPathsToRunningOptions(string claudePath, string piPath, string codexPath, string geminiPath, string openCodePath)
    {
        FileLog.Write("[SettingsDialog] ApplyToolPathsToRunningOptions");
        var options = CurrentOptions();
        ToolDetectionService.SetConfiguredPath(AgentKind.ClaudeCode, options, claudePath);
        ToolDetectionService.SetConfiguredPath(AgentKind.Pi, options, piPath);
        ToolDetectionService.SetConfiguredPath(AgentKind.Codex, options, codexPath);
        ToolDetectionService.SetConfiguredPath(AgentKind.Gemini, options, geminiPath);
        ToolDetectionService.SetConfiguredPath(AgentKind.OpenCode, options, openCodePath);
    }

    /// <summary>The control set for one tool's preset/model/enabled/override editors plus its
    /// path box (used by Launch preview) and the read-only "what launches" preview strip.</summary>
    private readonly record struct ToolPresetControls(
        AgentKind Tool, ComboBox Preset, TextBox Model, TextBox Override, CheckBox Enabled,
        TextBox Path, TextBox PreviewStrip);

    private ToolPresetControls[] PresetControls() => new[]
    {
        new ToolPresetControls(AgentKind.ClaudeCode, ClaudePresetCombo, ClaudeModelBox, ClaudeArgsOverrideBox, ClaudeEnabledCheck, ClaudePathBox, ClaudePreviewStrip),
        new ToolPresetControls(AgentKind.Pi, PiPresetCombo, PiModelBox, PiArgsOverrideBox, PiEnabledCheck, PiPathBox, PiPreviewStrip),
        new ToolPresetControls(AgentKind.Codex, CodexPresetCombo, CodexModelBox, CodexArgsOverrideBox, CodexEnabledCheck, CodexPathBox, CodexPreviewStrip),
        new ToolPresetControls(AgentKind.Gemini, GeminiPresetCombo, GeminiModelBox, GeminiArgsOverrideBox, GeminiEnabledCheck, GeminiPathBox, GeminiPreviewStrip),
        new ToolPresetControls(AgentKind.OpenCode, OpenCodePresetCombo, OpenCodeModelBox, OpenCodeArgsOverrideBox, OpenCodeEnabledCheck, OpenCodePathBox, OpenCodePreviewStrip),
    };

    /// <summary>
    /// Populate each tool section's command-line preset list (from the built-in catalog), and
    /// fill the selected preset, default model, args override, and enabled flag from the
    /// machine-level per-tool config in config.json (issue #391). A tool never configured shows
    /// the catalog default - for Claude that is now the Automatic (skip permissions) preset
    /// (issue #436, supersedes #391). Finally refresh every "what launches" preview strip.
    /// </summary>
    private void LoadToolPresets()
    {
        FileLog.Write("[SettingsDialog] LoadToolPresets");
        foreach (var c in PresetControls())
        {
            var entry = AgentToolCatalog.GetEntry(c.Tool);
            var presetNames = entry.Presets.Select(p => p.Name).ToList();
            c.Preset.ItemsSource = presetNames;

            var config = AgentToolConfig.Load(c.Tool);
            var index = presetNames.FindIndex(n => string.Equals(n, config.PresetName, StringComparison.OrdinalIgnoreCase));
            c.Preset.SelectedIndex = index >= 0 ? index : 0;
            c.Model.Text = config.DefaultModel;
            c.Override.Text = config.ArgsOverride;
            c.Enabled.IsChecked = config.Enabled;
        }

        RefreshAllPreviewStrips();
    }

    /// <summary>
    /// Live handler wired to every tool card's default-model box and advanced override box. Either
    /// changing re-renders that card's "what launches" preview strip instantly, with no save/close
    /// (issue #436). It refreshes ALL strips because the cost is trivial.
    /// </summary>
    private void ToolPreviewInput_Changed(object? sender, TextChangedEventArgs e) => RefreshAllPreviewStrips();

    /// <summary>
    /// Live handler wired to every tool card's command-line preset dropdown. Changing the preset
    /// re-renders that card's "what launches" preview strip instantly, with no save/close (issue
    /// #436).
    /// </summary>
    private void ToolPreviewInput_Changed(object? sender, SelectionChangedEventArgs e) => RefreshAllPreviewStrips();

    /// <summary>Recompute and set every tool card's "what launches" preview strip text.</summary>
    private void RefreshAllPreviewStrips()
    {
        foreach (var c in PresetControls())
            c.PreviewStrip.Text = BuildPreviewCommandLine(c);
    }

    /// <summary>
    /// Compose the fully-resolved command line a real launch would use for one tool card from its
    /// LIVE editor state - exe path plus the effective preset/override arguments and the composed
    /// <c>--model</c> flag. The argument composition is delegated to the shared
    /// <see cref="AgentToolConfig.ResolveEffectiveCommandLineArguments"/> so the preview matches
    /// exactly what App startup launches with (issue #436).
    /// </summary>
    private static string BuildPreviewCommandLine(ToolPresetControls c)
    {
        var config = new AgentToolConfig
        {
            Tool = c.Tool,
            PresetName = c.Preset.SelectedItem as string ?? "",
            DefaultModel = c.Model.Text?.Trim() ?? "",
            ArgsOverride = c.Override.Text?.Trim() ?? "",
        };

        var exe = c.Path.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(exe))
            exe = ToolDetectionService.DisplayName(c.Tool).ToLowerInvariant();

        var args = config.ResolveEffectiveCommandLineArguments();
        return string.IsNullOrEmpty(args) ? exe : $"{exe} {args}";
    }

    /// <summary>
    /// Read the per-tool editor controls into a list of <see cref="AgentToolConfig"/>. Must run on
    /// the UI thread (it reads Avalonia control state); the actual disk write is done off-thread by
    /// <see cref="PersistToolPresets"/>.
    /// </summary>
    private List<AgentToolConfig> SnapshotToolPresets()
    {
        var snapshot = new List<AgentToolConfig>();
        foreach (var c in PresetControls())
        {
            snapshot.Add(new AgentToolConfig
            {
                Tool = c.Tool,
                PresetName = c.Preset.SelectedItem as string ?? "",
                DefaultModel = c.Model.Text?.Trim() ?? "",
                ArgsOverride = c.Override.Text?.Trim() ?? "",
                Enabled = c.Enabled.IsChecked == true,
            });
        }

        return snapshot;
    }

    /// <summary>
    /// Persist each tool's snapshot to the machine-level config.json (no gateway call). Returns
    /// true if any tool's persisted config changed from what was on disk. Runs off the UI thread;
    /// the snapshot is taken on the UI thread by <see cref="SnapshotToolPresets"/>.
    /// </summary>
    private static bool PersistToolPresets(List<AgentToolConfig> snapshot)
    {
        FileLog.Write("[SettingsDialog] PersistToolPresets");
        var anyChanged = false;
        foreach (var desired in snapshot)
        {
            var loaded = AgentToolConfig.Load(desired.Tool);
            var presetName = string.IsNullOrEmpty(desired.PresetName) ? loaded.PresetName : desired.PresetName;

            var changed = presetName != loaded.PresetName
                || desired.DefaultModel != loaded.DefaultModel
                || desired.ArgsOverride != loaded.ArgsOverride
                || desired.Enabled != loaded.Enabled;
            if (!changed)
                continue;

            new AgentToolConfig
            {
                Tool = desired.Tool,
                PresetName = presetName,
                DefaultModel = desired.DefaultModel,
                ArgsOverride = desired.ArgsOverride,
                Enabled = desired.Enabled,
            }.Save();
            anyChanged = true;
        }

        return anyChanged;
    }

    /// <summary>
    /// Recompute the running Claude default args from the just-saved Claude per-tool config so a
    /// new session launched without restart uses the configured preset and default model. Mirrors
    /// the App startup wiring (App.ApplyConfiguredToolPresets) for the live options instance.
    /// </summary>
    private static void ApplyClaudePresetToRunningOptions()
    {
        FileLog.Write("[SettingsDialog] ApplyClaudePresetToRunningOptions");
        var options = CurrentOptions();
        var config = AgentToolConfig.Load(AgentKind.ClaudeCode);
        var args = config.ResolveEffectiveCommandLineArguments();
        options.DefaultClaudeArgs = args;
        FileLog.Write($"[SettingsDialog] ApplyClaudePresetToRunningOptions: defaultArgs='{args}'");
    }

    /// <summary>
    /// Open the CC Director config.json in the OS default handler. If the file does not exist
    /// yet (nothing has been saved on this machine), report it clearly instead of crashing.
    /// </summary>
    private void BtnOpenConfig_Click(object? sender, RoutedEventArgs e)
    {
        var path = CcStorage.ConfigJson();
        FileLog.Write($"[SettingsDialog] BtnOpenConfig_Click: {path}");
        if (!File.Exists(path))
        {
            StatusText.Text = $"config.json not found yet at {path} - save a setting first to create it.";
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            { UseShellExecute = true });
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
