using System.IO;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Settings;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// View model for displaying Claude sessions in the Resume Session tab.
/// Wraps ClaudeSessionMetadata with display-friendly properties.
/// </summary>
public class ClaudeSessionViewModel
{
    private readonly ClaudeSessionMetadata _metadata;
    private readonly string? _customName;
    private readonly string? _customColor;

    public ClaudeSessionViewModel(ClaudeSessionMetadata metadata, string? customName = null, string? customColor = null)
    {
        _metadata = metadata;
        _customName = customName;
        _customColor = customColor;
    }

    public ClaudeSessionMetadata Metadata => _metadata;
    public string SessionId => _metadata.SessionId;
    public string DisplayName => !string.IsNullOrWhiteSpace(_customName) ? _customName : ProjectName;
    public string ProjectNameSuffix => HasCustomName ? $"({ProjectName})" : string.Empty;
    public bool HasCustomName => !string.IsNullOrWhiteSpace(_customName);
    public bool HasCustomColor => !string.IsNullOrWhiteSpace(_customColor);

    public ISolidColorBrush? CustomColorBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_customColor)) return null;
            try
            {
                var color = Color.Parse(_customColor);
                return new SolidColorBrush(color);
            }
            catch { return null; }
        }
    }

    public string ProjectName
    {
        get
        {
            if (string.IsNullOrEmpty(_metadata.ProjectPath))
                return "Unknown Project";
            return Path.GetFileName(_metadata.ProjectPath.TrimEnd('\\', '/'));
        }
    }

    public string ProjectPath => _metadata.ProjectPath ?? string.Empty;
    public string MessageCountDisplay => $"{_metadata.MessageCount} msgs";

    public string TimeAgo
    {
        get
        {
            if (_metadata.Modified == DateTime.MinValue)
                return string.Empty;

            return RelativeTime.Ago(DateTime.UtcNow - _metadata.Modified.ToUniversalTime());
        }
    }

    public string DisplaySummary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_metadata.Summary))
                return TruncateWithEllipsis(_metadata.Summary, 120);
            if (!string.IsNullOrWhiteSpace(_metadata.FirstPrompt))
                return TruncateWithEllipsis(_metadata.FirstPrompt, 120);
            return $"{_metadata.MessageCount} messages";
        }
    }

    internal static string TruncateWithEllipsis(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        text = text.Replace("\r", " ").Replace("\n", " ");
        while (text.Contains("  "))
            text = text.Replace("  ", " ");

        if (text.Length <= maxLength)
            return text.Trim();

        return text.Substring(0, maxLength - 3).Trim() + "...";
    }
}

/// <summary>
/// View model for displaying session history entries in the Resume Session tab.
/// </summary>
public class SessionHistoryViewModel
{
    private readonly SessionHistoryEntry _entry;
    private readonly ClaudeSessionMetadata? _claudeMetadata;

    public SessionHistoryViewModel(SessionHistoryEntry entry, ClaudeSessionMetadata? claudeMetadata)
    {
        _entry = entry;
        _claudeMetadata = claudeMetadata;
    }

    public string? ClaudeSessionId => _entry.ClaudeSessionId;

    public string DisplayName => !string.IsNullOrWhiteSpace(_entry.CustomName)
        ? _entry.CustomName
        : ProjectName;

    public string ProjectName
    {
        get
        {
            if (string.IsNullOrEmpty(_entry.RepoPath))
                return "Unknown Project";
            return Path.GetFileName(_entry.RepoPath.TrimEnd('\\', '/'));
        }
    }

    public string ProjectNameSuffix => HasCustomName ? $"({ProjectName})" : string.Empty;
    public bool HasCustomName => !string.IsNullOrWhiteSpace(_entry.CustomName);
    public bool HasCustomColor => !string.IsNullOrWhiteSpace(_entry.CustomColor);

    public ISolidColorBrush? CustomColorBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_entry.CustomColor)) return null;
            try
            {
                var color = Color.Parse(_entry.CustomColor);
                return new SolidColorBrush(color);
            }
            catch { return null; }
        }
    }

    public string ProjectPath => _entry.RepoPath;

    public string MessageCountDisplay => _claudeMetadata != null
        ? $"{_claudeMetadata.MessageCount} msgs"
        : string.Empty;

    public string TimeAgo
    {
        get
        {
            if (_entry.LastUsedAt == default)
                return string.Empty;

            var span = DateTimeOffset.UtcNow - _entry.LastUsedAt;

            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
            if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
            return $"{(int)(span.TotalDays / 365)}y ago";
        }
    }

    public string DisplaySummary
    {
        get
        {
            if (_claudeMetadata != null)
            {
                if (!string.IsNullOrWhiteSpace(_claudeMetadata.Summary))
                    return ClaudeSessionViewModel.TruncateWithEllipsis(_claudeMetadata.Summary, 120);
                if (!string.IsNullOrWhiteSpace(_claudeMetadata.FirstPrompt))
                    return ClaudeSessionViewModel.TruncateWithEllipsis(_claudeMetadata.FirstPrompt, 120);
            }

            if (!string.IsNullOrWhiteSpace(_entry.FirstPromptSnippet))
                return ClaudeSessionViewModel.TruncateWithEllipsis(_entry.FirstPromptSnippet, 120);

            return string.Empty;
        }
    }
}

