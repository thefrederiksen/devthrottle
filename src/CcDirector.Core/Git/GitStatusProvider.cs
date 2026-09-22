using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

public enum GitFileStatus { Modified, Added, Deleted, Renamed, Copied, Untracked, Unknown }

public class GitFileEntry
{
    public GitFileStatus Status { get; init; }
    public string StatusChar { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public bool IsStaged { get; init; }
}

public class GitStatusResult
{
    public List<GitFileEntry> StagedChanges { get; init; } = new();
    public List<GitFileEntry> UnstagedChanges { get; init; } = new();
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// The result of counting changed files: the count, plus whether the probe actually succeeded.
/// A failed probe carries Success=false and a meaningless count, so callers fail closed rather
/// than read the zero as "clean" (issue 516).
/// </summary>
public readonly record struct GitCountResult(bool Success, int Count);

public class GitStatusProvider
{
    private readonly string _executable;

    /// <summary>Runs the <c>git</c> on this machine's PATH. This is the only production form.</summary>
    public GitStatusProvider() : this("git") { }

    /// <summary>
    /// Runs <paramref name="executable"/> instead of <c>git</c>. A TEST SEAM, matching the one
    /// <see cref="GitCommandRunner"/> and <see cref="GitWriteService"/> already have, and the only
    /// way to reach the could-not-launch branch WITHOUT depending on what the host machine has
    /// installed: pass a name that resolves nowhere and the launch fails for precisely the reason it
    /// fails on a clean Windows install, identically on every machine. Production never calls this.
    /// </summary>
    public GitStatusProvider(string executable)
    {
        _executable = executable;
    }

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private readonly record struct CacheEntry(string RawOutput, GitStatusResult Result, DateTime Timestamp);

    // Static cache keyed by normalized repo path; shared across all GitStatusProvider instances
    private static readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _cacheLock = new();

    /// <summary>
    /// Per-repository cache generation, bumped by every <see cref=InvalidateCache/>. A status read
    /// records the generation when it STARTS and may store its result only if the generation is
    /// unchanged when it finishes. Without this, a read that began before a change and finished after
    /// the change's invalidation wrote its pre-change answer back into the cache, and the recompute
    /// the change asked for then took that stale hit and published the tree as it was before - the
    /// change was dropped (review of pull request 3337).
    /// </summary>
    private static readonly Dictionary<string, long> _generations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces the git process for this instance. A TEST SEAM only: a test holds a status read open,
    /// invalidates underneath it, and proves the stale answer is not cached. Production never sets it.
    /// </summary>
    internal Func<string, CancellationToken, Task<(string Output, string? Error, int ExitCode)>>? RunStatusOverride { get; init; }

    private static long GenerationLocked(string repoPath)
        => _generations.TryGetValue(repoPath, out var g) ? g : 0;

    /// <summary>Stores a result only if no invalidation happened since the read began.</summary>
    private static void StoreIfCurrent(string repoPath, long startedAtGeneration, CacheEntry entry)
    {
        lock (_cacheLock)
        {
            if (GenerationLocked(repoPath) != startedAtGeneration)
            {
                FileLog.Write($"[GitStatusProvider] status for {repoPath} was invalidated while it ran - not cached");
                return;
            }
            _cache[repoPath] = entry;
        }
    }

    public async Task<GitStatusResult> GetStatusAsync(string repoPath, CancellationToken ct = default)
    {
        FileLog.Write($"[GitStatusProvider] GetStatusAsync: repoPath={repoPath}");

        // Check cache first
        long generation;
        lock (_cacheLock)
        {
            generation = GenerationLocked(repoPath);
            if (_cache.TryGetValue(repoPath, out var cached)
                && DateTime.UtcNow - cached.Timestamp < CacheTtl)
            {
                FileLog.Write($"[GitStatusProvider] GetStatusAsync: cache hit for {repoPath}");
                return cached.Result;
            }
        }

        var (rawOutput, error, exitCode) = await RunGitStatusAsync(repoPath, ct);
        if (exitCode < 0)
            return new GitStatusResult { Success = false, Error = error ?? "Failed to start git process" };
        if (exitCode != 0)
            return new GitStatusResult { Success = false, Error = error };

        var result = ParsePorcelainOutput(rawOutput);

        StoreIfCurrent(repoPath, generation, new CacheEntry(rawOutput, result, DateTime.UtcNow));

        FileLog.Write($"[GitStatusProvider] GetStatusAsync: staged={result.StagedChanges.Count}, unstaged={result.UnstagedChanges.Count}");
        return result;
    }

    /// <summary>
    /// The total count of changed files (staged + unstaged), WITH whether the probe succeeded.
    /// <see cref="GitCountResult.Success"/> is false when git could not be run - the count is then
    /// UNKNOWN, and callers must not treat it as "zero, therefore clean" (issue 516).
    /// </summary>
    public async Task<GitCountResult> GetCountAsync(string repoPath, CancellationToken ct = default)
    {
        FileLog.Write($"[GitStatusProvider] GetCountAsync: repoPath={repoPath}");

        // Check cache - if we have a full result, derive count from it
        long generation;
        lock (_cacheLock)
        {
            generation = GenerationLocked(repoPath);
            if (_cache.TryGetValue(repoPath, out var cached)
                && DateTime.UtcNow - cached.Timestamp < CacheTtl)
            {
                int cachedCount = cached.Result.StagedChanges.Count + cached.Result.UnstagedChanges.Count;
                FileLog.Write($"[GitStatusProvider] GetCountAsync: cache hit, count={cachedCount}");
                return new GitCountResult(Success: true, Count: cachedCount);
            }
        }

        var (rawOutput, error, exitCode) = await RunGitStatusAsync(repoPath, ct);
        if (exitCode != 0)
        {
            // A permissions problem, a transient process failure, a corrupt repository, or a missing
            // git executable - the count is UNKNOWN. Reporting 0 here would erase the distinction
            // between "clean" and "could not tell", which downstream reads as verified-clean.
            FileLog.Write($"[GitStatusProvider] GetCountAsync: git failed (exit={exitCode}) - count is unknown: {error}");
            return new GitCountResult(Success: false, Count: 0);
        }

        int count = CountPorcelainLines(rawOutput);

        // Parse full result and cache it so subsequent GetStatusAsync calls benefit
        var result = ParsePorcelainOutput(rawOutput);
        StoreIfCurrent(repoPath, generation, new CacheEntry(rawOutput, result, DateTime.UtcNow));

        FileLog.Write($"[GitStatusProvider] GetCountAsync: count={count}");
        return new GitCountResult(Success: true, Count: count);
    }

