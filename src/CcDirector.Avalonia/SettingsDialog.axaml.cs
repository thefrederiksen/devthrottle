using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CcDirector.Core.Account;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Onboarding;
using CcDirector.Core.Settings;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// CC Director's own settings page (distinct from the Claude Code config dialog). Edits
/// config.json - screenshots directory, gateway connection, and the user-defined ordered
/// agent list (issue #489) - via the single round-trip-preserving writer
/// <see cref="CcDirectorConfigService"/>, so untouched sections are never dropped.
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

    // The user-defined ordered agent list (issue #489) bound to the CRUD list. The baseline
    // snapshot lets Save detect whether the list changed at all.
    private readonly ObservableCollection<AgentEntryRow> _agentRows = new();
    private string _loadedAgentsSnapshot = "";

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

        AgentEntriesList.ItemsSource = _agentRows;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        FileLog.Write("[SettingsDialog] LoadAsync: reading config.json");
        try
        {
            var (screenshots, url, advertised, token) = await Task.Run(ReadConfigSnapshot);

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

            // Read-only Account tab (issue #651): the account is managed by the Gateway, so show the
            // configured Gateway URL immediately and fetch the signed-in identity from the Gateway's
            // /account/status in the background (no local sign-in, no logout, no consent toggle here).
            ShowGatewayManaged(url, token);

            LoadAgentEntries();

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

    /// <summary>Read config off the UI thread. Returns the current screenshots/gateway field values.</summary>
    private static (string Screenshots, string Url, string Advertised, string Token) ReadConfigSnapshot()
    {
        var root = CcDirectorConfigService.ReadRaw();
        var gateway = root["gateway"] as JsonObject;

        string Get(JsonObject? obj, string key) =>
            obj?[key] is JsonNode n && n is JsonValue ? n.GetValue<string>() : "";

        var screenshots = Get(root["screenshots"] as JsonObject, "source_directory");
        var url = Get(gateway, "url");
        var advertised = Get(gateway, "tailnetEndpoint");
        var token = Get(gateway, "token");

        return (screenshots, url, advertised, token);
    }

    /// <summary>
    /// Show the read-only gateway connection + identity on the Account tab (issue #651). The configured
    /// Gateway URL (or an explicit "not configured" state) is rendered immediately so the panel is
    /// responsive; the signed-in identity is then read from the Gateway's <c>GET /account/status</c>
    /// (issue #638) in the background and the identity line updated when it arrives. This is purely
    /// informational and read-only: the account is managed by the Gateway, so there is no local sign-in,
    /// logout, or consent control here, and an unreachable Gateway never blocks the Director.
    /// </summary>
    /// <param name="gatewayUrl">The configured Gateway URL from config.json (may be empty).</param>
    /// <param name="gatewayToken">The configured Gateway bearer token from config.json (may be empty).</param>
    private void ShowGatewayManaged(string gatewayUrl, string gatewayToken)
    {
        var configured = !string.IsNullOrWhiteSpace(gatewayUrl);
        GatewayManagedUrlText.Text = configured
            ? gatewayUrl
            : "Not configured (set the Gateway URL on the Gateway tab)";

        if (!configured)
        {
            // No Gateway configured: there is nothing to read an identity from. This is informational,
            // never a gate - the Director still runs.
            GatewayIdentityText.Text = "No Gateway configured. Connect this Director to a Gateway on the Gateway tab.";
            return;
        }

        GatewayIdentityText.Text = "Checking the Gateway...";

        // Build the immutable config snapshot here on the UI thread, then fetch off it. Failures and
        // signed-out states are surfaced as plain text on the identity line, never as a blocking error.
        var config = new GatewayConfig { Url = gatewayUrl, Token = gatewayToken };
        _ = LoadGatewayIdentityAsync(config);
    }

    /// <summary>
    /// Fetch the Gateway's signed-in identity from <c>GET /account/status</c> and update the read-only
    /// identity line (issue #651). Best-effort: a signed-out Gateway or an unreachable Gateway is shown
    /// as a clear "not signed in" / "not connected" line, never an error dialog and never a gate.
    /// </summary>
    private async Task LoadGatewayIdentityAsync(GatewayConfig config)
    {
        FileLog.Write("[SettingsDialog] LoadGatewayIdentityAsync: reading the Gateway signed-in status");
        var client = new GatewayAccountStatusClient();
        var status = await client.GetStatusAsync(config);
        GatewayIdentityText.Text = DescribeGatewayIdentity(status);
        FileLog.Write($"[SettingsDialog] LoadGatewayIdentityAsync: reachable={status.Reachable}, signedIn={status.SignedIn}");
    }

    /// <summary>
    /// Turn a <see cref="GatewayAccountStatus"/> into the read-only identity line: the signed-in email
    /// (with provider when known), or a clear not-signed-in / not-connected state. Identity only, never
    /// any token material (security rule DT-05).
    /// </summary>
    private static string DescribeGatewayIdentity(GatewayAccountStatus status)
    {
        if (!status.Reachable)
            return status.Error ?? "Not connected to the Gateway.";

        if (!status.SignedIn)
            return "The Gateway is not signed in to DevThrottle. Sign in from the Cockpit.";

        if (string.IsNullOrWhiteSpace(status.Email))
            return "Signed in (identity unavailable).";

        return string.IsNullOrWhiteSpace(status.Provider)
            ? status.Email
            : $"{status.Email}  (via {status.Provider})";
    }

    // ----------------------------------------------------------------------------------------
    // Agent entries (issue #489): the user-defined ordered list, its CRUD operations, the
    // add/edit editor, and per-entry detect/quick-check using the existing ToolDetectionService.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Load the ordered agent entries (seeding from the legacy <c>*_path</c> keys on first load),
    /// fill the bound list, and remember a baseline snapshot so Save can tell if anything changed.
    /// </summary>
    private void LoadAgentEntries()
    {
        FileLog.Write("[SettingsDialog] LoadAgentEntries");
        var options = CurrentOptions();
        var entries = AgentEntryStore.LoadEntries(options);

        _agentRows.Clear();
        foreach (var entry in entries)
            _agentRows.Add(AgentEntryRow.From(entry, ReadStatusText(entry)));

        _loadedAgentsSnapshot = SnapshotAgents();
        UpdateAgentEntriesEmptyState();
    }

    /// <summary>Show the empty-state hint only when the list has no entries.</summary>
    private void UpdateAgentEntriesEmptyState() =>
        AgentEntriesEmpty.IsVisible = _agentRows.Count == 0;

    /// <summary>
    /// Read the persisted validation status line for an entry's type/path, so the list's Status
    /// column shows the last Quick-check result. Custom (RawCli) entries have no validation key.
    /// </summary>
    private string ReadStatusText(AgentEntry entry)
    {
        if (!ToolDetectionService.SupportedTools.Contains(entry.Type))
            return "";

        try
        {
            var status = ToolDetectionService.ReadValidationStatus(entry.Type, CurrentOptions());
            if (status is null)
                return "Not checked";
            if (!ValidationMatchesEntryPath(entry, status))
                return "Not checked";
            return status.Ok ? "OK" : "Failed";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] ReadStatusText FAILED: {ex.Message}");
            return "";
        }
    }

    private static bool ValidationMatchesEntryPath(AgentEntry entry, ToolValidationStatus status)
    {
        var entryPath = entry.ExecutablePath?.Trim() ?? "";
        if (entryPath.Length == 0)
            return status.MatchesCurrentPath;

        var resolved = ExecutableResolver.Resolve(entryPath);
        return string.Equals(status.Path, entryPath, StringComparison.OrdinalIgnoreCase)
            || (resolved is not null && string.Equals(status.Path, resolved, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A stable string snapshot of the agent list, used to detect changes on Save.</summary>
    private string SnapshotAgents()
    {
        var array = new JsonArray();
        foreach (var row in _agentRows)
            array.Add(new JsonObject
            {
                ["id"] = row.Id,
                ["display_name"] = row.DisplayName,
                ["type"] = row.Type.ToString(),
                ["enabled"] = row.Enabled,
                ["executable_path"] = row.ExecutablePath,
                ["preset_id"] = row.PresetId,
                ["default_model"] = row.DefaultModel,
                ["args_override"] = row.ArgsOverride,
                ["launch_mode"] = row.LaunchMode.ToString(),
            });
        return array.ToJsonString();
    }

    private AgentEntryRow? FindRow(object? tag) =>
        tag is string id ? _agentRows.FirstOrDefault(r => r.Id == id) : null;

    private void BtnMoveUp_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnMoveUp_Click");
        var row = FindRow((sender as Control)?.Tag);
        if (row is null) return;
        var index = _agentRows.IndexOf(row);
        if (index > 0)
            _agentRows.Move(index, index - 1);
    }

    private void BtnMoveDown_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnMoveDown_Click");
        var row = FindRow((sender as Control)?.Tag);
        if (row is null) return;
        var index = _agentRows.IndexOf(row);
        if (index >= 0 && index < _agentRows.Count - 1)
            _agentRows.Move(index, index + 1);
    }

    private async void BtnRemoveAgent_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnRemoveAgent_Click");
        try
        {
            var row = FindRow((sender as Control)?.Tag);
            if (row is null) return;

            // Trash-icon Remove confirms before destroying data (AC2).
            var confirm = new ConfirmDialog(
                "Remove agent?",
                $"Remove the agent \"{row.DisplayName}\" from the list?",
                confirmLabel: "Remove");
            if (await confirm.ShowDialog<bool>(this) != true)
                return;

            _agentRows.Remove(row);
            UpdateAgentEntriesEmptyState();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnRemoveAgent_Click FAILED: {ex.Message}");
            StatusText.Text = $"Could not remove agent: {ex.Message}";
        }
    }

    private async void BtnAddAgent_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnAddAgent_Click");
        await OpenAgentEditorAsync(null);
    }

    private async void BtnEditAgent_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnEditAgent_Click");
        var row = FindRow((sender as Control)?.Tag);
        if (row is null) return;
        await OpenAgentEditorAsync(row);
    }

    /// <summary>
    /// Open the Add/Edit modal over this Settings dialog (AC5). <paramref name="row"/> null = add;
    /// non-null = edit that entry. The modal returns the committed <see cref="AgentEntry"/> on Save
    /// (null on Cancel/discard); on Save we apply it to the in-memory list AND persist the full
    /// list to config.json immediately, so the deliberate save cannot be dropped by how the parent
    /// is later closed (AC11, documented persistence model (a)).
    /// </summary>
    private async Task OpenAgentEditorAsync(AgentEntryRow? row)
    {
        try
        {
            // Sibling names = every OTHER entry's display name, for "(N)" disambiguation (AC7).
            var siblingNames = _agentRows
                .Where(r => r.Id != row?.Id)
                .Select(r => r.DisplayName)
                .ToList();

            var existing = row is null ? null : new AgentEntry
            {
                Id = row.Id,
                DisplayName = row.DisplayName,
                Type = row.Type,
                Enabled = row.Enabled,
                ExecutablePath = row.ExecutablePath,
                PresetId = row.PresetId,
                DefaultModel = row.DefaultModel,
                ArgsOverride = row.ArgsOverride,
                LaunchMode = row.LaunchMode,
            };

            var dialog = new AgentEditorDialog(existing, siblingNames, CurrentOptions());
            var result = await dialog.ShowDialog<AgentEntry?>(this);
            if (result is null)
            {
                FileLog.Write("[SettingsDialog] OpenAgentEditorAsync: modal cancelled");
                return;
            }

            ApplyEditorResult(result);
            await PersistAgentsAsync();
            UpdateAgentEntriesEmptyState();
            FileLog.Write($"[SettingsDialog] OpenAgentEditorAsync: saved entry {result.Id}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] OpenAgentEditorAsync FAILED: {ex.Message}");
            StatusText.Text = $"Could not save agent: {ex.Message}";
        }
    }

    /// <summary>Apply a modal Save result to the in-memory list: update the matching row or add a new one.</summary>
    private void ApplyEditorResult(AgentEntry result)
    {
        var existing = _agentRows.FirstOrDefault(r => r.Id == result.Id);
        if (existing is not null)
        {
            existing.DisplayName = result.DisplayName;
            existing.Type = result.Type;
            existing.Enabled = result.Enabled;
            existing.ExecutablePath = result.ExecutablePath;
            existing.PresetId = result.PresetId;
            existing.DefaultModel = result.DefaultModel;
            existing.ArgsOverride = result.ArgsOverride;
            existing.LaunchMode = result.LaunchMode;
            existing.StatusText = ReadStatusText(result);
        }
        else
        {
            _agentRows.Add(AgentEntryRow.From(result, ReadStatusText(result)));
        }
    }

    /// <summary>
    /// Persist the current ordered agent list to config.json (the documented non-lossy model (a):
    /// each deliberate modal Save flushes immediately) and reset the baseline so the dialog's own
    /// Save/Cancel does not re-write or revert it.
    /// </summary>
    private async Task PersistAgentsAsync()
    {
        var entries = BuildEntriesFromRows();
        await Task.Run(() => AgentEntryStore.SaveEntries(entries));
        _loadedAgentsSnapshot = SnapshotAgents();
        FileLog.Write($"[SettingsDialog] PersistAgentsAsync: wrote {entries.Count} entries");
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

            // A Gateway URL is required (issue #442): a blank or non-absolute-http(s) URL is
            // rejected so config.json can never be saved with an empty gateway.url through this
            // dialog. The dialog stays open, the Gateway tab is shown, and the inline message names
            // the fix. Reachability is NOT checked here - that is what the Test button is for. Reuses
            // the same syntactic rule the onboarding wizard already enforces (and unit-tests).
            var urlValidation = OnboardingModel.ValidateGatewayUrl(url);
            if (!urlValidation.IsValid)
            {
                FileLog.Write($"[SettingsDialog] BtnSave_Click: rejected invalid gateway url='{url}': {urlValidation.Message}");
                SelectGatewayTab();
                ShowGatewayStatus("A Gateway URL is required. " + urlValidation.Message, error: true);
                SaveButton.IsEnabled = true;
                return;
            }

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

            // The user-defined agent list (issue #489) persists as the ordered agent.entries array.
            // Snapshot it on the UI thread; the disk write runs off it.
            var agentsChanged = SnapshotAgents() != _loadedAgentsSnapshot;
            var agentEntries = agentsChanged ? BuildEntriesFromRows() : null;

            if (patch.Count == 0 && !alphaChanged && !agentsChanged)
            {
                // Nothing changed - "Save and Close" just closes, same as Cancel.
                FileLog.Write("[SettingsDialog] BtnSave_Click: no changes; closing");
                Close();
                return;
            }

            if (agentEntries is not null)
                await Task.Run(() => AgentEntryStore.SaveEntries(agentEntries));

            if (patch.Count > 0)
                await Task.Run(() => CcDirectorConfigService.MergePatch(patch));

            // Persist the alpha flag and notify long-lived windows (MainWindow re-gates its
            // alpha buttons via AlphaMode.Changed). Persisted off the UI thread; the Changed
            // handler in MainWindow posts back to the UI thread itself.
            if (alphaChanged)
                await Task.Run(() => AlphaMode.SetEnabled(alpha));

            FileLog.Write($"[SettingsDialog] BtnSave_Click: saved sections={patch.Count}, gatewayChanged={gatewayChanged}, agentsChanged={agentsChanged}, alphaChanged={alphaChanged}");

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

            // Update the loaded baseline to reflect what's now on disk.
            _loadedScreenshots = screenshots;
            _loadedGatewayUrl = url;
            _loadedGatewayAdvertised = advertised;
            _loadedGatewayToken = token;
            _loadedAlpha = alpha;
            _loadedAgentsSnapshot = SnapshotAgents();

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

    /// <summary>Project the bound rows into the persistable <see cref="AgentEntry"/> list, in list order.</summary>
    private List<AgentEntry> BuildEntriesFromRows()
    {
        var entries = new List<AgentEntry>();
        foreach (var row in _agentRows)
            entries.Add(new AgentEntry
            {
                Id = row.Id,
                DisplayName = row.DisplayName,
                Type = row.Type,
                Enabled = row.Enabled,
                ExecutablePath = row.ExecutablePath,
                PresetId = row.PresetId,
                DefaultModel = row.DefaultModel,
                ArgsOverride = row.ArgsOverride,
                LaunchMode = row.LaunchMode,
            });
        return entries;
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

    /// <summary>Select the Gateway tab so a gateway-related message (e.g. the required-URL
    /// validation block on Save, issue #442) is visible to the user.</summary>
    public void SelectGatewayTab()
    {
        // Tab order in SettingsDialog.axaml: Account(0), Gateway(1), Agents(2), ...
        const int gatewayTabIndex = 1;
        SettingsTabs.SelectedIndex = gatewayTabIndex;
    }

    /// <summary>
    /// Re-run the first-run tool-detection wizard on demand (issue #392). On accept it writes the
    /// selected tools to config.json (legacy <c>*_path</c> keys); we reload the agent list so any
    /// newly-added tools show up as entries.
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
                LoadAgentEntries();
                ShowAgentToolsStatus(BuildWizardResultMessage(dialog.LastResult), error: false);
                FileLog.Write("[SettingsDialog] BtnRunWizard_Click: wizard accepted; reloaded agent list");
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

    /// <summary>
    /// Re-run the first-run onboarding wizard on demand (issue #370). The wizard may persist a new
    /// gateway.url and the onboarding-complete marker, so afterwards we reload the dialog from disk so
    /// the Gateway fields reflect any change the wizard made.
    /// </summary>
    /// <summary>
    /// Issue #469: open the "Connect to Gateway" dialog to enroll THIS device with a pairing code.
    /// The dialog enrolls the device (its stable device id) and writes the issued per-device key to
    /// the local credential file. Prefills the URL from the Gateway URL field if one is entered.
    /// </summary>
    private async void BtnConnectToGateway_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnConnectToGateway_Click");
        ConnectToGatewayButton.IsEnabled = false;
        try
        {
            var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
            if (host is null)
            {
                ShowGatewayStatus("The Control API is not started yet. Try again in a moment.", error: true);
                return;
            }

            var prefillUrl = (GatewayUrlBox.Text ?? "").Trim();
            var dialog = new ConnectToGatewayDialog(host.DirectorId, prefillUrl);
            var joined = await dialog.ShowDialog<bool?>(this);
            if (joined == true)
            {
                // The dialog wrote the URL + per-device key to config.json; reload so the tab shows it
                // and re-apply the gateway so the running client picks up the new key immediately.
                await LoadAsync();
                if (_reapplyGateway is not null)
                    await _reapplyGateway();
                FileLog.Write("[SettingsDialog] BtnConnectToGateway_Click: device registered; gateway re-applied");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnConnectToGateway_Click FAILED: {ex.Message}");
            ShowGatewayStatus($"Connect to Gateway failed: {ex.Message}", error: true);
        }
        finally
        {
            ConnectToGatewayButton.IsEnabled = true;
        }
    }

    private async void BtnRerunOnboarding_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnRerunOnboarding_Click");
        RerunOnboardingButton.IsEnabled = false;
        try
        {
            var options = CurrentOptions();
            var dialog = new OnboardingWizardDialog(options);
            await dialog.ShowDialog<bool?>(this);
            await LoadAsync();
            LoadAgentEntries();
            FileLog.Write("[SettingsDialog] BtnRerunOnboarding_Click: wizard closed; reloaded settings");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnRerunOnboarding_Click FAILED: {ex.Message}");
            ShowGatewayStatus($"Setup wizard failed: {ex.Message}", error: true);
        }
        finally
        {
            RerunOnboardingButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Build the honest after-wizard status: name the agents that were actually added to the list
    /// and how many selected ones were skipped because they were already present. Avoids the old
    /// "the tools it added are shown above" message that lied when nothing new was added.
    /// </summary>
    private static string BuildWizardResultMessage(WizardAcceptResult? result)
    {
        if (result is null)
            return "Detection wizard finished.";

        var addedNames = result.AddedTools.Select(t => AgentToolCatalog.GetEntry(t).DisplayName).ToList();
        var addedPart = addedNames.Count switch
        {
            0 => "No new agents added",
            1 => $"Added 1 new agent: {addedNames[0]}",
            _ => $"Added {addedNames.Count} new agents: {string.Join(", ", addedNames)}",
        };

        var skippedCount = result.SkippedTools.Count;
        var skippedPart = skippedCount == 0
            ? ""
            : $" {skippedCount} selected {(skippedCount == 1 ? "tool was" : "tools were")} already in your list and left unchanged.";

        return addedPart + "." + skippedPart;
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

    /// <summary>
    /// Turn off the Windows notification that pops up the first time each new build opens its
    /// local network port ("Do you want to allow ... on public and private networks?"). Changing a
    /// firewall profile needs administrator rights, which CC Director does not have, so we launch a
    /// short elevated PowerShell command. Windows shows one User Account Control approval prompt; if
    /// the user declines it, the launch throws error 1223 and we say so plainly. The work runs off
    /// the UI thread so the window stays responsive while the approval prompt is up.
    /// </summary>
    private async void BtnSuppressFirewallPrompt_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnSuppressFirewallPrompt_Click");
        SuppressFirewallButton.IsEnabled = false;
        ShowFirewallStatus("Asking Windows for administrator approval...", error: false);
        try
        {
            var (ok, message) = await Task.Run(RunFirewallPromptSuppression);
            ShowFirewallStatus(message, error: !ok);
            FileLog.Write($"[SettingsDialog] BtnSuppressFirewallPrompt_Click: ok={ok}");
        }
        catch (Win32Exception wex) when (wex.NativeErrorCode == 1223)
        {
            // 1223 = the user clicked No on the User Account Control approval prompt.
            FileLog.Write("[SettingsDialog] BtnSuppressFirewallPrompt_Click: administrator approval declined");
            ShowFirewallStatus("No change was made - administrator approval was declined.", error: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnSuppressFirewallPrompt_Click FAILED: {ex.Message}");
            ShowFirewallStatus($"Could not change the setting: {ex.Message}", error: true);
        }
        finally
        {
            SuppressFirewallButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Run, with administrator rights, the PowerShell command that disables the "notify me when a new
    /// app is blocked" firewall notification on all three profiles. Returns whether it applied and a
    /// message to show the user. The elevated script reports success or failure through its exit code
    /// (0 = applied, 1 = the firewall cmdlet itself failed, e.g. blocked by organization policy).
    /// </summary>
    private static (bool Ok, string Message) RunFirewallPromptSuppression()
    {
        FileLog.Write("[SettingsDialog] RunFirewallPromptSuppression: launching elevated PowerShell");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command " +
                        "\"try { Set-NetFirewallProfile -Profile Domain,Public,Private -NotifyOnListen False -ErrorAction Stop; exit 0 } catch { exit 1 }\"",
            Verb = "runas",            // Raises the Windows administrator approval prompt.
            UseShellExecute = true,    // Required for Verb to take effect.
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        var process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("Windows did not start the elevated PowerShell process.");

        process.WaitForExit();
        FileLog.Write($"[SettingsDialog] RunFirewallPromptSuppression: exit code={process.ExitCode}");

        if (process.ExitCode == 0)
            return (true, "Done. Windows will no longer pop up the firewall question when a new build runs for the first time.");

        return (false, "The setting could not be applied. The firewall may be controlled by your organization's policy.");
    }

    private void ShowFirewallStatus(string text, bool error)
    {
        FirewallStatus.Text = text;
        FirewallStatus.IsVisible = true;
        FirewallStatus.Foreground = error
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

/// <summary>One selectable agent type in the editor's Type dropdown.</summary>
public sealed record AgentTypeOption(AgentKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The bindable view of one <see cref="AgentEntry"/> for the Settings CRUD list. Mutable +
/// change-notifying so the Enabled checkbox, reorder, and status updates reflect live in the list
/// without rebuilding it.
/// </summary>
public sealed class AgentEntryRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnChanged(); }
    }

    private AgentKind _type = AgentKind.ClaudeCode;
    public AgentKind Type
    {
        get => _type;
        set { _type = value; OnChanged(); OnChanged(nameof(TypeLabel)); }
    }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnChanged(); }
    }

    public string ExecutablePath { get; set; } = "";
    public string PresetId { get; set; } = "";
    public string DefaultModel { get; set; } = "";
    public string ArgsOverride { get; set; } = "";
    public LaunchMode LaunchMode { get; set; } = LaunchMode.Guided;

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set
        {
            _statusText = value;
            OnChanged();
            OnChanged(nameof(HasStatus));
            OnChanged(nameof(StatusPillBackground));
            OnChanged(nameof(StatusPillForeground));
        }
    }

    /// <summary>Whether to show the status pill at all (hidden for entries with no status, e.g. Custom).</summary>
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    /// <summary>Pill background by status: green OK / amber Failed / grey Not checked.</summary>
    public global::Avalonia.Media.IBrush StatusPillBackground => StatusText switch
    {
        "OK" => new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#1B3A2A")),
        "Failed" => new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#3A2A1B")),
        _ => new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#404040")),
    };

    /// <summary>Pill text color matching the background semantics.</summary>
    public global::Avalonia.Media.IBrush StatusPillForeground => StatusText switch
    {
        "OK" => new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#22C55E")),
        "Failed" => new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#F59E0B")),
        _ => new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#AAAAAA")),
    };

    /// <summary>Human-readable type label shown in the Type column.</summary>
    public string TypeLabel => Type switch
    {
        AgentKind.ClaudeCode => "Claude Code",
        AgentKind.Pi => "Pi",
        AgentKind.Codex => "Codex",
        AgentKind.Gemini => "Gemini",
        AgentKind.OpenCode => "OpenCode",
        AgentKind.RawCli => "Custom",
        _ => Type.ToString(),
    };

    public static AgentEntryRow From(AgentEntry entry, string statusText) => new()
    {
        Id = entry.Id,
        DisplayName = entry.DisplayName,
        Type = entry.Type,
        Enabled = entry.Enabled,
        ExecutablePath = entry.ExecutablePath,
        PresetId = entry.PresetId,
        DefaultModel = entry.DefaultModel,
        ArgsOverride = entry.ArgsOverride,
        LaunchMode = entry.LaunchMode,
        StatusText = statusText,
    };

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
