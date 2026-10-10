using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>One folder git deregistered but could not physically delete, and the repository it
/// belonged to (so a retry is scoped to that repository and can check whether git has since
/// re-registered the path).</summary>
public sealed record WorktreeLeftover(string Path, string RepositoryPath);

/// <summary>
/// A machine-local, persisted list of worktree folders git already DEREGISTERED but could not
/// physically delete because a build output was locked (inspection round 4). Git no longer lists
/// such a path as a worktree, so NO future inventory can rediscover it - the reaper would never
/// revisit it and the folder would leak forever, even though the reap UI promises it "will be
/// retried". Persisting the path here lets the reaper retry the physical delete on a later run.
///
/// SAFE RETRY (inspection round 5). Each leftover records its owning repository. A retry runs only
/// for the repository being reaped and, crucially, must re-verify that the path is still a
/// DEREGISTERED orphan (not a NEW worktree the owner later created at the same path) before deleting
/// anything - that check lives in the reaper, which has git in hand. Writes are atomic (temp + move)
/// so a concurrent reader never sees a half-written set.
/// </summary>
public sealed class WorktreeLeftoverStore
{
    private readonly string _dir;
    private readonly string _file;
    private readonly object _gate = new();

    /// <param name="dir">Leftover directory (defaults to the machine-local cc-director path).</param>
    public WorktreeLeftoverStore(string? dir = null)
    {
        _dir = dir ?? CcStorage.WorktreeLeftovers();
        _file = Path.Combine(_dir, "leftovers.json");
    }

    /// <summary>The folders recorded as undeleted leftovers, each with its owning repository.</summary>
    public IReadOnlyList<WorktreeLeftover> All()
    {
        try
        {
            if (!File.Exists(_file))
                return Array.Empty<WorktreeLeftover>();
            return JsonSerializer.Deserialize<List<WorktreeLeftover>>(File.ReadAllText(_file))
                ?? new List<WorktreeLeftover>();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeLeftoverStore] read FAILED: {ex.Message}");
            return Array.Empty<WorktreeLeftover>();
        }
    }

    /// <summary>Record a folder git deregistered but could not fully delete. Best-effort - idempotent
    /// on the path.</summary>
    public void Add(string normalizedPath, string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return;
        lock (_gate)
        {
            var rows = All().Where(r => !PathEquals(r.Path, normalizedPath)).ToList();
            rows.Add(new WorktreeLeftover(normalizedPath, repositoryPath ?? ""));
            Write(rows);
        }
    }

    /// <summary>Drop a leftover once it has been deleted (retry succeeded) or removed by hand.</summary>
    public void Remove(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return;
        lock (_gate)
        {
            var rows = All();
            var kept = rows.Where(r => !PathEquals(r.Path, normalizedPath)).ToList();
            if (kept.Count != rows.Count)
                Write(kept);
        }
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void Write(IReadOnlyList<WorktreeLeftover> rows)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(rows));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeLeftoverStore] write FAILED: {ex.Message}");
        }
    }
}
