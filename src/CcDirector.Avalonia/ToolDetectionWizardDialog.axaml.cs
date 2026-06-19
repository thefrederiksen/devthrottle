using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// First-run tool-detection wizard (issue #392). Scans this machine for the known agent CLIs
/// via <see cref="ToolDetectionWizardModel"/> (which reuses <see cref="ToolDetectionService"/>),
/// shows each catalog tool with a found / not-found status, pre-checks the found ones with their
/// recommended command line and default model, and writes the user-accepted tools to the
/// machine-level config.json on accept. No tool is ever written with
/// --dangerously-skip-permissions: the catalog default (Standard) preset is used.
///
/// Opened automatically on first run (no tools configured) and re-runnable on demand from the
/// Settings &gt; Agents tab. Returns dialog result true when the user accepted at least one tool.
/// </summary>
public partial class ToolDetectionWizardDialog : Window
{
    private readonly AgentOptions _options;
    private readonly ToolDetectionWizardModel _model = new(new ToolDetectionService());

    // One checkbox per catalog tool, keyed by tool, so Accept can read each tool's checked state
    // and the resolved path the scan found for it.
    private readonly List<ToolRow> _rows = new();

    private sealed record ToolRow(AgentKind Tool, string DisplayName, CheckBox Check, string ResolvedPath, bool Found, bool AlreadyAdded);

    /// <summary>
    /// The outcome of the last accept (added vs skipped tools), so the caller can report honestly
    /// after the dialog closes. Null until the user accepts at least once.
    /// </summary>
    public WizardAcceptResult? LastResult { get; private set; }

    public ToolDetectionWizardDialog() : this(new AgentOptions()) { }

    public ToolDetectionWizardDialog(AgentOptions options)
    {
        FileLog.Write("[ToolDetectionWizardDialog] Constructor: initializing");
        _options = options ?? throw new ArgumentNullException(nameof(options));
        InitializeComponent();

        Loaded += async (_, _) => await ScanAsync();
    }

    /// <summary>
    /// Run the detection scan off the UI thread, then build one row per catalog tool: found tools
    /// are pre-checked and show their recommended command line + model; not-found tools are shown
    /// disabled and unchecked. Keeps the UI responsive with an immediate "Scanning..." status.
    /// </summary>
    private async Task ScanAsync()
    {
        FileLog.Write("[ToolDetectionWizardDialog] ScanAsync");
        try
        {
            // Scan the catalog AND read the user's current agent.entries (without seeding) on the
            // background thread, so we can mark tools already in the list and pre-check only the
            // genuinely new ones.
            var (suggestions, existingTypes) = await Task.Run(() =>
            {
                var scanned = _model.ScanSuggestions(_options);
                var present = new HashSet<AgentKind>(AgentEntryStore.ReadCurrentEntries().Select(e => e.Type));
                return (scanned, present);
            });
            BuildRows(suggestions, existingTypes);

            var foundCount = suggestions.Count(s => s.Found);
            var addableCount = suggestions.Count(s => s.Found && !existingTypes.Contains(s.Tool));
            var alreadyCount = suggestions.Count(s => s.Found && existingTypes.Contains(s.Tool));

            if (foundCount == 0)
                ScanStatusText.Text = "No known agent tools were found on this machine. Install one, or close this and configure a path on the Agents tab in Settings.";
            else if (addableCount == 0)
                ScanStatusText.Text = $"Found {foundCount} of {suggestions.Count} known tools, but all are already in your Agents list. Nothing new to add.";
            else
                ScanStatusText.Text = alreadyCount > 0
                    ? $"Found {foundCount} of {suggestions.Count} known tools ({addableCount} new, {alreadyCount} already in your list). Review the selection and click Add selected tools."
                    : $"Found {foundCount} of {suggestions.Count} known tools. Review the selection and click Add selected tools.";

            ScanStatusText.FontStyle = FontStyle.Normal;
            AcceptButton.IsEnabled = addableCount > 0;
            FileLog.Write($"[ToolDetectionWizardDialog] ScanAsync: found={foundCount}, addable={addableCount}, alreadyAdded={alreadyCount}, total={suggestions.Count}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolDetectionWizardDialog] ScanAsync FAILED: {ex.Message}");
            ScanStatusText.Text = $"Tool scan failed: {ex.Message}";
            ScanStatusText.Foreground = Brushes.IndianRed;
        }
    }

