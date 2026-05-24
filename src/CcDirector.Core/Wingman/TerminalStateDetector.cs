using System.Collections.Concurrent;
using System.Text;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// SHADOW-MODE prototype. Derives a session's state from its terminal byte stream
/// ALONE - no hooks, no agent-specific assumptions - and logs that verdict next to
/// the hook-derived <see cref="Session.ActivityState"/>. The point is to measure how
/// well a terminal-only detector tracks reality (and where the hooks go stale, e.g.
/// stuck "Working" after /clear or a cancel) BEFORE we cut the hooks over.
///
/// It writes NOTHING to the session - no StatusColor, no ActivityState. Observe only.
///
/// Two stages, as designed with the user:
///   1. Quiet gate (free, agent-agnostic): bytes flowing => ACTIVE; no bytes for
///      <see cref="QuietThreshold"/> => the gate fires. A working agent repaints its
///      spinner / elapsed-timer roughly every second, so it rarely stays quiet that
///      long; a genuine pause at the prompt does. The gate decides WHEN to look, it
///      does NOT decide the state.
///   2. LLM judge (optional): on the ACTIVE->QUIET transition we hand the terminal
///      tail to <see cref="WingmanService.ClassifyTerminalStateAsync"/> for the real
///      label. Bounded - at most once per quiet transition, with a cooldown - so the
///      token cost is roughly one cheap call per turn-end. Toggle with the
///      <c>useLlm</c> ctor flag.
/// </summary>
public sealed class TerminalStateDetector : IDisposable
{
    /// <summary>How long the terminal must be silent before the gate fires.</summary>
    public static readonly TimeSpan QuietThreshold = TimeSpan.FromSeconds(5);

    /// <summary>Minimum gap between LLM classifications for one session.</summary>
    private static readonly TimeSpan LlmCooldown = TimeSpan.FromSeconds(8);

    private readonly SessionManager _sessionManager;
    private readonly string _claudePath;
    private readonly bool _useLlm;
    private readonly bool _driveState;
    private readonly ConcurrentDictionary<Guid, Watcher> _watchers = new();
    private bool _started;
    private bool _disposed;

    /// <param name="driveState">
    /// When true, the detector is AUTHORITATIVE: it sets <see cref="Session.ActivityState"/>
    /// and raises turn-completed from the terminal. When false it is shadow-only (logs the
    /// terminal verdict next to the hook state, writes nothing).
    /// </param>
    public TerminalStateDetector(SessionManager sessionManager, string claudePath, bool useLlm, bool driveState)
    {
        _sessionManager = sessionManager;
        _claudePath = claudePath;
        _useLlm = useLlm;
        _driveState = driveState;
    }

    /// <summary>
    /// Map an LLM terminal-state verdict to the app's <see cref="ActivityState"/>.
    /// "idle"/"cancelled"/"unknown" all mean "not working, waiting on you" =&gt; WaitingForInput
    /// (the safe default: never claim "working" without positive evidence).
    /// </summary>
    internal static ActivityState MapVerdictToActivityState(string verdict) => verdict switch
    {
        "working" => ActivityState.Working,
        "waiting_for_permission" => ActivityState.WaitingForPerm,
        _ => ActivityState.WaitingForInput,
    };

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write($"[TerminalStateDetector] Start (mode={(_driveState ? "authoritative" : "shadow")}, llm={_useLlm}, quiet={QuietThreshold.TotalSeconds}s)");

