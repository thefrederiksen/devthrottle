using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CcDirector.Core.Browser;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class WorkflowEditorWindow : Window
{
    private readonly WorkflowStore _store;
    private readonly string _connection;
    private readonly string _workflowName;
    private WorkflowTemplate _template = new();
    private WorkflowTemplate _originalTemplate = new();
    private string _recordingDir = "";
    private bool _dirty;

    public WorkflowEditorWindow(WorkflowStore store, string connection, string workflowName)
    {
        InitializeComponent();
        _store = store;
        _connection = connection;
        _workflowName = workflowName;

        FileLog.Write($"[WorkflowEditor] Created: connection={connection}, workflow={workflowName}");

        Title = $"Workflow Editor: {workflowName}";
        TitleText.Text = "Loading...";

        Loaded += async (_, _) =>
        {
            try
            {
                var template = await Task.Run(() => store.LoadTemplate(connection, workflowName));
                if (template is null)
                    throw new InvalidOperationException($"Workflow not found: {workflowName}");

                _template = template;
                _recordingDir = store.RecordingDir(connection, workflowName);

                var json = JsonSerializer.Serialize(_template);
                _originalTemplate = JsonSerializer.Deserialize<WorkflowTemplate>(json)
                    ?? throw new InvalidOperationException("Failed to clone template for undo snapshot");

                TitleText.Text = $"EDITOR: {workflowName}";
                RefreshStepsList();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WorkflowEditor] Load FAILED: {ex.Message}");
                Close();
            }
        };
    }

    // -----------------------------------------------------------------------
    // Step list
    // -----------------------------------------------------------------------

    private void RefreshStepsList()
    {
        FileLog.Write($"[WorkflowEditor] RefreshStepsList: {_template.Steps.Count} steps");

        var entries = new List<StepDisplayEntry>();

        // Initial screenshot entry
        if (!string.IsNullOrEmpty(_template.InitialScreenshotFile))
        {
            var thumbPath = ResolveScreenshotPath(_template.InitialScreenshotFile);
            entries.Add(new StepDisplayEntry
            {
                StepLabel = "INITIAL",
                CommandSummary = "Starting state",
                Thumbnail = LoadThumbnail(thumbPath),
                ScreenshotPath = thumbPath,
                StepIndex = -1,
                IsInitial = true,
            });
        }

        AddStepEntries(entries, _template.Steps, 0, 0);

        StepsList.ItemsSource = entries;
        UpdateDirtyState();
    }

    private int AddStepEntries(List<StepDisplayEntry> entries, List<WorkflowStep> steps, int startIndex, int indent)
    {
        var idx = startIndex;
        foreach (var step in steps)
        {
            if (step.Type == "condition" && step.Condition != null)
            {
                var condLabel = step.Condition.Check switch
                {
                    "elementExists" => $"IF {step.Condition.Selector}",
                    "urlContains" => $"IF url contains \"{step.Condition.Value}\"",
                    "textVisible" => $"IF text \"{step.Condition.Value}\"",
                    _ => "IF ???",
                };

                entries.Add(new StepDisplayEntry
                {
                    StepLabel = new string(' ', indent * 2) + condLabel,
                    CommandSummary = $"then: {step.Condition.ThenSteps.Count}, else: {step.Condition.ElseSteps.Count}",
                    StepIndex = idx,
                    IsCondition = true,
                });

                if (step.Condition.ThenSteps.Count > 0)
                    AddStepEntries(entries, step.Condition.ThenSteps, 0, indent + 1);
                if (step.Condition.ElseSteps.Count > 0)
                {
                    entries.Add(new StepDisplayEntry
                    {
                        StepLabel = new string(' ', indent * 2) + "ELSE",
                        CommandSummary = "",
                        StepIndex = -2,
                    });
                    AddStepEntries(entries, step.Condition.ElseSteps, 0, indent + 1);
                }
            }
            else if (step.Action != null)
            {
                var ssPath = ResolveScreenshotPath(step.Action.ScreenshotFile);
                var paramSummary = "";
                if (step.Action.Params is { Count: > 0 })
                {
                    paramSummary = string.Join(", ",
                        step.Action.Params.Select(kv => $"{kv.Key}: {kv.Value}"));
                }

                entries.Add(new StepDisplayEntry
                {
                    StepLabel = $"#{idx + 1} {step.Action.Command}",
                    CommandSummary = paramSummary.Length > 40
                        ? paramSummary[..37] + "..."
                        : paramSummary,
                    Thumbnail = LoadThumbnail(ssPath),
                    ScreenshotPath = ssPath,
                    StepIndex = idx,
                });
                idx++;
            }
        }
        return idx;
    }

    private string? ResolveScreenshotPath(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var path = Path.Combine(_recordingDir, fileName);
        return File.Exists(path) ? path : null;
    }

    private static Bitmap? LoadThumbnail(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Selection
    // -----------------------------------------------------------------------

    private void StepsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            StepsList_SelectionChangedCore();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowEditor] SelectionChanged FAILED: {ex.Message}");
        }
    }

    private void StepsList_SelectionChangedCore()
    {
        ParamEditor.Children.Clear();
        ScreenshotPreview.Source = null;
        ScreenshotLabel.Text = "";

        if (StepsList.SelectedItem is not StepDisplayEntry entry) return;

        // Show screenshot preview
        if (!string.IsNullOrEmpty(entry.ScreenshotPath) && File.Exists(entry.ScreenshotPath))
        {
            try
            {
                using var stream = File.OpenRead(entry.ScreenshotPath);
                ScreenshotPreview.Source = new Bitmap(stream);
                ScreenshotLabel.Text = Path.GetFileName(entry.ScreenshotPath);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WorkflowEditor] Screenshot load FAILED: {ex.Message}");
            }
        }

        if (entry.IsInitial)
        {
            AddParamLabel("Initial screenshot - starting state before first action.");
            return;
        }

        if (entry.IsCondition)
        {
            ShowConditionDetail(entry);
            return;
        }

        // Show action detail with editable params
        var step = FindStepByFlatIndex(entry.StepIndex);
        if (step?.Action == null) return;

        AddParamLabel($"Command: {step.Action.Command}");

        if (step.Action.Params != null)
        {
            foreach (var kv in step.Action.Params.ToList())
            {
                var label = new TextBlock
                {
                    Text = kv.Key,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 10,
                    Margin = new global::Avalonia.Thickness(0, 8, 0, 2),
                };
                ParamEditor.Children.Add(label);

                var key = kv.Key;
                var textBox = new TextBox
                {
                    Text = kv.Value?.ToString() ?? "",
                    Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
                    BorderThickness = new global::Avalonia.Thickness(1),
                    Padding = new global::Avalonia.Thickness(6, 3, 6, 3),
                    FontSize = 11,
                };

                var capturedIndex = entry.StepIndex;
                textBox.LostFocus += (_, _) =>
                {
                    var s = FindStepByFlatIndex(capturedIndex);
                    if (s?.Action?.Params != null)
                    {
                        s.Action.Params[key] = textBox.Text ?? "";
                        _dirty = true;
                        UpdateDirtyState();
                    }
                };

                ParamEditor.Children.Add(textBox);
            }
        }
    }

    private void ShowConditionDetail(StepDisplayEntry entry)
    {
        var step = FindStepByFlatIndex(entry.StepIndex);
        if (step?.Condition == null) return;

        AddParamLabel($"Condition: {step.Condition.Check}");

        if (!string.IsNullOrEmpty(step.Condition.Selector))
            AddParamLabel($"Selector: {step.Condition.Selector}");
        if (!string.IsNullOrEmpty(step.Condition.Value))
            AddParamLabel($"Value: {step.Condition.Value}");

        AddParamLabel($"Then: {step.Condition.ThenSteps.Count} steps");
        AddParamLabel($"Else: {step.Condition.ElseSteps.Count} steps");
    }

    private void AddParamLabel(string text)
    {
        ParamEditor.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize = 11,
            Margin = new global::Avalonia.Thickness(0, 2, 0, 2),
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
        });
    }

    // -----------------------------------------------------------------------
    // Editing operations
    // -----------------------------------------------------------------------

    private void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (StepsList.SelectedItem is not StepDisplayEntry entry) return;
        if (entry.IsInitial || entry.StepIndex < 0) return;

        FileLog.Write($"[WorkflowEditor] Delete step: index={entry.StepIndex}");

        if (RemoveStepByFlatIndex(entry.StepIndex))
        {
            _dirty = true;
            RefreshStepsList();
        }
    }

    private void BtnMoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if (StepsList.SelectedItem is not StepDisplayEntry entry) return;
        if (entry.IsInitial || entry.StepIndex <= 0) return;

        FileLog.Write($"[WorkflowEditor] Move up step: index={entry.StepIndex}");

        if (SwapStepByFlatIndex(entry.StepIndex, -1))
        {
            _dirty = true;
            RefreshStepsList();
            SelectStepByFlatIndex(entry.StepIndex - 1);
        }
    }

    private void BtnMoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if (StepsList.SelectedItem is not StepDisplayEntry entry) return;
        if (entry.IsInitial) return;

        FileLog.Write($"[WorkflowEditor] Move down step: index={entry.StepIndex}");

        if (SwapStepByFlatIndex(entry.StepIndex, +1))
        {
            _dirty = true;
            RefreshStepsList();
            SelectStepByFlatIndex(entry.StepIndex + 1);
        }
    }

    private void BtnDuplicate_Click(object? sender, RoutedEventArgs e)
    {
        if (StepsList.SelectedItem is not StepDisplayEntry entry) return;
        if (entry.IsInitial || entry.StepIndex < 0) return;

        FileLog.Write($"[WorkflowEditor] Duplicate step: index={entry.StepIndex}");

        var step = FindStepByFlatIndex(entry.StepIndex);
        if (step == null) return;

        var json = JsonSerializer.Serialize(step);
        var clone = JsonSerializer.Deserialize<WorkflowStep>(json);
        if (clone == null) return;

        InsertStepAfter(entry.StepIndex, clone);
        _dirty = true;
        RefreshStepsList();
    }

    private async void BtnAddCondition_Click(object? sender, RoutedEventArgs e)
    {
        if (StepsList.SelectedItem is not StepDisplayEntry entry) return;
        if (entry.IsInitial || entry.StepIndex < 0) return;

        FileLog.Write($"[WorkflowEditor] Add condition for step: index={entry.StepIndex}");

        var dialog = new WorkflowConditionDialog();
        var result = await dialog.ShowDialog<bool?>(this);
        if (result != true) return;

        var step = FindStepByFlatIndex(entry.StepIndex);
        if (step == null) return;

        // Clone the step to put in thenSteps
        var json = JsonSerializer.Serialize(step);
        var clonedStep = JsonSerializer.Deserialize<WorkflowStep>(json);
        if (clonedStep == null) return;

        // Replace the step with a condition wrapping it
        var conditionStep = new WorkflowStep
        {
            Type = "condition",
            Condition = new WorkflowCondition
            {
                Check = dialog.SelectedCheck,
                Selector = dialog.SelectorValue,
                Value = dialog.CheckValue,
                ThenSteps = new List<WorkflowStep> { clonedStep },
            },
        };

        ReplaceStep(entry.StepIndex, conditionStep);
        _dirty = true;
        RefreshStepsList();
    }

    // -----------------------------------------------------------------------
    // Save / Undo / Close
    // -----------------------------------------------------------------------

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[WorkflowEditor] BtnSave_Click: {_template.Steps.Count} steps");

        // Sync steps back to actions for backward compat
        _template.Actions.Clear();
        FlattenStepsToActions(_template.Steps, _template.Actions);

        _store.SaveTemplate(_template);

        var json = JsonSerializer.Serialize(_template);
        _originalTemplate = JsonSerializer.Deserialize<WorkflowTemplate>(json)
            ?? throw new InvalidOperationException("Failed to clone template after save");

        _dirty = false;
        UpdateDirtyState();

        FileLog.Write("[WorkflowEditor] Template saved");
    }

    private static void FlattenStepsToActions(List<WorkflowStep> steps, List<WorkflowAction> actions)
    {
        foreach (var step in steps)
        {
            if (step.Type == "action" && step.Action != null)
            {
                actions.Add(step.Action);
            }
            else if (step.Type == "condition" && step.Condition != null)
            {
                FlattenStepsToActions(step.Condition.ThenSteps, actions);
                FlattenStepsToActions(step.Condition.ElseSteps, actions);
            }
        }
    }

    private void BtnUndoAll_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[WorkflowEditor] BtnUndoAll_Click");

        var json = JsonSerializer.Serialize(_originalTemplate);
        _template = JsonSerializer.Deserialize<WorkflowTemplate>(json)
            ?? throw new InvalidOperationException("Failed to restore original template");
        _dirty = false;
        RefreshStepsList();
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        // If dirty, just close -- Avalonia doesn't have sync MessageBox
        Close();
    }

    // -----------------------------------------------------------------------
    // Screenshot viewer
    // -----------------------------------------------------------------------

    private void ScreenshotPreview_DoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (StepsList.SelectedItem is not StepDisplayEntry entry) return;
            if (string.IsNullOrEmpty(entry.ScreenshotPath) || !File.Exists(entry.ScreenshotPath)) return;

            FileLog.Write($"[WorkflowEditor] Opening screenshot: {entry.ScreenshotPath}");

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = entry.ScreenshotPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowEditor] Screenshot viewer FAILED: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // UI Helpers
    // -----------------------------------------------------------------------

    private void UpdateDirtyState()
    {
        TitleText.Text = _dirty
            ? $"EDITOR: {_workflowName} *"
            : $"EDITOR: {_workflowName}";
    }

    private void SelectStepByFlatIndex(int flatIndex)
    {
        if (StepsList.ItemsSource is List<StepDisplayEntry> entries)
        {
            var match = entries.FirstOrDefault(e => e.StepIndex == flatIndex);
            if (match != null)
                StepsList.SelectedItem = match;
        }
    }

    // -----------------------------------------------------------------------
    // Step tree visitor -- single traversal used by all flat-index operations
    // -----------------------------------------------------------------------

    private static bool VisitStepByFlatIndex(
        List<WorkflowStep> steps,
        int targetIndex,
        int currentIndex,
        Func<List<WorkflowStep>, int, bool> onFound,
        out int nextIndex)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (currentIndex == targetIndex)
            {
                nextIndex = currentIndex + 1;
                return onFound(steps, i);
            }
            currentIndex++;

            var step = steps[i];
            if (step.Type == "condition" && step.Condition != null)
            {
                if (VisitStepByFlatIndex(step.Condition.ThenSteps, targetIndex, currentIndex, onFound, out var n))
                {
                    nextIndex = n;
                    return true;
                }
                currentIndex = n;

                if (VisitStepByFlatIndex(step.Condition.ElseSteps, targetIndex, currentIndex, onFound, out n))
                {
                    nextIndex = n;
                    return true;
                }
                currentIndex = n;
            }
        }

        nextIndex = currentIndex;
        return false;
    }

    private WorkflowStep? FindStepByFlatIndex(int targetIndex)
    {
        WorkflowStep? found = null;
        VisitStepByFlatIndex(_template.Steps, targetIndex, 0,
            (list, i) => { found = list[i]; return true; },
            out _);
        return found;
    }

    private bool RemoveStepByFlatIndex(int targetIndex)
    {
        return VisitStepByFlatIndex(_template.Steps, targetIndex, 0,
            (list, i) => { list.RemoveAt(i); return true; },
            out _);
    }

    private bool SwapStepByFlatIndex(int targetIndex, int direction)
    {
        return VisitStepByFlatIndex(_template.Steps, targetIndex, 0,
            (list, i) =>
            {
                var newI = i + direction;
                if (newI < 0 || newI >= list.Count) return false;
                (list[i], list[newI]) = (list[newI], list[i]);
                return true;
            },
            out _);
    }

    private bool InsertStepAfter(int targetIndex, WorkflowStep newStep)
    {
        return VisitStepByFlatIndex(_template.Steps, targetIndex, 0,
            (list, i) => { list.Insert(i + 1, newStep); return true; },
            out _);
    }

    private bool ReplaceStep(int targetIndex, WorkflowStep replacement)
    {
        return VisitStepByFlatIndex(_template.Steps, targetIndex, 0,
            (list, i) => { list[i] = replacement; return true; },
            out _);
    }

    // -----------------------------------------------------------------------
    // Data classes
    // -----------------------------------------------------------------------

    private class StepDisplayEntry
    {
        public string StepLabel { get; set; } = "";
        public string CommandSummary { get; set; } = "";
        public Bitmap? Thumbnail { get; set; }
        public string? ScreenshotPath { get; set; }
        public int StepIndex { get; set; }
        public bool IsInitial { get; set; }
        public bool IsCondition { get; set; }
    }
}