/// <summary>
/// View model for displaying handover documents in the Handovers tab.
/// </summary>
public class HandoverViewModel
{
    public string FilePath { get; }
    public string Title { get; }
    public string DateDisplay { get; }
    public DateTime FileDate { get; }
    public string? RepoPath { get; }
    public List<string> RepoPaths { get; } = new();
    public string? SessionName { get; }

    public string RepoDisplay => string.IsNullOrEmpty(RepoPath)
        ? "Unknown"
        : Path.GetFileName(RepoPath.TrimEnd('\\', '/'));

    public HandoverViewModel(string filePath)
    {
        FilePath = filePath;
        var name = Path.GetFileNameWithoutExtension(filePath);

        if (name.Length >= 13 && name[8] == '_'
            && DateTime.TryParseExact(name.Substring(0, 8) + name.Substring(9, 4),
                "yyyyMMddHHmm", null, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            FileDate = parsed;
            DateDisplay = parsed.ToString("yyyy-MM-dd HH:mm");

            var slug = name.Length > 14 ? name.Substring(14) : string.Empty;
            Title = string.IsNullOrEmpty(slug)
                ? "Handover"
                : char.ToUpper(slug[0]) + slug.Substring(1).Replace("-", " ");
        }
        else
        {
            FileDate = File.GetLastWriteTime(filePath);
            DateDisplay = FileDate.ToString("yyyy-MM-dd HH:mm");
            Title = name;
        }

        var frontmatter = ExtractFrontmatter(filePath);
        RepoPaths = frontmatter.RepoPaths;
        RepoPath = RepoPaths.FirstOrDefault();
        SessionName = frontmatter.SessionName;
    }

    private record HandoverFrontmatter(List<string> RepoPaths, string? SessionName);

    private static HandoverFrontmatter ExtractFrontmatter(string filePath)
    {
        var paths = new List<string>();
        string? sessionName = null;
        try
        {
            using var reader = new StreamReader(filePath);
            var firstLine = reader.ReadLine();
            if (firstLine == "---")
            {
                bool inRepositories = false;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line == "---") break;

                    if (line.StartsWith("session_name:"))
                    {
                        inRepositories = false;
                        sessionName = line.Substring("session_name:".Length).Trim();
                        if (string.IsNullOrEmpty(sessionName))
                            sessionName = null;
                        continue;
                    }

                    if (line.StartsWith("repositories:"))
                    {
                        inRepositories = true;
                        continue;
                    }

                    if (inRepositories && line.Length > 0 && !char.IsWhiteSpace(line[0]))
                        inRepositories = false;

                    if (inRepositories && line.TrimStart().StartsWith("- path:"))
                    {
                        var path = line.Substring(line.IndexOf("- path:") + 7).Trim();
                        if (Directory.Exists(path))
                            paths.Add(path);
                    }
                }
            }
            else
            {
                var line = firstLine;
                for (int i = 0; i < 10 && line != null; i++)
                {
                    if (line.StartsWith("**Repository:**"))
                    {
                        var raw = line.Substring("**Repository:**".Length).Trim();
                        foreach (var segment in raw.Split(','))
                        {
                            var cleaned = Regex.Replace(
                                segment.Trim(), @"\s*\(.*?\)\s*$", "").Trim();
                            if (Directory.Exists(cleaned))
                                paths.Add(cleaned);
                        }
                        break;
                    }
                    line = reader.ReadLine();
                }
            }
        }
        catch { /* Non-critical */ }

        return new HandoverFrontmatter(paths, sessionName);
    }
}

/// <summary>
/// Display-ready preview of one group member for the Group dropdown's preview card (issue
/// #254): the role label, the type badge text/color, and the resulting session name suffix.
/// Built from a <see cref="SessionGroupMember"/> so the XAML binds to plain display strings.
/// </summary>
public sealed class GroupMemberPreview
{
    // Badge colors mirror the rail badges in SessionViewModel so the preview reads the same
    // as the sessions it will create. Kept here (not shared) to avoid a UI-layer cross-coupling.
    private static readonly ISolidColorBrush DeveloperBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)); // neutral - no rail badge
    private static readonly ISolidColorBrush ProductBrush = new SolidColorBrush(Color.FromRgb(0xEC, 0x48, 0x99));   // magenta
    private static readonly ISolidColorBrush QaBrush = new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7));        // violet
    private static readonly ISolidColorBrush SupportBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));   // emerald
    private static readonly ISolidColorBrush DiscussBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));   // cyan
    private static readonly ISolidColorBrush LegacyBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));    // amber
    private static readonly ISolidColorBrush ImplementationBrush = new SolidColorBrush(Color.FromRgb(0x14, 0xB8, 0xA6)); // teal (#259)

    public GroupMemberPreview(SessionGroupMember member)
    {
        RoleLabel = member.Role;
        NameSuffix = member.NameSuffix;
        TypeBadge = member.Type switch
        {
            SessionType.Developer => "[ ] Developer",
            SessionType.Product => "[P] Product",
            SessionType.QA => "[Q] QA",
            SessionType.Support => "[S] Support",
            SessionType.Discuss => "[D] Discuss",
            SessionType.Implementation => "[I] Implementation",
            SessionType.IssueSubmitter => "[S] Issue Submitter",
            _ => member.Type.ToString(),
        };
        BadgeBrush = member.Type switch
        {
            SessionType.Product => ProductBrush,
            SessionType.QA => QaBrush,
            SessionType.Support => SupportBrush,
            SessionType.Discuss => DiscussBrush,
            SessionType.Implementation => ImplementationBrush,
            SessionType.IssueSubmitter => LegacyBrush,
            _ => DeveloperBrush,
        };
    }

    public string RoleLabel { get; }
    public string NameSuffix { get; }
    public string TypeBadge { get; }
    public ISolidColorBrush BadgeBrush { get; }
}

