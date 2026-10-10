using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CcDirector.Core.Browser;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class WorkflowRunsDialog : Window
{
    private readonly WorkflowStore _store;
    private readonly string _connection;
    private readonly string _workflowName;
    private readonly List<RunEntry> _runEntries = new();

    public WorkflowRunsDialog(WorkflowStore store, string connection, string workflowName)
    {
        InitializeComponent();
        _store = store;
        _connection = connection;
        _workflowName = workflowName;

        FileLog.Write($"[WorkflowRunsDialog] Created: connection={connection}, workflow={workflowName}");

        Title = $"Runs - {workflowName}";
        RunsHeader.Text = $"RUNS: {workflowName}";

        Loaded += async (_, _) =>
        {
            try
            {
                var runs = await Task.Run(() => _store.ListRuns(connection, workflowName));
                LoadRuns(runs);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WorkflowRunsDialog] Load FAILED: {ex.Message}");
            }
        };
    }

    private void LoadRuns(List<WorkflowRun> runs)
    {
        FileLog.Write($"[WorkflowRunsDialog] LoadRuns: {runs.Count} runs");
        _runEntries.Clear();

        foreach (var run in runs)
        {
            var statusColor = run.Status switch
            {
                "completed" => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
                "failed" => new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E)),
                "cancelled" => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
                _ => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            };

            _runEntries.Add(new RunEntry
            {
                Run = run,
                DisplayTime = FormatTime(run.StartedAt),
                StatusText = run.Status.ToUpperInvariant(),
                StatusColor = statusColor,
                StepSummary = $"{run.Steps.Count} steps",
            });
        }

        RunsList.ItemsSource = _runEntries;
    }

    private void RunsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (RunsList.SelectedItem is not RunEntry entry)
            {
                StepsList.ItemsSource = null;
                ParamValuesText.Text = "";
                return;
            }

            FileLog.Write($"[WorkflowRunsDialog] Selected run: {entry.Run.Id}");

            // Show parameter values
            if (entry.Run.ParameterValues.Count > 0)
            {
                ParamValuesText.Text = "Parameters: " + string.Join(", ",
                    entry.Run.ParameterValues.Select(kv => $"{kv.Key}=\"{kv.Value}\""));
            }
            else
            {
                ParamValuesText.Text = "";
            }

            // Build step entries
            var ssDir = _store.RunScreenshotDir(_connection, _workflowName, entry.Run.Id);
            var stepEntries = new List<StepEntry>();

            foreach (var step in entry.Run.Steps)
            {
                var stepStatusColor = step.Status switch
                {
                    "completed" => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
                    "failed" => new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E)),
                    _ => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                };

                Bitmap? thumbnail = null;
                string? fullPath = null;
                if (!string.IsNullOrEmpty(step.ScreenshotFile))
                {
                    fullPath = Path.Combine(ssDir, step.ScreenshotFile);
                    if (File.Exists(fullPath))
                    {
                        try
                        {
                            using var stream = File.OpenRead(fullPath);
                            thumbnail = new Bitmap(stream);
                        }
                        catch (Exception ex)
                        {
                            FileLog.Write($"[WorkflowRunsDialog] Thumbnail load FAILED: {ex.Message}");
                        }
                    }
                }

                var paramsDisplay = "";
                if (step.Params is { Count: > 0 })
                {
                    paramsDisplay = string.Join(", ",
                        step.Params.Select(kv => $"{kv.Key}: {kv.Value}"));
                }

                stepEntries.Add(new StepEntry
                {
                    Step = step,
                    CommandDisplay = $"#{step.Index + 1} {step.Command}",
                    ParamsDisplay = paramsDisplay,
                    StatusText = step.Status.ToUpperInvariant(),
                    StatusColor = stepStatusColor,
                    DurationDisplay = $"{step.DurationMs}ms",
                    ThumbnailPath = thumbnail,
                    FullScreenshotPath = fullPath,
                });
            }

            StepsList.ItemsSource = stepEntries;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowRunsDialog] SelectionChanged FAILED: {ex.Message}");
        }
    }

    private void StepsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (StepsList.SelectedItem is not StepEntry entry) return;
            if (string.IsNullOrEmpty(entry.FullScreenshotPath)) return;
            if (!File.Exists(entry.FullScreenshotPath)) return;

            FileLog.Write($"[WorkflowRunsDialog] Opening screenshot: {entry.FullScreenshotPath}");

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = entry.FullScreenshotPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowRunsDialog] Screenshot viewer FAILED: {ex.Message}");
        }
    }

    private static string FormatTime(string isoTime)
    {
        if (DateTime.TryParse(isoTime, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        return isoTime;
    }

    private class RunEntry
    {
        public required WorkflowRun Run { get; set; }
        public string DisplayTime { get; set; } = "";
        public string StatusText { get; set; } = "";
        public ISolidColorBrush StatusColor { get; set; } = new SolidColorBrush(Colors.Gray);
        public string StepSummary { get; set; } = "";
    }

    private class StepEntry
    {
        public required WorkflowRunStep Step { get; set; }
        public string CommandDisplay { get; set; } = "";
        public string ParamsDisplay { get; set; } = "";
        public string StatusText { get; set; } = "";
        public ISolidColorBrush StatusColor { get; set; } = new SolidColorBrush(Colors.Gray);
        public string DurationDisplay { get; set; } = "";
        public Bitmap? ThumbnailPath { get; set; }
        public string? FullScreenshotPath { get; set; }
    }
}
