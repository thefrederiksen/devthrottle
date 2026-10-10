using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Records EVERY session's resolved terminal grid over time to build the ground-truth
/// corpus for OFFLINE analysis and learning (docs/wingman/WINGMAN.md, section 6). We stopped trusting
/// idealized fixtures and one-off driven runs; instead the Director quietly logs what real
/// sessions actually look like - across Claude Code versions and real workflows - so we can
/// later replay it, find where finish detection / the Wingman get it wrong, and build
/// fixtures from reality.
///
/// One append-only JSONL per session at
/// <c>%LOCALAPPDATA%/cc-director/session-recordings/&lt;sessionId&gt;/grid.jsonl</c>. A frame is
/// written only when the resolved grid actually CHANGES (deduped); each frame carries the raw
/// screen rows plus the session's activity state at capture (driven by the trigger + LLM judge
/// in TerminalStateDetector). Offline analysis can replay the raw rows through whatever judge
/// we want to evaluate.
///
/// OBSERVE-ONLY: it never changes session behavior. Capped per session so it cannot grow
/// unbounded; once the cap is hit, recording for that session stops (the early/most-varied
/// part of a session is the interesting part).
///
/// CAPTURE and PURGE are two different lifecycles, deliberately. Capture - writing new frames -
/// is what the visible setting switches. Purge - deleting a removed session's recording directory -
/// is not optional and does not follow the setting: recordings made while capture WAS on (the old
/// default-on release, or a setting since switched off) must not outlive their session just because
/// nothing is capturing today. So the host constructs and starts this recorder unconditionally, the
/// removal subscription is always live, and only the capture side is gated. Startup additionally
/// sweeps recordings whose sessions were removed before this process existed - the removal event
/// for those fired (or never fired) in a previous life, so subscription alone can never reach them.
/// </summary>
public sealed class TerminalSessionRecorder : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly string _root;
    private readonly long _maxBytesPerSession;
    private readonly bool _captureEnabled;
    private readonly ConcurrentDictionary<Guid, Recorder> _recorders = new();
    private bool _started;
    private bool _disposed;

    // captureEnabled defaults to FALSE so that the safe state is the free one: the shipped product
    // records nothing unless the operator turns it on, and a future call site that forgets the flag
    // inherits that posture instead of silently recording. Production passes
    // SessionRecordingConfig.IsEnabled() explicitly; this default is the backstop, not the policy.
    public TerminalSessionRecorder(SessionManager sessionManager, string? root = null, long maxBytesPerSession = 8L * 1024 * 1024, bool captureEnabled = false)
    {
        _sessionManager = sessionManager;
        _root = root ?? CcStorage.SessionRecordings();
        _maxBytesPerSession = maxBytesPerSession;
        _captureEnabled = captureEnabled;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        // Purge is subscribed before anything else and regardless of the capture setting - see the
        // class note: a pre-existing recording must be deleted with its session even on an install
        // where capture is off.
        _sessionManager.OnSessionRemoved += OnSessionRemoved;

        // Startup reconciliation, also regardless of the capture setting: the removal subscription
        // only covers sessions removed while THIS process runs. A recording whose session was
        // removed before the upgrade - under the old default-on release, when no purge handler
        // existed - has no live session left to emit a removal event, so without this sweep it
        // survives forever on exactly the installs the purge policy was written for.
        SweepOrphanedRecordings();

        if (!_captureEnabled)
        {
            FileLog.Write($"[TerminalSessionRecorder] capture is OFF; purge-on-removal stays active (root={_root})");
            return;
        }

        try { Directory.CreateDirectory(_root); } catch (Exception ex) { FileLog.Write($"[TerminalSessionRecorder] cannot create {_root} FAILED: {ex.Message}"); }
        FileLog.Write($"[TerminalSessionRecorder] Start (root={_root}, capPerSession={_maxBytesPerSession / (1024 * 1024)}MB)");
        _sessionManager.OnSessionCreated += OnSessionCreated;
        foreach (var s in _sessionManager.ListSessions())
            Wire(s);
    }

    private void OnSessionCreated(Session session) => Wire(session);

    /// <summary>
    /// Stop recording when a session is removed, and DELETE what was recorded for it.
    ///
    /// Disposing the writer was all this did, so every removed session left its recording behind
    /// forever: the file has no age limit, and the session that owned it - the only thing that made
    /// it meaningful, and the only thing anyone would think to remove - was gone. An install that
    /// had recording switched on accumulated the screens of sessions that no longer existed, in a
    /// directory nothing ever swept. The recording exists to study a session; when the session is
    /// gone, so is the reason to keep it.
    ///
    /// Best-effort by necessity: a file still held open by an antivirus scanner or a reader must not
    /// take down the session-removal path, which does the real work of closing a session down. A
    /// failure is logged loudly rather than swallowed, because a purge that quietly did nothing is
    /// the same as no purge at all.
    /// </summary>
    private void OnSessionRemoved(Session session)
    {
        if (_recorders.TryRemove(session.Id, out var r))
            r.Dispose();

        PurgeRecording(session.Id);
    }

    /// <summary>Delete one session's recording directory. Exposed for the regression test that
    /// proves a removed session leaves nothing behind.</summary>
    internal void PurgeRecording(Guid sessionId)
    {
        // "N" - the same format the Recorder writes with. A guid has more than one spelling and only
        // one of them is the directory that exists; the default spelling would have deleted nothing,
        // logged nothing, and left a purge that could never fail because it never found anything.
        var dir = Path.Combine(_root, sessionId.ToString("N"));
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                FileLog.Write($"[TerminalSessionRecorder] purged the recording for removed session {sessionId}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalSessionRecorder] could NOT purge {dir} FAILED: {ex.Message}. "
                          + "That session's recorded screens are still on disk.");
        }
    }

    /// <summary>
    /// Delete every recording directory whose session no longer exists. Only directories whose name
    /// is a session id in the recorder's own spelling ("N") are touched - anything else under the
    /// root was not written by us, and a sweep that guesses about foreign directories is a data
    /// deleter, not a janitor. A directory created concurrently for a NEW session cannot collide:
    /// session ids are fresh guids, so a name either belongs to a live session (kept) or to nothing.
    /// </summary>
    private void SweepOrphanedRecordings()
    {
        string[] dirs;
        try
        {
            if (!Directory.Exists(_root)) return;
            dirs = Directory.GetDirectories(_root);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalSessionRecorder] startup sweep could not list {_root} FAILED: {ex.Message}");
            return;
        }

        var live = _sessionManager.ListSessions().Select(s => s.Id).ToHashSet();
        var swept = 0;
        foreach (var dir in dirs)
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out var sessionId) || live.Contains(sessionId))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                swept++;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TerminalSessionRecorder] startup sweep could NOT delete {dir} FAILED: {ex.Message}. "
                              + "That removed session's recorded screens are still on disk.");
            }
        }
        if (swept > 0)
            FileLog.Write($"[TerminalSessionRecorder] startup sweep purged {swept} recording(s) whose sessions were removed before this run");
    }

    private void Wire(Session session)
    {
        if (session.Buffer is null) return;
        if (_recorders.ContainsKey(session.Id)) return;
        var r = new Recorder(session, _root, _maxBytesPerSession);
        if (_recorders.TryAdd(session.Id, r))
            r.Start();
        else
            r.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionManager.OnSessionCreated -= OnSessionCreated;
        _sessionManager.OnSessionRemoved -= OnSessionRemoved;
        foreach (var r in _recorders.Values)
            r.Dispose();
        _recorders.Clear();
    }

    private sealed class Recorder : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        private readonly Session _session;
        private readonly CircularTerminalBuffer _buffer;
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly Action<byte[]> _onBytes;
        private readonly object _gate = new();
        private string _lastHash = "";
        private long _written;
        private bool _capped;
        private int _disposed;

        public Recorder(Session session, string root, long maxBytes)
        {
            _session = session;
            _buffer = session.Buffer!;
            _maxBytes = maxBytes;
            _onBytes = OnBytes;
            _path = Path.Combine(root, session.Id.ToString("N"), "grid.jsonl");
        }

        public void Start()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); }
            catch (Exception ex) { FileLog.Write($"[TerminalSessionRecorder] {_session.Id} cannot create dir FAILED: {ex.Message}"); return; }
            _buffer.OnBytesWritten += _onBytes;
        }

        private void OnBytes(byte[] _)
        {
            if (Volatile.Read(ref _disposed) != 0 || _capped) return;
            try
            {
                var rows = _session.SnapshotScreenRows();
                if (rows.Length == 0) return;

                // Dedupe: only persist when the resolved grid actually changed.
                var joined = string.Join("\n", rows);
                var hash = Sha256(joined);
                if (hash == _lastHash) return;

                var frame = new Frame(
                    DateTime.UtcNow.ToString("o"),
                    _session.ActivityState.ToString(),
                    rows);
                var line = JsonSerializer.Serialize(frame, Json);

                lock (_gate)
                {
                    if (_capped) return;
                    File.AppendAllText(_path, line + "\n", Encoding.UTF8);
                    _written += line.Length + 1;
                    _lastHash = hash;
                    if (_written >= _maxBytes)
                    {
                        _capped = true;
                        FileLog.Write($"[TerminalSessionRecorder] {_session.Id} reached {_maxBytes / (1024 * 1024)}MB cap; recording stopped for this session");
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TerminalSessionRecorder] {_session.Id} record FAILED: {ex.Message}");
            }
        }

        private static string Sha256(string s)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _buffer.OnBytesWritten -= _onBytes;
        }

        /// <summary>One recorded grid frame: timestamp, the session's activity state at capture
        /// (driven by the LLM judge), and the resolved screen rows. The raw rows are the corpus;
        /// offline analysis can replay them through whatever judge we want to evaluate.</summary>
        private sealed record Frame(string T, string Activity, string[] Rows);
    }
}
