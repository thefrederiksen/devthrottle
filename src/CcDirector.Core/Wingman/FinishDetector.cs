using System.Collections.Concurrent;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Live wiring for finish detection (docs/wingman/REDESIGN.md). TERMINAL-ONLY: CC Director
/// is hook-free by design (the default terminal-driven mode does not install Claude Code
/// hooks), so one per-session watcher drives a pure <see cref="FinishDetectorCore"/> from
/// the terminal alone:
///
///   - the resolved terminal screen (<see cref="Session.SnapshotScreenRows"/> read by
///     <see cref="ClaudeScreenReader"/>) on each byte burst - positive working/parked evidence;
///   - a periodic tick - so the parked-confirm window can expire without new bytes.
///
/// When the core declares a turn finished, the watcher fires once. In SHADOW mode
/// (<c>driveState=false</c>, the default) it only logs the decision next to the current
/// state, so we can validate the detector against real sessions before trusting it - it
/// changes nothing. In DRIVE mode it is authoritative: it sets the activity state and raises
/// the turn-ended pulse. DRIVE must not run alongside another detector that also drives state.
/// </summary>
public sealed class FinishDetector : IDisposable
{
    /// <summary>How often to advance the core's clock so the terminal-only confirm window
    /// can elapse without waiting on a new byte/hook.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(350);

    private readonly SessionManager _sessionManager;
    private readonly bool _driveState;
    private readonly ConcurrentDictionary<Guid, Watcher> _watchers = new();
    private bool _started;
    private bool _disposed;

    public FinishDetector(SessionManager sessionManager, bool driveState = false)
    {
        _sessionManager = sessionManager;
        _driveState = driveState;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write($"[FinishDetector] Start (mode={(_driveState ? "drive" : "shadow")}, tick={TickInterval.TotalMilliseconds:F0}ms)");
        _sessionManager.OnSessionCreated += OnSessionCreated;
        foreach (var s in _sessionManager.ListSessions())
            Wire(s);
    }

    private void OnSessionCreated(Session session) => Wire(session);

    private void Wire(Session session)
    {
        if (session.Buffer is null) return;
        if (_watchers.ContainsKey(session.Id)) return;
        var w = new Watcher(session, _driveState);
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

    private sealed class Watcher : IDisposable
    {
        private readonly Session _session;
        private readonly CircularTerminalBuffer _buffer;
        private readonly bool _driveState;
        private readonly FinishDetectorCore _core = new();
        private readonly Action<byte[]> _onBytes;
        private readonly System.Threading.Timer _tickTimer;
        private ScreenParkState _lastScreen = ScreenParkState.Unknown;
        private int _disposed;

        public Watcher(Session session, bool driveState)
        {
            _session = session;
            _buffer = session.Buffer!;
            _driveState = driveState;
            _onBytes = OnBytes;
            _tickTimer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            _buffer.OnBytesWritten += _onBytes;
            _tickTimer.Change(TickInterval, TickInterval);
        }

        private void OnBytes(byte[] _)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                var screen = ClaudeScreenReader.Read(_session.SnapshotScreenRows());
                _lastScreen = screen;
                if (_core.OnScreen(screen, DateTime.UtcNow))
                    Fire($"screen:{screen}");
            }
            catch (Exception ex) { FileLog.Write($"[FinishDetector] {_session.Id} OnBytes failed: {ex.Message}"); }
        }

        private void OnTick(object? _)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                if (_core.OnTick(DateTime.UtcNow))
                    Fire("tick");
            }
            catch (Exception ex) { FileLog.Write($"[FinishDetector] {_session.Id} OnTick failed: {ex.Message}"); }
        }

        private void Fire(string trigger)
        {
            FileLog.Write($"[FinishDetector] {_session.Id} TURN FINISHED via {trigger}, screen={_lastScreen}, mode={(_driveState ? "drive" : "shadow")} | hookState={_session.ActivityState} color={_session.StatusColor}");

            if (!_driveState) return; // shadow: observe + log only, change nothing.

            // Authoritative: map the parked screen to the activity state and raise the
            // turn-ended pulse (the same pulse the legacy terminal detector raises).
            var state = _lastScreen == ScreenParkState.ParkedForPermission
                ? ActivityState.WaitingForPerm
                : ActivityState.WaitingForInput;
            _session.ApplyTerminalActivityState(state);
            _session.NotifyTurnEndedFromTerminal();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _buffer.OnBytesWritten -= _onBytes;
            _tickTimer.Dispose();
        }
    }
}
