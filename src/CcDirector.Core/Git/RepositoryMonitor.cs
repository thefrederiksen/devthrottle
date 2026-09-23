using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// A long-lived, in-memory model of the repositories under the registered root directories, updated
/// by progressive background scans. It is the source of truth the Repository screen (and, later, the
/// per-session Worktrees tab and the Cockpit) render - they subscribe and display; they never scan.
///
/// A rescan streams results: each repository is published as soon as it is computed
/// (<see cref="Upserted"/>), progress is reported as it goes, and repositories no longer found are
/// removed at the end (<see cref="Removed"/>). Events fire on the scanning thread; UI subscribers
/// marshal to their dispatcher.
///
/// Consistency rules (issue devthrottle_internal#510, inspection round 1):
/// - The monitor owns the live-session source (<see cref="LiveSessionsProvider"/>) and consults it
///   on EVERY compute, so no compute path can erase the in-use-by-session classification.
/// - Newest compute wins, enforced AT THE PUBLISH (inspection round 2, ruling R2-5): every compute
///   takes a monotonically increasing start stamp, and a publish whose stamp is older than the one
///   the model already recorded for that key - including a removal - is dropped. The per-repository
///   semaphore (single-flight) and the defer-during-scan rule remain as efficiency devices; the
///   stamp rule is the correctness device. Deferred recomputes keep their requester's own token.
/// - A linked-worktree path is canonicalized to its PRIMARY checkout before computing, so a
///   worktree path never becomes its own model entry.
/// </summary>
public sealed class RepositoryMonitor
{
    private readonly Func<IEnumerable<string>, IReadOnlyList<string>> _enumerate;
    private readonly Func<string, IReadOnlyList<LiveSessionRef>?, CancellationToken, Task<RepositoryStatus>> _compute;
    private readonly Func<string, CancellationToken, Task<string?>> _resolvePrimary;
    private readonly Func<string, bool> _isRepository;

    private readonly object _gate = new();
    private readonly Dictionary<string, RepositoryStatus> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _repoLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeferredRecompute> _deferredRecomputes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Repository key -> the ONE follow-up owed to requests that arrived while that repository's
    /// single recompute was running (null when none is owed). A key is present only while a
    /// recompute of it runs. See <see cref="ComputeCoalescedAsync"/>.
    /// </summary>
    private readonly Dictionary<string, TaskCompletionSource?> _recomputesInFlight = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private readonly string? _cachePath;

    /// <summary>A single-repository recompute parked while a full scan runs - it keeps the
    /// ORIGINAL requester's token, so a request whose requester gave up is skipped at drain.</summary>
    private readonly record struct DeferredRecompute(string Path, CancellationToken Token);

    /// <summary>
    /// Monotonically increasing compute-start stamps (ruling R2-5): every compute - scan or
    /// single recompute - takes a stamp when it STARTS, and the model records per key the stamp
    /// of the newest accepted publish (a removal counts as a publish of "absent"). A publish
    /// whose stamp is older than the recorded one is dropped, so an older compute can never
    /// overwrite - or resurrect - a newer result, whatever order the publishes arrive in. The
    /// per-repository semaphore remains an efficiency device (it avoids duplicate concurrent
    /// walks); THIS rule is the correctness device.
    /// </summary>
    private long _computeStampCounter;
    private readonly Dictionary<string, long> _publishStamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// In-flight compute registrations (ruling R4-3): key -> the start stamps of every compute
    /// currently in flight for it. A compute's start stamp otherwise lives in a local variable
    /// until publication, which made a ROWLESS in-flight compute invisible to scan
    /// reconciliation - the scan observed the path absent, tombstoned nothing (no row), and the
    /// older compute's later publish resurrected the repository. Every compute registers its
    /// start stamp here in the same gated region that hands the stamp out, and clears it when
    /// it publishes or abandons; reconciliation tombstones every unseen key that has a row OR a
    /// pending compute.
    /// </summary>
    private readonly Dictionary<string, List<long>> _pendingComputes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while a scan is in progress.</summary>
    public bool IsScanning { get; private set; }

    /// <summary>
    /// True once a scan has RUN TO COMPLETION in this process - the same moment
    /// <see cref="ScanCompleted"/> is raised, and never set by a superseded, cancelled or faulted scan.
    ///
    /// It answers one question and it is asked by one caller: has this Director's view of its own disk
    /// settled? <see cref="IsScanning"/> cannot answer it, because it is false both after a scan and
    /// BEFORE the first one has started, and those two states are opposites. <see cref="Snapshot"/>
    /// cannot answer it either: an empty model means "nothing found" after a scan and "nothing looked
    /// at yet" before one.
    ///
    /// The caller is <c>ControlApiHost.SnapshotRepositories</c>, which folds the machine's registered
    /// repository list into the snapshot it pushes to the Gateway, and must not do so until this is
    /// true. The Gateway treats a push with no unverified entry in it as a COMPLETE view of what that
    /// Director knows, and reconciles against it - so a push made before the first scan had run, and
    /// carrying nothing but the registry, would read as "every repository under every root folder has
    /// gone away" and delete rows that were simply not looked at yet. A warm-start cache normally
    /// masks that, because its entries are provisional and a provisional entry suspends
    /// reconciliation; on a machine with no cache yet there is nothing to mask it.
    ///
    /// It is deliberately NOT reset when a later scan starts. A machine whose view has settled once
    /// does not become unknown again while it is being re-checked, and the model keeps its previous
    /// entries throughout a rescan rather than emptying first.
    /// </summary>
    public bool HasCompletedAScan { get; private set; }