        _sessionManager.OnSessionCreated += OnSessionCreated;
        foreach (var s in _sessionManager.ListSessions())
            Wire(s);
    }

    private void OnSessionCreated(Session session) => Wire(session);

    private void Wire(Session session)
    {
        if (session.Buffer is null) return;
        if (_watchers.ContainsKey(session.Id)) return;
        var w = new Watcher(session, _claudePath, _useLlm, _driveState);
        if (_watchers.TryAdd(session.Id, w))
            w.Start();
        else
            w.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionManager.OnSessionCreated -= OnSessionCreated;
        foreach (var w in _watchers.Values)
            w.Dispose();
        _watchers.Clear();
    }

    /// <summary>Per-session byte-activity gate + (optional) LLM classification.</summary>
    private sealed class Watcher : IDisposable
    {
        private readonly Session _session;
        private readonly CircularTerminalBuffer _buffer;
        private readonly string _claudePath;
        private readonly bool _useLlm;
        private readonly bool _driveState;
        private readonly Action<byte[]> _onBytes;
        private readonly System.Threading.Timer _quietTimer;

        private long _lastByteTicks;
        private bool _active;
        private DateTime _lastLlmAt = DateTime.MinValue;
        private int _llmInFlight;
        private int _disposed;

        public Watcher(Session session, string claudePath, bool useLlm, bool driveState)
        {
            _session = session;
            _buffer = session.Buffer!;
            _claudePath = claudePath;
            _useLlm = useLlm;
            _driveState = driveState;
            _lastByteTicks = DateTime.UtcNow.Ticks;
            _onBytes = OnBytes;
            _quietTimer = new System.Threading.Timer(OnQuiet, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            _buffer.OnBytesWritten += _onBytes;
            ArmQuietTimer();
        }

        private void ArmQuietTimer()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _quietTimer.Change(QuietThreshold, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* race with Dispose */ }
        }

        private void OnBytes(byte[] bytes)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _lastByteTicks, DateTime.UtcNow.Ticks);
            if (!_active)
            {
                _active = true;
                FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=ACTIVE (output flowing) | hook={_session.ActivityState} color={_session.StatusColor}");
                // Output is flowing => the agent is working. Instant, byte-based, agent-agnostic.
                if (_driveState) _session.ApplyTerminalActivityState(ActivityState.Working);
            }
            ArmQuietTimer(); // restart the countdown on every write
        }

        private void OnQuiet(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            // Guard against an early/raced fire: confirm we have really been silent.
            var lastByte = new DateTime(Volatile.Read(ref _lastByteTicks), DateTimeKind.Utc);
            var idleMs = (DateTime.UtcNow - lastByte).TotalMilliseconds;
            if (idleMs + 250 < QuietThreshold.TotalMilliseconds)
            {
                ArmQuietTimer();
                return;
            }

            if (!_active) return; // already reported quiet; nothing changed
            _active = false;
            FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=QUIET ({idleMs / 1000.0:F1}s no output) | hook={_session.ActivityState} color={_session.StatusColor}");

            if (_driveState)
            {
                // The output went quiet: the turn is over. Provisionally "waiting for you"
                // (never assume working without evidence); the LLM may refine to
                // waiting_for_permission below. Then raise the turn-ended pulse so the
                // Wingman summarises this turn from the terminal.
                _session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
                _session.NotifyTurnEndedFromTerminal();
            }

            if (_useLlm)
                MaybeClassify();
        }

        private void MaybeClassify()
        {
            if (DateTime.UtcNow - _lastLlmAt < LlmCooldown) return;
            if (Interlocked.CompareExchange(ref _llmInFlight, 1, 0) != 0) return;
            _lastLlmAt = DateTime.UtcNow;

            _ = Task.Run(async () =>
            {
                try
                {
                    var tail = SnapshotTail();
                    if (string.IsNullOrWhiteSpace(tail)) return;
                    var (st, reason) = await WingmanService.ClassifyTerminalStateAsync(
                        tail, _session.AgentKind.ToString(), _claudePath);
                    FileLog.Write($"[TerminalStateDetector] {_session.Id} LLM verdict={st} (\"{reason}\") | hook={_session.ActivityState} color={_session.StatusColor}");

                    // Only refine state if the terminal is still quiet (no new turn started
                    // while the model was thinking). If bytes resumed, OnBytes already set Working.
                    if (_driveState && !_active && st != "unknown")
                        _session.ApplyTerminalActivityState(MapVerdictToActivityState(st));
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[TerminalStateDetector] {_session.Id} classify failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _llmInFlight, 0);
                }
            });
        }

        private string SnapshotTail(int maxChars = 4000)
        {
            var bytes = _buffer.DumpAll();
            if (bytes.Length == 0) return string.Empty;
            var text = TerminalOutputParser.StripAnsi(Encoding.UTF8.GetString(bytes));
            if (text.Length > maxChars) text = text[^maxChars..];
            return text;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _buffer.OnBytesWritten -= _onBytes;
            _quietTimer.Dispose();
        }
    }
}
