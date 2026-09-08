using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.ControlApi;
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
/// each one is pushed up and its file renamed aside to <c>.imported</c>.
///
/// NOTHING IS EVER DELETED AND NOTHING IS EVER PARTIALLY IMPORTED. Those two rules are what the whole
/// class is for, and each has a specific way it would otherwise be broken:
///
///  - A file is renamed aside ONLY once it has been fully dealt with, to a name that does not already
///    exist. Deleting a previous <c>.imported</c> file to make room would destroy the older saved bytes.
///  - A file that cannot be read, that holds an entry with no repository, or that holds no entries at all
///    is REFUSED WHOLE and left exactly where it is, under its own name. Importing the entries that
///    happened to parse and renaming the file as done would silently drop the rest.
///  - Two files whose names slug to the same id are a collision: the first is imported and the second is
///    left in place with both names logged, because overwriting is what this exists to prevent.
///
/// It runs ONCE per process, and it is not a background sweep. If the Gateway is unreachable it throws,
/// the caller reports it, and the files are still there to import next time - which is the honest
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
    public static async Task<LegacyImportResult> RunOnceAsync(
        IWorkspaceCatalog catalog, string? folderPath = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        await Gate.WaitAsync(ct);
        try
        {
            if (_done) return LegacyImportResult.Nothing;

            var folder = folderPath ?? CcStorage.Workspaces();
            if (!Directory.Exists(folder))
            {
                _done = true;
                return LegacyImportResult.Nothing;
            }

            // SORTED ORDINALLY, so which file wins a slug collision is a defined answer and not whatever
            // the filesystem happened to enumerate first. Ordinal and not case-insensitive: two names
            // differing only by case compare EQUAL under the latter, which puts the winner back in the
            // hands of the filesystem on any system where both can exist.
            var files = Directory.GetFiles(folder, "*.workspace.json");
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length == 0)
            {
                _done = true;
                return LegacyImportResult.Nothing;
            }

            FileLog.Write($"[LegacyWorkspaceImport] RunOnceAsync: {files.Length} legacy file(s) in {folder}");

            // THERE IS NO LIST-THEN-WRITE HERE ANY MORE. Reading the ids first and treating absence
            // from that snapshot as permission to create is the shape that destroyed data: between the
            // list and the write, another Director, another window, or a person on the Cockpit could
            // create that id, and this loop would replace their bytes and then archive the legacy file
            // as though the import had been safe. Every write below is create-only, decided on the
            // Gateway under the lock it writes under, so there is no snapshot to go stale.

            // Which id each file in THIS run claimed, so a second file claiming the same one is a
            // collision that can be reported with both names rather than silently dropped.
            var claimedInThisRun = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var imported = 0;
            var refused = new List<string>();
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);

                WorkspaceDocument doc;
                try
                {
                    doc = ReadLegacy(file);
                }
                catch (LegacyWorkspaceFileException ex)
                {
                    // Left exactly where it is, under its own name, so the bytes are still there for
                    // somebody to look at. Renaming it aside would say it had been dealt with.
                    FileLog.Write($"[LegacyWorkspaceImport] REFUSED {name}: {ex.Message}");
                    refused.Add($"{name}: {ex.Message}");
                    continue;
                }

                if (claimedInThisRun.TryGetValue(doc.Id, out var firstFile))
                {
                    FileLog.Write(
                        $"[LegacyWorkspaceImport] REFUSED {name}: its name gives the id '{doc.Id}', which " +
                        $"{firstFile} already claimed in this run. Rename one of them and try again; " +
                        "importing it would overwrite the other.");
                    refused.Add(
                        $"{name}: its name gives the same id as {firstFile}, so importing it would " +
                        "overwrite that one. Rename one of them.");
                    continue;
                }

                try
                {
                    await catalog.CreateAsync(doc, ct);
                    imported++;
                    FileLog.Write(
                        $"[LegacyWorkspaceImport] imported {name} as '{doc.Id}' ({doc.Seats.Count} seat(s))");
                }
                catch (WorkspaceAlreadyExistsException)
                {
                    // Somebody already has that id - either from before this ran, or created while it
                    // was running. Either way this file is not imported and the Gateway's copy is left
                    // exactly as it is. An import never overwrites; that is a decision for a person at
                    // the Save dialog, not for a migration nobody asked to run.
                    FileLog.Write(
                        $"[LegacyWorkspaceImport] {name} -> id '{doc.Id}' already on the Gateway; leaving " +
                        "the Gateway's copy alone");
                }

                claimedInThisRun[doc.Id] = name;
                RenameAside(file);
            }

            _done = true;
            FileLog.Write(
                $"[LegacyWorkspaceImport] RunOnceAsync: {imported} workspace(s) imported, " +
                $"{refused.Count} refused");
            return new LegacyImportResult(imported, refused);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// What the import did: how many went up, and every file it REFUSED with the reason.
    ///
    /// The refusals are returned rather than only logged, because otherwise "there were no legacy files"
    /// and "your saved files were refused and are missing from this list" produce the same screen - a
    /// list that reads as complete and is not. That is the same shape as everything else this change
    /// spent the day removing.
    /// </summary>
    /// <param name="Imported">How many workspaces were pushed to the Gateway.</param>
    /// <param name="Refused">One line per file that was left alone, saying which and why.</param>
    public sealed record LegacyImportResult(int Imported, IReadOnlyList<string> Refused)
    {
        /// <summary>Nothing to do: no folder, or no legacy files in it.</summary>
        public static LegacyImportResult Nothing { get; } = new(0, Array.Empty<string>());
    }

    /// <summary>Reset the once-per-process gate. Tests only.</summary>
    public static void ResetForTests() => _done = false;

    /// <summary>A legacy workspace file cannot be imported whole, so it is not imported at all.</summary>
    private sealed class LegacyWorkspaceFileException : Exception
    {
        public LegacyWorkspaceFileException(string message) : base(message) { }
    }

    private static WorkspaceDocument ReadLegacy(string path)
    {
        LegacyWorkspace? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<LegacyWorkspace>(File.ReadAllText(path), LegacyJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new LegacyWorkspaceFileException($"it could not be read ({ex.Message})");
        }

        if (parsed is null)
            throw new LegacyWorkspaceFileException("it deserialized to nothing");
        if (string.IsNullOrWhiteSpace(parsed.Name))
            throw new LegacyWorkspaceFileException("it has no name, so there is nothing to store it under");

        var entries = parsed.Sessions;
        if (entries is null || entries.Count == 0)
            throw new LegacyWorkspaceFileException("it holds no sessions");

        // Ordered by the file's OWN sortOrder, then re-indexed from zero. Taking the array position
        // would silently re-order a workspace whose entries were saved out of order.
        var ordered = entries.Select((e, i) => (Entry: e, Index: i))
            .OrderBy(x => x.Entry?.SortOrder ?? int.MaxValue)
            .ThenBy(x => x.Index)
            .ToList();

        var seats = new List<WorkspaceSeat>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (e, position) = ordered[i];
            if (e is null)
                throw new LegacyWorkspaceFileException($"sessions[{position}] is empty");

            var repo = (e.RepoPath ?? "").Trim();
            if (repo.Length == 0)
                throw new LegacyWorkspaceFileException(
                    $"sessions[{position}] has no repoPath, and a seat with nowhere to run cannot be imported " +
                    "- importing the rest would silently drop it");

            var seatName = !string.IsNullOrWhiteSpace(e.CustomName)
                ? e.CustomName!.Trim()
                : Path.GetFileName(repo.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(seatName))
                throw new LegacyWorkspaceFileException(
                    $"sessions[{position}] has no name and none can be taken from its repoPath '{repo}'");

            seats.Add(new WorkspaceSeat
            {
                // A legacy entry never held a session id: these workspaces always started FRESH sessions.
                SessionId = null,
                Name = seatName,
                // Null means the file predates the agent field, and those genuinely were Claude Code -
                // every session in such a file was created through the old default.
                Agent = string.IsNullOrWhiteSpace(e.Agent) ? "ClaudeCode" : e.Agent!.Trim(),
                RepoPath = repo,
                Color = e.CustomColor,
                AgentArgs = e.ClaudeArgs,
                SortOrder = i,
            });
        }

        return new WorkspaceDocument
        {
            Id = WorkspaceSlug.From(parsed.Name),
            Name = parsed.Name!.Trim(),
            Description = string.IsNullOrWhiteSpace(parsed.Description) ? null : parsed.Description,
            Origin = WorkspaceOrigins.Authored,
            Seats = seats,
        };
    }

    /// <summary>
    /// Rename the file aside to a name that does not exist yet. NEVER deletes: an existing
    /// <c>.imported</c> file is somebody's older saved workspace, and removing it to make room would
    /// destroy exactly what this class is here to preserve.
    /// </summary>
    private static void RenameAside(string path)
    {
        try
        {
            var target = path + ImportedSuffix;
            var n = 2;
            while (File.Exists(target))
                target = $"{path}{ImportedSuffix}-{n++}";

            File.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The workspace IS on the Gateway - that write already succeeded - so this is untidiness, not
            // data loss, and the guard above (id already present) is what stops a second run duplicating.
            FileLog.Write($"[LegacyWorkspaceImport] RenameAside FAILED for {path}: {ex.Message}");
        }
    }

    /// <summary>The shape of a pre-Gateway workspace file. Private because nothing new writes it.</summary>
    private sealed class LegacyWorkspace
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<LegacySession?>? Sessions { get; set; }
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
