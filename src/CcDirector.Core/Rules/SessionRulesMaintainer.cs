using System.Collections.Concurrent;
using CcDirector.Core.Agents;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Rules;

/// <summary>
/// Session Rules part 2, phase 1: keeps every live Claude Code session's rules file (wire contract W2)
/// CURRENT for as long as the session runs - the same life as the preamble file
/// (<see cref="SessionPreambleMaintainer"/>), for the same reason. Rules are created and changed while
/// sessions run; a file written once at launch would hide every rule made after it, and nothing would
/// look broken.
///
/// What it does, and when:
///   - After every Director store refresh, <see cref="RefreshAllAsync"/> asks the Gateway (W1) for the
///     hook rules that reach this machine and each live session's repo, and rewrites each file whose
///     rules changed. One question per distinct repo: two worktrees of one repo share an answer.
///   - When a session is created (and, at start, for every session already on the roster), the same
///     refresh runs for that session alone, so it does not wait a whole interval for its first file.
///   - When a session is removed, its file goes.
///
/// A session's repo identity is read from its checkout's origin remote (<see cref="SessionRepoIdentity"/>).
/// A real repo is read once and kept; "no repo" is asked again on every refresh, because a folder can
/// gain an origin while its session runs.
///
/// WHAT IS NEVER WRITTEN: a file the Director cannot vouch for. When git could not be asked, or the
/// Gateway has never answered for the session's repo, there is NO file - and the hook's evaluation log
/// says <c>no_rules_file</c>, which is the truth. An empty rules list would say "no rule reaches this
/// session", which nobody knows. Once the Gateway has answered, a later failed question keeps the last
/// answer, exactly as the other Gateway-owned stores keep their last-known cache.
/// </summary>
public sealed class SessionRulesMaintainer : IDisposable
{
    private readonly SessionManager _sessions;
    private readonly Func<ISessionRuleGateway?> _gateway;
    private readonly GitCommandRunner _git;
    private readonly string? _directory;
    private readonly string _machine;
    private readonly Func<DateTime> _utcNow;

    // One refresh at a time: the store-refresh timer and a session's creation can both start one.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    // Guards "is this session still live?" plus the write, against the removal's delete - so a refresh
    // that was asking the Gateway when the session went can never write its file back afterwards.
    private readonly object _fileGate = new();

    private readonly ConcurrentDictionary<Guid, string> _identities = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<HookRuleEntry>> _rulesByRepo = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Task> _createRefreshes = new();
    private bool _started;
    private bool _disposed;

