using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Browser;

/// <summary>
/// Manages workflow templates and run records on disk.
///
/// Storage layout:
///   connections/{connection}/workflows/
///     {name}.json                    -- Template
///     {name}/
///       recording/step-001.jpg ...   -- Recording screenshots
///       runs/
///         {timestamp}.json           -- Run record
///         {timestamp}/step-001.jpg   -- Run screenshots
/// </summary>
public class WorkflowStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // -------------------------------------------------------------------
    // Templates
    // -------------------------------------------------------------------

    /// <summary>Save a workflow template to disk.</summary>
    public void SaveTemplate(WorkflowTemplate template)
    {
        FileLog.Write($"[WorkflowStore] SaveTemplate: name={template.Name}, connection={template.Connection}");

        var dir = CcStorage.ConnectionWorkflows(template.Connection);
        var safeName = SafeFileName(template.Name);
        var filePath = Path.Combine(dir, $"{safeName}.json");

        var json = JsonSerializer.Serialize(template, JsonOptions);
        File.WriteAllText(filePath, json);

        FileLog.Write($"[WorkflowStore] Template saved: {filePath}");
    }

    /// <summary>Load a single workflow template by connection and workflow name.</summary>
    public WorkflowTemplate? LoadTemplate(string connection, string workflowName)
    {
        FileLog.Write($"[WorkflowStore] LoadTemplate: connection={connection}, name={workflowName}");

        var dir = CcStorage.ConnectionWorkflows(connection);
        var safeName = SafeFileName(workflowName);
        var filePath = Path.Combine(dir, $"{safeName}.json");

        if (!File.Exists(filePath))
        {
            FileLog.Write($"[WorkflowStore] Template not found: {filePath}");
            return null;
        }

        var json = File.ReadAllText(filePath);
        var template = JsonSerializer.Deserialize<WorkflowTemplate>(json, JsonOptions);
        if (template != null)
            MigrateToV2(template);
        FileLog.Write($"[WorkflowStore] Template loaded: version={template?.Version}, steps={template?.Steps.Count ?? 0}, params={template?.Parameters.Count ?? 0}");
        return template;
    }

    /// <summary>Migrate a v1 template (flat actions) to v2 (structured steps).</summary>
    internal static void MigrateToV2(WorkflowTemplate template)
    {
        if (template.Version >= 2 && template.Steps.Count > 0)
            return;

        if (template.Actions.Count > 0 && template.Steps.Count == 0)
        {
            FileLog.Write($"[WorkflowStore] Migrating v1->v2: {template.Name}, {template.Actions.Count} actions");
            foreach (var action in template.Actions)
            {
                template.Steps.Add(new WorkflowStep
                {
                    Type = "action",
                    Action = action,
                });
            }
        }

        template.Version = 2;
    }

    /// <summary>List all workflow templates for a connection.</summary>
    public List<WorkflowTemplate> ListTemplates(string connection)
    {
        FileLog.Write($"[WorkflowStore] ListTemplates: connection={connection}");

        var dir = CcStorage.ConnectionWorkflows(connection);
        var templates = new List<WorkflowTemplate>();

        if (!Directory.Exists(dir))
            return templates;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var template = JsonSerializer.Deserialize<WorkflowTemplate>(json, JsonOptions);
                if (template != null)
                    templates.Add(template);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WorkflowStore] Failed to load template {file} FAILED: {ex.Message}");
            }
        }

        FileLog.Write($"[WorkflowStore] Listed {templates.Count} templates");
        return templates;
    }

    /// <summary>Delete a workflow template and all its associated data (recording screenshots, runs).</summary>
    public void DeleteTemplate(string connection, string workflowName)
    {
        FileLog.Write($"[WorkflowStore] DeleteTemplate: connection={connection}, name={workflowName}");

        var dir = CcStorage.ConnectionWorkflows(connection);
        var safeName = SafeFileName(workflowName);

        var jsonFile = Path.Combine(dir, $"{safeName}.json");
        if (File.Exists(jsonFile))
            File.Delete(jsonFile);

        var dataDir = Path.Combine(dir, safeName);
        if (Directory.Exists(dataDir))
            Directory.Delete(dataDir, recursive: true);

        FileLog.Write($"[WorkflowStore] Template deleted: {workflowName}");
    }

    // -------------------------------------------------------------------
    // Recording screenshots
    // -------------------------------------------------------------------

    /// <summary>Get the recording screenshots directory for a workflow, creating it if needed.</summary>
    public string RecordingDir(string connection, string workflowName)
    {
        var dir = CcStorage.ConnectionWorkflows(connection);
        var safeName = SafeFileName(workflowName);
        return CcStorage.Ensure(Path.Combine(dir, safeName, "recording"));
    }

    // -------------------------------------------------------------------
    // Runs
    // -------------------------------------------------------------------

    /// <summary>Save a workflow run record to disk.</summary>
    public void SaveRun(WorkflowRun run)
    {
        FileLog.Write($"[WorkflowStore] SaveRun: id={run.Id}, workflow={run.WorkflowName}, status={run.Status}");

        var runsDir = RunsDir(run.Connection, run.WorkflowName);
        var filePath = Path.Combine(runsDir, $"{run.Id}.json");

        var json = JsonSerializer.Serialize(run, JsonOptions);
        File.WriteAllText(filePath, json);

        FileLog.Write($"[WorkflowStore] Run saved: {filePath}");
    }

    /// <summary>Load a single run by connection, workflow name, and run ID.</summary>
    public WorkflowRun? LoadRun(string connection, string workflowName, string runId)
    {
        FileLog.Write($"[WorkflowStore] LoadRun: connection={connection}, workflow={workflowName}, id={runId}");

        var runsDir = RunsDir(connection, workflowName);
        var filePath = Path.Combine(runsDir, $"{runId}.json");

        if (!File.Exists(filePath))
        {
            FileLog.Write($"[WorkflowStore] Run not found: {filePath}");
            return null;
        }

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<WorkflowRun>(json, JsonOptions);
    }

    /// <summary>List all runs for a workflow, most recent first.</summary>
    public List<WorkflowRun> ListRuns(string connection, string workflowName)
    {
        FileLog.Write($"[WorkflowStore] ListRuns: connection={connection}, workflow={workflowName}");

        var runsDir = RunsDir(connection, workflowName);
        var runs = new List<WorkflowRun>();

        if (!Directory.Exists(runsDir))
            return runs;

        foreach (var file in Directory.GetFiles(runsDir, "*.json").OrderByDescending(f => f))
        {
            try
            {
                var json = File.ReadAllText(file);
                var run = JsonSerializer.Deserialize<WorkflowRun>(json, JsonOptions);
                if (run != null)
                    runs.Add(run);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WorkflowStore] Failed to load run {file} FAILED: {ex.Message}");
            }
        }

        FileLog.Write($"[WorkflowStore] Listed {runs.Count} runs");
        return runs;
    }

    /// <summary>Delete a run record and its screenshots.</summary>
    public void DeleteRun(string connection, string workflowName, string runId)
    {
        FileLog.Write($"[WorkflowStore] DeleteRun: connection={connection}, workflow={workflowName}, id={runId}");

        var runsDir = RunsDir(connection, workflowName);

        var jsonFile = Path.Combine(runsDir, $"{runId}.json");
        if (File.Exists(jsonFile))
            File.Delete(jsonFile);

        var screenshotDir = Path.Combine(runsDir, runId);
        if (Directory.Exists(screenshotDir))
            Directory.Delete(screenshotDir, recursive: true);

        FileLog.Write($"[WorkflowStore] Run deleted: {runId}");
    }

    /// <summary>Get the screenshot directory for a specific run, creating it if needed.</summary>
    public string RunScreenshotDir(string connection, string workflowName, string runId)
    {
        var runsDir = RunsDir(connection, workflowName);
        return CcStorage.Ensure(Path.Combine(runsDir, runId));
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private string RunsDir(string connection, string workflowName)
    {
        var dir = CcStorage.ConnectionWorkflows(connection);
        var safeName = SafeFileName(workflowName);
        return CcStorage.Ensure(Path.Combine(dir, safeName, "runs"));
    }

    private static string SafeFileName(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
    }
}
