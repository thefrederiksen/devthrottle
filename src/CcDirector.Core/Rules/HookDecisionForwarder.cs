using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Rules;

/// <summary>
/// Session Rules part 2, phase 1: picks up the decisions a session's before-a-command hook drops (wire
/// contract W3) and forwards them to the Gateway as firings (W5), with retry.
///
/// Built the way <see cref="SessionPointerWatcher"/> is, for the same reasons, read there:
///
/// THE DIRECTORY NAME IS THE CAPABILITY. A session's decisions live in <c>decisions/&lt;id&gt;.&lt;token&gt;/</c>,
/// where the token is the session's own pointer-drop token, handed to it in its environment and to
/// nobody else. A directory whose token does not match is never forwarded and is left in place as
/// evidence - a sibling process that can spell a session's id cannot put firings on its record. A
/// decision whose body names a different session than its directory is refused and deleted.
///
/// THE SWEEP IS THE DELIVERY GUARANTEE; THE WATCHER ONLY MAKES IT FAST. A file-system watcher was
/// measured missing about one notification in ten on the pointer drop, so a timer sweep reads the whole
/// box and the watcher only starts a sweep sooner. Both run the same <see cref="SweepAsync"/>.
///
/// A DROP IS DELETED ONLY WHEN ITS FATE IS KNOWN. Accepted, or called a duplicate (decisionId is unique
/// per tenant, so a retried post is never a second firing): deleted. Refused: logged with the Gateway's
/// reason, and deleted, because a refusal will never become an acceptance. The post failed, or the
/// answer did not account for every decision sent: LEFT for the next sweep. After a failure the next
/// attempt waits, doubling up to <see cref="MaxBackoff"/>, so a Gateway that is down is not asked on
/// every tick.
///
/// A session's decisions outlive the session: a session removed with decisions still waiting has them
/// forwarded by the next sweep, and its directory is removed once it is empty.
/// </summary>
public sealed class HookDecisionForwarder : IDisposable
{
    /// <summary>How often the box is swept. The steady-state cost is one enumeration of a folder of
    /// empty directories, because a forwarded drop is deleted.</summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between attempts while the Gateway keeps failing.</summary>
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>The most decisions one post carries.</summary>
    internal const int MaxBatch = 100;

    /// <summary>A decision is a few hundred bytes with a subject of at most 4000 characters; anything
    /// larger than this was not written by the hook.</summary>
    internal const int MaxDecisionBytes = 64 * 1024;

    private readonly SessionManager _sessions;
    private readonly Func<ISessionRuleGateway?> _gateway;
    private readonly string? _pointerDirectory;
    private readonly string _root;
    private readonly Background.BackgroundJobs _jobs;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _sweepGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, string> _removedTokens = new();
    private readonly ConcurrentDictionary<string, byte> _refusedDirectories = new(StringComparer.Ordinal);
    private int _consecutiveFailures;
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private Background.BackgroundJob? _sweepJob;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>
    /// Test seam: start WITHOUT the file-system watcher, so a test drives <see cref="SweepAsync"/> itself
    /// and no assertion waits on a notification.
    /// </summary>
    internal bool SuppressWatcherForTests { get; set; }

    /// <param name="sessions">The roster whose sessions' tokens prove a directory.</param>
    /// <param name="gateway">Reads the Director's CURRENT Gateway client on every sweep, or null when none
    /// is configured - then nothing is posted and every drop stays.</param>
    /// <param name="pointerDirectory">Tests pin the pointer drop box; production uses the storage root.</param>
    /// <param name="jobs">The scheduler the sweep runs on.</param>
    /// <param name="utcNow">Tests move the clock by hand; production reads the real one.</param>
    public HookDecisionForwarder(
        SessionManager sessions, Func<ISessionRuleGateway?> gateway, string? pointerDirectory = null,
        Background.BackgroundJobs? jobs = null, Func<DateTime>? utcNow = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _pointerDirectory = string.IsNullOrWhiteSpace(pointerDirectory) ? null : pointerDirectory;
        _root = SessionHookFiles.DecisionsRootFor(_pointerDirectory);
        _jobs = jobs ?? Background.BackgroundJobs.Default;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The folder holding every session's decision directory.</summary>
    public string Directory => _root;

    /// <summary>
    /// Create the box and start watching and sweeping. Unlike the pointer drop box, nothing is purged:
    /// a decision left by a previous Director is a real record, not stale state, and deleting it would
    /// destroy evidence. Directories nobody on this roster holds the token for are logged once and kept.
    /// </summary>
    public void Start()
    {
        System.IO.Directory.CreateDirectory(_root);
        _sessions.OnSessionRemoved += OnRemoved;

        if (!SuppressWatcherForTests)
        {
            _watcher = new FileSystemWatcher(_root, "*" + SessionHookFiles.DropExtension)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
            };
            _watcher.Created += OnDropped;
            _watcher.Renamed += OnDropped;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;
        }

        _sweepJob = _jobs.Register(
            new Background.BackgroundJobSpec("Session rule decision forwarding", Background.BackgroundJobTier.SlowAndSteady, SweepInterval, "the forwarder is disposed"),
            ct => SweepAsync(ct));
        _sweepJob.StartTimer();

        FileLog.Write($"[HookDecisionForwarder] watching {_root} for rule decisions (sweeping every {SweepInterval.TotalSeconds:0.#}s; " +
                      $"notifications {(SuppressWatcherForTests ? "SUPPRESSED for a test" : "on")})");
    }

