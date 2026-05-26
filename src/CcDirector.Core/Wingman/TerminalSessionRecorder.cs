using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Records EVERY session's resolved terminal grid over time to build the ground-truth
/// corpus for OFFLINE analysis and learning (docs/wingman/REDESIGN.md). We stopped trusting
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
/// </summary>
public sealed class TerminalSessionRecorder : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly string _root;
    private readonly long _maxBytesPerSession;
    private readonly ConcurrentDictionary<Guid, Recorder> _recorders = new();
    private bool _started;
    private bool _disposed;

    public TerminalSessionRecorder(SessionManager sessionManager, string? root = null, long maxBytesPerSession = 8L * 1024 * 1024)
    {
        _sessionManager = sessionManager;
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cc-director", "session-recordings");
        _maxBytesPerSession = maxBytesPerSession;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        try { Directory.CreateDirectory(_root); } catch (Exception ex) { FileLog.Write($"[TerminalSessionRecorder] cannot create {_root}: {ex.Message}"); }
        FileLog.Write($"[TerminalSessionRecorder] Start (root={_root}, capPerSession={_maxBytesPerSession / (1024 * 1024)}MB)");
        _sessionManager.OnSessionCreated += OnSessionCreated;
        foreach (var s in _sessionManager.ListSessions())
            Wire(s);
    }

    private void OnSessionCreated(Session session) => Wire(session);

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
            catch (Exception ex) { FileLog.Write($"[TerminalSessionRecorder] {_session.Id} cannot create dir: {ex.Message}"); return; }
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
                FileLog.Write($"[TerminalSessionRecorder] {_session.Id} record failed: {ex.Message}");
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
