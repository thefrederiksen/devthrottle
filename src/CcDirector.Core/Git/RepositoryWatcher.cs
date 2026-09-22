using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Watches the registered root directories and each known repository, and asks the
/// <see cref="RepositoryMonitor"/> to recompute ONLY the affected repository - so after the first
/// scan the model stays current by reacting to change instead of re-scanning everything
/// (issue devthrottle_internal#510, phase A).
///
/// Watch set:
/// - each ROOT, non-recursive, directory events only: a repo folder appearing or vanishing;
/// - each repo, recursive: inside .git only the state signals (HEAD, packed-refs, refs/, logs/HEAD, and
///   each linked worktree's HEAD, logs/HEAD and locked marker) fire - .git/index, each linked
///   worktree's index and .git/objects are ignored because our own status scans touch the indexes
///   (self-echo) and object writes are covered by the reflog; OUTSIDE .git, any
///   working-tree change fires, so editing a tracked file, adding an untracked file, or deleting a
///   worktree file updates the repository's cleanliness and dirty-age (issue 516).
///
/// Events are debounced per repository (a burst - including a build-output flood - collapses to one
/// recompute after it settles).
///
/// Recovery (issue 516): a FileSystemWatcher can silently drop events on an internal-buffer overflow,
/// and some state changes emit no event a per-repo watcher can see (a repository created by
/// "git init" in an existing folder, a slow clone whose directory appeared before its .git). Two
/// safety nets cover these: a <see cref="FileSystemWatcher.Error"/> handler and a periodic timer both
/// raise <see cref="ReconciliationRequested"/>, which the host wires to a full rescan.
/// </summary>
public sealed class RepositoryWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly RepositoryMonitor _monitor;
    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _rootWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _repoWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Background.BackgroundJob _reconcileTimer;
    private bool _disposed;

    /// <summary>Raised after a debounced change triggered a recompute (test observability).</summary>
    public event Action<string>? Recomputed;

    /// <summary>
    /// Raised when the watcher cannot trust its incremental view and a FULL rescan is needed: a
    /// FileSystemWatcher error (buffer overflow) or the periodic reconciliation tick. The host wires
    /// this to its repository rescan. This is how a "git init" in an existing folder, a slow clone,
    /// and any events dropped on overflow are eventually reconciled.
    /// </summary>
    public event Action? ReconciliationRequested;

    public RepositoryWatcher(RepositoryMonitor monitor, TimeSpan? reconcileInterval = null, Background.BackgroundJobs? jobs = null)
    {
        _monitor = monitor;
        var interval = reconcileInterval ?? TimeSpan.FromMinutes(5);
        // A slow-and-steady job on the scheduler (docs/BackgroundWork.md): the safety net behind the
        // file events, on the cadence it always had.
        _reconcileTimer = (jobs ?? Background.BackgroundJobs.Default).Register(
            new Background.BackgroundJobSpec("Repository watcher: full reconciliation", Background.BackgroundJobTier.SlowAndSteady, interval, "the watcher is disposed"),
            _ => { RaiseReconciliation("periodic reconciliation"); return Task.CompletedTask; });
        _reconcileTimer.StartTimer();
    }

    /// <summary>
    /// Bring the watch set in line with the current roots and known repositories. Idempotent - call
    /// after every completed scan; new repos gain a watcher, vanished ones lose theirs.
    /// </summary>
    public void SyncWatches(IEnumerable<string> roots, IEnumerable<string> repoPaths)
    {
        if (_disposed) return;
        lock (_gate)
        {
            var wantedRoots = roots.Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wantedRepos = repoPaths.Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var stale in _rootWatchers.Keys.Where(k => !wantedRoots.Contains(k)).ToList())
            {
                _rootWatchers[stale].Dispose();
                _rootWatchers.Remove(stale);
            }
            foreach (var stale in _repoWatchers.Keys.Where(k => !wantedRepos.Contains(k)).ToList())
            {
                _repoWatchers[stale].Dispose();
                _repoWatchers.Remove(stale);
            }

            foreach (var root in wantedRoots.Where(r => !_rootWatchers.ContainsKey(r)))
            {
                try { _rootWatchers[root] = CreateRootWatcher(root); }
                catch (Exception ex) { FileLog.Write($"[RepositoryWatcher] root watch failed for {root}: {ex.Message}"); }
            }
            foreach (var repo in wantedRepos.Where(r => !_repoWatchers.ContainsKey(r)))
            {
                var gitDir = Path.Combine(repo, ".git");
                if (!Directory.Exists(gitDir))
                    continue; // a worktree-style .git FILE has its real state under the primary's .git
                try { _repoWatchers[repo] = CreateRepoWatcher(repo); }
                catch (Exception ex) { FileLog.Write($"[RepositoryWatcher] git watch failed for {repo}: {ex.Message}"); }
            }

            FileLog.Write($"[RepositoryWatcher] watching {_rootWatchers.Count} roots, {_repoWatchers.Count} repositories");
        }
    }

    private FileSystemWatcher CreateRootWatcher(string root)
    {
        var w = new FileSystemWatcher(root)
        {
            NotifyFilter = NotifyFilters.DirectoryName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        void OnDir(object _, FileSystemEventArgs e) => Schedule(Path.Combine(root, Path.GetFileName(e.Name ?? "")));
        w.Created += OnDir;
        w.Deleted += OnDir;
        w.Renamed += (_, e) =>
        {
            Schedule(Path.Combine(root, Path.GetFileName(e.OldName ?? "")));
            Schedule(Path.Combine(root, Path.GetFileName(e.Name ?? "")));
        };
        w.Error += (_, e) => OnWatcherError($"root {root}", e);
        return w;
    }

    private FileSystemWatcher CreateRepoWatcher(string repoPath)
    {
        var w = new FileSystemWatcher(repoPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024, // the largest allowed - fewer overflows on busy trees
            EnableRaisingEvents = true,
        };
        void OnChange(object _, FileSystemEventArgs e)
        {
            if (IsRepoSignal(e.Name))
                Schedule(repoPath);
        }
        w.Created += OnChange;
        w.Deleted += OnChange;
        w.Changed += OnChange;
        w.Renamed += (_, e) => { if (IsRepoSignal(e.Name) || IsRepoSignal(e.OldName)) Schedule(repoPath); };
        w.Error += (_, e) => OnWatcherError($"repository {repoPath}", e);
        return w;
    }

    /// <summary>
    /// Whether a path relative to the repository ROOT should trigger a recompute. Pure and testable.
    /// Inside .git only the state signals count (the index and object churn are ignored); OUTSIDE
    /// .git, any working-tree change counts, so cleanliness and dirty-age stay current (issue 516).
    /// </summary>
    internal static bool IsRepoSignal(string? relative)
    {
        if (string.IsNullOrEmpty(relative))
            return false;
        var p = relative.Replace('/', '\\');

        if (p.Equals(".git", StringComparison.OrdinalIgnoreCase))
            return false; // the .git directory itself changing is not a state signal
        if (p.StartsWith(".git\\", StringComparison.OrdinalIgnoreCase))
            return IsSignal(p[".git\\".Length..]); // inside .git: only the state signals

        return true; // a working-tree path - the tree may have become dirty or clean
    }

    /// <summary>True when a path relative to .git is a state signal we react to. Pure and testable.</summary>
    internal static bool IsSignal(string? relative)
    {
        if (string.IsNullOrEmpty(relative))
            return false;
        var p = relative.Replace('/', '\\');
        return p.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            || p.Equals("packed-refs", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("refs\\", StringComparison.OrdinalIgnoreCase)
            || p.Equals("logs\\HEAD", StringComparison.OrdinalIgnoreCase)
            || IsLinkedWorktreeSignal(p);
    }

    /// <summary>
    /// True when a path under .git\worktrees\ is a linked worktree's state signal: its HEAD (a branch
    /// switch or commit there), its logs\HEAD, or its locked marker. Everything else in a linked
    /// worktree's admin folder is ignored - above all its index and index.lock, which our own status
    /// scans rewrite in every linked worktree. Counting those made each recompute trigger the next one:
    /// a repository with linked worktrees recomputed every ~11s forever, each time downloading the
    /// fleet session list from the Gateway (about 55 MB an hour on a repo with 26 worktrees).
    /// </summary>
    private static bool IsLinkedWorktreeSignal(string p)
    {
        if (!p.StartsWith("worktrees\\", StringComparison.OrdinalIgnoreCase))
            return false;
        var slash = p.IndexOf('\\', "worktrees\\".Length);
        if (slash < 0)
            return false; // the worktree's folder itself: its mtime moves whenever a lock file comes and goes
        var inner = p[(slash + 1)..];
        return inner.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            || inner.Equals("logs\\HEAD", StringComparison.OrdinalIgnoreCase)
            || inner.Equals("locked", StringComparison.OrdinalIgnoreCase);
    }

    private void OnWatcherError(string context, ErrorEventArgs e)
    {
        FileLog.Write($"[RepositoryWatcher] watcher error for {context}: {e.GetException()?.Message} - requesting full reconciliation");
        RaiseReconciliation($"watcher error ({context})");
    }

    private void RaiseReconciliation(string why)
    {
        if (_disposed)
            return;
        FileLog.Write($"[RepositoryWatcher] full reconciliation requested: {why}");
        try
        {
            ReconciliationRequested?.Invoke();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryWatcher] reconciliation handler threw: {ex.Message}");
        }
    }

    /// <summary>Debounced per-repo: a burst of events collapses into one recompute.</summary>
    private void Schedule(string repoPath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(repoPath))
            return;
        var key = WorktreeReaperService.NormalizePath(repoPath);
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var old))
                old.Cancel();
            cts = new CancellationTokenSource();
            _pending[key] = cts;
        }
        _ = DebouncedRecomputeAsync(repoPath, key, cts);
    }

    private async Task DebouncedRecomputeAsync(string repoPath, string key, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(Debounce, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer event for the same repo
        }

        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var current) && current == cts)
                _pending.Remove(key);
        }

        try
        {
            FileLog.Write($"[RepositoryWatcher] change settled - recomputing {repoPath}");
            // Drop the 10-second status cache for this repository FIRST (inspection): a working-tree
            // change that lands right after a scan populated the cache would otherwise recompute from
            // the stale "clean" count and republish clean, staying wrong until the periodic
            // reconciliation. Invalidating here makes the recompute read the tree as it is now.
            GitStatusProvider.InvalidateCache(repoPath);
            await _monitor.RecomputeOneAsync(repoPath);
            Recomputed?.Invoke(repoPath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryWatcher] recompute failed for {repoPath}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _reconcileTimer.Dispose();
            foreach (var w in _rootWatchers.Values) w.Dispose();
            foreach (var w in _repoWatchers.Values) w.Dispose();
            _rootWatchers.Clear();
            _repoWatchers.Clear();
            foreach (var c in _pending.Values) c.Cancel();
            _pending.Clear();
        }
    }
}