    private void BuildRows(IReadOnlyList<ToolDetectionSuggestion> suggestions, ISet<AgentKind> existingTypes)
    {
        _rows.Clear();
        ToolListPanel.Children.Clear();

        foreach (var s in suggestions)
        {
            // A tool already in agent.entries is shown disabled and unchecked with an "ALREADY
            // ADDED" badge, so the checkboxes represent exactly what Accept will add. Only a found
            // tool that is NOT already present is checkable/pre-checked.
            var alreadyAdded = existingTypes.Contains(s.Tool);
            var addable = s.Found && !alreadyAdded;

            var check = new CheckBox
            {
                IsChecked = addable,
                IsEnabled = addable,
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.IsCheckedChanged += (_, _) => UpdateAcceptEnabled();

            var name = new TextBlock
            {
                Text = s.DisplayName,
                Foreground = Brush(s.Found ? "#CCCCCC" : "#888888"),
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var badgeText = alreadyAdded ? "ALREADY ADDED" : s.Found ? "FOUND" : "NOT FOUND";
            var badgeBg = alreadyAdded ? "#1B2A3A" : s.Found ? "#1B3A2A" : "#3A2A1B";
            var badgeFg = alreadyAdded ? "#60A5FA" : s.Found ? "#22C55E" : "#F59E0B";
            var statusBadge = new Border
            {
                Background = Brush(badgeBg),
                CornerRadius = new global::Avalonia.CornerRadius(3),
                Padding = new global::Avalonia.Thickness(6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = badgeText,
                    Foreground = Brush(badgeFg),
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                },
            };

            var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            global::Avalonia.Controls.Grid.SetColumn(check, 0);
            global::Avalonia.Controls.Grid.SetColumn(name, 1);
            global::Avalonia.Controls.Grid.SetColumn(statusBadge, 2);
            name.Margin = new global::Avalonia.Thickness(8, 0, 8, 0);
            headerRow.Children.Add(check);
            headerRow.Children.Add(name);
            headerRow.Children.Add(statusBadge);

            var details = new StackPanel { Spacing = 2, Margin = new global::Avalonia.Thickness(28, 4, 0, 0) };
            if (alreadyAdded)
            {
                details.Children.Add(DetailLine("Already in your Agents list - it will not be added again."));
                if (s.Found)
                    details.Children.Add(DetailLine($"Path: {s.ResolvedPath}"));
            }
            else if (s.Found)
            {
                details.Children.Add(DetailLine($"Path: {s.ResolvedPath}"));
                details.Children.Add(DetailLine($"Command line: {s.RecommendedPresetName}"));
                details.Children.Add(DetailLine(string.IsNullOrWhiteSpace(s.RecommendedModel)
                    ? "Default model: (tool default)"
                    : $"Default model: {s.RecommendedModel}"));
            }
            else
            {
                details.Children.Add(DetailLine(s.DetectionMessage));
            }

            var card = new Border
            {
                Background = Brush("#1E1E1E"),
                BorderBrush = Brush("#3C3C3C"),
                BorderThickness = new global::Avalonia.Thickness(1),
                Padding = new global::Avalonia.Thickness(10),
                Child = new StackPanel { Spacing = 4, Children = { headerRow, details } },
            };

            ToolListPanel.Children.Add(card);
            _rows.Add(new ToolRow(s.Tool, s.DisplayName, check, s.ResolvedPath, s.Found, alreadyAdded));
        }
    }

    private static TextBlock DetailLine(string text) => new()
    {
        Text = text,
        Foreground = Brush("#AAAAAA"),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
    };

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    private void UpdateAcceptEnabled()
    {
        AcceptButton.IsEnabled = _rows.Any(r => r.Check.IsChecked == true);
    }

    private async void BtnAccept_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ToolDetectionWizardDialog] BtnAccept_Click");
        AcceptButton.IsEnabled = false;
        try
        {
            var selections = _rows
                .Where(r => r.Check.IsChecked == true)
                .Select(r => new AcceptedToolSelection(r.Tool, r.ResolvedPath))
                .ToList();

            if (selections.Count == 0)
            {
                ShowResult("Select at least one tool, or click Skip for now.", error: true);
                AcceptButton.IsEnabled = true;
                return;
            }

            var result = await Task.Run(() => ToolDetectionWizardModel.AcceptSelected(selections));
            LastResult = result;

            FileLog.Write($"[ToolDetectionWizardDialog] BtnAccept_Click: added={result.AddedTools.Count}, skipped={result.SkippedTools.Count}");
            Close(true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolDetectionWizardDialog] BtnAccept_Click FAILED: {ex.Message}");
            ShowResult($"Could not save the selected tools: {ex.Message}", error: true);
            AcceptButton.IsEnabled = true;
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ToolDetectionWizardDialog] BtnCancel_Click: closing without changes");
        Close(false);
    }

    private void ShowResult(string text, bool error)
    {
        ResultStatusText.Text = text;
        ResultStatusText.IsVisible = true;
        ResultStatusText.Foreground = error ? Brushes.IndianRed : Brushes.MediumSeaGreen;
    }
}
