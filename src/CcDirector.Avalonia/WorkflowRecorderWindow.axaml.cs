using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CcDirector.Core.Browser;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class WorkflowRecorderWindow : Window
{
    private readonly string _connectionName;
    private readonly int _daemonPort;
    private readonly List<RecordedAction> _actions = new();
    private readonly List<WorkflowFile> _savedWorkflows = new();
    private readonly WorkflowStore _store = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private DispatcherTimer? _pollTimer;
    private string? _recordingSince;
    private DateTime _recordingStartTime;
    private string? _recordingTempDir;
    private string? _initialScreenshotFile;
    private int _lastScreenshotCount;

    private enum RecorderState { Idle, Recording, Replaying }
    private RecorderState _state = RecorderState.Idle;

    public WorkflowRecorderWindow(string connectionName, int daemonPort)
    {
        InitializeComponent();

        _connectionName = connectionName;
        _daemonPort = daemonPort;

        TitleText.Text = $"WORKFLOW: {connectionName}";

        FileLog.Write($"[WorkflowRecorder] Created: connection={connectionName}, port={daemonPort}");

        Loaded += (_, _) => LoadSavedWorkflows();
    }

    // -----------------------------------------------------------------------
    // Record / Stop / Clear
    // -----------------------------------------------------------------------

    /// <summary>
    /// Start recording (issue #1107, item 1 - the worst of the nine).
    ///
    /// This handler used to fail COMPLETELY SILENTLY. The button was disabled by SetState only AFTER the
    /// POST, so it stayed live throughout; and when the POST failed the handler simply returned, having
    /// written to FileLog and nothing else. The interface still said IDLE, so from the user's side the
    /// Record button did not appear to exist. Every repeat click cleared the recorded actions and orphaned
    /// another temp directory on disk.
    ///
    /// Now: the button shows it is working before the POST, a second click cannot start a second recording,
    /// a failure says so on the status line, and the temp directory is created only once the daemon has
    /// actually agreed to record - so a refusal leaves nothing behind.
    /// </summary>
    private async void BtnRecord_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        await BusyAction.RunAsync(button, "workflow recorder", "start recording", StartRecordingAsync, "Starting...",
            onFailure: message =>
            {
                StatusText.Text = "COULD NOT START";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));
            });
    }

    private async Task StartRecordingAsync()
    {
        FileLog.Write("[WorkflowRecorder] BtnRecord_Click");

        _actions.Clear();
        _initialScreenshotFile = null;
        UpdateActionLog();
        _recordingSince = DateTime.UtcNow.ToString("o");
        _recordingStartTime = DateTime.UtcNow;
        _lastScreenshotCount = 0;

        // Tell the browser extension to start capturing user actions. This happens BEFORE the temp
        // directory is created: a daemon that refuses to record must not leave a directory behind, and the
        // old order (create, then ask, then return on refusal) orphaned one on every failed click.
        var payload = JsonSerializer.Serialize(new { connection = _connectionName });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"http://127.0.0.1:{_daemonPort}/record/start", content);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            FileLog.Write($"[WorkflowRecorder] record/start FAILED: {err}");
            // Thrown, not returned. The helper turns this into something the user can see - returning here
            // is exactly how this button came to fail invisibly.
            throw new InvalidOperationException(
                $"The browser daemon refused to start recording (HTTP {(int)response.StatusCode}). {err}".Trim());
        }

        FileLog.Write("[WorkflowRecorder] record/start OK");

        // Only now is there a recording to keep screenshots for.
        _recordingTempDir = Path.Combine(Path.GetTempPath(), $"wf-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_recordingTempDir);
        FileLog.Write($"[WorkflowRecorder] Recording temp dir: {_recordingTempDir}");

        SetState(RecorderState.Recording);

        // Capture initial screenshot
        try
        {
            var ssFile = await CaptureScreenshotAsync(_recordingTempDir, -1, "step-000.jpg");
            _initialScreenshotFile = ssFile;
            FileLog.Write($"[WorkflowRecorder] Initial screenshot captured: {ssFile}");
            UpdateActionLog();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowRecorder] Initial screenshot FAILED: {ex.Message}");
        }

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) =>
        {
            try { await PollHistory(); }
            catch (Exception ex) { FileLog.Write($"[WorkflowRecorder] PollTimer FAILED: {ex.Message}"); }
        };
        _pollTimer.Start();
    }

    private async void BtnStop_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        // The stop's failure used to go only to the log while its callback said "reported to the user". It is now on
        // the status line, where the start's failure already was.
        await BusyAction.RunAsync(button, "workflow recorder", "stop recording", StopRecordingAsync, "Stopping...",
            onFailure: message =>
            {
                StatusText.Text = $"COULD NOT STOP: {message}";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));
            });
    }

    private async Task StopRecordingAsync()
    {
        FileLog.Write($"[WorkflowRecorder] BtnStop_Click: {_actions.Count} actions recorded");

        _pollTimer?.Stop();
        _pollTimer = null;

        try
        {
            var payload = JsonSerializer.Serialize(new { connection = _connectionName });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            await _http.PostAsync($"http://127.0.0.1:{_daemonPort}/record/stop", content);
            FileLog.Write("[WorkflowRecorder] record/stop OK");
        }
        catch (Exception ex)
        {
            // Deliberately swallowed, unlike the start path. The poll timer is already stopped and the local
            // recording is already over, so telling the daemon is best-effort housekeeping - and going back
            // to IDLE below is the honest report of the state the user is actually in. Failing loudly here
            // would say "stop did not work" when it did.
            FileLog.Write($"[WorkflowRecorder] record/stop FAILED (recording is stopped locally regardless): {ex.Message}");
        }

        SetState(RecorderState.Idle);
    }

    private void BtnClear_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[WorkflowRecorder] BtnClear_Click");
        _actions.Clear();
        _initialScreenshotFile = null;
        UpdateActionLog();
    }

    // -----------------------------------------------------------------------
    // Save / Load / Edit / Replay
    // -----------------------------------------------------------------------

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        var workflowName = WorkflowNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(workflowName))
        {
            FileLog.Write("[WorkflowRecorder] BtnSave_Click: empty name");
            return;
        }

        if (_actions.Count == 0)
        {
            FileLog.Write("[WorkflowRecorder] BtnSave_Click: no actions");
            return;
        }

        FileLog.Write($"[WorkflowRecorder] BtnSave_Click: name={workflowName}, actions={_actions.Count}");

        var template = new WorkflowTemplate
        {
            Version = 2,
            Name = workflowName,
            Connection = _connectionName,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            InitialScreenshotFile = _initialScreenshotFile,
        };

        for (var i = 0; i < _actions.Count; i++)
        {
            var a = _actions[i];
            var action = new WorkflowAction
            {
                Command = a.Command,
                Params = ConvertParams(a.Params),
                ScreenshotFile = a.ScreenshotFile,
            };

            template.Actions.Add(action);
            template.Steps.Add(new WorkflowStep
            {
                Type = "action",
                Action = action,
            });
        }

        _store.SaveTemplate(template);

        // Move recording screenshots from temp to permanent location
        if (_recordingTempDir != null && Directory.Exists(_recordingTempDir))
        {
            var recordingDir = _store.RecordingDir(_connectionName, workflowName);
            foreach (var file in Directory.GetFiles(_recordingTempDir, "*.jpg"))
            {
                var dest = Path.Combine(recordingDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }
            FileLog.Write($"[WorkflowRecorder] Copied screenshots to recording dir");

            try { Directory.Delete(_recordingTempDir, recursive: true); }
            catch (Exception ex) { FileLog.Write($"[WorkflowRecorder] Temp dir cleanup FAILED: {ex.Message}"); }
            _recordingTempDir = null;
        }

        FileLog.Write($"[WorkflowRecorder] Workflow saved: {workflowName}");
        LoadSavedWorkflows();
    }

    private void LoadSavedWorkflows()
    {
        FileLog.Write($"[WorkflowRecorder] LoadSavedWorkflows: connection={_connectionName}");
        _savedWorkflows.Clear();
        WorkflowList.Items.Clear();

        var templates = _store.ListTemplates(_connectionName);
        foreach (var t in templates)
        {
            var paramCount = t.Parameters.Count;
            var stepCount = t.Steps.Count > 0 ? t.Steps.Count : t.Actions.Count;
            var suffix = paramCount > 0 ? $" [{paramCount} params]" : "";
            var wf = new WorkflowFile
            {
                Name = t.Name,
                ActionCount = stepCount,
                ParamCount = paramCount,
            };
            _savedWorkflows.Add(wf);
            WorkflowList.Items.Add($"{t.Name} ({stepCount} steps{suffix})");
        }

        FileLog.Write($"[WorkflowRecorder] Loaded {_savedWorkflows.Count} saved workflows");
    }

    private void WorkflowList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var idx = WorkflowList.SelectedIndex;
        if (idx < 0 || idx >= _savedWorkflows.Count)
        {
            BtnReplay.IsEnabled = false;
            BtnEdit.IsEnabled = false;
            BtnParameterize.IsEnabled = false;
            BtnRuns.IsEnabled = false;
            return;
        }

        var wf = _savedWorkflows[idx];
        FileLog.Write($"[WorkflowRecorder] Selected workflow: {wf.Name}");

        var template = _store.LoadTemplate(_connectionName, wf.Name);
        if (template != null)
        {
            _actions.Clear();
            _initialScreenshotFile = template.InitialScreenshotFile;

            var actions = template.Steps.Count > 0
                ? template.Steps
                    .Where(s => s.Action != null)
                    .Select(s => s.Action)
                    .OfType<WorkflowAction>()
                    .ToList()
                : template.Actions;

            var recordingDir = _store.RecordingDir(_connectionName, wf.Name);
            foreach (var action in actions)
            {
                _actions.Add(new RecordedAction
                {
                    Command = action.Command,
                    Params = ConvertParamsToJsonElement(action.Params),
                    ScreenshotFile = action.ScreenshotFile,
                    ScreenshotFullPath = !string.IsNullOrEmpty(action.ScreenshotFile)
                        ? Path.Combine(recordingDir, action.ScreenshotFile)
                        : null,
                });
            }
            UpdateActionLog();
        }

        BtnReplay.IsEnabled = _actions.Count > 0 && _state != RecorderState.Replaying;
        BtnEdit.IsEnabled = _state == RecorderState.Idle;
        BtnParameterize.IsEnabled = _actions.Count > 0 && _state == RecorderState.Idle;
        BtnRuns.IsEnabled = _state == RecorderState.Idle;
    }

    // -----------------------------------------------------------------------
    // Edit
    // -----------------------------------------------------------------------

    private async void BtnEdit_Click(object? sender, RoutedEventArgs e)
    {
        var idx = WorkflowList.SelectedIndex;
        if (idx < 0 || idx >= _savedWorkflows.Count) return;

        var wf = _savedWorkflows[idx];
        FileLog.Write($"[WorkflowRecorder] BtnEdit_Click: {wf.Name}");

        var editor = new WorkflowEditorWindow(_store, _connectionName, wf.Name);
        await editor.ShowDialog<bool?>(this);

        LoadSavedWorkflows();
    }

    // -----------------------------------------------------------------------
    // Parameterize
    // -----------------------------------------------------------------------

    private async void BtnParameterize_Click(object? sender, RoutedEventArgs e)
    {
        var idx = WorkflowList.SelectedIndex;
        if (idx < 0 || idx >= _savedWorkflows.Count) return;

        var wf = _savedWorkflows[idx];
        FileLog.Write($"[WorkflowRecorder] BtnParameterize_Click: {wf.Name}");

        var template = _store.LoadTemplate(_connectionName, wf.Name);
        if (template == null) return;

        var dialog = new WorkflowParameterizeDialog(template);
        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true && dialog.Saved)
        {
            _store.SaveTemplate(template);
            FileLog.Write($"[WorkflowRecorder] Template parameterized: {template.Parameters.Count} params");
            LoadSavedWorkflows();
        }
    }

    // -----------------------------------------------------------------------
    // Runs dialog
    // -----------------------------------------------------------------------

    private void BtnRuns_Click(object? sender, RoutedEventArgs e)
    {
        var idx = WorkflowList.SelectedIndex;
        if (idx < 0 || idx >= _savedWorkflows.Count) return;

        var wf = _savedWorkflows[idx];
        FileLog.Write($"[WorkflowRecorder] BtnRuns_Click: {wf.Name}");

        var dialog = new WorkflowRunsDialog(_store, _connectionName, wf.Name);
        dialog.Show();
    }

    // -----------------------------------------------------------------------
    // Sequential Replay with WorkflowRunner
    // -----------------------------------------------------------------------

    private async void BtnReplay_Click(object? sender, RoutedEventArgs e)
    {
        var idx = WorkflowList.SelectedIndex;
        if (idx < 0 || idx >= _savedWorkflows.Count) return;

        var wf = _savedWorkflows[idx];
        FileLog.Write($"[WorkflowRecorder] BtnReplay_Click: {wf.Name}");

        var template = _store.LoadTemplate(_connectionName, wf.Name);
        if (template == null || template.Steps.Count == 0) return;

        // If parameterized, prompt for values
        var paramValues = new Dictionary<string, string>();
        if (template.Parameters.Count > 0)
        {
            var paramDialog = new WorkflowParametersDialog(template.Parameters);
            var result = await paramDialog.ShowDialog<bool?>(this);
            if (result != true)
                return;
            paramValues = paramDialog.ResolvedValues;
        }

        SetState(RecorderState.Replaying);

        // Create run record
        var runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
        var ssDir = _store.RunScreenshotDir(_connectionName, wf.Name, runId);

        var runner = new WorkflowRunner(_connectionName, _daemonPort, _http, ssDir, paramValues);
        runner.OnStepProgress = (stepIndex, command) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText.Text = $"STEP {stepIndex + 1}/{template.Steps.Count}";
                ActionCountText.Text = $"Running: {command}";
            });
        };

        await runner.RunAsync(template.Steps);

        var run = new WorkflowRun
        {
            Id = runId,
            WorkflowName = wf.Name,
            Connection = _connectionName,
            StartedAt = DateTime.UtcNow.ToString("o"),
            CompletedAt = DateTime.UtcNow.ToString("o"),
            // shown-error-exempt (not an error): a field of the run record that is saved, not text on a screen
            Status = runner.AllSucceeded ? "completed" : "failed",
            ParameterValues = paramValues,
            Steps = runner.CompletedSteps,
        };

        _store.SaveRun(run);
        FileLog.Write($"[WorkflowRecorder] Run finished: id={runId}, status={run.Status}, steps={run.Steps.Count}");

        SetState(RecorderState.Idle);
    }

    // -----------------------------------------------------------------------
    // Screenshots
    // -----------------------------------------------------------------------

    private async Task<string?> CaptureScreenshotAsync(string targetDir, int stepIndex, string? overrideFileName = null)
    {
        var payload = JsonSerializer.Serialize(new { connection = _connectionName });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"http://127.0.0.1:{_daemonPort}/screenshot", content);

        if (!response.IsSuccessStatusCode)
        {
            FileLog.Write($"[WorkflowRecorder] Screenshot FAILED: HTTP {(int)response.StatusCode}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        if (!doc.TryGetProperty("data", out var dataEl))
        {
            FileLog.Write("[WorkflowRecorder] Screenshot response missing 'data' field");
            return null;
        }

        var base64 = dataEl.GetString();
        if (string.IsNullOrEmpty(base64))
        {
            FileLog.Write("[WorkflowRecorder] Screenshot data is empty");
            return null;
        }

        var fileName = overrideFileName ?? $"step-{stepIndex + 1:D3}.jpg";
        var filePath = Path.Combine(targetDir, fileName);
        var bytes = Convert.FromBase64String(base64);
        await File.WriteAllBytesAsync(filePath, bytes);

        FileLog.Write($"[WorkflowRecorder] Screenshot saved: {fileName} ({bytes.Length} bytes)");
        return fileName;
    }

    // -----------------------------------------------------------------------
    // Polling with deduplication
    // -----------------------------------------------------------------------

    private async Task PollHistory()
    {
        try
        {
            var url = $"http://127.0.0.1:{_daemonPort}/history?connection={Uri.EscapeDataString(_connectionName)}";
            if (_recordingSince != null)
                url += $"&since={Uri.EscapeDataString(_recordingSince)}";

            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(json);
            if (!data.TryGetProperty("actions", out var actions)) return;

            var newActions = new List<RecordedAction>();
            foreach (var act in actions.EnumerateArray())
            {
                var isRecorded = act.TryGetProperty("recorded", out var recEl)
                    && recEl.ValueKind == JsonValueKind.True;
                if (!isRecorded) continue;

                var command = act.GetProperty("command").GetString() ?? "";
                var timestamp = act.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null;

                Dictionary<string, JsonElement>? parms = null;
                if (act.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    parms = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(p.GetRawText());
                }

                newActions.Add(new RecordedAction
                {
                    Command = command,
                    Params = parms,
                    Timestamp = timestamp,
                });
            }

            if (newActions.Count > _actions.Count)
            {
                _actions.Clear();
                _actions.AddRange(newActions);

                if (_recordingTempDir != null)
                {
                    for (var i = _lastScreenshotCount; i < _actions.Count; i++)
                    {
                        try
                        {
                            var ssFile = await CaptureScreenshotAsync(_recordingTempDir, i);
                            _actions[i].ScreenshotFile = ssFile;
                            if (ssFile != null)
                                _actions[i].ScreenshotFullPath = Path.Combine(_recordingTempDir, ssFile);
                        }
                        catch (Exception ssEx)
                        {
                            FileLog.Write($"[WorkflowRecorder] Recording screenshot {i} FAILED: {ssEx.Message}");
                        }
                    }
                    _lastScreenshotCount = _actions.Count;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    UpdateActionLog();
                    var elapsed = (int)(DateTime.UtcNow - _recordingStartTime).TotalSeconds;
                    ElapsedText.Text = $"  |  Elapsed: {elapsed}s";
                });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowRecorder] PollHistory FAILED: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Thumbnail double-click viewer
    // -----------------------------------------------------------------------

    private void ActionLogList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (ActionLogList.SelectedItem is not ActionDisplayEntry entry) return;
            if (string.IsNullOrEmpty(entry.ScreenshotFullPath) || !File.Exists(entry.ScreenshotFullPath)) return;

            FileLog.Write($"[WorkflowRecorder] Opening screenshot: {entry.ScreenshotFullPath}");

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = entry.ScreenshotFullPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkflowRecorder] Screenshot viewer FAILED: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // UI Helpers
    // -----------------------------------------------------------------------

    private void SetState(RecorderState state)
    {
        _state = state;
        switch (state)
        {
            case RecorderState.Idle:
                StatusText.Text = "IDLE";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                BtnRecord.IsEnabled = true;
                BtnStop.IsEnabled = false;
                BtnReplay.IsEnabled = _actions.Count > 0;
                BtnSave.IsEnabled = true;
                BtnEdit.IsEnabled = WorkflowList.SelectedIndex >= 0;
                BtnParameterize.IsEnabled = WorkflowList.SelectedIndex >= 0;
                BtnRuns.IsEnabled = WorkflowList.SelectedIndex >= 0;
                break;

            case RecorderState.Recording:
                StatusText.Text = "RECORDING";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));
                BtnRecord.IsEnabled = false;
                BtnStop.IsEnabled = true;
                BtnReplay.IsEnabled = false;
                BtnSave.IsEnabled = false;
                BtnEdit.IsEnabled = false;
                BtnParameterize.IsEnabled = false;
                BtnRuns.IsEnabled = false;
                break;

            case RecorderState.Replaying:
                StatusText.Text = "REPLAYING";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                BtnRecord.IsEnabled = false;
                BtnStop.IsEnabled = false;
                BtnReplay.IsEnabled = false;
                BtnSave.IsEnabled = false;
                BtnEdit.IsEnabled = false;
                BtnParameterize.IsEnabled = false;
                BtnRuns.IsEnabled = false;
                break;
        }

        ActionCountText.Text = $"Actions: {_actions.Count}";
    }

    private void UpdateActionLog()
    {
        var entries = new List<ActionDisplayEntry>();

        // Initial screenshot entry
        if (!string.IsNullOrEmpty(_initialScreenshotFile) && _recordingTempDir != null)
        {
            var ssPath = Path.Combine(_recordingTempDir, _initialScreenshotFile);
            entries.Add(new ActionDisplayEntry
            {
                CommandDisplay = "INITIAL STATE",
                ParamSummary = "Starting state",
                Thumbnail = LoadThumbnail(ssPath),
                ScreenshotFullPath = File.Exists(ssPath) ? ssPath : null,
            });
        }

        foreach (var action in _actions)
        {
            var timeStr = "";
            if (!string.IsNullOrEmpty(action.Timestamp) &&
                DateTime.TryParse(action.Timestamp, out var dt))
            {
                timeStr = dt.ToLocalTime().ToString("HH:mm:ss") + " ";
            }

            var paramSummary = "";
            if (action.Params != null)
            {
                paramSummary = string.Join(", ", action.Params.Select(kv =>
                {
                    var val = kv.Value.ValueKind == JsonValueKind.String
                        ? kv.Value.GetString()
                        : kv.Value.GetRawText();
                    return $"{kv.Key}: {val}";
                }));
            }

            entries.Add(new ActionDisplayEntry
            {
                CommandDisplay = $"{timeStr}{action.Command}",
                ParamSummary = paramSummary.Length > 60
                    ? paramSummary[..57] + "..."
                    : paramSummary,
                Thumbnail = LoadThumbnail(action.ScreenshotFullPath),
                ScreenshotFullPath = action.ScreenshotFullPath,
            });
        }

        ActionLogList.ItemsSource = entries;
        ActionCountText.Text = $"Actions: {_actions.Count}";
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
    // Conversion helpers
    // -----------------------------------------------------------------------

    private static Dictionary<string, object>? ConvertParams(Dictionary<string, JsonElement>? jsonParams)
    {
        if (jsonParams == null) return null;

        var result = new Dictionary<string, object>();
        foreach (var kv in jsonParams)
        {
            result[kv.Key] = kv.Value.ValueKind == JsonValueKind.String
                ? kv.Value.GetString() ?? kv.Value.GetRawText()
                : kv.Value.GetRawText();
        }

        return result;
    }

    private static Dictionary<string, JsonElement>? ConvertParamsToJsonElement(Dictionary<string, object>? objParams)
    {
        if (objParams == null) return null;

        var json = JsonSerializer.Serialize(objParams);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
    }

    // -----------------------------------------------------------------------
    // Data Classes
    // -----------------------------------------------------------------------

    private class RecordedAction
    {
        public string Command { get; set; } = "";
        public Dictionary<string, JsonElement>? Params { get; set; }
        public string? Timestamp { get; set; }
        public string? ScreenshotFile { get; set; }
        public string? ScreenshotFullPath { get; set; }
    }

    private class WorkflowFile
    {
        public string Name { get; set; } = "";
        public int ActionCount { get; set; }
        public int ParamCount { get; set; }
    }

    private class ActionDisplayEntry
    {
        public string CommandDisplay { get; set; } = "";
        public string ParamSummary { get; set; } = "";
        public Bitmap? Thumbnail { get; set; }
        public string? ScreenshotFullPath { get; set; }
    }
}