/// <summary>
/// One selectable agent option in the New Session "Agent:" row (issue #490). Wraps a single
/// ENABLED <see cref="AgentEntry"/> from the ordered <c>agent.entries</c> list (#489) so the
/// XAML radio template binds to a plain display name and a two-way selection flag. The whole
/// row is rendered from these in stored order; the first one is pre-selected when the dialog
/// opens. There is no separate "default" concept - first-in-order is the selection.
/// </summary>
public sealed class AgentEntryOption
{
    public AgentEntryOption(AgentEntry entry, bool isSelected)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        IsSelected = isSelected;
    }

    /// <summary>The underlying configured agent entry this option launches.</summary>
    public AgentEntry Entry { get; }

    /// <summary>The label shown on the radio - the entry's free-text display name.</summary>
    public string DisplayName => Entry.DisplayName;

    /// <summary>Two-way bound to the radio's IsChecked; exactly one option is selected at a time.</summary>
    public bool IsSelected { get; set; }
}

public partial class NewSessionDialog : Window
{
    private static readonly ISolidColorBrush ResumeButtonBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly ISolidColorBrush NewSessionButtonBrush = new SolidColorBrush(Color.Parse("#007ACC"));
    private static readonly ISolidColorBrush DisabledButtonBrush = new SolidColorBrush(Color.Parse("#4A4A4A"));
    private static readonly ISolidColorBrush DisabledTextBrush = new SolidColorBrush(Color.Parse("#AAAAAA"));
    private static readonly ISolidColorBrush EnabledTextBrush = Brushes.White;

    private readonly RepositoryRegistry? _registry;
    private readonly SessionHistoryStore? _historyStore;
    private List<SessionHistoryViewModel>? _allSessions;
    private List<RepositoryConfig>? _allRepos;
    private List<HandoverViewModel>? _allHandovers;
    private bool _sessionsLoaded;
    private bool _handoversLoaded;
    private string _repoSortColumn = "LastUsed";
    private bool _repoSortAscending;

    public string? SelectedPath { get; private set; }
    public string? SelectedResumeSessionId { get; private set; }
    public string? SelectedHandoverPath { get; private set; }

    /// <summary>
    /// When <see cref="SelectedAgentKind"/> is <see cref="AgentKind.RawCli"/>, the
    /// command (executable path or bare command name) the user typed. Null otherwise.
    /// </summary>
    public string? SelectedCustomCommand { get; private set; }

    /// <summary>
    /// When <see cref="SelectedAgentKind"/> is <see cref="AgentKind.RawCli"/>, the
    /// optional extra arguments the user typed. Null otherwise.
    /// </summary>
    public string? SelectedCustomArgs { get; private set; }

    /// <summary>
    /// Non-null when the user chose the GitHub (Remote) tab and clicked Start. The
    /// caller (MainWindow) creates a GitHub Actions session from this instead of a
    /// local one. SelectedPath stays null in that case.
    /// </summary>
    public RemoteSessionConfig? RemoteConfig { get; private set; }

    private const int GitHubTabIndex = 3;
    public bool BypassPermissions => BypassPermissionsCheckBox.IsChecked == true;
    public bool EnableRemoteControl => false;
    public bool WingmanEnabled => WingmanCheckBox?.IsChecked == true;
    public bool IsStudioMode => false;

    /// <summary>
    /// The enabled agent entries shown in the picker (issue #490), in stored order. Built once in
    /// the constructor from <c>agent.entries</c> (#489); the bound radio list and the launch both
    /// read from this. Empty when the user has no enabled agents configured.
    /// </summary>
    private readonly List<AgentEntryOption> _agentOptions = new();

    /// <summary>
    /// The configured agent entry the user selected in the "Agent:" row (issue #490), or null when
    /// no enabled entry exists. The launch reads this entry's type/path/preset/model/args. The
    /// selected radio is the first option with <see cref="AgentEntryOption.IsSelected"/> set.
    /// </summary>
    public AgentEntry? SelectedAgentEntry =>
        _agentOptions.FirstOrDefault(o => o.IsSelected)?.Entry
        ?? _agentOptions.FirstOrDefault()?.Entry;

    /// <summary>The agent KIND of the selected entry (issue #490). Defaults to ClaudeCode when
    /// no entry is selected, preserving the legacy default for callers.</summary>
    public AgentKind SelectedAgentKind => SelectedAgentEntry?.Type ?? AgentKind.ClaudeCode;

    /// <summary>The session type chosen in the Type dropdown (issue #211, redesigned to a
    /// ComboBox in #254). Each ComboBoxItem carries its enum name in Tag. Defaults to
    /// Developer when nothing is selected.</summary>
    public SessionType SelectedSessionType
    {
        get
        {
            if (TypeCombo?.SelectedItem is ComboBoxItem item
                && item.Tag is string tag
                && SessionTypeNames.TryParse(tag, out var type))
                return type;
            // The picker defaults to Implementation (#259); a missing/divider selection falls back to it.
            return SessionType.Implementation;
        }
    }

