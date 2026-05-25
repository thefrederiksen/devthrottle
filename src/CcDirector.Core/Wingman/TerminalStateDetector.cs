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

    private readonly SessionManager _sessionManager;
    private readonly string _claudePath;
    private readonly bool _useLlm;
    private readonly bool _driveState;
    private readonly bool _useFullSession;
    private readonly ConcurrentDictionary<Guid, Watcher> _watchers = new();
    private bool _started;
    private bool _disposed;

    /// <param name="driveState">
    /// When true, the detector is AUTHORITATIVE: it sets <see cref="Session.ActivityState"/>
    /// and raises turn-completed from the terminal. When false it is shadow-only (logs the
    /// terminal verdict next to the hook state, writes nothing).
    /// </param>
    /// <param name="useFullSession">
    /// When true, the LLM judge is a FULL-POWER fresh Claude Code session that reads the
    /// terminal snapshot via its own read-only tools (Phase 2,
    /// <see cref="WingmanService.ClassifyTerminalStateViaSessionAsync"/>). When false it is
    /// the lighter one-shot call with a pasted tail
    /// (<see cref="WingmanService.ClassifyTerminalStateAsync"/>). Only meaningful when
    /// <paramref name="useLlm"/> is true.
    /// </param>
    public TerminalStateDetector(SessionManager sessionManager, string claudePath, bool useLlm, bool driveState, bool useFullSession = false)
    {
        _sessionManager = sessionManager;
        _claudePath = claudePath;
        _useLlm = useLlm;
        _driveState = driveState;
        _useFullSession = useFullSession;
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

    /// <summary>The canonical "I am working" footer Claude Code shows while a turn
    /// or tool is running. ASCII, very specific to Claude Code, so it is a reliable
    /// working signal and is unlikely to appear in an idle status line.</summary>
    internal const string WorkingFooterMarker = "esc to interrupt";

    /// <summary>
    /// True when any row of the resolved on-screen grid carries the working footer
    /// (<see cref="WorkingFooterMarker"/>). This is positive evidence the agent is
    /// mid-turn even when no bytes are flowing -- a quiet tool / network wait leaves
    /// the footer statically on screen. The quiet gate consults this before declaring
    /// a turn over so it never repaints a working session green. An empty grid
    /// (Embedded backend, no resolved screen) is treated as "still working" so we never
    /// fabricate a turn-end we cannot see.
    /// </summary>
    internal static bool ScreenShowsWorkingFooter(string[] rows)
    {
        if (rows is null || rows.Length == 0) return true;
        foreach (var row in rows)
        {
            if (row.IndexOf(WorkingFooterMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write($"[TerminalStateDetector] Start (mode={(_driveState ? "authoritative" : "shadow")}, llm={_useLlm}, judge={(_useFullSession ? "full-session" : "tail-paste")}, quiet={QuietThreshold.TotalSeconds}s)");

        _sessionManager.OnSessionCreated += OnSessionCreated;
        foreach (var s in _sessionManager.ListSessions())
            Wire(s);
    }

    private void OnSessionCreated(Session session) => Wire(session);

    private void Wire(Session session)
    {
        if (session.Buffer is null) return;
        if (_watchers.ContainsKey(session.Id)) return;
        var w = new Watcher(session, _claudePath, _useLlm, _driveState, _useFullSession);
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
        private readonly bool _useFullSession;
        private readonly Action<byte[]> _onBytes;
        private readonly System.Threading.Timer _quietTimer;

        private long _lastByteTicks;
        private bool _active;
        private int _llmInFlight;
        private int _disposed;
        private string _lastUpperSig = "";

        /// <summary>
        /// How many rows at the BOTTOM of the screen to ignore when deciding whether
        /// real content changed. Claude Code's input box, spinner / elapsed-time
        /// counter, mode footer and any user status line all live here and churn
        /// cosmetically; the agent's actual output appears above them.
        /// </summary>
        private const int CosmeticBottomRows = 6;

        public Watcher(Session session, string claudePath, bool useLlm, bool driveState, bool useFullSession)
        {
            _session = session;
            _buffer = session.Buffer!;
            _claudePath = claudePath;
            _useLlm = useLlm;
            _driveState = driveState;
            _useFullSession = useFullSession;
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

            // Content-aware gate (issue #137 items 1-2). The previous logic reset the
            // quiet countdown on EVERY byte, so a continuously-repainting status line
            // or a long-running hook kept the gate "active" forever, starved the LLM
            // judge, and pinned a stale Working verdict. We instead reset only on REAL
            // activity, read from the RESOLVED on-screen grid (not the raw byte buffer,
            // where old frames linger): either the working footer is on screen, or the
            // upper (non-cosmetic) region of the screen actually changed. A cosmetic
            // repaint of the bottom rows alone no longer keeps the session "busy".
            var (workingNow, upperSig) = InspectScreen();
            bool contentChanged = !string.Equals(upperSig, _lastUpperSig, StringComparison.Ordinal);
            if (contentChanged) _lastUpperSig = upperSig;
            if (!(workingNow || contentChanged))
                return; // cosmetic only -> do NOT reset the countdown; let the gate fire

            Volatile.Write(ref _lastByteTicks, DateTime.UtcNow.Ticks);
            if (!_active)
            {
                _active = true;
                FileLog.Write($"[TerminalStateDetector] {_session.Id} terminal=ACTIVE (working={workingNow} contentChanged={contentChanged}) | hook={_session.ActivityState} color={_session.StatusColor}");
                if (_driveState) _session.ApplyTerminalActivityState(ActivityState.Working);
            }
            ArmQuietTimer(); // restart the countdown on real activity
        }

        /// <summary>
        /// Read the resolved on-screen grid and report (1) whether a working footer is
        /// currently visible, and (2) a signature of the upper (non-cosmetic) screen
        /// region for change detection. Reading the grid -- not the raw circular byte
        /// buffer -- is what lets us tell a working spinner ("esc to interrupt" on
        /// screen) apart from an idle status-line repaint. When there is no grid
        /// (Embedded backend) we fall back to the legacy "any byte is activity".
        /// </summary>
        private (bool workingNow, string upperSig) InspectScreen()
        {
            var rows = _session.SnapshotScreenRows();
            if (rows.Length == 0) return (true, "");

            bool working = ScreenShowsWorkingFooter(rows);
            var sb = new StringBuilder(rows.Length * 16);
            int upperCount = Math.Max(0, rows.Length - CosmeticBottomRows);
            for (int i = 0; i < upperCount; i++)
            {
                var row = rows[i];
                if (row.Length > 0)
                {
                    sb.Append(row);
                    sb.Append('\n');
                }
            }
            return (working, sb.ToString());
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

            // The byte stream went silent, but silence alone is not a turn-end. A working
            // agent blocked on a quiet tool (a long Bash, a network wait, a sub-agent)
            // stops emitting bytes while Claude Code keeps its "esc to interrupt" footer
            // statically on screen. Declaring "quiet" here would flip the session to
            // WaitingForInput -> green ("ready for anyone") mid-turn -- the observed bug.
            // Re-check the RESOLVED screen: if the working footer is still up, the agent is
            // still working. Stay ACTIVE/blue and keep waiting for it to truly finish.
            var (workingNow, _) = InspectScreen();
            if (workingNow)
            {
                ArmQuietTimer();
                return;
            }

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
            if (Interlocked.CompareExchange(ref _llmInFlight, 1, 0) != 0) return;
            // Global per-session floor: never call the LLM within 5s of the last one
            // (shared with the turn summariser) so a flappy session can't loop on it.
            if (!WingmanLlmThrottle.TryAcquire(_session.Id))
            {
                Interlocked.Exchange(ref _llmInFlight, 0);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    string st, reason, awaiting;
                    if (_useFullSession)
                    {
                        // Phase 2: hand the WHOLE terminal to a full-power read-only session
                        // and let it pull what it needs.
                        var full = SnapshotFull();
                        if (string.IsNullOrWhiteSpace(full)) return;
                        (st, reason, awaiting) = await WingmanService.ClassifyTerminalStateViaSessionAsync(
                            full, _session.AgentKind.ToString(), _session.RepoPath, _claudePath);
                    }
                    else
                    {
                        var tail = SnapshotTail();
                        if (string.IsNullOrWhiteSpace(tail)) return;
                        (st, reason, awaiting) = await WingmanService.ClassifyTerminalStateAsync(
                            tail, _session.AgentKind.ToString(), _claudePath);
                    }
                    FileLog.Write($"[TerminalStateDetector] {_session.Id} LLM verdict={st} (\"{reason}\") awaiting=\"{(awaiting.Length > 60 ? awaiting[..60] + "..." : awaiting)}\" judge={(_useFullSession ? "full-session" : "tail-paste")} | hook={_session.ActivityState} color={_session.StatusColor}");

                    // Only refine state if the terminal is still quiet (no new turn started
                    // while the model was thinking). If bytes resumed, OnBytes already set Working.
                    if (_driveState && !_active && st != "unknown")
                    {
                        var actState = MapVerdictToActivityState(st);
                        _session.ApplyTerminalActivityState(actState);
                        // Issue #137 item 3: the detector is the single colour authority in
                        // terminal-driven mode. ColorFromVerdict maps the state + the verbatim
                        // pending request ("awaiting") to colour: red positive-evidence for a
                        // pending question / permission / interrupt, green when idle, blue when
                        // working. The model's OWN reason is the wingman-log entry (tagged LLM).
                        var (color, cReason, csource) = SessionStatusWingman.ColorFromVerdict(st, reason, awaiting);
                        _session.SetStatusColor(color, cReason, llm: true, source: csource);
                    }
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

        /// <summary>
        /// The whole ANSI-stripped terminal history (capped to a sane ceiling) for the
        /// full-power session judge to read on its own. Unlike <see cref="SnapshotTail"/>
        /// this does not pre-truncate to a guess - the session decides how far back to look.
        /// </summary>
        private string SnapshotFull(int maxChars = 200_000)
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