    /// <param name="sessions">The session roster this maintainer follows.</param>
    /// <param name="gateway">
    /// Reads the Director's CURRENT Gateway client on every refresh - a settings change replaces it - or
    /// null when no Gateway is configured, in which case nothing is asked and nothing is written.
    /// </param>
    /// <param name="git">Tests pass a git that starts no process; production runs the machine's git.</param>
    /// <param name="directory">Tests pin the output directory; production uses the storage root.</param>
    /// <param name="machine">Tests pin the machine name; production uses this machine's (ruling P5).</param>
    /// <param name="utcNow">Tests move the clock by hand; production reads the real one.</param>
    public SessionRulesMaintainer(
        SessionManager sessions, Func<ISessionRuleGateway?> gateway, GitCommandRunner? git = null,
        string? directory = null, string? machine = null, Func<DateTime>? utcNow = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _git = git ?? new GitCommandRunner();
        _directory = directory;
        _machine = string.IsNullOrWhiteSpace(machine) ? Environment.MachineName : machine;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Begin following the roster, and start a refresh for every session already on it. Idempotent.
    /// Sessions restored at Director start were never created by this process, so nothing else would
    /// ever write theirs.
    /// </summary>
    public void Start()
    {
        lock (_fileGate)
        {
            if (_started) return;
            _started = true;
        }

        _sessions.OnSessionCreated += OnCreated;
        _sessions.OnSessionRemoved += OnRemoved;

        foreach (var session in _sessions.ListSessions())
            OnCreated(session);

        FileLog.Write($"[SessionRulesMaintainer] following the session roster (machine={_machine})");
    }

    /// <summary>
    /// Ask the Gateway for every live Claude Code session's rules and rewrite each file whose rules
    /// changed. Called at the end of the Director's store refresh.
    /// </summary>
    /// <returns>How many files were written.</returns>
    public Task<int> RefreshAllAsync(CancellationToken ct = default)
        => RefreshAsync(_sessions.ListSessions().Where(IsPoliced).ToList(), ct);

    /// <summary>Test seam: the refresh a session's creation started, so a test awaits it rather than
    /// waiting on the clock.</summary>
    internal Task WhenCreateRefreshDone(Guid sessionId)
        => _createRefreshes.TryGetValue(sessionId, out var refresh) ? refresh : Task.CompletedTask;

    /// <summary>Phase 1 polices Claude Code sessions alone: theirs is the only before-a-command hook (P9).</summary>
    private static bool IsPoliced(Session session) => session.AgentKind == AgentKind.ClaudeCode;

    private async Task<int> RefreshAsync(IReadOnlyList<Session> sessions, CancellationToken ct)
    {
        if (_disposed || sessions.Count == 0)
            return 0;
        var gateway = _gateway();
        if (gateway is null)
            return 0;

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. Each session's repo. A real repo is read once and kept. The EMPTY identity is never kept:
            //    a folder can gain an origin while the session runs (a clone inside it, "git remote add", a
            //    repaired worktree), so it is asked again on every refresh. A session git could not answer
            //    for is left out of this refresh and asked again on the next one.
            var known = new List<(Session Session, string Repo)>();
            foreach (var session in sessions)
            {
                if (!_identities.TryGetValue(session.Id, out var repo))
                {
                    var resolved = await SessionRepoIdentity.ResolveAsync(_git, session.WorkingDirectory, ct).ConfigureAwait(false);
                    if (resolved.Repo is null)
                    {
                        FileLog.Write($"[SessionRulesMaintainer] no repo identity for session {session.Id} yet, so no rules file: {resolved.Failure}");
                        continue;
                    }
                    repo = resolved.Repo;
                    if (repo.Length > 0)
                        _identities[session.Id] = repo;
                }
                known.Add((session, repo));
            }

            // 2. One question per distinct repo.
            foreach (var repo in known.Select(k => k.Repo).Distinct(StringComparer.Ordinal))
            {
                var fetch = await gateway.FetchEffectiveHookRulesAsync(_machine, repo, ct).ConfigureAwait(false);
                if (fetch.Rules is not null)
                    _rulesByRepo[repo] = fetch.Rules;
                else
                    FileLog.Write($"[SessionRulesMaintainer] effective rules for machine={_machine} repo='{repo}' could not be read " +
                                  $"({fetch.Failure}); {(_rulesByRepo.ContainsKey(repo) ? "keeping the last answer" : "no file until the Gateway answers")}");
            }

            // 3. Write what the Gateway has answered.
            var written = 0;
            foreach (var (session, repo) in known)
            {
                if (_rulesByRepo.TryGetValue(repo, out var rules) && Write(session.Id, repo, rules))
                    written++;
            }
            if (written > 0)
                FileLog.Write($"[SessionRulesMaintainer] RefreshAsync: {written} session rules file(s) changed");
            return written;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private bool Write(Guid sessionId, string repo, IReadOnlyList<HookRuleEntry> rules)
    {
        lock (_fileGate)
        {
            if (_disposed || _sessions.GetSession(sessionId) is null)
                return false;
            var path = SessionHookFiles.RulesPathFor(sessionId, _directory);
            var written = SessionRulesFile.WriteIfChanged(path, sessionId, _machine, repo, rules, _utcNow());
            if (written)
                FileLog.Write($"[SessionRulesMaintainer] wrote {path} (repo='{repo}', {rules.Count} rule(s))");
            return written;
        }
    }

    private void OnCreated(Session session)
    {
        if (!IsPoliced(session))
            return;
        _createRefreshes[session.Id] = RefreshCreatedAsync(session);
    }

    /// <summary>The created session's first refresh, started from a roster event: nothing awaits it,
    /// so this is the boundary that catches and says what went wrong.</summary>
    private async Task RefreshCreatedAsync(Session session)
    {
        try
        {
            await RefreshAsync(new[] { session }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionRulesMaintainer] first rules file for session {session.Id} FAILED (the next refresh tries again): {ex.Message}");
        }
    }

    private void OnRemoved(Session session)
    {
        _identities.TryRemove(session.Id, out _);
        _createRefreshes.TryRemove(session.Id, out _);
        lock (_fileGate)
        {
            try
            {
                SessionRulesFile.DeleteFor(session.Id, _directory);
            }
            catch (Exception ex)
            {
                // A roster event handler: an escaping exception would reach the SessionManager's removal.
                FileLog.Write($"[SessionRulesMaintainer] could not delete the rules file of session {session.Id} FAILED: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_fileGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _sessions.OnSessionCreated -= OnCreated;
        _sessions.OnSessionRemoved -= OnRemoved;
    }
}