    /// <summary>The group definition chosen when "Group" mode is selected (issue #225,
    /// dropdown in #254), or null when "Single session" is selected. When non-null, MainWindow
    /// creates the whole group and the per-session <see cref="SelectedSessionType"/> is ignored
    /// (the group defines each type).</summary>
    public SessionGroupDefinition? SelectedGroupDefinition
        => ModeRadioGroup?.IsChecked == true
            ? GroupCombo?.SelectedItem as SessionGroupDefinition
            : null;

    /// <summary>Single vs group toggle (issue #254): show the Type dropdown for a single
    /// session, or the Group dropdown + preview card for a group. Only one is visible at a
    /// time so the dialog reads as one control, not two competing pickers.</summary>
    private void CreateModeRadio_CheckedChanged(object? sender, RoutedEventArgs e)
    {
        var groupMode = ModeRadioGroup?.IsChecked == true;
        if (TypePickerPanel is not null)
            TypePickerPanel.IsVisible = !groupMode;
        if (GroupPickerPanel is not null)
            GroupPickerPanel.IsVisible = groupMode;
        // Refresh the Start button so it shows "Start N Sessions" / "Start Session" (issue #259).
        UpdateActionButton();
    }

    /// <summary>Render the preview card for the selected group: exactly which member sessions
    /// will be created, in order, each with its role and type badge (issue #254).</summary>
    private void GroupCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GroupPreviewList is null)
            return;

        var group = GroupCombo?.SelectedItem as SessionGroupDefinition;
        GroupPreviewList.ItemsSource = group?.Members
            .Select(m => new GroupMemberPreview(m))
            .ToList();
        // The chosen group's size drives the "Start N Sessions" button text (issue #259).
        UpdateActionButton();
        FileLog.Write($"[NewSessionDialog] GroupCombo_SelectionChanged: group={group?.Name}, members={group?.Members.Count ?? 0}");
    }

    private static AgentOptions CurrentOptions()
    {
        FileLog.Write("[NewSessionDialog] CurrentOptions");
        return (Application.Current as App)?.SessionManager?.Options
            ?? (Application.Current as App)?.Options
            ?? new AgentOptions();
    }

    public NewSessionDialog(RepositoryRegistry? registry = null, SessionHistoryStore? historyStore = null)
    {
        FileLog.Write("[NewSessionDialog] Constructor: initializing");
        InitializeComponent();
        _registry = registry;
        _historyStore = historyStore;

        // Alpha gating: Handovers, GitHub remote sessions and the Assistant/Coach
        // quick-launch cards are alpha features - hidden by default. The dialog is
        // created fresh each time, so reading the flag once here is enough.
        //
        // The AGENT LIST no longer consults alpha (issue #490): it renders one option per
        // ENABLED agent.entries entry (#489), in stored order, the first pre-selected. Only
        // the non-agent surfaces below still gate on alpha.
        var alpha = AlphaMode.IsEnabled;
        AgentPickerPanel.IsVisible = true;
        LoadAgentEntries();
        HandoversTab.IsVisible = true;
        GitHubTab.IsVisible = alpha;
        QuickLaunchPanel.IsVisible = alpha;
        FileLog.Write($"[NewSessionDialog] Constructor: alphaFeatures={alpha}, enabledAgents={_agentOptions.Count}");

        // Set dialog size to 80% of primary screen
        var screen = Screens.Primary;
        if (screen != null)
        {
            Width = screen.WorkingArea.Width * 0.8 / screen.Scaling;
            Height = screen.WorkingArea.Height * 0.7 / screen.Scaling;
        }

        if (_registry != null && _registry.Repositories.Count > 0)
        {
            _allRepos = _registry.Repositories.ToList();
            ApplyRepoSort();
            RepoList.ItemsSource = _allRepos;
            FileLog.Write($"[NewSessionDialog] Loaded {_allRepos.Count} repositories");
        }
        else
        {
            _allRepos = new List<RepositoryConfig>();
        }

        // Group picker (issue #254): the built-in group definitions are DATA, so the dropdown
        // lists whatever ships (only "Product" today). Selecting the first one renders its
        // preview card; the panel stays hidden until the user switches to Group mode.
        GroupCombo.ItemsSource = SessionGroupDefinition.BuiltIn;
        if (SessionGroupDefinition.BuiltIn.Count > 0)
            GroupCombo.SelectedIndex = 0;
        FileLog.Write($"[NewSessionDialog] Loaded {SessionGroupDefinition.BuiltIn.Count} group definitions");

        Loaded += async (_, _) =>
        {
            Dispatcher.UIThread.Post(() => RepoSearchBox.Focus());
            await LoadSessionHistoryAsync();
        };

        FileLog.Write("[NewSessionDialog] Constructor: complete");
    }

    // Parameterless constructor for XAML designer
    public NewSessionDialog() : this(null, null) { }

    /// <summary>
    /// Build the agent picker (issue #490) from the ENABLED <c>agent.entries</c> (#489), in stored
    /// order. The first enabled entry is pre-selected. When no enabled entry exists, the picker is
    /// empty and a hint points the user at Settings; the Start button stays disabled for that case.
    /// </summary>
    private void LoadAgentEntries()
    {
        FileLog.Write("[NewSessionDialog] LoadAgentEntries");

        _agentOptions.Clear();
        var entries = AgentEntryStore.LoadEntries(CurrentOptions());
        var enabled = entries.Where(e => e.Enabled).ToList();
        for (var i = 0; i < enabled.Count; i++)
            _agentOptions.Add(new AgentEntryOption(enabled[i], isSelected: i == 0));

        AgentEntryList.ItemsSource = _agentOptions;
        NoAgentsHint.IsVisible = _agentOptions.Count == 0;

        // Reflect the pre-selected entry into the Claude-flag and Custom-CLI panel state.
        ApplyAgentSelection();

        FileLog.Write($"[NewSessionDialog] LoadAgentEntries: {entries.Count} entries, {_agentOptions.Count} enabled, first={(_agentOptions.FirstOrDefault()?.DisplayName ?? "(none)")}");
    }

    private void AgentRadio_CheckedChanged(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Every radio in the group fires when the selection swaps; only act on the one
            // becoming checked. Drive the model selection from the sender's bound option rather
            // than the two-way binding, which may not have propagated IsSelected yet at this point.
            if (sender is not RadioButton rb || rb.IsChecked != true) return;
            if (rb.DataContext is not AgentEntryOption chosen) return;

            foreach (var option in _agentOptions)
                option.IsSelected = ReferenceEquals(option, chosen);

            ApplyAgentSelection();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NewSessionDialog] AgentRadio_CheckedChanged FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply the consequences of the currently selected agent entry (issue #490): the
    /// BypassPermissions checkbox is Claude-only, and the Custom-CLI command/args panel shows only
    /// for a RawCli entry (seeded once from the entry's configured executable/args). Safe to call
    /// before the UI is fully built.
    /// </summary>
    private void ApplyAgentSelection()
    {
        var entry = SelectedAgentEntry;
        var agentKind = entry?.Type ?? AgentKind.ClaudeCode;

        // The Bypass-permissions checkbox maps to each agent's permission-bypass flag:
        // Claude's --dangerously-skip-permissions, Cursor's --force (issue #517), and GitHub
        // Copilot's --allow-all (issue #625). It is enabled for those agents and disabled (with a
        // neutral label) for agents that have no such per-session flag, so the UI never misleads.
        var isClaude = agentKind == AgentKind.ClaudeCode;
        var isCursor = agentKind == AgentKind.Cursor;
        var isCopilot = agentKind == AgentKind.Copilot;
        if (BypassPermissionsCheckBox is not null)
        {
            BypassPermissionsCheckBox.IsEnabled = isClaude || isCursor || isCopilot;
            BypassPermissionsCheckBox.Content = isCursor
                ? "Bypass permission prompts (--force)"
                : isCopilot
                    ? "Bypass permission prompts (--allow-all)"
                    : "Bypass permission prompts";
        }

        // Show the custom-CLI command/args panel only when a Custom CLI entry is selected, and
        // seed it from the entry's configured executable/args so the user sees what will run.
        var isRawCli = agentKind == AgentKind.RawCli;
        if (CustomCliPanel is not null)
            CustomCliPanel.IsVisible = isRawCli;
        if (isRawCli && entry is not null && CustomCommandBox is not null
            && string.IsNullOrWhiteSpace(CustomCommandBox.Text))
        {
            CustomCommandBox.Text = entry.ExecutablePath;
            if (CustomArgsBox is not null && string.IsNullOrWhiteSpace(CustomArgsBox.Text))
                CustomArgsBox.Text = entry.ArgsOverride;
        }

        // Refresh the Start button - it is disabled when Custom CLI is chosen but
        // the Command box is empty (validated inside UpdateActionButton).
        UpdateActionButton();

        FileLog.Write($"[NewSessionDialog] ApplyAgentSelection: agent={agentKind}, entry={(entry?.DisplayName ?? "(none)")}");
    }

    private void CustomCommandBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateActionButton();
    }

    private async Task LoadSessionHistoryAsync()
    {
        FileLog.Write("[NewSessionDialog] LoadSessionHistoryAsync: starting");

        try
        {
            var historyTask = Task.Run(() => _historyStore?.LoadAll() ?? new List<SessionHistoryEntry>());
            var claudeMetadataTask = Task.Run(() =>
            {
                var map = new Dictionary<string, ClaudeSessionMetadata>(StringComparer.Ordinal);
                foreach (var cm in ClaudeSessionReader.ScanAllProjects())
                    map.TryAdd(cm.SessionId, cm);
                return map;
            });

            await Task.WhenAll(historyTask, claudeMetadataTask);

            var historyEntries = historyTask.Result;
            var claudeMetadata = claudeMetadataTask.Result;

            FileLog.Write($"[NewSessionDialog] LoadSessionHistoryAsync: found {historyEntries.Count} history entries, {claudeMetadata.Count} Claude sessions");

            _allSessions = historyEntries.Select(entry =>
            {
                ClaudeSessionMetadata? meta = null;
                if (!string.IsNullOrEmpty(entry.ClaudeSessionId))
                    claudeMetadata.TryGetValue(entry.ClaudeSessionId, out meta);
                return new SessionHistoryViewModel(entry, meta);
            }).ToList();

            _sessionsLoaded = true;

            LoadingText.IsVisible = false;

            if (_allSessions.Count > 0)
            {
                SessionList.ItemsSource = _allSessions;
                SessionList.IsVisible = true;
            }
            else
            {
                NoSessionsText.Text = "No session history yet. Start a new session to begin.";
                NoSessionsText.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NewSessionDialog] LoadSessionHistoryAsync FAILED: {ex.Message}");
            LoadingText.IsVisible = false;
            NoSessionsText.Text = "Error loading sessions";
            NoSessionsText.IsVisible = true;
        }
    }

    private async void MainTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs)
            return;

        if (MainTabs.SelectedIndex == 2 && !_handoversLoaded)
            await LoadHandoversAsync();

        UpdateActionButton();
    }

    private void UpdateActionButton()
    {
        // The Single/Group toggle's IsChecked fires during XAML init, before the controls
        // below it (BtnAction etc.) exist; bail until the UI is fully built (issue #259).
        if (BtnAction is null || MainTabs is null || BtnCopyHandover is null)
            return;

        BtnCopyHandover.IsVisible = MainTabs.SelectedIndex == 2 && HandoverList.SelectedItem != null;

        if (MainTabs.SelectedIndex == 0)
        {
            // In Group mode the button reflects how many sessions get created (issue #259).
            var group = SelectedGroupDefinition;
            BtnAction.Content = group is not null ? $"Start {group.Members.Count} Sessions" : "Start Session";
            // For Custom CLI, also require a non-empty Command before enabling Start.
            var hasPath = !string.IsNullOrWhiteSpace(PathInput.Text);
            var isRawCli = SelectedAgentKind == AgentKind.RawCli;
            var hasCommand = !isRawCli || !string.IsNullOrWhiteSpace(CustomCommandBox?.Text);
            // An agent must be configured (issue #490): no enabled agent.entries -> nothing to launch.
            var hasAgent = SelectedAgentEntry is not null;
            var isEnabled = hasPath && hasCommand && hasAgent;
            BtnAction.IsEnabled = isEnabled;
            BtnAction.Background = isEnabled ? NewSessionButtonBrush : DisabledButtonBrush;
            BtnAction.Foreground = isEnabled ? EnabledTextBrush : DisabledTextBrush;
        }
        else if (MainTabs.SelectedIndex == 1)
        {
            BtnAction.Content = "Resume Selected";
            var isEnabled = SessionList.SelectedItem != null;
            BtnAction.IsEnabled = isEnabled;
            BtnAction.Background = isEnabled ? ResumeButtonBrush : DisabledButtonBrush;
            BtnAction.Foreground = isEnabled ? EnabledTextBrush : DisabledTextBrush;
        }
        else if (MainTabs.SelectedIndex == GitHubTabIndex)
        {
            BtnAction.Content = "Start Remote";
            var isEnabled = IsGitHubTabValid();
            BtnAction.IsEnabled = isEnabled;
            BtnAction.Background = isEnabled ? NewSessionButtonBrush : DisabledButtonBrush;
            BtnAction.Foreground = isEnabled ? EnabledTextBrush : DisabledTextBrush;
        }
        else
        {
            BtnAction.Content = "Start Session";
            var hvm = HandoverList.SelectedItem as HandoverViewModel;
            var isEnabled = hvm != null && !string.IsNullOrEmpty(hvm.RepoPath);
            BtnAction.IsEnabled = isEnabled;
            BtnAction.Background = isEnabled ? NewSessionButtonBrush : DisabledButtonBrush;
            BtnAction.Foreground = isEnabled ? EnabledTextBrush : DisabledTextBrush;
        }
    }

    private bool IsGitHubTabValid()
    {
        if (string.IsNullOrWhiteSpace(GhOwnerBox?.Text)) return false;
        if (string.IsNullOrWhiteSpace(GhRepoBox?.Text)) return false;
        if (string.IsNullOrWhiteSpace(GhPromptBox?.Text)) return false;

        // Existing-thread mode requires a positive issue/PR number.
        if (GhModeExisting?.IsChecked == true)
            return long.TryParse(GhThreadBox?.Text?.Trim(), out var n) && n > 0;

        return true;
    }

    private void GhField_Changed(object? sender, TextChangedEventArgs e) => UpdateActionButton();

    private void GhMode_Changed(object? sender, RoutedEventArgs e)
    {
        if (GhThreadBox is not null)
            GhThreadBox.IsEnabled = GhModeExisting?.IsChecked == true;
        UpdateActionButton();
    }

    private RemoteSessionConfig? BuildRemoteConfig()
    {
        if (!IsGitHubTabValid()) return null;

        var existing = GhModeExisting?.IsChecked == true;
        long? threadNumber = null;
        if (existing && long.TryParse(GhThreadBox?.Text?.Trim(), out var n))
            threadNumber = n;

        // Values are guaranteed non-empty by IsGitHubTabValid(); read them null-safely
        // (no null-forgiving operator) so the validation contract is the single source of truth.
        var branch = (GhBranchBox?.Text ?? string.Empty).Trim();

        return new RemoteSessionConfig
        {
            Owner = (GhOwnerBox?.Text ?? string.Empty).Trim(),
            Repo = (GhRepoBox?.Text ?? string.Empty).Trim(),
            BaseBranch = branch.Length == 0 ? "main" : branch,
            TriggerMode = existing ? RemoteTriggerMode.ExistingThread : RemoteTriggerMode.NewIssue,
            ThreadNumber = threadNumber,
            InitialPrompt = (GhPromptBox?.Text ?? string.Empty).Trim(),
        };
    }

    private void SessionSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_sessionsLoaded || _allSessions == null)
            return;

        var filter = SessionSearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            SessionList.ItemsSource = _allSessions;
        }
        else
        {
            SessionList.ItemsSource = _allSessions
                .Where(s => s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.ProjectName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.ProjectPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.DisplaySummary.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void HandoverSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_handoversLoaded || _allHandovers == null)
            return;

        var filter = HandoverSearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            HandoverList.ItemsSource = _allHandovers;
        }
        else
        {
            HandoverList.ItemsSource = _allHandovers
                .Where(h => h.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || h.RepoDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || h.DateDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || (h.SessionName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
    }

    private void RepoSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyRepoFilter();
    }

    private void ApplyRepoFilter()
    {
        if (_allRepos == null)
            return;

        var filter = RepoSearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            RepoList.ItemsSource = _allRepos;
        }
        else
        {
            RepoList.ItemsSource = _allRepos
                .Where(r => (r.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                         || (r.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
    }

    private void RepoHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string column)
            return;

        if (_repoSortColumn == column)
            _repoSortAscending = !_repoSortAscending;
        else
        {
            _repoSortColumn = column;
            _repoSortAscending = column != "LastUsed"; // LastUsed defaults descending
        }

        ApplyRepoSort();
        ApplyRepoFilter();
        UpdateRepoHeaderLabels();
        FileLog.Write($"[NewSessionDialog] RepoHeader_Click: sort={_repoSortColumn}, asc={_repoSortAscending}");
    }

    private void ApplyRepoSort()
    {
        if (_allRepos == null || _allRepos.Count == 0)
            return;

        _allRepos = _repoSortColumn switch
        {
            "Name" => _repoSortAscending
                ? _allRepos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : _allRepos.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "Path" => _repoSortAscending
                ? _allRepos.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList()
                : _allRepos.OrderByDescending(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => _repoSortAscending
                ? _allRepos.OrderBy(r => r.LastUsed ?? DateTime.MinValue).ToList()
                : _allRepos.OrderByDescending(r => r.LastUsed ?? DateTime.MinValue).ToList(),
        };
    }

    private void UpdateRepoHeaderLabels()
    {
        var arrow = _repoSortAscending ? "  ^" : "  v";
        RepoHeaderName.Content = "Name" + (_repoSortColumn == "Name" ? arrow : "");
        RepoHeaderName.Foreground = _repoSortColumn == "Name"
            ? new SolidColorBrush(Color.Parse("#CCCCCC"))
            : new SolidColorBrush(Color.Parse("#AAAAAA"));
        RepoHeaderPath.Content = "Path" + (_repoSortColumn == "Path" ? arrow : "");
        RepoHeaderPath.Foreground = _repoSortColumn == "Path"
            ? new SolidColorBrush(Color.Parse("#CCCCCC"))
            : new SolidColorBrush(Color.Parse("#AAAAAA"));
        RepoHeaderLastUsed.Content = "Last Used" + (_repoSortColumn == "LastUsed" ? arrow : "");
        RepoHeaderLastUsed.Foreground = _repoSortColumn == "LastUsed"
            ? new SolidColorBrush(Color.Parse("#CCCCCC"))
            : new SolidColorBrush(Color.Parse("#AAAAAA"));
    }

    private void SessionList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionHistoryViewModel vm)
        {
            SelectedResumeSessionId = vm.ClaudeSessionId;
            SelectedPath = vm.ProjectPath;
            FileLog.Write($"[NewSessionDialog] Session selected: claude={vm.ClaudeSessionId}, path: {vm.ProjectPath}");
        }
        else
        {
            SelectedResumeSessionId = null;
        }

        UpdateActionButton();
    }

    private void RepoList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RepoList.SelectedItem is RepositoryConfig repo)
        {
            PathInput.Text = repo.Path;
            SelectedPath = repo.Path;
            SelectedResumeSessionId = null;
            FileLog.Write($"[NewSessionDialog] Repo selected: {repo.Path}");
        }

        UpdateActionButton();
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[NewSessionDialog] BtnBrowse_Click");

        var storageProvider = StorageProvider;
        var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Repository Folder",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            var folderPath = result[0].Path.LocalPath;
            PathInput.Text = folderPath;
            SelectedPath = folderPath;
            SelectedResumeSessionId = null;

            RepoList.SelectedItem = null;

            if (_registry != null)
            {
                _registry.TryAdd(folderPath);
                _allRepos = _registry.Repositories.ToList();
                ApplyRepoSort();
                ApplyRepoFilter();
            }

            UpdateActionButton();
            FileLog.Write($"[NewSessionDialog] Browsed to: {folderPath}");
        }
    }

    private void BtnRemoveRepo_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string path)
            return;

        FileLog.Write($"[NewSessionDialog] BtnRemoveRepo_Click: {path}");

        if (_registry != null)
        {
            _registry.Remove(path);
            _allRepos = _registry.Repositories.ToList();
            ApplyRepoSort();
            ApplyRepoFilter();

            if (PathInput.Text == path)
            {
                PathInput.Text = string.Empty;
                SelectedPath = null;
                RepoList.SelectedItem = null;
                UpdateActionButton();
            }

            FileLog.Write($"[NewSessionDialog] Removed repository: {path}");
        }
    }

    private void BtnCoaching_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string category)
            return;

        FileLog.Write($"[NewSessionDialog] BtnCoaching_Click: category={category}");

        SelectedPath = CcStorage.CoachingCategory(category);
        SelectedResumeSessionId = null;
        BypassPermissionsCheckBox.IsChecked = true;

        FileLog.Write($"[NewSessionDialog] BtnCoaching_Click: starting session at {SelectedPath}");
        Close(true);
    }

    private void BtnAction_Click(object? sender, RoutedEventArgs e)
    {
        if (MainTabs.SelectedIndex == 0)
        {
            SelectedPath = PathInput.Text;
            SelectedResumeSessionId = null;

            if (string.IsNullOrWhiteSpace(SelectedPath))
            {
                FileLog.Write("[NewSessionDialog] BtnAction_Click: No path specified for new session");
                return;
            }

            // Capture custom CLI inputs when the RawCli agent is selected.
            if (SelectedAgentKind == AgentKind.RawCli)
            {
                SelectedCustomCommand = CustomCommandBox?.Text?.Trim();
                SelectedCustomArgs = CustomArgsBox?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(SelectedCustomCommand))
                {
                    FileLog.Write("[NewSessionDialog] BtnAction_Click: No command specified for Custom CLI session");
                    return;
                }
            }
            else
            {
                SelectedCustomCommand = null;
                SelectedCustomArgs = null;
            }

            FileLog.Write($"[NewSessionDialog] BtnAction_Click: Starting new session at {SelectedPath}, agent={SelectedAgentKind}, customCmd={SelectedCustomCommand ?? "(none)"}");
            Close(true);
        }
        else if (MainTabs.SelectedIndex == 1)
        {
            if (SessionList.SelectedItem is not SessionHistoryViewModel vm)
            {
                FileLog.Write("[NewSessionDialog] BtnAction_Click: No session selected for resume");
                return;
            }

            SelectedResumeSessionId = vm.ClaudeSessionId;
            SelectedPath = vm.ProjectPath;

            FileLog.Write($"[NewSessionDialog] BtnAction_Click: Resuming session claude={vm.ClaudeSessionId}, path={vm.ProjectPath}");
            Close(true);
        }
        else if (MainTabs.SelectedIndex == GitHubTabIndex)
        {
            var config = BuildRemoteConfig();
            if (config is null)
            {
                FileLog.Write("[NewSessionDialog] BtnAction_Click: GitHub config incomplete");
                return;
            }

            RemoteConfig = config;
            SelectedPath = null;
            SelectedResumeSessionId = null;
            FileLog.Write($"[NewSessionDialog] BtnAction_Click: Starting GitHub remote session for {config.Slug} mode={config.TriggerMode}");
            Close(true);
        }
        else
        {
            if (HandoverList.SelectedItem is not HandoverViewModel hvm || string.IsNullOrEmpty(hvm.RepoPath))
                return;

            SelectedPath = hvm.RepoPath;
            SelectedResumeSessionId = null;
            SelectedHandoverPath = hvm.FilePath;

            FileLog.Write($"[NewSessionDialog] BtnAction_Click: Starting session from handover, repo={hvm.RepoPath}, handover={hvm.FilePath}");
            Close(true);
        }
    }

    private async Task LoadHandoversAsync()
    {
        FileLog.Write("[NewSessionDialog] LoadHandoversAsync: starting");

        try
        {
            var dir = CcStorage.VaultHandovers();
            var files = await Task.Run(() =>
            {
                if (!Directory.Exists(dir))
                    return Array.Empty<string>();
                return Directory.GetFiles(dir, "*.md")
                    .OrderByDescending(f => Path.GetFileName(f))
                    .ToArray();
            });

            _handoversLoaded = true;
            HandoverLoadingText.IsVisible = false;

            if (files.Length > 0)
            {
                _allHandovers = files.Select(f => new HandoverViewModel(f)).ToList();
                HandoverList.ItemsSource = _allHandovers;
                HandoverList.IsVisible = true;
                FileLog.Write($"[NewSessionDialog] LoadHandoversAsync: found {files.Length} handovers");
            }
            else
            {
                NoHandoversText.IsVisible = true;
                FileLog.Write("[NewSessionDialog] LoadHandoversAsync: no handovers found");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NewSessionDialog] LoadHandoversAsync FAILED: {ex.Message}");
            HandoverLoadingText.IsVisible = false;
            NoHandoversText.Text = "Error loading handovers";
            NoHandoversText.IsVisible = true;
        }
    }

    private async void HandoverList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (HandoverList.SelectedItem is not HandoverViewModel hvm)
        {
            HandoverPreview.IsVisible = false;
            HandoverPlaceholder.IsVisible = true;
            HandoverRepoPath.Text = "No handover selected";
            UpdateActionButton();
            return;
        }

        FileLog.Write($"[NewSessionDialog] HandoverList_SelectionChanged: {hvm.FilePath}");

        HandoverRepoPath.Text = !string.IsNullOrEmpty(hvm.RepoPath)
            ? hvm.RepoPath
            : "Repository path not found in handover";

        var content = await Task.Run(() => File.ReadAllText(hvm.FilePath));
        HandoverPreview.Text = content;
        HandoverPreview.IsVisible = true;
        HandoverPlaceholder.IsVisible = false;
        UpdateActionButton();
    }

    private async void BtnCopyHandover_Click(object? sender, RoutedEventArgs e)
    {
        if (HandoverList.SelectedItem is not HandoverViewModel hvm)
            return;

        var content = await Task.Run(() => File.ReadAllText(hvm.FilePath));

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(content);

        FileLog.Write($"[NewSessionDialog] BtnCopyHandover_Click: Copied to clipboard: {hvm.FilePath}");

        BtnCopyHandover.Content = "Copied!";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            BtnCopyHandover.Content = "Copy to Clipboard";
            timer.Stop();
        };
        timer.Start();
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[NewSessionDialog] BtnCancel_Click");
        Close(false);
    }
}