    /// <summary>
    /// Removes the cached status for a repo so the next refresh fetches fresh data.
    /// </summary>
    public static void InvalidateCache(string repoPath)
    {
        FileLog.Write($"[GitStatusProvider] InvalidateCache: repoPath={repoPath}");
        lock (_cacheLock)
        {
            _cache.Remove(repoPath);
            _generations[repoPath] = GenerationLocked(repoPath) + 1;
        }
    }

    /// <summary>
    /// Returns the raw porcelain output string from the last cached result for
    /// the given repo, or null if not cached. Used for change detection.
    /// </summary>
    public string? GetCachedRawOutput(string repoPath)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(repoPath, out var cached)
                && DateTime.UtcNow - cached.Timestamp < CacheTtl)
            {
                return cached.RawOutput;
            }
        }
        return null;
    }

    private async Task<(string Output, string? Error, int ExitCode)> RunGitStatusAsync(string repoPath, CancellationToken ct)
    {
        if (RunStatusOverride != null)
            return await RunStatusOverride(repoPath, ct);
        try
        {
            // ProcessRunner drains stdout and stderr concurrently and honors cancellation by killing
            // the child (issue 516): the old code read stdout to end before stderr, so a git process
            // that filled its stderr pipe could deadlock, and it passed no token, so a superseded
            // scan could not stop it.
            var r = await ProcessRunner.RunAsync(_executable, new[] { "status", "--porcelain=v1", "-u" }, repoPath, ct);
            if (!r.Started)
                // Carries the REASON, not just the fact. On a machine with no git this is the
                // sentence the Source Control view puts on the screen (issue #1048); it used to be
                // the fixed "Failed to start git process", which threw the reason away.
                return ("", GitLaunchFailure.Describe(r.StartErrorCode, r.StandardError), -1);
            return (r.StandardOutput, r.StandardError, r.ExitCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitStatusProvider] RunGitStatusAsync FAILED: {ex.Message}");
            return ("", ex.Message, -1);
        }
    }

    /// <summary>
    /// Counts the number of change entries in porcelain output without allocating
    /// per-file objects. Each entry may produce 1 or 2 counts (staged + unstaged).
    /// </summary>
    internal static int CountPorcelainLines(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return 0;

        int count = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 3) continue;

            var x = line[0];
            var y = line[1];

            if (x == '?' && y == '?')
            {
                count++; // untracked = 1 unstaged entry
                continue;
            }
            if (x != ' ') count++; // staged
            if (y != ' ') count++; // unstaged
        }
        return count;
    }

    public static GitStatusResult ParsePorcelainOutput(string output)
    {
        var staged = new List<GitFileEntry>();
        var unstaged = new List<GitFileEntry>();

        if (string.IsNullOrWhiteSpace(output))
            return new GitStatusResult { StagedChanges = staged, UnstagedChanges = unstaged, Success = true };

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 3)
                continue;

            var x = line[0]; // index (staged) status
            var y = line[1]; // worktree (unstaged) status
            var filePath = line[3..].Trim();

            // Handle renames: "R  old -> new"
            if (filePath.Contains(" -> "))
                filePath = filePath.Split(" -> ")[1];

            // Strip trailing slashes from directory entries
            filePath = filePath.TrimEnd('/', '\\');

            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(fileName))
                fileName = filePath;

            // Untracked files
            if (x == '?' && y == '?')
            {
                unstaged.Add(new GitFileEntry
                {
                    Status = GitFileStatus.Untracked,
                    StatusChar = "?",
                    FilePath = filePath,
                    FileName = fileName,
                    IsStaged = false
                });
                continue;
            }

            // Staged changes (X is non-space)
            if (x != ' ')
            {
                staged.Add(new GitFileEntry
                {
                    Status = CharToStatus(x),
                    StatusChar = x.ToString(),
                    FilePath = filePath,
                    FileName = fileName,
                    IsStaged = true
                });
            }

            // Unstaged changes (Y is non-space)
            if (y != ' ')
            {
                unstaged.Add(new GitFileEntry
                {
                    Status = CharToStatus(y),
                    StatusChar = y.ToString(),
                    FilePath = filePath,
                    FileName = fileName,
                    IsStaged = false
                });
            }
        }

        return new GitStatusResult { StagedChanges = staged, UnstagedChanges = unstaged, Success = true };
    }

    private static GitFileStatus CharToStatus(char c) => c switch
    {
        'M' => GitFileStatus.Modified,
        'A' => GitFileStatus.Added,
        'D' => GitFileStatus.Deleted,
        'R' => GitFileStatus.Renamed,
        'C' => GitFileStatus.Copied,
        '?' => GitFileStatus.Untracked,
        _ => GitFileStatus.Unknown
    };
}
