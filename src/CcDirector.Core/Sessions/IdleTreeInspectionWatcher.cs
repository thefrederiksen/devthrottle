using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// When a session parks at the end of a turn, inspect the folder it works in and report that folder's
/// repository to the Gateway (the owner's decision, 5 November 2026).
///
/// WHY THIS EXISTS, AND IT IS NOT ABOUT COST. The Fleet Manager offers the owner <em>Close</em> on a
/// stopped session only when the Gateway's report of that session's repository was collected AFTER the
/// session last did anything - otherwise it cannot tell whether the work is pushed and merged, and it
/// refuses rather than guess (<c>FleetManagerCloseRule</c>, which says so in those words). That report
/// arrived on one schedule only: once about two minutes after the Director starts, then every six hours.
/// So from the moment a session stops until the next six-hourly report, Close was refused for it -
/// however clean and merged its work was. The number nobody looks at was refreshed 288 times a day while
/// the one number a decision depends on was refreshed four.
///
/// This makes the rule true by construction rather than by luck: the inspection is triggered BY the last
/// activity, so it is always after it.
///
/// THE TRIGGER IS A TURN END, AND THERE IS NO TIMER. It is the same trigger
/// <see cref="PendingInteractionWatcher"/> and <see cref="Storage.ConversationIngestor"/> use - the
/// moment a session has stopped and is waiting - so this class adds no polling of its own and does
/// nothing at all on a machine whose sessions are working.
///
/// IT ASKS FOR A REPOSITORY, NOT A FOLDER. A session usually works in a linked worktree - a pool slot
/// beside the repository, or a task worktree in a sibling folder - and the report the Close rule reads is
/// the REPOSITORY's, whose worktree list carries that folder. So the folder is resolved to the repository
/// that covers it before anything else happens, and a folder no registered repository covers asks for
/// nothing: there would be no row to refresh (outside review of pull request 4595, which found that
/// asking by folder helped only the sessions sitting in a repository root).
///
/// IT IS THROTTLED PER REPOSITORY, because a turn end is not rare and an inspection is a whole branch
/// and worktree inventory. Several sessions in one repository's worktrees parking together, or one
/// session parking repeatedly while its owner answers it, cost ONE inspection per
/// <see cref="MinimumGap"/>, not one each.
///
/// AN ASK INSIDE THE GAP IS DEFERRED, NOT DROPPED. Dropping it would leave that session with a report
/// older than its own last activity - exactly the refusal this class exists to end - until some later
/// park happened to fall outside the gap. So one trailing inspection is queued for the end of the gap,
/// and any number of asks inside the gap collapse into that single one.
///
/// It never throws onto the event thread, never blocks it, and swallows every fault: a failed inspection
/// leaves the Gateway holding the previous report, which is exactly the state this class improves and
/// never worsens.
/// </summary>
public sealed class IdleTreeInspectionWatcher : IDisposable
{
    /// <summary>The shortest gap between two inspections of the SAME folder asked for by a turn end.</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(2);

    private readonly SessionManager _sessionManager;
    private readonly Func<string, string?> _coveringRepository;
    private readonly Func<string, CancellationToken, Task> _inspect;
    private readonly Action<Action> _dispatch;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly TimeSpan _gap;

    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _handlers = new();
    private readonly Dictionary<string, DateTime> _lastInspected = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trailing = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private bool _started;
    private int _disposed;

    /// <param name="coveringRepository">The registered repository a folder is, or sits inside - null when
    /// none does. A delegate rather than a service: this class lives in the core and the registry and the
    /// reporting live in the Director's host.</param>
    /// <param name="inspect">What an inspection IS, as a delegate for the same reason, and it is the seam
    /// a test drives, so the trigger and the throttle are proven rather than described.</param>
    public IdleTreeInspectionWatcher(
        SessionManager sessionManager,
        Func<string, string?> coveringRepository,
        Func<string, CancellationToken, Task> inspect)
        : this(sessionManager, coveringRepository, inspect,
               work => Task.Run(work), () => DateTime.UtcNow, Task.Delay, MinimumGap)
    {
    }