    /// <summary>How many repositories have been computed in the current/last scan.</summary>
    public int ScanDone { get; private set; }

    /// <summary>How many repositories the current/last scan set out to compute.</summary>
    public int ScanTotal { get; private set; }

    /// <summary>Raised when a repository's status is added or updated in the model.</summary>
    public event Action<RepositoryStatus>? Upserted;

    /// <summary>Raised when a repository is removed from the model (no longer found on disk).</summary>
    public event Action<RepositoryStatus>? Removed;

    /// <summary>Raised when <see cref="IsScanning"/>, <see cref="ScanDone"/>, or <see cref="ScanTotal"/> changes.</summary>
    public event Action? ProgressChanged;

    /// <summary>Raised once when a scan finishes (not raised when a scan is superseded/cancelled).</summary>
    public event Action? ScanCompleted;

    /// <summary>
    /// THE live-session source for every compute this monitor runs - full scans and single-repository
    /// recomputes alike. The host MUST wire it before the first scan (ruling R2-8): scanning without
    /// a session source would silently publish session-blind safety classifications, so
    /// <see cref="RescanAsync"/> and <see cref="RecomputeOneAsync"/> throw while it is null - a
    /// programming error fails loudly instead of degrading.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>>? LiveSessionsProvider { get; set; }

    /// <summary>Fails loudly when no live-session source is wired (ruling R2-8).</summary>
    private void RequireLiveSessionsProvider(string operation)
    {
        if (LiveSessionsProvider is null)
            throw new InvalidOperationException(
                $"RepositoryMonitor.{operation} requires a LiveSessionsProvider - scanning without a " +
                "session source would publish session-blind safety classifications. Wire the provider " +
                "before triggering any scan or recompute.");
    }

    public RepositoryMonitor(
        Func<IEnumerable<string>, IReadOnlyList<string>>? enumerate = null,
        Func<string, IReadOnlyList<LiveSessionRef>?, CancellationToken, Task<RepositoryStatus>>? compute = null,
        string? cachePath = null,
        Func<string, CancellationToken, Task<string?>>? resolvePrimary = null,
        Func<string, bool>? isRepository = null,
        WorktreeMergeSignalCache? signalCache = null)
    {
        _enumerate = enumerate ?? DefaultEnumerate;
        SignalCache = signalCache ?? new WorktreeMergeSignalCache();
        _compute = compute ?? ((path, sessions, ct) => DefaultCompute(path, sessions, SignalCache, ct));
        _cachePath = cachePath;
        _resolvePrimary = resolvePrimary ?? DefaultResolvePrimary;
        _isRepository = isRepository ?? DefaultIsRepository;
    }

