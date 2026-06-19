using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Drivers;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The dedicated model picker opened from the Edit Agent dialog's "Choose model..." button
/// (issue #527). The model list is supplied by the agent's DRIVER (<see cref="IAgentDriver.KnownModels"/>),
/// so model knowledge lives with the driver, not the UI - the same pattern as driver-owned slash
/// commands. "Use default" is the recommended top option: it selects no model, so the tool uses
/// its own configured default. A free-text "Custom model ID" row accepts any alias or full id.
/// </summary>
public partial class ModelPickerDialog : Window
{
    private const string GroupName = "modelPickerGroup";

    private readonly List<(RadioButton Radio, string Id)> _knownRadios = new();
    private RadioButton? _defaultRadio;
    private RadioButton? _customRadio;
    private TextBox? _customBox;

    /// <summary>The model the user chose ("" = use the tool's default), or null if cancelled.</summary>
    public string? SelectedModelId { get; private set; }

    public ModelPickerDialog() : this(Array.Empty<AgentModelOption>(), "", null, "the agent") { }

    /// <param name="models">The driver's known models (never includes the "use default" option).</param>
    /// <param name="currentId">The currently-selected model id ("" = use default).</param>
    /// <param name="detectedDefault">The tool's configured default model, for a hint, or null.</param>
    /// <param name="toolLabel">Friendly tool name shown in the header.</param>
    public ModelPickerDialog(
        IReadOnlyList<AgentModelOption> models, string currentId, string? detectedDefault, string toolLabel)
    {
        FileLog.Write($"[ModelPickerDialog] Constructor: tool={toolLabel}, current='{currentId}', models={models.Count}");
        InitializeComponent();

        HeaderText.Text = $"Choose model for {toolLabel}";
        DetectedText.Text = string.IsNullOrWhiteSpace(detectedDefault)
            ? "\"Use default\" passes no model flag, so the tool uses its own configured default."
            : $"\"Use default\" passes no model flag. {toolLabel}'s configured default is currently \"{detectedDefault}\".";

        BuildOptions(models, currentId);
    }

    private void BuildOptions(IReadOnlyList<AgentModelOption> models, string currentId)
    {
        // 1. Use default (recommended) - the top, no-model option.
        _defaultRadio = MakeRadio("Use default", "recommended", "Omits the model flag; the tool uses its own configured default.");
        OptionsPanel.Children.Add(_defaultRadio);

        // 2. The driver's known models.
        foreach (var model in models)
        {
            var radio = MakeRadio(model.DisplayName, model.Badge, model.Description);
            _knownRadios.Add((radio, model.Id));
            OptionsPanel.Children.Add(radio);
        }

        // 3. Custom model id (free text).
        _customRadio = MakeRadio("Custom model ID", "", "Type any alias or full id (e.g. claude-opus-4-8[1m]).");
        OptionsPanel.Children.Add(_customRadio);

        _customBox = new TextBox
        {
            Classes = { "settingsInput" },
            Watermark = "claude-opus-4-8[1m]",
            Margin = new Thickness(30, 0, 6, 8),
        };
        _customBox.GotFocus += (_, _) => { if (_customRadio is not null) _customRadio.IsChecked = true; };
        OptionsPanel.Children.Add(_customBox);

        // Initial selection from the current id.
        if (string.IsNullOrWhiteSpace(currentId))
        {
            _defaultRadio.IsChecked = true;
            return;
        }

        foreach (var (radio, id) in _knownRadios)
        {
            if (string.Equals(id, currentId, StringComparison.OrdinalIgnoreCase))
            {
                radio.IsChecked = true;
                return;
            }
        }

        // Not the default and not a known model -> it's a custom id.
        _customBox.Text = currentId;
        _customRadio.IsChecked = true;
    }

    /// <summary>Build one radio with a bold title, an optional badge, and a description line.</summary>
    private static RadioButton MakeRadio(string title, string badge, string description)
    {
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush.Parse("#CCCCCC"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrEmpty(badge))
        {
            titleRow.Children.Add(new Border
            {
                Background = Brush.Parse("#1F4D2E"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = badge, Foreground = Brush.Parse("#9FE0AF"), FontSize = 10 },
            });
        }

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(titleRow);
        if (!string.IsNullOrEmpty(description))
        {
            content.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = Brush.Parse("#888888"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new RadioButton
        {
            GroupName = GroupName,
            Content = content,
            Foreground = Brush.Parse("#CCCCCC"),
            Padding = new Thickness(6, 6),
            Margin = new Thickness(0, 1),
        };
    }

    private void BtnUse_Click(object? sender, RoutedEventArgs e)
    {
        SelectedModelId = ResolveSelection();
        FileLog.Write($"[ModelPickerDialog] BtnUse_Click: selected='{SelectedModelId}'");
        Close();
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ModelPickerDialog] BtnCancel_Click");
        SelectedModelId = null;
        Close();
    }

    private string ResolveSelection()
    {
        if (_customRadio?.IsChecked == true)
            return _customBox?.Text?.Trim() ?? "";

        foreach (var (radio, id) in _knownRadios)
        {
            if (radio.IsChecked == true)
                return id;
        }

        // Default radio (or nothing) selected -> no model.
        return "";
    }
}