    internal IdleTreeInspectionWatcher(
        SessionManager sessionManager, Func<string, string?> coveringRepository,
        Func<string, CancellationToken, Task> inspect,
        Action<Action> dispatch, Func<DateTime> utcNow, Func<TimeSpan, Task> delay, TimeSpan gap)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(coveringRepository);
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(delay);
        _sessionManager = sessionManager;
        _coveringRepository = coveringRepository;
        _inspect = inspect;
        _dispatch = dispatch;
        _utcNow = utcNow;
        _delay = delay;
        _gap = gap;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write($"[IdleTreeInspectionWatcher] Start: one inspection per repository at a turn end, at most one every {_gap.TotalMinutes:0} minutes");
        _sessionManager.OnSessionCreated += WireSession;
        _sessionManager.OnSessionRemoved += UnwireSession;
        foreach (var session in _sessionManager.ListSessions())
            WireSession(session);
    }

    /// <summary>
    /// Watch one session's turn ends. Internal rather than private so a test can drive the REAL handler
    /// through a REAL activity-state flip, instead of calling the ask directly and proving nothing about
    /// the trigger.
    /// </summary>
    internal void WireSession(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_handlers.ContainsKey(session.Id)) return;

        Action<ActivityState, ActivityState> handler = (oldState, newState) =>
        {
            _ = oldState; // only the state we moved INTO matters here
            if (!ActivityStateFacts.IsTurnEnd(newState)) return;
            // The PRIMARY repository when this session works in a linked worktree: that is the report
            // the Close rule reads for it, because its worktree list carries this folder.
            Ask(session.PrimaryRepoPath ?? session.RepoPath);
        };
        _handlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;
    }

    private void UnwireSession(Session session)
    {
        if (_handlers.TryRemove(session.Id, out var handler))
            session.OnActivityStateChanged -= handler;
    }

    /// <summary>
    /// Ask for the inspection of whichever repository covers <paramref name="folder"/>. Inside the gap
    /// the ask is not dropped: one trailing inspection is queued for the end of the gap, and further
    /// asks collapse into it. Internal so a test can prove the throttle without driving a session.
    /// </summary>
    internal void Ask(string? folder)
    {
        if (_disposed != 0) return;
        if (string.IsNullOrWhiteSpace(folder))
            return; // a session with no folder has no repository to inspect

        var repository = _coveringRepository(folder);
        if (string.IsNullOrWhiteSpace(repository))
        {
            // Nothing the Gateway holds a row for, so there is nothing to refresh. A folder outside every
            // registered repository is not inspected rather than inspected into nowhere.
            FileLog.Write($"[IdleTreeInspectionWatcher] a session parked in {folder}, which no registered repository covers; nothing to inspect");
            return;
        }

        TimeSpan wait;
        lock (_gate)
        {
            if (!_lastInspected.TryGetValue(repository, out var last))
            {
                _lastInspected[repository] = _utcNow();
                wait = TimeSpan.Zero;
            }
            else
            {
                var since = _utcNow() - last;
                if (since >= _gap)
                {
                    _lastInspected[repository] = _utcNow();
                    wait = TimeSpan.Zero;
                }
                else
                {
                    // Inside the gap. One trailing inspection covers every ask that arrives in it.
                    if (!_trailing.Add(repository))
                        return;
                    wait = _gap - since;
                }
            }
        }

        if (wait == TimeSpan.Zero)
            FileLog.Write($"[IdleTreeInspectionWatcher] a session parked in {folder}; inspecting {repository}");
        else
            FileLog.Write($"[IdleTreeInspectionWatcher] a session parked in {folder}; {repository} was inspected {(_gap - wait).TotalSeconds:0}s ago, so one more inspection follows in {wait.TotalSeconds:0}s");

        // Off the event thread: an inspection runs git and talks to the Gateway, and neither may stall
        // the detector or the interface.
        _dispatch(() => _ = InspectAsync(repository, wait));
    }

    /// <summary>
    /// A fire-and-forget boundary, so it owns its try/catch: a fault here must never escape onto a
    /// background thread, and the worst outcome of one is the report the Gateway already had.
    /// </summary>
    private async Task InspectAsync(string repository, TimeSpan wait)
    {
        try
        {
            if (wait > TimeSpan.Zero)
            {
                await _delay(wait).ConfigureAwait(false);
                if (_disposed != 0) return;
                lock (_gate)
                {
                    _trailing.Remove(repository);
                    _lastInspected[repository] = _utcNow();
                }
            }

            await _inspect(repository, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate) { _trailing.Remove(repository); }
            FileLog.Write($"[IdleTreeInspectionWatcher] inspecting {repository} FAILED: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessionManager.OnSessionCreated -= WireSession;
        _sessionManager.OnSessionRemoved -= UnwireSession;
        foreach (var session in _sessionManager.ListSessions())
            UnwireSession(session);
        FileLog.Write("[IdleTreeInspectionWatcher] Dispose: stopped");
    }
}