    /// <summary>
    /// Warm start: load the last run's repositories from the JSON cache into the model, so a screen
    /// opened before the first scan finishes shows content immediately. The scan then re-verifies and
    /// reconciles. Best-effort and silent - a bad or missing cache just means an empty warm start.
    /// </summary>
    public void LoadCache()
    {
        if (string.IsNullOrEmpty(_cachePath) || !File.Exists(_cachePath))
            return;
        try
        {
            var cached = JsonSerializer.Deserialize<List<RepositoryStatus>>(File.ReadAllText(_cachePath));
            if (cached == null)
                return;
            lock (_gate)
            {
                foreach (var s in cached)
                    if (!string.IsNullOrWhiteSpace(s.Path))
                        // Cached entries are PROVISIONAL: shown dimmed as "verifying", never acted
                        // on, until the live scan re-confirms them (the warm-start trust rule).
                        _byPath[WorktreeReaperService.NormalizePath(s.Path)] = s with { Provisional = true };
            }
            FileLog.Write($"[RepositoryMonitor] warm-start: loaded {cached.Count} repositories from cache");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryMonitor] LoadCache failed: {ex.Message}");
        }
    }

    private void SaveCache()
    {
        if (string.IsNullOrEmpty(_cachePath))
            return;
        try
        {
            List<RepositoryStatus> snapshot;
            lock (_gate)
                snapshot = _byPath.Values.ToList();
            var dir = Path.GetDirectoryName(_cachePath);
            if (dir != null)
                Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryMonitor] SaveCache failed: {ex.Message}");
        }
    }

    /// <summary>A thread-safe copy of the current model.</summary>
    public IReadOnlyList<RepositoryStatus> Snapshot()
    {
        lock (_gate)
            return _byPath.Values.ToList();
    }

    /// <summary>
    /// Finds the repository entry a path belongs to: the repository itself, or the repository one of
    /// whose worktrees IS that path. This is how a session sitting inside a worktree finds its repo's
    /// entry (the one-brain rule: the per-session tab renders the same model as the Repositories home).
    /// </summary>
    public RepositoryStatus? FindForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var key = WorktreeReaperService.NormalizePath(path);
        lock (_gate)
        {
            if (_byPath.TryGetValue(key, out var direct))
                return direct;
            foreach (var s in _byPath.Values)
                foreach (var w in s.Worktrees)
                    if (string.Equals(WorktreeReaperService.NormalizePath(w.Path), key, StringComparison.OrdinalIgnoreCase))
                        return s;
        }
        return null;
    }

    /// <summary>
    /// Rescan the given roots, streaming each repository's status into the model as it is computed.
    /// A new rescan supersedes any in-flight one. Live sessions come from
    /// <see cref="LiveSessionsProvider"/> on every compute.
    /// </summary>
    public async Task RescanAsync(IEnumerable<string> roots, CancellationToken externalCt = default)
    {
        RequireLiveSessionsProvider(nameof(RescanAsync));
        CancellationTokenSource cts;
        long scanStartStamp;
        // Ownership, the scanning flag, and the scan's start stamp are taken in ONE gated
        // region BEFORE enumeration (ruling R4-4): from the moment this scan owns the monitor
        // it is visibly scanning, so a recompute arriving during enumeration - or drained by a
        // predecessor's exit - defers to this scan instead of racing it.
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            cts = _cts;
            // The scan's removals publish with the SCAN'S OWN START stamp (ruling R3-5),
            // captured here when the scan begins - never a fresh stamp at reconciliation time.
            // A compute that starts after this moment carries a newer stamp, so its publish
            // outranks this scan's removals: a repository created and published mid-scan
            // survives the scan's reconciliation instead of being removed by it.
            scanStartStamp = NextComputeStampLocked();
            IsScanning = true;
            ScanTotal = 0;
            ScanDone = 0;
        }
        var ct = cts.Token;

        // Everything from here runs inside try/finally (ruling R4-4): an enumeration fault in
        // a scan that owns the monitor must still release the lifecycle state and drain the
        // deferred queue on its way out - before this, the fault left IsScanning and the
        // deferred requests stranded forever.
        bool completed = false;
        try
        {
            ProgressChanged?.Invoke();
            var paths = _enumerate(roots);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (_gate)
            {
                // A superseded scan must not clobber its replacement's progress counters.
                if (ct.IsCancellationRequested || !ReferenceEquals(_cts, cts))
                    throw new OperationCanceledException(ct);
                ScanTotal = paths.Count;
            }
            ProgressChanged?.Invoke();
            FileLog.Write($"[RepositoryMonitor] rescan started: {paths.Count} repositories");

            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                var pendingKey = WorktreeReaperService.NormalizePath(path);
                var repoLock = GetRepoLock(pendingKey);
                await repoLock.WaitAsync(ct);
                RepositoryStatus? published;
                long computeStamp;
                lock (_gate)
                {
                    // The stamp is handed out and the compute REGISTERED as in flight in one
                    // gated region (ruling R4-3), so a concurrent scan's reconciliation can
                    // see this compute even before it has published a row.
                    computeStamp = NextComputeStampLocked();
                    RegisterPendingComputeLocked(pendingKey, computeStamp);
                }
                try
                {
                    var sessions = await FetchLiveSessionsAsync(ct);
                    var status = await _compute(path, sessions, ct);
                    var key = WorktreeReaperService.NormalizePath(status.Path);
                    seen.Add(key);
                    lock (_gate)
                    {
                        // A cancelled or superseded scan never publishes (ruling R2-2):
                        // cancellation can land in the narrow interval after the compute
                        // returned, and the next loop iteration's check is too late. Re-check
                        // the token AND that this scan still owns the model, under the gate,
                        // before writing anything.
                        if (ct.IsCancellationRequested || !ReferenceEquals(_cts, cts))
                            throw new OperationCanceledException(ct);
                        published = PublishIfNewestLocked(key, status, computeStamp);
                        ScanDone++;
                    }
                }
                finally
                {
                    lock (_gate)
                        ClearPendingComputeLocked(pendingKey, computeStamp);
                    repoLock.Release();
                }
                if (published != null)
                    Upserted?.Invoke(published);
                ProgressChanged?.Invoke();
            }

            // Reconcile: drop repositories that were in the model but not found this scan.
            List<RepositoryStatus> removed;
            lock (_gate)
            {
                // Same rule at the reconcile (ruling R2-2): only the owning, uncancelled scan
                // may remove entries - a superseded scan's roots are not the truth any more.
                if (ct.IsCancellationRequested || !ReferenceEquals(_cts, cts))
                    throw new OperationCanceledException(ct);
                removed = new List<RepositoryStatus>();
                // Every unseen key with a model row OR a pending in-flight compute gets the
                // scan's absence tombstone (ruling R4-3): a rowless compute older than the
                // scan would otherwise be invisible here and publish stale state afterward.
                var unseenKeys = _byPath.Keys.Where(k => !seen.Contains(k))
                    .Concat(_pendingComputes.Keys.Where(k => !seen.Contains(k)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var key in unseenKeys)
                {
                    // A removal is a publish of "absent" (ruling R2-5) stamped with the scan's
                    // START stamp (ruling R3-5). A key whose recorded publish is NEWER than the
                    // scan start was legitimately published by a compute that began after this
                    // scan enumerated - that publish outranks the removal, so the entry stays.
                    // (A repository genuinely gone is removed by its own gone-path recompute or
                    // by the next scan.)
                    if (_publishStamps.TryGetValue(key, out var newest) && newest > scanStartStamp)
                        continue;
                    _publishStamps[key] = scanStartStamp;
                    if (_byPath.TryGetValue(key, out var row))
                    {
                        _byPath.Remove(key);
                        removed.Add(row);
                    }
                }
            }
            // The signal cache is brought in line with the model BEFORE any Removed subscriber runs:
            // every repository above has already left the model, so a subscriber that throws on the
            // first one must not leave the later ones cached. Forget each dropped repository, then
            // sweep anything cached for a repository the model does not hold (an unseen key with no
            // row, a root that was unregistered).
            foreach (var r in removed)
                SignalCache.Forget(r.Path);
            List<string> modelPaths;
            lock (_gate)
                modelPaths = _byPath.Keys.ToList();
            SignalCache.KeepRepositoriesOnly(modelPaths);

            foreach (var r in removed)
                Removed?.Invoke(r);

            // Persist the verified model so the next launch warm-starts.
            SaveCache();

            // The size cache only stays meaningful for worktrees that still exist: evict entries
            // this completed scan did not see (reaped, moved, or deleted worktrees).
            RepositoryStatusService.EvictSizeCacheExcept(CurrentWorktreePaths());

            // Per-repository compute state is evicted alongside it (ruling R2-11): semaphores
            // and stale publish stamps for keys no longer in the model would otherwise
            // accumulate for the process lifetime.
            var removedThisScan = new HashSet<string>(
                removed.Select(r => WorktreeReaperService.NormalizePath(r.Path)),
                StringComparer.OrdinalIgnoreCase);
            EvictStaleComputeState(removedThisScan);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[RepositoryMonitor] rescan superseded/cancelled");
        }
        finally
        {
            // Scan lifecycle state belongs to the CURRENT scan only (ruling R3-6): ownership is
            // checked under the gate against the monitor's current cancellation source. A
            // superseded scan exits without touching IsScanning or progress - its replacement is
            // still scanning and owns both. The owner check, the IsScanning clear, and the
            // completion decision are ONE gate acquisition (ruling R4-4): ScanCompleted and the
            // completion log are raised only when that same gated decision said owner.
            bool owner, raiseCompleted;
            lock (_gate)
            {
                owner = ReferenceEquals(_cts, cts);
                if (owner)
                    IsScanning = false;
                raiseCompleted = owner && completed;
                // Set in the SAME gated decision that decides whether ScanCompleted is raised, so the
                // flag and the event can never disagree about whether a scan finished.
                if (raiseCompleted)
                    HasCompletedAScan = true;
            }
            if (owner)
            {
                // Deferred requests are drained on EVERY exit path of the OWNING scan (ruling
                // R3-6) - completed, externally cancelled, or faulted - so a cancelled scan with
                // no successor never strands them. Each deferred request kept its ORIGINAL
                // requester's token (ruling R2-5). When a newer scan superseded this one, that
                // newer scan owns the drain: every one of ITS exit paths runs this same block.
                // Each drained request goes back through RecomputeOneAsync's own deferral check
                // (ruling R4-4): when a newer scan has meanwhile started, it re-defers to that
                // scan instead of running concurrently with it. A THROWING ProgressChanged
                // subscriber must not skip the drain (round-5 finding) - IsScanning is already
                // cleared here, so a skipped drain would strand the deferred requests. The
                // subscriber's exception is CAPTURED and rethrown with its type intact after
                // the drain (round-6 finding: an awaited drain in a finally would let a drain
                // failure silently replace it); if BOTH throw, both surface together.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo? progressFailure = null;
                try
                {
                    ProgressChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    progressFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                }
                try
                {
                    await DrainDeferredRecomputesAsync();
                }
                catch (Exception drainEx) when (progressFailure != null)
                {
                    throw new AggregateException(progressFailure.SourceException, drainEx);
                }
                progressFailure?.Throw();
            }
            if (raiseCompleted)
            {
                FileLog.Write($"[RepositoryMonitor] rescan completed: {ScanDone}/{ScanTotal}");
                ScanCompleted?.Invoke();
            }
        }
    }

    /// <summary>
    /// Recompute ONE repository and publish the result - the file watcher's path: a change under
    /// one repo never re-scans the others. Removes the entry when the directory is gone. A linked
    /// worktree path is canonicalized to its primary checkout first, so the PRIMARY entry is
    /// recomputed and a worktree path never becomes a model entry of its own. While a full scan is
    /// running the recompute is deferred and runs when the scan completes.
    /// </summary>
    public async Task RecomputeOneAsync(string repoPath, CancellationToken ct = default)
    {
        RequireLiveSessionsProvider(nameof(RecomputeOneAsync));
        lock (_gate)
        {
            if (IsScanning)
            {
                // The deferred request keeps ITS OWN token (ruling R2-5): if the requester gives
                // up before the scan completes, the drain skips this request instead of running
                // it under someone else's token.
                _deferredRecomputes[WorktreeReaperService.NormalizePath(repoPath)] = new DeferredRecompute(repoPath, ct);
                FileLog.Write($"[RepositoryMonitor] recompute deferred until the running scan completes: {repoPath}");
                return;
            }
        }

        // The observation stamp is taken BEFORE touching the filesystem (ruling R4-5): stamps
        // order by OBSERVATION time, and an absence observed before a newer add published must
        // lose to that add. Stamping after the observation handed the OLDER observation the
        // NEWER stamp, letting it remove the legitimately added repository.
        long observationStamp;
        lock (_gate)
            observationStamp = NextComputeStampLocked();

        // "Gone" means the folder disappeared OR it is no longer a git repository (a root watcher
        // fires for ANY subdirectory; a non-repo folder must never become a model entry). The
        // observation goes through the injectable seam so tests can hold it open at its exact
        // point in time (ruling R4-5).
        bool isRepo = _isRepository(repoPath);
        if (!isRepo)
        {
            var key = WorktreeReaperService.NormalizePath(repoPath);
            RepositoryStatus? gone = null;
            bool applied = false;
            lock (_gate)
            {
                // Apply the removal only when no NEWER publish exists for the key (ruling
                // R4-5): a newer publish means this absence observation is stale - the newer
                // state stands, and a repository truly gone is removed by a later observation.
                if (!(_publishStamps.TryGetValue(key, out var newest) && newest > observationStamp))
                {
                    applied = true;
                    if (_byPath.TryGetValue(key, out gone))
                        _byPath.Remove(key);
                    // Absence is ALWAYS stamped (ruling R3-4a) - whether or not a model row
                    // exists. A FIRST-TIME compute can be in flight for this key with no row
                    // published yet; without a tombstone its later publish would create the
                    // very repository this newer check just saw vanish. A removal is a publish
                    // of "absent" (ruling R2-5).
                    _publishStamps[key] = observationStamp;
                }
            }
            if (!applied)
            {
                FileLog.Write($"[RepositoryMonitor] recompute: absence observation for {repoPath} is stale - a newer publish stands, yielding");
                return;
            }
            SignalCache.Forget(repoPath);
            if (gone != null)
            {
                FileLog.Write($"[RepositoryMonitor] recompute: {repoPath} is gone - removed");
                Removed?.Invoke(gone);
                SaveCache();
            }
            return;
        }

        // A .git FILE marks a linked worktree - canonicalize to the primary checkout and recompute
        // THAT entry. Failing to resolve is a real error, not a reason to guess.
        bool gitIsFile = File.Exists(Path.Combine(repoPath, ".git"));
        var targetPath = repoPath;
        if (gitIsFile)
        {
            var primary = await _resolvePrimary(repoPath, ct);
            if (string.IsNullOrWhiteSpace(primary))
                throw new InvalidOperationException(
                    $"could not resolve the primary repository for the linked worktree path: {repoPath}");
            FileLog.Write($"[RepositoryMonitor] recompute: {repoPath} is a linked worktree of {primary}");
            targetPath = primary;
        }

        await ComputeCoalescedAsync(targetPath, WorktreeReaperService.NormalizePath(targetPath), ct);
    }

    /// <summary>
    /// Computes and publishes one repository, COALESCING requests (plan step 7c). While a recompute of
    /// this repository runs, a further request does not queue a compute of its own: it marks the
    /// repository dirty and joins the ONE follow-up that runs when the current compute finishes. So
    /// there are never two computes in parallel, and never a dropped change - a request that arrived
    /// mid-compute may have been missed by it, so the follow-up always runs, and each joiner's await
    /// ends only when the follow-up it joined has published.
    ///
    /// Before this, the per-repository semaphore serialized the computes but ran one full compute per
    /// waiting request: five requests during one slow compute became five more git inventories.
    ///
    /// Only the compute is coalesced. The gone check and the worktree canonicalization in
    /// <see cref="RecomputeOneAsync"/> still run per request and never wait behind a compute, so an
    /// absence observation is not held up by a slow compute (ruling R3-4a).
    ///
    /// The follow-up is owed to every joiner at once and belongs to none of them, so it runs under no
    /// requester's token; each joiner stops WAITING when its own token is cancelled.
    /// </summary>
    private async Task ComputeCoalescedAsync(string targetPath, string targetKey, CancellationToken ct)
    {
        Task? joined = null;
        lock (_gate)
        {
            if (_recomputesInFlight.TryGetValue(targetKey, out var followUp))
            {
                followUp ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _recomputesInFlight[targetKey] = followUp;
                joined = followUp.Task;
            }
            else
            {
                _recomputesInFlight[targetKey] = null;
            }
        }
        if (joined != null)
        {
            FileLog.Write($"[RepositoryMonitor] recompute already running - coalesced into its follow-up: {targetPath}");
            await joined.WaitAsync(ct);
            return;
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo? ownFailure = null;
        TaskCompletionSource? completing = null;
        var token = ct;
        while (true)
        {
            try
            {
                await ComputeAndPublishOneAsync(targetPath, targetKey, token);
                completing?.TrySetResult();
            }
            catch (Exception ex)
            {
                if (completing == null)
                    ownFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                else
                    completing.TrySetException(ex);
            }

            lock (_gate)
            {
                completing = _recomputesInFlight[targetKey];
                if (completing == null)
                {
                    _recomputesInFlight.Remove(targetKey);
                    break;
                }
                _recomputesInFlight[targetKey] = null; // requests from here on are owed the NEXT follow-up
            }
            token = CancellationToken.None;
            FileLog.Write($"[RepositoryMonitor] running the coalesced follow-up recompute: {targetPath}");
        }
        ownFailure?.Throw();
    }

    private async Task ComputeAndPublishOneAsync(string targetPath, string targetKey, CancellationToken ct)
    {
        var repoLock = GetRepoLock(targetKey);
        await repoLock.WaitAsync(ct);
        RepositoryStatus? published;
        long computeStamp;
        lock (_gate)
        {
            // Stamp hand-out and in-flight registration are one gated region (ruling R4-3):
            // a scan that observes this path absent while the compute is still rowless can
            // tombstone it at reconciliation instead of being resurrected by its late publish.
            computeStamp = NextComputeStampLocked();
            RegisterPendingComputeLocked(targetKey, computeStamp);
        }
        try
        {
            var sessions = await FetchLiveSessionsAsync(ct);
            var status = await _compute(targetPath, sessions, ct);
            lock (_gate)
                published = PublishIfNewestLocked(targetKey, status, computeStamp);
        }
        finally
        {
            lock (_gate)
                ClearPendingComputeLocked(targetKey, computeStamp);
            repoLock.Release();
        }
        if (published == null)
            return; // a newer compute (or a newer removal) already ruled this key - dropped
        FileLog.Write($"[RepositoryMonitor] recomputed one: {published.Name}");
        Upserted?.Invoke(published);
        SaveCache();
    }

    /// <summary>
    /// One compute at a time per repository. An EFFICIENCY device only (it avoids duplicate
    /// concurrent walks of the same working tree) - publish ordering is guaranteed by the
    /// compute-start stamps in <see cref="PublishIfNewestLocked"/>, not by this lock.
    /// </summary>
    private SemaphoreSlim GetRepoLock(string key)
    {
        lock (_gate)
        {
            if (!_repoLocks.TryGetValue(key, out var sem))
                _repoLocks[key] = sem = new SemaphoreSlim(1, 1);
            return sem;
        }
    }

    /// <summary>
    /// Evicts per-repository compute state after a completed scan (ruling R2-11): a semaphore is
    /// removed when its key is no longer in the model AND it is currently un-held (a held one has
    /// a compute in flight and is kept). Accepted and documented crossover: a compute that still
    /// holds a REFERENCE to an evicted semaphore while the same key is re-created briefly gives
    /// that key two semaphores - single-flight degrades to double-flight for one compute, and the
    /// publish-stamp rule (R2-5) still guarantees the newest compute start wins. Evicted
    /// semaphores are not disposed: a stale reference may still be awaited, and an un-disposed
    /// SemaphoreSlim without a wait handle holds no operating system resources.
    ///
    /// Publish stamps are evicted for keys neither in the model nor removed by THIS scan: a
    /// removal stamp must survive until the next completed scan so it can still drop a late
    /// publish from a compute that started before the removal. A stamp whose key's semaphore is
    /// currently HELD is never evicted (ruling R3-4b) - the held semaphore proves a compute is
    /// still in flight, and its tombstone must outlive that compute whatever the scan count.
    /// </summary>
    private void EvictStaleComputeState(IReadOnlySet<string> removedThisScan)
    {
        lock (_gate)
        {
            int evictedLocks = 0, evictedStamps = 0;
            foreach (var key in _repoLocks.Keys.ToList())
            {
                if (_byPath.ContainsKey(key))
                    continue;
                if (_repoLocks[key].CurrentCount == 0)
                    continue; // held - a compute is in flight for this key right now
                _repoLocks.Remove(key);
                evictedLocks++;
            }
            foreach (var key in _publishStamps.Keys.ToList())
            {
                if (_byPath.ContainsKey(key) || removedThisScan.Contains(key))
                    continue;
                // A registered pending compute is DIRECT proof a compute is in flight for this
                // key (ruling R4-3) - its tombstone must outlive that compute even when the
                // compute holds a stale reference to an already-evicted semaphore (the
                // documented single-flight crossover), which the held-semaphore check below
                // cannot see.
                if (_pendingComputes.ContainsKey(key))
                    continue;
                // A held semaphore is proof a compute is still in flight for this key (ruling
                // R3-4b): its stamp - a removal tombstone in particular - must survive until
                // that compute has published and been dropped, however many scans complete in
                // between. Eviction never removes stamp state out from under a live compute.
                if (_repoLocks.TryGetValue(key, out var heldCheck) && heldCheck.CurrentCount == 0)
                    continue;
                _publishStamps.Remove(key);
                evictedStamps++;
            }
            if (evictedLocks > 0 || evictedStamps > 0)
                FileLog.Write($"[RepositoryMonitor] evicted compute state for departed repositories: {evictedLocks} semaphore(s), {evictedStamps} stamp(s)");
        }
    }

    /// <summary>Test probe: whether a per-repository semaphore exists for this path.</summary>
    internal bool RepoLockExistsFor(string path)
    {
        lock (_gate)
            return _repoLocks.ContainsKey(WorktreeReaperService.NormalizePath(path));
    }

    /// <summary>The next compute-start stamp. Callers MUST hold <see cref="_gate"/>.</summary>
    private long NextComputeStampLocked() => ++_computeStampCounter;

    /// <summary>Registers a compute as in flight for its key (ruling R4-3). Callers MUST hold
    /// <see cref="_gate"/> and MUST have taken the stamp in the same gated region.</summary>
    private void RegisterPendingComputeLocked(string key, long stamp)
    {
        if (!_pendingComputes.TryGetValue(key, out var stamps))
            _pendingComputes[key] = stamps = new List<long>();
        stamps.Add(stamp);
    }

    /// <summary>Clears a compute's in-flight registration on publish or abandon (ruling R4-3).
    /// Callers MUST hold <see cref="_gate"/>.</summary>
    private void ClearPendingComputeLocked(string key, long stamp)
    {
        if (!_pendingComputes.TryGetValue(key, out var stamps))
            return;
        stamps.Remove(stamp);
        if (stamps.Count == 0)
            _pendingComputes.Remove(key);
    }

    /// <summary>
    /// THE one guarded publish path (ruling R2-5) - every write of a computed status into the
    /// model, from the full scan and from single recomputes alike, goes through here. Newest
    /// compute start wins: when the key already carries a newer stamp (a newer compute
    /// published, or a newer scan removed the key), this publish is DROPPED and null is
    /// returned. Callers must hold <see cref="_gate"/>.
    /// </summary>
    private RepositoryStatus? PublishIfNewestLocked(string key, RepositoryStatus status, long computeStamp)
    {
        // A FAILED compute is UNKNOWN, not new truth (inspection): the DTO layer drops Success/Error,
        // so a published failure would be served as a clean, zero-value repository and would overwrite
        // a correct row (and, via the Gateway, a correct history row) with zeros. Never publish it -
        // keep the previous row; the next successful compute updates it. The scan still marks the key
        // seen, so reconciliation does not remove the prior row for a transient failure.
        if (!status.Success)
        {
            FileLog.Write($"[RepositoryMonitor] compute for {status.Path} returned unknown (Success=false) - not published, previous row kept");
            return null;
        }
        if (_publishStamps.TryGetValue(key, out var newest) && newest > computeStamp)
        {
            FileLog.Write($"[RepositoryMonitor] publish dropped for {status.Path}: a newer compute (stamp {newest}) already ruled this key (this compute started at stamp {computeStamp})");
            return null;
        }
        _publishStamps[key] = computeStamp;
        var enriched = Enrich(status, _byPath.TryGetValue(key, out var prev) ? prev : null);
        _byPath[key] = enriched;
        return enriched;
    }

    private async Task<IReadOnlyList<LiveSessionRef>?> FetchLiveSessionsAsync(CancellationToken ct)
    {
        // The entry points already refused to start unwired (ruling R2-8); this guard keeps the
        // failure loud even if the provider were unset mid-flight.
        var provider = LiveSessionsProvider
            ?? throw new InvalidOperationException("RepositoryMonitor lost its LiveSessionsProvider mid-compute");
        return await provider(ct);
    }

    private IReadOnlyList<string> CurrentWorktreePaths()
    {
        lock (_gate)
            return _byPath.Values.SelectMany(s => s.Worktrees).Select(w => w.Path).ToList();
    }

    private async Task DrainDeferredRecomputesAsync()
    {
        List<DeferredRecompute> deferred;
        lock (_gate)
        {
            if (_deferredRecomputes.Count == 0)
                return;
            deferred = _deferredRecomputes.Values.ToList();
            _deferredRecomputes.Clear();
        }
        FileLog.Write($"[RepositoryMonitor] running {deferred.Count} recompute(s) deferred during the scan");
        foreach (var request in deferred)
        {
            // The request runs under ITS OWN requester's token (ruling R2-5) - never the
            // scan's. A requester that already gave up is skipped, not run on its behalf.
            if (request.Token.IsCancellationRequested)
            {
                FileLog.Write($"[RepositoryMonitor] deferred recompute skipped - its requester cancelled: {request.Path}");
                continue;
            }
            try
            {
                await RecomputeOneAsync(request.Path, request.Token);
            }
            catch (OperationCanceledException)
            {
                FileLog.Write($"[RepositoryMonitor] deferred recompute cancelled by its requester: {request.Path}");
            }
            catch (Exception ex)
            {
                // A deferred failure is an ERROR - the requester cannot observe it, so the log
                // is the only place it can surface. Never absorbed into a success path.
                FileLog.Write($"[RepositoryMonitor] ERROR: deferred recompute failed for {request.Path}: {ex}");
            }
        }
    }

    /// <summary>
    /// Model-level enrichment applied on every upsert: a freshly computed status is never
    /// provisional, and dirty-since is carried forward from the previous entry (or stamped now when
    /// the tree just turned dirty), so "uncommitted work sitting for N days" survives rescans and -
    /// via the cache - restarts.
    /// </summary>
    internal static RepositoryStatus Enrich(RepositoryStatus fresh, RepositoryStatus? previous)
    {
        DateTime? dirtySince = null;
        if (!fresh.IsClean)
            dirtySince = previous is { IsClean: false, DirtySinceUtc: not null }
                ? previous.DirtySinceUtc
                : DateTime.UtcNow;

        return fresh with { Provisional = false, DirtySinceUtc = dirtySince };
    }

    private static IReadOnlyList<string> DefaultEnumerate(IEnumerable<string> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            foreach (var (_, path) in RemoteRepoProvider.ScanLocalRepos(root))
                if (seen.Add(WorktreeReaperService.NormalizePath(path)))
                    result.Add(path);
        }
        return result;
    }

    /// <summary>
    /// The merge-signal cache every default compute of this monitor shares (plan step 7d). It belongs to
    /// the monitor, not the process, because the monitor is what knows when a repository leaves: every
    /// removal forgets that repository's entries, and every completed scan sweeps the cache down to the
    /// repositories still in the model. Each compute builds its own status service, so a cache per
    /// service would never be read twice.
    /// </summary>
    internal WorktreeMergeSignalCache SignalCache { get; }

    private static async Task<RepositoryStatus> DefaultCompute(string path, IReadOnlyList<LiveSessionRef>? sessions, WorktreeMergeSignalCache signalCache, CancellationToken ct)
        => await new RepositoryStatusService(worktrees: new WorktreeInventoryService(signalCache: signalCache))
            .GetStatusAsync(path, sessions, fetchPrune: false, ct);

    /// <summary>A path is a repository when it exists and holds a .git directory (a primary
    /// checkout) or a .git file (a linked worktree).</summary>
    private static bool DefaultIsRepository(string path)
        => Directory.Exists(path)
           && (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")));

    /// <summary>
    /// The primary checkout owning a linked worktree: git names the shared .git directory via
    /// rev-parse --git-common-dir, and <see cref="LinkedWorktree.PrimaryWorkingTreeOf"/> says which
    /// working tree that directory belongs to. Null when git cannot answer (not a repository), and null
    /// for a bare repository, which has no primary checkout.
    ///
    /// The last step is shared with <see cref="LinkedWorktree.ParentRepositoryOf"/>, which the
    /// one-repository-list mission uses on the session-creation path. The two reach the same question
    /// from opposite sides - this one asks git, because it is on a background scan where a process costs
    /// nothing and it wants git's own resolved spelling of the path; that one reads the worktree's .git
    /// file, because a person is waiting and a machine with no git installed is supported. Which
    /// repository a git directory belongs to is the same answer either way and is written once.
    /// </summary>
    private static async Task<string?> DefaultResolvePrimary(string path, CancellationToken ct)
    {
        var result = await new GitCommandRunner().RunAsync(
            path, new[] { "rev-parse", "--path-format=absolute", "--git-common-dir" }, ct);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            return null;
        return LinkedWorktree.PrimaryWorkingTreeOf(result.Output);
    }
}
