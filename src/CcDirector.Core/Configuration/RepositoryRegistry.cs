using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The machine's list of registered repositories, held in <c>config/director/repositories.json</c>.
///
/// <para><b>Every entry point is serialized, because this list now has many writers.</b> Until the
/// recency signal was completed, <see cref="MarkUsed"/> had exactly one caller - the desktop New
/// Session dialog's Start button - so its writes were serialized by accident on the user interface
/// thread. It is now written on whatever thread created the session: a tunnel command thread serving
/// the Cockpit or the phone, a restore thread, a schedule. That is the most frequent event in the
/// product, and it happens beside the <c>repo-add</c>, <c>repo-delete</c> and <c>repo-rename</c>
/// verbs, which have always arrived on tunnel threads of their own.
///
/// Unsynchronized, two of those writers racing would have cost the user the whole list: the plain
/// <see cref="List{T}"/> loses entries when two threads add at once and throws
/// <c>"Collection was modified"</c> when one enumerates while another mutates, and two overlapping
/// <c>File.WriteAllText</c> calls leave a half-written file behind - on POSIX the second open
/// truncates the first writer's file. An empty repository list is exactly the defect this list is
/// meant to stop, so it may never arrive from here.
///
/// <para><b>Two mechanisms, because there are two kinds of writer.</b> A process-wide lock serializes
/// this process's own threads: every mutation and its save are one critical section, and every read
/// hands back a snapshot rather than a live view. A temporary file plus a single move makes the write
/// itself atomic, which is the part the lock cannot do: one machine runs several Directors off the
/// same root, so a second process can write this file at any moment. Across processes the outcome is
/// last-writer-wins; what it can never be is a torn file.
///
/// <para><b>An unreadable file is never overwritten.</b> <see cref="Load"/> throws rather than
/// starting fresh - see the remarks on it.
/// </summary>
public class RepositoryRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Serializes every mutation, every save and every read of <see cref="_repositories"/>. Held for
    /// the whole of a mutate-and-save so the file on disk always matches a list that existed.
    /// </summary>
    private readonly object _gate = new();

    private readonly List<RepositoryConfig> _repositories = new();

    public string FilePath { get; }

    /// <summary>
    /// A snapshot of the registered repositories, not a live view: a caller on one thread may be
    /// enumerating this while another thread adds or removes, and a live view would throw
    /// <c>"Collection was modified"</c> at it.
    /// </summary>
    public IReadOnlyList<RepositoryConfig> Repositories
    {
        get
        {
            lock (_gate)
                return _repositories.ToArray();
        }
    }

    public RepositoryRegistry(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            CcStorage.ToolConfig("director"),
            "repositories.json");
    }

    /// <summary>
    /// Read the registered repositories from disk, creating an empty list when there is no file yet.
    /// </summary>
    /// <remarks>
    /// A file that exists but cannot be parsed THROWS, and is left on disk exactly as it is. Starting
    /// fresh instead would wipe the user's entire registered repository list without a word, and the
    /// very next save would write that emptiness over the only copy - destroying the user's data to
    /// hide a problem. This is the same rule <see cref="CcDirectorConfigService"/> applies to
    /// <c>config.json</c>. An empty or whitespace-only file is not a parse failure: it is a file
    /// nothing has been written into yet, and it reads as an empty list.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The file exists and could not be parsed.</exception>
    public void Load()
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(FilePath))
            {
                WriteAtomic("[]");
                return;
            }

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                _repositories.Clear();
                return;
            }

            List<RepositoryConfig>? loaded;
            try
            {
                loaded = JsonSerializer.Deserialize<List<RepositoryConfig>>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[RepositoryRegistry] Load FAILED: {FilePath} is not readable: {ex.Message}");
                throw new InvalidOperationException(
                    $"The registered repository list could not be read: {FilePath}. {ex.Message} " +
                    "The file has been left exactly as it is, so nothing is lost - repair it, or move " +
                    "it aside to start with an empty list.", ex);
            }

            if (loaded is null)
            {
                FileLog.Write($"[RepositoryRegistry] Load FAILED: {FilePath} parsed to null");
                throw new InvalidOperationException(
                    $"The registered repository list could not be read: {FilePath} parsed to null. " +
                    "The file has been left exactly as it is, so nothing is lost - repair it, or move " +
                    "it aside to start with an empty list.");
            }

            _repositories.Clear();
            _repositories.AddRange(loaded);
        }
    }

    /// <summary>
    /// Register one folder a PERSON chose - the desktop dialog's Browse button, and the <c>repo-add</c>
    /// verb the Cockpit and the phone use.
    ///
    /// <para><b>A WORKTREE IS NOT A REPOSITORY, so a worktree is registered as the repository it is a
    /// worktree OF</b> (the one-repository-list mission). Nothing here ever checked what a folder was,
    /// and since the Director's push became the scan UNION this list, a worktree added by hand became a
    /// row in the one repository list that the root-folder scan could never have put there - the second
    /// door into the defect the owner reported, still shut on every machine measured on 20 September
    /// 2026, and shut here before anyone walks through it. If its repository is already registered this
    /// returns false, as it does for any duplicate: the repository is already in the list, which is the
    /// true answer.</para>
    ///
    /// <para>A folder that cannot be positively resolved to a repository that exists is registered as
    /// itself, exactly as before. The rule never guesses - see
    /// <see cref="Git.LinkedWorktree.ParentRepositoryOf"/>.</para>
    /// </summary>
    public bool TryAdd(string folderPath)
        => TryAddExact(Git.LinkedWorktree.ParentRepositoryOf(folderPath) ?? folderPath);

    /// <summary>
    /// Register the folder AS GIVEN, asking nothing about what it is.
    ///
    /// <para>This is what <see cref="SeedFrom"/> uses, and the difference from <see cref="TryAdd"/> is
    /// deliberate: seeding replays a list somebody already has, and rewriting their entries while
    /// loading them would silently change the user's own file. A person adding a folder is a decision
    /// being made now; a seed is a decision that was made before.</para>
    /// </summary>
    private bool TryAddExact(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

        lock (_gate)
        {
            var duplicate = _repositories.Any(r =>
                string.Equals(
                    Path.GetFullPath(r.Path).TrimEnd('\\', '/'),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));

            if (duplicate)
                return false;

            var name = Path.GetFileName(normalized);
            _repositories.Add(new RepositoryConfig { Name = name, Path = normalized });
            Save();
            return true;
        }
    }

    public bool Remove(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

        lock (_gate)
        {
            var index = _repositories.FindIndex(r =>
                string.Equals(
                    Path.GetFullPath(r.Path).TrimEnd('\\', '/'),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));

            if (index < 0)
                return false;

            _repositories.RemoveAt(index);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Rename a registered repository (display name only; the path is the identity).
    /// Returns false when the path is not registered.
    /// </summary>
    public bool Rename(string folderPath, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("name is required", nameof(newName));

        var normalized = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

        lock (_gate)
        {
            var repo = _repositories.FirstOrDefault(r =>
                string.Equals(
                    Path.GetFullPath(r.Path).TrimEnd('\\', '/'),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));

            if (repo is null)
                return false;

            repo.Name = newName.Trim();
            Save();
            FileLog.Write($"[RepositoryRegistry] Rename: {normalized} -> \"{repo.Name}\"");
            return true;
        }
    }

    public void MarkUsed(string folderPath)
    {
        FileLog.Write($"[RepositoryRegistry] MarkUsed: {folderPath}");
        var normalized = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

        RepositoryConfig? marked;
        lock (_gate)
        {
            marked = _repositories.FirstOrDefault(r =>
                string.Equals(
                    Path.GetFullPath(r.Path).TrimEnd('\\', '/'),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));

            if (marked != null)
            {
                marked.LastUsed = DateTime.UtcNow;
                Save();
            }
        }

        if (marked is null)
        {
            FileLog.Write($"[RepositoryRegistry] MarkUsed: repo not found for {normalized}");
            return;
        }

        // Outside the lock on purpose: this runs the user interface's binding handlers, and a handler
        // that came back into the registry while the gate was held would be waiting on the thread that
        // is already inside it.
        marked.NotifyLastUsedChanged();
        FileLog.Write($"[RepositoryRegistry] MarkUsed: updated LastUsed for {marked.Name}");
    }

    public void SeedFrom(IEnumerable<RepositoryConfig> repos)
    {
        // One critical section for the whole batch, not one per entry: a reader must never see half a
        // seed, and the enumerable is walked under the gate so nothing can be added between two of its
        // entries. The lock is re-entrant, so the TryAddExact calls below take it again harmlessly.
        lock (_gate)
        {
            foreach (var repo in repos)
            {
                if (!string.IsNullOrWhiteSpace(repo.Path))
                    TryAddExact(repo.Path);
            }
        }
    }

    /// <remarks>Only ever called with <see cref="_gate"/> held.</remarks>
    private void Save()
    {
        WriteAtomic(JsonSerializer.Serialize(_repositories, JsonOptions));
    }

    /// <summary>
    /// Write the list through a temporary file and a single move, so no reader - in this process or in
    /// another Director sharing this root - can ever see half of it, and so a crash mid-write cannot
    /// leave an unparseable file behind.
    /// </summary>
    private void WriteAtomic(string json)
    {
        var dir = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException($"The repository list has no directory: {FilePath}");
        Directory.CreateDirectory(dir);

        // A unique temporary name, because a fixed one would itself be the thing two writers race over.
        var temp = Path.Combine(dir, $".repositories.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);
    }
}
