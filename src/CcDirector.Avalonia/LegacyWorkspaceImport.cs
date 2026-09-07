using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// Moves the workspaces this machine saved BEFORE they lived on the Gateway (issue #2722) up to the
/// Gateway, once, and then gets out of the way.
///
/// Until this change a workspace was a file under this Director's own configuration directory
/// (<c>config/director/workspaces/*.workspace.json</c>). Those files are somebody's saved work, and the
/// upgrade must not make them disappear - so on the first use of the workspace dialogs after the upgrade,
/// each one is pushed up and its file renamed aside to <c>.imported</c>. The bytes stay on disk under the
/// new name; nothing is deleted.
///
/// It only ever CREATES. A legacy file whose id already exists on the Gateway is left alone and renamed
/// aside anyway: the Gateway's copy is the one people have been editing since, and quietly overwriting it
/// with a file from before the upgrade would lose exactly the work this exists to protect.
///
/// It runs ONCE per process, and it is not a background sweep. If the Gateway is unreachable it throws,
/// the dialog reports it, and the files are still there to import next time - which is the honest
/// behaviour: silently skipping would leave the user looking at a list that is missing their workspaces
/// with nothing said about why.
/// </summary>
public static class LegacyWorkspaceImport
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _done;

    /// <summary>The suffix a legacy file is renamed to once it has been dealt with.</summary>
    public const string ImportedSuffix = ".imported";

    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Import every legacy workspace file that has not been imported yet. Returns how many were pushed to
    /// the Gateway. Does nothing after the first successful run in this process.
    /// </summary>
    /// <param name="catalog">Where the workspaces go.</param>
    /// <param name="folderPath">The legacy folder. Null uses this machine's real one; tests pass their own.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<int> RunOnceAsync(
        IWorkspaceCatalog catalog, string? folderPath = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        await Gate.WaitAsync(ct);
        try
        {
            if (_done) return 0;

            var folder = folderPath ?? CcStorage.Workspaces();
            if (!Directory.Exists(folder))
            {
                _done = true;
                return 0;
            }

            var files = Directory.GetFiles(folder, "*.workspace.json");
            if (files.Length == 0)
            {
                _done = true;
                return 0;
            }

            FileLog.Write($"[LegacyWorkspaceImport] RunOnceAsync: {files.Length} legacy file(s) in {folder}");

            var existing = (await catalog.ListAsync(ct))
                .Select(w => w.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var imported = 0;
            foreach (var file in files)
            {
                var legacy = ReadLegacy(file);
                if (legacy is null)
                {
                    // A file that cannot be parsed is left exactly where it is, under its own name, so the
                    // bytes are still there for somebody to look at. Renaming it aside would say it had
                    // been dealt with.
                    FileLog.Write($"[LegacyWorkspaceImport] skipping unreadable file {Path.GetFileName(file)}");
                    continue;
                }

                var doc = ToDocument(legacy);
                if (existing.Contains(doc.Id))
                {
                    FileLog.Write(
                        $"[LegacyWorkspaceImport] {Path.GetFileName(file)} -> id '{doc.Id}' already on the " +
                        "Gateway; leaving the Gateway's copy alone");
                }
                else
                {
                    await catalog.SaveAsync(doc, ct);
                    existing.Add(doc.Id);
                    imported++;
                    FileLog.Write(
                        $"[LegacyWorkspaceImport] imported {Path.GetFileName(file)} as '{doc.Id}' " +
                        $"({doc.Seats.Count} seat(s))");
                }

                RenameAside(file);
            }

            _done = true;
            FileLog.Write($"[LegacyWorkspaceImport] RunOnceAsync: {imported} workspace(s) imported");
            return imported;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Reset the once-per-process gate. Tests only.</summary>
    public static void ResetForTests() => _done = false;

    private static LegacyWorkspace? ReadLegacy(string path)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<LegacyWorkspace>(File.ReadAllText(path), LegacyJsonOptions);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Name)) return null;
            return parsed;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[LegacyWorkspaceImport] ReadLegacy FAILED for {path}: {ex.Message}");
            return null;
        }
    }

    private static void RenameAside(string path)
    {
        var target = path + ImportedSuffix;
        try
        {
            if (File.Exists(target)) File.Delete(target);
            File.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The workspace IS on the Gateway - that write already succeeded - so this is untidiness, not
            // data loss, and the guard above (id already present) is what stops a second run duplicating.
            FileLog.Write($"[LegacyWorkspaceImport] RenameAside FAILED for {path}: {ex.Message}");
        }
    }

    private static WorkspaceDocument ToDocument(LegacyWorkspace legacy)
    {
        var seats = (legacy.Sessions ?? new List<LegacySession>())
            .OrderBy(s => s.SortOrder)
            .Select((s, i) => new WorkspaceSeat
            {
                // A legacy entry never held a session id: these workspaces always started FRESH sessions.
                SessionId = null,
                Name = !string.IsNullOrWhiteSpace(s.CustomName)
                    ? s.CustomName!
                    : Path.GetFileName((s.RepoPath ?? "").TrimEnd('\\', '/')),
                // Null means the file predates the agent field, and those genuinely were Claude Code -
                // every session in such a file was created through the old default.
                Agent = string.IsNullOrWhiteSpace(s.Agent) ? "ClaudeCode" : s.Agent!,
                RepoPath = s.RepoPath ?? "",
                Color = s.CustomColor,
                AgentArgs = s.ClaudeArgs,
                SortOrder = i,
            })
            .Where(s => !string.IsNullOrWhiteSpace(s.RepoPath) && !string.IsNullOrWhiteSpace(s.Name))
            .ToList();

        return new WorkspaceDocument
        {
            Id = WorkspaceSlug.From(legacy.Name),
            Name = legacy.Name!,
            Description = string.IsNullOrWhiteSpace(legacy.Description) ? null : legacy.Description,
            Origin = WorkspaceOrigins.Authored,
            Seats = seats,
        };
    }

    /// <summary>The shape of a pre-Gateway workspace file. Private because nothing new writes it.</summary>
    private sealed class LegacyWorkspace
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<LegacySession>? Sessions { get; set; }
    }

    /// <summary>One entry inside a pre-Gateway workspace file.</summary>
    private sealed class LegacySession
    {
        public string? RepoPath { get; set; }
        public string? CustomName { get; set; }
        public string? CustomColor { get; set; }
        public int SortOrder { get; set; }
        public string? ClaudeArgs { get; set; }
        public string? Agent { get; set; }
    }
}
