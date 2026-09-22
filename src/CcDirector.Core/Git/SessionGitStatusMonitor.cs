using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Keeps every live session's "how many files are changed in my working tree" count up to date
/// (<see cref="Session.UncommittedCount"/>), so the desktop rail, the Cockpit roster and the phone all read
/// ONE number that the Director produced instead of each surface polling git for itself.
///
/// This poll used to live in the desktop window (<c>MainWindow.RefreshSessionGitStatusAsync</c>), which
/// meant the number existed only where it was rendered: it never reached <c>SessionDto</c>, so the Gateway
/// was never told and the Cockpit roster had nothing to show. Moving it here puts it on the Session, which
/// is what the Control API maps onto the wire.
///
/// FAIL CLOSED, NEVER FAIL ZERO (issue 516). A git probe can fail - no git on the path, a permissions
/// problem, a repository mid-rebase - and the count is then UNKNOWN. This monitor publishes only a count a
/// probe actually produced; a failed probe leaves the previous value alone, and a session whose probe has
/// never succeeded keeps a null count. Reporting 0 would tell every reader downstream "this tree is clean",
/// which is the one thing we do not know.
/// </summary>
public sealed class SessionGitStatusMonitor : IDisposable
{
    /// <summary>The default poll interval - the same fifteen seconds the desktop rail used.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    /// <summary>How many repositories are probed at once. Matches the desktop's old limit.</summary>
    private const int MaxConcurrentProbes = 4;

    private readonly SessionManager _sessionManager;
    private readonly Func<string, CancellationToken, Task<GitCountResult>> _probe;
    private readonly TimeSpan _interval;
    private readonly Func<string, bool> _directoryExists;

    private readonly Background.BackgroundJobs _jobs;
    private Background.BackgroundJob? _job;

    /// <param name="sessionManager">The live session list to walk each cycle.</param>
    /// <param name="interval">Poll interval; null uses <see cref="DefaultInterval"/>. A test seam.</param>
    /// <param name="probe">The git probe; null uses a shared <see cref="GitStatusProvider"/>. Its ten-second
    /// cache is keyed by path and shared across instances, so several sessions in ONE working tree cost one
    /// <c>git status</c> call between them, not one each. A test seam.</param>
    /// <param name="directoryExists">Existence check for a session's repository path. A test seam; production
    /// passes null and gets <see cref="Directory.Exists"/>.</param>
    public SessionGitStatusMonitor(
        SessionManager sessionManager,
        TimeSpan? interval = null,
        Func<string, CancellationToken, Task<GitCountResult>>? probe = null,
        Func<string, bool>? directoryExists = null,
        Background.BackgroundJobs? jobs = null)
    {
        _jobs = jobs ?? Background.BackgroundJobs.Default;
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        var provider = new GitStatusProvider();
        _probe = probe ?? provider.GetCountAsync;
        _interval = interval ?? DefaultInterval;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    /// <summary>Start the probe: a slow-and-steady job on the scheduler (docs/BackgroundWork.md), at once
    /// and then every interval, never overlapping. Idempotent.</summary>
    public void Start()
    {
        if (_job is not null) return;
        _job = _jobs.Register(
            new Background.BackgroundJobSpec("Per-session uncommitted count", Background.BackgroundJobTier.SlowAndSteady, _interval, "the monitor is disposed"),
            async ct => { await RefreshOnceAsync(ct).ConfigureAwait(false); });
        _job.StartTimer(TimeSpan.Zero);
        FileLog.Write($"[SessionGitStatusMonitor] Start: probing every {_interval.TotalSeconds:0} seconds");
    }

    /// <summary>
    /// Probe every live session once and publish the counts. Internal (not private) so a test can drive one
    /// cycle deterministically instead of waiting on the timer. Returns how many sessions got a fresh count.
    /// </summary>
    internal async Task<int> RefreshOnceAsync(CancellationToken ct = default)
    {
        var sessions = _sessionManager.ListSessions();
        if (sessions.Count == 0) return 0;

        // ONE PROBE PER REPOSITORY, NOT ONE PER SESSION (issue #1111, item 2). Sessions cluster hard on
        // repositories - two dozen of them routinely sit on half a dozen trees - so probing per session ran
        // `git status` ten times over on one working tree to answer a question with one answer. Measured on
        // this repository's own harness against the distribution in the issue: 23 sessions, 6 repositories,
        // 23 probes. Grouped, that is 6.
        //
        // The grouping key is CANONICAL, not the raw RepoPath. The same directory is stored under several
        // spellings (D:/x and D:\x), so grouping on the raw string would have counted one tree as two and
        // quietly kept most of the duplication - the trap item 3 of the issue warns about.
        //
        // This is not the same saving as GitStatusProvider's ten-second cache, and does not rely on it. That
        // cache is keyed on the raw path so the two spellings never share an entry; it is populated on
        // completion, so probes issued together all miss together; and its ten seconds are shorter than this
        // fifteen-second poll, so it is cold at the top of every cycle. Deduplicating here is what actually
        // collapses the work, and it happens before a single process is spawned.
        var byRepo = sessions
            .Where(s => !string.IsNullOrWhiteSpace(s.RepoPath))
            .GroupBy(s => RepoPathKey.For(s.RepoPath))
            .ToList();

        var published = 0;
        using var gate = new SemaphoreSlim(MaxConcurrentProbes);

        var probes = byRepo.Select(async group =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Probe the path as one of these sessions actually spells it - git is handed a real path,
                // never the lowercased comparison key.
                var repoPath = group.First().RepoPath;

                // A session whose folder has been deleted or moved is not "clean" - it is unreadable. Leave
                // whatever the last successful probe said and move on.
                if (!_directoryExists(repoPath)) return;

                var count = await _probe(repoPath, ct).ConfigureAwait(false);
                if (!count.Success) return;   // unknown, not zero - see the class summary

                // Fan the one answer out to every session on this tree. They share a working directory, so
                // they share its uncommitted count by definition.
                foreach (var session in group)
                {
                    session.UncommittedCount = count.Count;
                    Interlocked.Increment(ref published);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown mid-probe; the loop's handler owns it.
            }
            catch (Exception ex)
            {
                // One unreadable repository must never stop the other repositories being probed.
                FileLog.Write($"[SessionGitStatusMonitor] probe FAILED for repository {group.Key}, "
                    + $"leaving the last known count in place for its {group.Count()} session(s): {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(probes).ConfigureAwait(false);
        FileLog.Write($"[SessionGitStatusMonitor] RefreshOnceAsync: published {published} of {sessions.Count} "
            + $"session counts from {byRepo.Count} repository probe(s)");
        return published;
    }

    public void Dispose()
    {
        _job?.Dispose();
        _job = null;
        FileLog.Write("[SessionGitStatusMonitor] Dispose: stopped");
    }
}
