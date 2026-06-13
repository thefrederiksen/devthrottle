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

    private sealed record ToolRow(AgentKind Tool, CheckBox Check, string ResolvedPath, bool Found);

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
            var suggestions = await Task.Run(() => _model.ScanSuggestions(_options));
            BuildRows(suggestions);

            var foundCount = suggestions.Count(s => s.Found);
            ScanStatusText.Text = foundCount > 0
                ? $"Found {foundCount} of {suggestions.Count} known tools. Review the selection and click Add selected tools."
                : $"No known agent tools were found on this machine. Install one, or close this and configure a path on the Agents tab in Settings.";
            ScanStatusText.FontStyle = FontStyle.Normal;
            AcceptButton.IsEnabled = foundCount > 0;
            FileLog.Write($"[ToolDetectionWizardDialog] ScanAsync: found={foundCount}, total={suggestions.Count}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolDetectionWizardDialog] ScanAsync FAILED: {ex.Message}");
            ScanStatusText.Text = $"Tool scan failed: {ex.Message}";
            ScanStatusText.Foreground = Brushes.IndianRed;
        }
    }

    private void BuildRows(IReadOnlyList<ToolDetectionSuggestion> suggestions)
    {
        _rows.Clear();
        ToolListPanel.Children.Clear();

        foreach (var s in suggestions)
        {
            var check = new CheckBox
            {
                IsChecked = s.Found,
                IsEnabled = s.Found,
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

            var statusBadge = new Border
            {
                Background = Brush(s.Found ? "#1B3A2A" : "#3A2A1B"),
                CornerRadius = new global::Avalonia.CornerRadius(3),
                Padding = new global::Avalonia.Thickness(6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = s.Found ? "FOUND" : "NOT FOUND",
                    Foreground = Brush(s.Found ? "#22C55E" : "#F59E0B"),
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
            if (s.Found)
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
            _rows.Add(new ToolRow(s.Tool, check, s.ResolvedPath, s.Found));
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

            var written = await Task.Run(() => ToolDetectionWizardModel.AcceptSelected(selections));

            // Apply the accepted Claude command line + paths to the running options so the next
            // session launched without a restart uses them (mirrors the Settings dialog wiring).
            ApplyAcceptedToRunningOptions(selections);

            FileLog.Write($"[ToolDetectionWizardDialog] BtnAccept_Click: wrote {written} tool(s)");
            Close(true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolDetectionWizardDialog] BtnAccept_Click FAILED: {ex.Message}");
            ShowResult($"Could not save the selected tools: {ex.Message}", error: true);
            AcceptButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Push the just-saved per-tool config onto the running AgentOptions so a session launched
    /// without restarting picks up the accepted tool paths and the Claude default command line.
    /// </summary>
    private void ApplyAcceptedToRunningOptions(IReadOnlyList<AcceptedToolSelection> selections)
    {
        var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options
            ?? (global::Avalonia.Application.Current as App)?.Options;
        if (options is null)
        {
            FileLog.Write("[ToolDetectionWizardDialog] ApplyAcceptedToRunningOptions: no running options; skipped");
            return;
        }

        foreach (var selection in selections)
        {
            if (!string.IsNullOrWhiteSpace(selection.ResolvedPath))
                ToolDetectionService.SetConfiguredPath(selection.Tool, options, selection.ResolvedPath);
        }

        var claudeConfig = AgentToolConfig.Load(AgentKind.ClaudeCode);
        var args = claudeConfig.ResolveEffectiveArguments().Trim();
        var model = claudeConfig.DefaultModel?.Trim() ?? "";
        if (model.Length > 0 && !args.Contains("--model", StringComparison.OrdinalIgnoreCase))
            args = string.IsNullOrEmpty(args) ? $"--model {model}" : $"{args} --model {model}";
        options.DefaultClaudeArgs = args;
        FileLog.Write($"[ToolDetectionWizardDialog] ApplyAcceptedToRunningOptions: claudeArgs='{args}'");
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