    /// <summary>
    /// Forward every decision currently in the box. THIS is the delivery path: the timer calls it, the
    /// watcher only calls it sooner, and a test calls it directly.
    /// </summary>
    /// <returns>How many decisions the Gateway accepted or already had, and were deleted.</returns>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        if (_disposed || !System.IO.Directory.Exists(_root))
            return 0;
        var gateway = _gateway();
        if (gateway is null)
            return 0;
        if (_utcNow() < _nextAttemptUtc)
            return 0;

        // A sweep already running will forward what is there; the next tick takes anything it missed.
        if (!await _sweepGate.WaitAsync(0, ct).ConfigureAwait(false))
            return 0;
        try
        {
            var files = new List<(string Path, Guid SessionId)>();
            foreach (var directory in System.IO.Directory.EnumerateDirectories(_root))
            {
                if (!TryOwnedDirectory(directory, out var sessionId))
                    continue;
                foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*" + SessionHookFiles.DropExtension))
                    files.Add((path, sessionId));
            }

            var forwarded = 0;
            var batch = new List<(string Path, string DecisionId, JsonElement Decision)>(MaxBatch);
            foreach (var (path, sessionId) in files)
            {
                if (ReadDecision(path, sessionId) is not { } read)
                    continue;
                batch.Add((path, read.DecisionId, read.Decision));
                if (batch.Count < MaxBatch)
                    continue;
                var sent = await PostAsync(gateway, batch, ct).ConfigureAwait(false);
                batch.Clear();
                if (sent is null)
                    return forwarded;
                forwarded += sent.Value;
            }
            if (batch.Count > 0)
            {
                var sent = await PostAsync(gateway, batch, ct).ConfigureAwait(false);
                if (sent is null)
                    return forwarded;
                forwarded += sent.Value;
            }

            RemoveDrainedDirectories();
            return forwarded;
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    /// <summary>
    /// True when <paramref name="directory"/> is a session's own decision directory: named id dot token,
    /// for a session on the roster (or removed with decisions still waiting), with that session's exact
    /// token. A directory that fails is logged ONCE and left alone.
    /// </summary>
    private bool TryOwnedDirectory(string directory, out Guid sessionId)
    {
        // TryParseDropName reads a FILE name, so the extension it strips is supplied.
        if (!SessionHookFiles.TryParseDropName(Path.GetFileName(directory) + SessionHookFiles.DropExtension, out sessionId, out var token))
        {
            RefuseOnce(directory, "its name is not a session id plus drop token");
            return false;
        }

        var expected = _sessions.GetSession(sessionId)?.PointerDropToken
                       ?? (_removedTokens.TryGetValue(sessionId, out var removed) ? removed : null);
        if (expected is null)
        {
            RefuseOnce(directory, $"session {sessionId} is not on this Director's roster");
            return false;
        }
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(expected)))
        {
            RefuseOnce(directory, $"REFUSED: the drop token does not match session {sessionId} - written by something other than that session's own hook");
            return false;
        }
        return true;
    }

    private void RefuseOnce(string directory, string why)
    {
        if (_refusedDirectories.TryAdd(directory, 0))
            FileLog.Write($"[HookDecisionForwarder] not forwarding {directory}: {why}. It is left in place.");
    }

    /// <summary>
    /// Read one decision, or null when there is nothing to send. A file that can never be valid - too
    /// large, not JSON, not a version 1 decision, or naming a session other than its directory's - is
    /// logged and DELETED, so the sweep does not retry it forever.
    /// </summary>
    private (string DecisionId, JsonElement Decision)? ReadDecision(string path, Guid sessionId)
    {
        string text;
        try
        {
            if (new FileInfo(path).Length > MaxDecisionBytes)
            {
                Discard(path, $"it is larger than {MaxDecisionBytes} bytes");
                return null;
            }
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            // ROUTINE: a sweep the watcher started got there first and already forwarded it.
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Discard(path, "it is not a JSON object");
                return null;
            }
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v != 1)
            {
                Discard(path, "it is not a version 1 decision");
                return null;
            }
            if (!root.TryGetProperty("decisionId", out var decisionId) || decisionId.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(decisionId.GetString()))
            {
                Discard(path, "it carries no decisionId");
                return null;
            }
            if (!root.TryGetProperty("sessionId", out var claimed) || claimed.ValueKind != JsonValueKind.String
                || !Guid.TryParse(claimed.GetString(), out var claimedId) || claimedId != sessionId)
            {
                Discard(path, $"REFUSED: it names a session other than {sessionId}, whose directory it is in");
                return null;
            }
            return (decisionId.GetString()!, root.Clone());
        }
        catch (JsonException ex)
        {
            Discard(path, $"it is not valid JSON ({ex.Message})");
            return null;
        }
    }

    private static void Discard(string path, string why)
    {
        FileLog.Write($"[HookDecisionForwarder] discarding {path}: {why}");
        try { File.Delete(path); }
        catch (Exception ex) { FileLog.Write($"[HookDecisionForwarder] could not delete {path} FAILED: {ex.Message}"); }
    }

    /// <summary>
    /// Post one batch and settle each drop by the answer. Null when the post failed: the drops all stay,
    /// the next attempt waits, and the sweep stops here.
    /// </summary>
    /// <returns>How many were accepted or duplicates and deleted.</returns>
    private async Task<int?> PostAsync(
        ISessionRuleGateway gateway, IReadOnlyList<(string Path, string DecisionId, JsonElement Decision)> batch, CancellationToken ct)
    {
        var post = await gateway.PostHookFiringsAsync(batch.Select(b => b.Decision).ToList(), ct).ConfigureAwait(false);
        if (post.Answer is not { } answer)
        {
            _consecutiveFailures++;
            var wait = TimeSpan.FromTicks(Math.Min(
                MaxBackoff.Ticks, SweepInterval.Ticks * (1L << Math.Min(_consecutiveFailures, 30))));
            _nextAttemptUtc = _utcNow() + wait;
            FileLog.Write($"[HookDecisionForwarder] forwarding {batch.Count} decision(s) FAILED ({post.Failure}); " +
                          $"they stay, next attempt in {wait.TotalSeconds:0}s");
            return null;
        }

        _consecutiveFailures = 0;
        _nextAttemptUtc = DateTime.MinValue;

        var refused = (answer.Refused ?? Array.Empty<HookFiringRefusal>())
            .GroupBy(r => r.DecisionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Reason, StringComparer.Ordinal);
        var accountedFor = answer.Accepted + answer.Duplicates + refused.Count == batch.Count;

        var delivered = 0;
        foreach (var (path, decisionId, _) in batch)
        {
            if (refused.TryGetValue(decisionId, out var reason))
            {
                Discard(path, $"the Gateway refused decision {decisionId}: {reason}");
            }
            else if (accountedFor)
            {
                try { File.Delete(path); delivered++; }
                catch (Exception ex) { FileLog.Write($"[HookDecisionForwarder] forwarded but could not delete {path} FAILED: {ex.Message}"); }
            }
        }

        if (accountedFor)
            FileLog.Write($"[HookDecisionForwarder] forwarded {batch.Count} decision(s): accepted={answer.Accepted}, " +
                          $"duplicates={answer.Duplicates}, refused={refused.Count}");
        else
            FileLog.Write($"[HookDecisionForwarder] the Gateway's answer accounts for {answer.Accepted + answer.Duplicates + refused.Count} " +
                          $"of {batch.Count} decision(s) sent (accepted={answer.Accepted}, duplicates={answer.Duplicates}, " +
                          $"refused={refused.Count}); keeping the others for the next sweep");
        return delivered;
    }

    /// <summary>Remove the directory of each removed session once it holds no decision.</summary>
    private void RemoveDrainedDirectories()
    {
        foreach (var (sessionId, token) in _removedTokens)
        {
            var directory = SessionHookFiles.DecisionDirectoryFor(sessionId, token, _pointerDirectory);
            try
            {
                if (System.IO.Directory.Exists(directory)
                    && System.IO.Directory.EnumerateFiles(directory, "*" + SessionHookFiles.DropExtension).Any())
                    continue;
                if (System.IO.Directory.Exists(directory))
                    System.IO.Directory.Delete(directory, recursive: true);
                _removedTokens.TryRemove(sessionId, out _);
                FileLog.Write($"[HookDecisionForwarder] removed the decision directory of ended session {sessionId}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[HookDecisionForwarder] could not remove {directory} FAILED: {ex.Message}");
            }
        }
    }

    private void OnRemoved(Session session)
    {
        // Keep the token until the directory drains: the session's last decisions are still its record.
        _removedTokens[session.Id] = session.PointerDropToken;
    }

    private void OnDropped(object sender, FileSystemEventArgs e) => _ = SweepFromEventAsync($"a drop at {e.FullPath}");

    private void OnWatcherError(object sender, ErrorEventArgs e)
        => _ = SweepFromEventAsync($"the watcher losing events ({e.GetException().Message})");

    /// <summary>A sweep started from a watcher event: nothing awaits it, so this boundary catches.</summary>
    private async Task SweepFromEventAsync(string cause)
    {
        try { await SweepAsync().ConfigureAwait(false); }
        catch (Exception ex) { FileLog.Write($"[HookDecisionForwarder] sweep after {cause} FAILED: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessions.OnSessionRemoved -= OnRemoved;
        _sweepJob?.Dispose();
        _sweepJob = null;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnDropped;
            _watcher.Renamed -= OnDropped;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
