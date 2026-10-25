using System.Collections.Concurrent;
using CcDirector.Core.Drivers;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Keeps the session facts that live ONLY in the tool's own records fresh (issue #1637): the model the
/// agent is currently using (<see cref="Session.CurrentModel"/>) and its cumulative token spend
/// (<see cref="Session.TokenTotals"/>). Every time a session's turn ends (the detector flips it to
/// <see cref="ActivityState.WaitingForInput"/> - the same trigger <see cref="Storage.TurnReviewLogger"/>
/// uses), it reads both from the driver and stamps them on the session. The stamped values ride the
/// existing snapshot/delta path to the Gateway on <see cref="Gateway.Contracts.SessionDto.CurrentModel"/>
/// and <see cref="Gateway.Contracts.SessionDto.TokenTotals"/>, where the statistics fold consumes them.
///
/// The model and the tokens are the SAME QUESTION asked of the same records at the same moment - "what did
/// this turn just do" - so they are refreshed together, in one handler. The model that produced a turn and
/// the tokens it cost belong to each other; reading them at one turn-end keeps them consistent and keeps a
/// second, separate turn-end walk from existing.
///
/// Turn-end-driven ON PURPOSE, not read in the DTO mapper: these reads walk the agent's on-disk records
/// (transcript walk, rollout scan, SQLite query), and the mapper runs on every roster snapshot - exactly
/// the per-poll O(history) work the Gateway statistics mission calls out as the anti-pattern. A turn-end
/// read runs once per completed turn, off the detector's callback thread. It also stamps once at wire-up,
/// which answers immediately for resumed/restored sessions whose records already exist; a genuinely fresh
/// session honestly reports null for both until its first turn-end (records-only - see
/// <see cref="RefreshModel"/> for why the launch alias is never used).
///
/// The two facts are gated INDEPENDENTLY on their own capabilities: a driver is asked for the model only
/// if it declares <see cref="DriverCapabilities.ModelReport"/>, and for tokens only if it declares
/// <see cref="DriverCapabilities.TokenUsage"/>. Neither is ever asked with a NotSupportedException as
/// control flow - several drivers implement <see cref="IAgentDriver.ReadUsage"/> as a throw, so an ungated
/// token read would raise at every turn-end. A session whose driver declares neither is never wired at
/// all; both facts honestly stay null. One instance per Director.
/// </summary>
public sealed class SessionRecordsWatcher : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _handlers = new();
    private bool _started;
    private int _disposed;

    public SessionRecordsWatcher(SessionManager sessionManager) => _sessionManager = sessionManager;

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[SessionRecordsWatcher] Start");
        _sessionManager.OnSessionCreated += WireSession;
        _sessionManager.OnSessionRemoved += UnwireSession;
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s);
    }

    private void WireSession(Session session)
    {
        // Wired if the driver can answer EITHER question; each read then gates on its own capability
        // inside RefreshFromRecords. A driver that can report neither is not watched at all.
        var caps = session.Driver.Capabilities;
        if (!caps.HasFlag(DriverCapabilities.ModelReport) && !caps.HasFlag(DriverCapabilities.TokenUsage))
            return;
        if (_handlers.ContainsKey(session.Id)) return;

        Action<ActivityState, ActivityState> handler = (oldState, newState) =>
        {
            _ = oldState; // unused: we only care about the state we transitioned INTO
            // The single trigger: the turn just ended. The agent's records now carry the model that
            // produced it and the tokens it cost.
            if (!ActivityStateFacts.IsTurnEnd(newState)) return;
            Task.Run(() => RefreshFromRecords(session));
        };
        _handlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;

        // Initial stamp for sessions that already HAVE records (a resume, a restore): their concrete
        // model and spend-to-date are known immediately. A genuinely fresh session stamps nothing here -
        // records-only, see RefreshModel - and reports both at its first turn-end.
        _ = Task.Run(() => RefreshFromRecords(session));
    }

    /// <summary>
    /// Refresh both records-only facts at a turn-end, each gated on its own capability so a driver that
    /// declares one and not the other is asked only what it can answer. A boundary (fire-and-forget
    /// target): each read owns its try/catch so a records-read fault never escapes onto a background
    /// thread and one fact's failure never suppresses the other.
    /// </summary>
    internal static void RefreshFromRecords(Session session)
    {
        if (session.Driver.Capabilities.HasFlag(DriverCapabilities.ModelReport))
            RefreshModel(session);
        if (session.Driver.Capabilities.HasFlag(DriverCapabilities.TokenUsage))
            RefreshTokens(session);
    }

    private void UnwireSession(Session session)
    {
        if (_handlers.TryRemove(session.Id, out var h))
            session.OnActivityStateChanged -= h;
    }

    /// <summary>
    /// Ask the driver for the current model and stamp it. A boundary (fire-and-forget target): it
    /// owns its try/catch so a records-read fault never escapes onto a background thread. A null
    /// answer is a missed read and stamps nothing (<see cref="Session.SetCurrentModel"/> ignores it).
    ///
    /// RECORDS-ONLY, DELIBERATELY: launch args are NOT passed, so ClaudeDriver's pre-first-turn
    /// launch-flag fallback never fires here. The launch value is an ALIAS (<c>opus[1m]</c>), and the
    /// transcript later records the concrete id (<c>claude-opus-4-8</c>, no suffix) - two names for
    /// one model that no equality function maps together, so a statistics fold would split one
    /// session across two "models". Worse, the alias window is not hypothetical: the input-stats
    /// bucket increments at turn SUBMISSION (Session.SendTextAsync -> InputStats.RecordTurn), so a
    /// roster poll between submission and the turn-end stamp would pair the alias with a non-zero
    /// delta and attribute a real turn to a model name that never produced anything (found by the
    /// gateway-sqlite Architect's review of #1651). Records-only closes that entirely: CurrentModel
    /// is null until the tool has RECORDED a model, and a mid-session model switch attributes with a
    /// one-turn lag (the switch turn's rows land on the model that produced the previous turn-end
    /// stamp) - honest-null and one-turn-late beat plausibly-wrong. The driver verb keeps its
    /// launchArgs parameter for callers whose question really is "what was it told to use".
    /// </summary>
    internal static void RefreshModel(Session session)
    {
        try
        {
            var model = session.Driver.ReadCurrentModel(session.ClaudeSessionId ?? "", session.RepoPath, launchArgs: null);
            session.SetCurrentModel(model);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionRecordsWatcher] refresh model FAILED: session={session.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Ask the driver for the session's cumulative token spend and stamp it. A boundary (fire-and-forget
    /// target): it owns its try/catch so a records-read fault never escapes onto a background thread. A
    /// null answer is a missed read and stamps nothing (<see cref="Session.SetTokenTotals"/> ignores it).
    ///
    /// Reads the full <see cref="Gateway.Contracts.SessionUsageDto"/> and keeps only the running totals -
    /// the per-turn breakdown it also carries is for the on-demand usage view and is far too heavy to ride
    /// the roster snapshot for every session on every poll. ContextTokens is carried as the live gauge; it
    /// is the one figure here that is NOT summable spend.
    /// </summary>
    internal static void RefreshTokens(Session session)
    {
        try
        {
            var usage = session.Driver.ReadUsage(session.ClaudeSessionId ?? "", session.RepoPath);
            if (usage is null) return;
            session.SetTokenTotals(new Gateway.Contracts.TokenTotalsDto
            {
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                CacheReadTokens = usage.CacheReadTokens,
                CacheCreationTokens = usage.CacheCreationTokens,
                ContextTokens = usage.ContextTokens,
                AsOfUtc = usage.LastMessageUtc,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionRecordsWatcher] refresh tokens FAILED: session={session.Id}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessionManager.OnSessionCreated -= WireSession;
        _sessionManager.OnSessionRemoved -= UnwireSession;
        foreach (var s in _sessionManager.ListSessions())
            if (_handlers.TryRemove(s.Id, out var h))
                s.OnActivityStateChanged -= h;
    }
}
