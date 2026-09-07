using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class ClaudeConfigDialog : Window
{
    private readonly string _claudeDir;
    private readonly string _claudeJsonPath;
    private readonly string _settingsJsonPath;
    private readonly string? _projectSettingsPath;
    private readonly string? _projectLocalSettingsPath;

    /// <summary>
    /// Whether the controls on screen were populated from a settings file that was actually READ.
    /// False after a load that could not read it, and only a successful reload clears it. Saving is
    /// refused while it is false, because blank controls that never described the file must never be
    /// written over it - not even once the file itself has been repaired.
    /// </summary>
    private bool _controlsReflectTheFile = true;

    private readonly ObservableCollection<string> _allowedRules = new();
    private readonly ObservableCollection<string> _deniedRules = new();
    private readonly ObservableCollection<PluginEntry> _plugins = new();

    private static readonly string[] PermissionModes =
        ["plan", "acceptEdits", "auto", "bypassPermissions"];

    private static readonly string[] EffortLevels =
        ["", "low", "medium", "high"];

    public ClaudeConfigDialog()
    {
        InitializeComponent();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _claudeDir = Path.Combine(home, ".claude");
        _claudeJsonPath = Path.Combine(home, ".claude.json");
        _settingsJsonPath = Path.Combine(_claudeDir, "settings.json");
    }

    public ClaudeConfigDialog(string? repoPath = null, string? initialTab = null)
    {
        InitializeComponent();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _claudeDir = Path.Combine(home, ".claude");
        _claudeJsonPath = Path.Combine(home, ".claude.json");
        _settingsJsonPath = Path.Combine(_claudeDir, "settings.json");

        if (!string.IsNullOrEmpty(repoPath))
        {
            _projectSettingsPath = Path.Combine(repoPath, ".claude", "settings.json");
            _projectLocalSettingsPath = Path.Combine(repoPath, ".claude", "settings.local.json");
        }

        PermissionModeCombo.ItemsSource = PermissionModes;
        EffortLevelCombo.ItemsSource = EffortLevels;
        AllowedToolsList.ItemsSource = _allowedRules;
        DeniedToolsList.ItemsSource = _deniedRules;
        PluginsList.ItemsSource = _plugins;

        Loaded += (_, _) =>
        {
            LoadConfig();

            if (initialTab != null)
            {
                var tabIndex = initialTab.ToLowerInvariant() switch
                {
                    "general" => 0,
                    "permissions" => 1,
                    "plugins" => 2,
                    "hooks" => 3,
                    "files" => 4,
                    _ => 0
                };
                ConfigTabs.SelectedIndex = tabIndex;
                FileLog.Write($"[ClaudeConfigDialog] Initial tab: {initialTab} -> index {tabIndex}");
            }
        };

        KeyDown += OnWindowKeyDown;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    // -- Load -----------------------------------------------------------------

    private void LoadConfig()
    {
        FileLog.Write("[ClaudeConfigDialog] LoadConfig: reading configuration files");

        var claudeJson = ReadJsonFile(_claudeJsonPath);

        // The settings file is read through the three-answer reader, because a file that is THERE and
        // unreadable must not be presented as an empty one. Loading it blank showed a plausible screen -
        // permission mode "plan", no rules, no plugins, "No hooks configured" - that described nothing
        // on disk, and pressing Save on that screen is what destroyed the file.
        var settingsRead = ClaudeSettingsFile.Read(_settingsJsonPath);
        var settingsJson = settingsRead.Root;

        LoadGeneralTab(claudeJson, settingsJson);
        LoadPermissionsTab(settingsJson);
        LoadPluginsTab(settingsJson);
        LoadHooksTab(settingsJson);
        LoadFilesTab();

        // LATCHED on the load, not re-derived at save time. The controls now on screen either came
        // from a successful read or they did not, and only the load knows which. Re-asking the file at
        // save time answers a different question: if the person repairs the file externally and
        // presses Save without Reload, the fresh read succeeds and these BLANK controls - "plan", no
        // rules, no env, no plugins - are written over the repaired file. That would have moved the
        // defect rather than fixed it, which is the failure this whole change exists to stop.
        _controlsReflectTheFile = settingsRead.Kind != ConfigReadKind.Unreadable;

        if (!_controlsReflectTheFile)
        {
            SaveStatusText.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
            SaveStatusText.Text = $"{Path.GetFileName(_settingsJsonPath)} {settingsRead.Problem}. "
                                  + "The fields below are EMPTY because it could not be read, not because "
                                  + "it is empty. Fix or move that file and press Reload - saving is "
                                  + "refused until a reload succeeds, so these blanks cannot overwrite it.";
            FileLog.Write($"[ClaudeConfigDialog] LoadConfig: settings file unreadable - {settingsRead.Problem}");
        }
        else
        {
            SaveStatusText.Foreground = new SolidColorBrush(Color.Parse("#22C55E"));
            SaveStatusText.Text = "";
        }
    }

    private void LoadGeneralTab(JsonNode? claudeJson, JsonNode? settingsJson)
    {
        // Permission mode
        var mode = settingsJson?["permissions"]?["defaultMode"]?.GetValue<string>();
        PermissionModeCombo.SelectedItem = mode ?? "plan";

        // Model override (stored in env)
        var model = settingsJson?["env"]?["ANTHROPIC_MODEL"]?.GetValue<string>();
        ModelOverrideInput.Text = model ?? "";

        // Effort level
        var effort = settingsJson?["env"]?["CLAUDE_CODE_EFFORT_LEVEL"]?.GetValue<string>();
        EffortLevelCombo.SelectedItem = effort ?? "";

        // Max output tokens
        var tokens = settingsJson?["env"]?["CLAUDE_CODE_MAX_OUTPUT_TOKENS"]?.GetValue<string>();
        MaxTokensInput.Text = tokens ?? "";

        // Bash timeout
        var timeout = settingsJson?["env"]?["BASH_DEFAULT_TIMEOUT_MS"]?.GetValue<string>();
        BashTimeoutInput.Text = timeout ?? "";

        // Auto-updates
        var autoUpdates = claudeJson?["autoUpdates"];
        AutoUpdatesCheck.IsChecked = autoUpdates != null && autoUpdates.GetValue<bool>();
    }

    private void LoadPermissionsTab(JsonNode? settingsJson)
    {
        _allowedRules.Clear();
        _deniedRules.Clear();

        if (settingsJson?["permissions"]?["allow"] is JsonArray allowArr)
        {
            foreach (var item in allowArr)
            {
                var val = item?.GetValue<string>();
                if (val != null) _allowedRules.Add(val);
            }
        }

        if (settingsJson?["permissions"]?["deny"] is JsonArray denyArr)
        {
            foreach (var item in denyArr)
            {
                var val = item?.GetValue<string>();
                if (val != null) _deniedRules.Add(val);
            }
        }
    }

    private void LoadPluginsTab(JsonNode? settingsJson)
    {
        _plugins.Clear();

        if (settingsJson?["enabledPlugins"] is JsonObject plugins)
        {
            foreach (var prop in plugins)
            {
                var isEnabled = prop.Value is JsonValue jv && jv.GetValue<bool>();
                var displayName = prop.Key.Split('@')[0];
                _plugins.Add(new PluginEntry
                {
                    FullKey = prop.Key,
                    DisplayName = displayName,
                    IsEnabled = isEnabled,
                });
            }
        }

        NoPluginsText.IsVisible = _plugins.Count == 0;
    }

    private void LoadHooksTab(JsonNode? settingsJson)
    {
        var hookEntries = new List<HookEntry>();

        if (settingsJson?["hooks"] is JsonObject hooks)
        {
            foreach (var eventProp in hooks)
            {
                var commands = new List<string>();
                if (eventProp.Value is JsonArray groupArray)
                {
                    foreach (var group in groupArray)
                    {
                        if (group?["hooks"] is JsonArray innerHooks)
                        {
                            foreach (var hook in innerHooks)
                            {
                                var cmd = hook?["command"]?.GetValue<string>();
                                if (cmd == null) continue;

                                var isAsync = hook?["async"] is JsonValue av && av.GetValue<bool>();
                                if (isAsync) cmd += " (async)";
                                commands.Add(cmd);
                            }
                        }
                    }
                }

                if (commands.Count > 0)
                    hookEntries.Add(new HookEntry { EventName = eventProp.Key, Commands = commands });
            }
        }

        HooksList.ItemsSource = hookEntries.Count > 0 ? hookEntries : null;
        NoHooksText.IsVisible = hookEntries.Count == 0;
    }

    private void LoadFilesTab()
    {
        var files = new List<ConfigFileEntry>
        {
            new("User preferences (.claude.json)", _claudeJsonPath),
            new("User settings (permissions, hooks, plugins)", _settingsJsonPath),
        };

        if (_projectSettingsPath != null)
            files.Add(new("Project settings (shared)", _projectSettingsPath));

        if (_projectLocalSettingsPath != null)
            files.Add(new("Project settings (local)", _projectLocalSettingsPath));

        FilesList.ItemsSource = files;
    }

    // -- Save -----------------------------------------------------------------

    private void SaveConfig()
    {
        FileLog.Write("[ClaudeConfigDialog] SaveConfig: writing configuration files");

        if (!_controlsReflectTheFile)
        {
            // The file may well be readable again by now - that is exactly the case this guards. The
            // question is not "can the file be read?" but "do these controls describe it?", and the
            // answer was settled at load time and has not changed since.
            SaveStatusText.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
            SaveStatusText.Text = $"NOT saved - {Path.GetFileName(_settingsJsonPath)} could not be read when "
                                  + "this window opened, so the fields shown never described it. Press Reload "
                                  + "first; saving now would overwrite the file with blanks.";
            FileLog.Write("[ClaudeConfigDialog] SaveConfig: REFUSED, controls never reflected the settings file");
            return;
        }

        var settings = SaveSettingsJson();
        if (!settings.Saved)
        {
            // Say so, and say it in the place the word "Saved" would otherwise have appeared. Reporting
            // success over a write that did not happen is how the old behaviour hid: the file was left
            // alone only by accident of the caller, and the person was told it had been saved either way.
            // Tested POSITIVELY on Saved rather than on Refused, so a new outcome added to that enum
            // cannot quietly join the success path.
            var advice = settings.Refused
                ? "Fix or move that file, then reopen this window."
                : "Your settings were not changed.";
            SaveStatusText.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
            SaveStatusText.Text = $"NOT saved - {Path.GetFileName(_settingsJsonPath)} {settings.Problem}. {advice}";
            FileLog.Write($"[ClaudeConfigDialog] SaveConfig: ABORTED, outcome={settings.Kind}");
            return;
        }

        var prefs = SaveClaudeJson();
        if (!prefs.Saved)
        {
            SaveStatusText.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
            SaveStatusText.Text = $"Settings saved, but {Path.GetFileName(_claudeJsonPath)} {prefs.Problem} - "
                                  + "the automatic-updates setting was NOT changed.";
            FileLog.Write($"[ClaudeConfigDialog] SaveConfig: partial, .claude.json outcome={prefs.Kind}");
            return;
        }

        SaveStatusText.Foreground = new SolidColorBrush(Color.Parse("#22C55E"));
        SaveStatusText.Text = "Saved";
        FileLog.Write("[ClaudeConfigDialog] SaveConfig: complete");
    }

    /// <summary>
    /// Merge this dialog's fields into the user's settings file. The merge, and the refusal to merge
    /// into a file that cannot be read, live in <see cref="ClaudeSettingsFile"/>; this only gathers
    /// what the controls hold.
    /// </summary>
    private SettingsSaveResult SaveSettingsJson()
    {
        var edits = new ClaudeSettingsEdits(
            PermissionMode: PermissionModeCombo.SelectedItem as string,
            Allow: _allowedRules.ToList(),
            Deny: _deniedRules.ToList(),
            Model: ModelOverrideInput.Text,
            EffortLevel: EffortLevelCombo.SelectedItem as string,
            MaxOutputTokens: MaxTokensInput.Text,
            BashTimeoutMs: BashTimeoutInput.Text,
            EnabledPlugins: PluginMap());

        return ClaudeSettingsFile.Save(_settingsJsonPath, edits);
    }

    /// <summary>
    /// The plugin enablement map, LAST ONE WINS on a repeated key.
    ///
    /// Deliberately not <c>ToDictionary</c>: that throws on a duplicate key, and the code this
    /// replaced assigned through a JsonObject indexer, which silently overwrote. Today the keys come
    /// from JSON object property names and cannot repeat, so the two agree - but the difference is
    /// between "overwrites" and "throws out of a button click", and this dialog's click handler is
    /// exactly where an exception used to disappear.
    /// </summary>
    private Dictionary<string, bool> PluginMap()
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var p in _plugins)
            map[p.FullKey] = p.IsEnabled;
        return map;
    }

    /// <summary>
    /// Save the one field this dialog owns in <c>.claude.json</c>, and say which of the three things
    /// happened. It reads through the same three-answer reader as the settings file: the old reader
    /// returned null for absent and unreadable alike, and while this method declined to write on
    /// either - so it never clobbered anything - the caller then displayed a green "Saved" over an
    /// edit that was silently dropped.
    /// </summary>
    private SettingsSaveResult SaveClaudeJson()
    {
        var read = ClaudeSettingsFile.Read(_claudeJsonPath);

        // Absent is deliberately NOT an error and NOT a create: this dialog does not own the file's
        // existence, and minting one Claude Code never wrote is not this button's business.
        if (read.Kind == ConfigReadKind.Absent)
            return new SettingsSaveResult(SettingsSaveKind.Merged, null);

        if (read.Kind == ConfigReadKind.Unreadable)
        {
            FileLog.Write($"[ClaudeConfigDialog] SaveClaudeJson REFUSED: {_claudeJsonPath} {read.Problem}");
            return new SettingsSaveResult(SettingsSaveKind.RefusedUnreadable, read.Problem);
        }

        var obj = read.Root!;
        obj["autoUpdates"] = AutoUpdatesCheck.IsChecked == true;

        try
        {
            WriteJsonFile(_claudeJsonPath, obj);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeConfigDialog] SaveClaudeJson FAILED writing {_claudeJsonPath}: {ex.Message}");
            return new SettingsSaveResult(SettingsSaveKind.WriteFailed, $"could not be written ({ex.Message})");
        }

        return new SettingsSaveResult(SettingsSaveKind.Merged, null);
    }

    // -- Permission Rules -----------------------------------------------------

    private void BtnAddAllow_Click(object? sender, RoutedEventArgs e) => AddRule(_allowedRules, AddAllowInput);
    private void BtnRemoveAllow_Click(object? sender, RoutedEventArgs e) => RemoveSelected(_allowedRules, AllowedToolsList);
    private void BtnAddDeny_Click(object? sender, RoutedEventArgs e) => AddRule(_deniedRules, AddDenyInput);
    private void BtnRemoveDeny_Click(object? sender, RoutedEventArgs e) => RemoveSelected(_deniedRules, DeniedToolsList);

    private void AddAllowInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddRule(_allowedRules, AddAllowInput); e.Handled = true; }
    }

    private void AddDenyInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddRule(_deniedRules, AddDenyInput); e.Handled = true; }
    }

    private static void AddRule(ObservableCollection<string> rules, TextBox input)
    {
        var text = input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (!rules.Contains(text))
        {
            rules.Add(text);
            FileLog.Write($"[ClaudeConfigDialog] Added rule: {text}");
        }
        input.Clear();
        input.Focus();
    }

    private static void RemoveSelected(ObservableCollection<string> rules, ListBox list)
    {
        if (list.SelectedItem is string selected)
        {
            rules.Remove(selected);
            FileLog.Write($"[ClaudeConfigDialog] Removed rule: {selected}");
        }
    }

    // -- File Open ------------------------------------------------------------

    private void BtnOpenFile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var path = btn.Tag as string;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        FileLog.Write($"[ClaudeConfigDialog] Opening config file: {path}");
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeConfigDialog] Open file FAILED: {ex.Message}");
            // TODO: Replace with proper Avalonia dialog notification
        }
    }

    // -- Button Handlers ------------------------------------------------------

    private void BtnReload_Click(object? sender, RoutedEventArgs e) => LoadConfig();
    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        // An event handler is an entry point and carries the catch. Without one, anything thrown here
        // reaches the application's unhandled-UI-exception handler, which marks it Handled - so the
        // press produces no file, no message and no trace on screen. That silence is the same defect
        // this dialog was fixed for, one layer up.
        try
        {
            SaveConfig();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeConfigDialog] BtnSave_Click FAILED: {ex}");
            SaveStatusText.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
            SaveStatusText.Text = $"NOT saved - {ex.Message}";
        }
    }
    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    // -- JSON Helpers ---------------------------------------------------------

    private static JsonNode? ReadJsonFile(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var text = File.ReadAllText(path);
            return JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeConfigDialog] ReadJsonFile FAILED: {path} -> {ex.Message}");
            return null;
        }
    }

    private static void WriteJsonFile(string path, JsonNode node)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = node.ToJsonString(options);
        File.WriteAllText(path, json);
        FileLog.Write($"[ClaudeConfigDialog] WriteJsonFile: {path} ({json.Length} bytes)");
    }

    // -- Inner Types ----------------------------------------------------------

    private class HookEntry
    {
        public string EventName { get; set; } = "";
        public List<string> Commands { get; set; } = new();
    }

    internal class PluginEntry : INotifyPropertyChanged
    {
        private bool _isEnabled;

        public string FullKey { get; set; } = "";
        public string DisplayName { get; set; } = "";

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private class ConfigFileEntry
    {
        public ConfigFileEntry(string label, string filePath)
        {
            Label = label;
            FilePath = filePath;
            Exists = File.Exists(filePath);
            StatusText = Exists ? "exists" : "not found";
            var color = Exists ? Color.FromRgb(0x22, 0xC5, 0x5E) : Color.FromRgb(0x66, 0x66, 0x66);
            StatusBrush = new SolidColorBrush(color);
        }

        public string Label { get; }
        public string FilePath { get; }
        public bool Exists { get; }
        public string StatusText { get; }
        public IBrush StatusBrush { get; }
    }
}
