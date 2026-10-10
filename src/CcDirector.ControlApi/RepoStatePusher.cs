using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Ships this Director's repo-state snapshots to the Gateway on a schedule (issue #2118): once shortly
/// after startup, then every <see cref="CycleHours"/> hours. The Gateway keeps the newest snapshot per
/// repository, and the morning report reads it for the stale-worktree and unmerged-branch rows - the
/// flagship content of the daily email, and the one part of it that no Gateway-side store can supply.
///
/// FAIL-SAFE IS THE WHOLE DESIGN, and it is stronger than "wrapped in a try/catch". This is a background
/// feed for an email; it must never be able to disturb the sessions a person is working in. So:
///   - the collection and the push both run off the Director's threads on a timer, never on a UI or session
///     path;
///   - every cycle is wrapped, and a failure is LOGGED and dropped - the next cycle simply tries again;
///   - there is NO outbox and no retry queue, deliberately. This pushes the CURRENT state of the
///     repositories, so a lost push is not lost data: the next cycle's snapshot supersedes it entirely.
///     An outbox here would replay a stale picture of the repositories on top of a fresher one.
///
/// It is not started at all when the Director has no repository registry - there is nothing to report - and
/// its push delegate is a no-op when no Gateway is configured, so an install with no Gateway does no work.
/// </summary>
public sealed class RepoStatePusher : IDisposable
{
    /// <summary>How long after start the first snapshot goes out. Long enough to be behind the Director's
    /// own startup work (including the repository rescan) rather than competing with it.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>The cycle: every six hours, per the issue. Repository hygiene changes over days.</summary>
    public const int CycleHours = 6;

    private readonly Func<RepoStatePushRequest, CancellationToken, Task<RepoStatePushResponse?>> _push;
    private readonly RepositoryRegistry _repositories;
    private readonly RepoStateSnapshotCollector _collector;
    private readonly string _directorId;
    private readonly string _machineName;
    private readonly Func<IReadOnlyList<LiveSessionRef>>? _liveSessions;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _cycle;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _disposed;

    /// <param name="push">The push itself, as a delegate rather than a Gateway client. Late-bound (the
    /// uploader pattern - the client is created after this and can be replaced when the Gateway configuration
    /// changes), and it is the seam a test drives, so the fail-safe promise below is PROVEN rather than
    /// asserted in a comment. Returning null means the push did not land.</param>
    /// <param name="liveSessions">This machine's live sessions, so a worktree someone is working in is
    /// reported as occupied rather than as abandoned. Null means none are known.</param>
    public RepoStatePusher(
        Func<RepoStatePushRequest, CancellationToken, Task<RepoStatePushResponse?>> push,
        RepositoryRegistry repositories,
        string directorId,
        string machineName,
        Func<IReadOnlyList<LiveSessionRef>>? liveSessions = null,
        RepoStateSnapshotCollector? collector = null,
        TimeSpan? startupDelay = null,
        TimeSpan? cycle = null)
    {
        _push = push ?? throw new ArgumentNullException(nameof(push));
        _repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
        _directorId = directorId ?? "";
        _machineName = machineName ?? "";
        _liveSessions = liveSessions;
        _collector = collector ?? new RepoStateSnapshotCollector();
        _startupDelay = startupDelay ?? StartupDelay;
        _cycle = cycle ?? TimeSpan.FromHours(CycleHours);
    }

    /// <summary>Start the background loop. Idempotent.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        FileLog.Write($"[RepoStatePusher] Start: first push in {_startupDelay.TotalMinutes:0} minutes, then every {_cycle.TotalHours:0} hours");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_startupDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await PushOnceAsync(ct).ConfigureAwait(false);
                await Task.Delay(_cycle, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception ex)
        {
            // The loop itself dying is the one failure a per-cycle catch cannot cover, and it would silently
            // end the feed for the life of the process - so it is logged as loudly as anything here gets.
            FileLog.Write($"[RepoStatePusher] the push loop ENDED unexpectedly; no further repo state will be reported until this Director restarts FAILED: {ex}");
        }
    }

    /// <summary>
    /// Collect and push once. Internal (not private) so a test can drive one cycle deterministically instead
    /// of waiting on the timer. Returns the number of repositories the Gateway acknowledged, or null when
    /// the cycle did not land.
    /// </summary>
    internal async Task<int?> PushOnceAsync(CancellationToken ct = default)
    {
        try
        {
            var repositories = _repositories.Repositories;
            if (repositories.Count == 0)
            {
                FileLog.Write("[RepoStatePusher] PushOnce: no registered repositories, nothing to report");
                return 0;
            }

            var snapshots = await _collector
                .CollectAsync(repositories, _liveSessions?.Invoke(), ct)
                .ConfigureAwait(false);

            if (snapshots.Count == 0)
            {
                // Every repository failed to collect. Pushing an EMPTY batch would be harmless (the Gateway
                // overwrites nothing), but saying so in the log matters: silence here would look identical
                // to a healthy machine with no repositories.
                FileLog.Write("[RepoStatePusher] PushOnce: collected 0 snapshots from " +
                              $"{repositories.Count} registered repositories - nothing pushed this cycle");
                return 0;
            }

            var response = await _push(new RepoStatePushRequest
            {
                DirectorId = _directorId,
                MachineName = _machineName,
                Repositories = snapshots,
            }, ct).ConfigureAwait(false);

            if (response is null)
            {
                FileLog.Write("[RepoStatePusher] PushOnce: the Gateway did not accept the push; retrying next cycle");
                return null;
            }

            FileLog.Write($"[RepoStatePusher] PushOnce: the Gateway stored {response.Stored} repository snapshots");
            return response.Stored;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepoStatePusher] PushOnce FAILED, retrying next cycle: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts?.Cancel(); } catch { /* best effort */ }
        _cts?.Dispose();
        FileLog.Write("[RepoStatePusher] Dispose: stopped");
    }
}
