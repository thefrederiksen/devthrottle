using System.Collections.Concurrent;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Auto-resume of a Claude Code session stalled on a TRANSIENT Anthropic API server error
/// (issue #476). The Wingman's badge detector (<see cref="TerminalStateDetector"/>) is purely
/// time-based and cannot tell "stalled on a retryable 500" apart from "finished cleanly"; this
/// adds the missing CONTENT signal: it scans the live resolved terminal grid for a transient
/// error signature (<see cref="TransientErrorSignatures"/>) and, when the auto-resume setting is
/// ON, nudges the session to continue on a cadence until it recovers or a give-up bound is hit.
///
/// CHARTER NOTE (docs/wingman/WINGMAN.md invariant 8). This is the one, explicitly human-approved
/// exception to "the Wingman never acts on its own": auto-resume is a Director-driven actuator,
/// not a request-driven one. It is gated behind <see cref="AutoResumeConfig"/> which DEFAULTS OFF
/// (opt-in, the human decision on assumption A-3), and every actuation still funnels through the
/// single write chokepoint <see cref="WingmanActionExecutor"/> (invariant 7 is preserved). When
/// the setting is OFF this class does literally nothing - zero scans armed, zero retries.
///
/// Per-session state machine:
///   * No transient error on screen  -> Idle (no timer armed).
///   * Transient error detected       -> Armed: schedule the first auto-continue after
///                                       <see cref="AutoResumeConfig.FirstRetryDelay"/>.
///   * Timer fires                    -> if the error is STILL on screen, submit a continue nudge
///                                       (attempt N), then re-arm for <see cref="AutoResumeConfig.Interval"/>.
///   * Error cleared (output resumed) -> Recovered: stop the loop, reset attempt count.
///   * Give-up bound hit              -> stop the loop and flag the session red "needs you".
///
/// What "continue" sends (assumption A-2): a single submitted line, <see cref="ContinueNudge"/>,
/// rather than re-sending the original prompt - so a partially-completed action is never re-run.
/// Claude Code resumes the interrupted turn from this nudge.
/// </summary>
public sealed class TransientErrorAutoResume : IDisposable
{
    /// <summary>
    /// The minimal nudge submitted to a stalled session (assumption A-2). A short, unambiguous
    /// "continue" that resumes the interrupted turn without re-running a partially-done action.
    /// </summary>
    public const string ContinueNudge = "Please continue.";

    /// <summary>
    /// Debounce between the last terminal byte and running the content scan. Long enough for the
    /// Ink TUI to settle on a final frame; well under any retry interval. Mirrors the
    /// <see cref="SessionStatusWingman.PromptInjectionDebounce"/> rationale.
    /// </summary>
    internal static readonly TimeSpan ScanDebounce = TimeSpan.FromMilliseconds(750);

    private readonly SessionManager _sessionManager;
    private readonly Func<AutoResumeConfig> _configProvider;
    private readonly ConcurrentDictionary<Guid, Watcher> _watchers = new();
    private bool _started;
    private bool _disposed;

    /// <param name="configProvider">Supplies the live <see cref="AutoResumeConfig"/>. Defaults to
    /// reading config.json each call (so a toggle takes effect without a Director restart); tests
    /// inject a fixed provider.</param>
    public TransientErrorAutoResume(SessionManager sessionManager, Func<AutoResumeConfig>? configProvider = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _configProvider = configProvider ?? AutoResumeConfig.Get;
    }

    /// <summary>Begin watching sessions for transient errors. Idempotent.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        var cfg = _configProvider();
        FileLog.Write($"[TransientErrorAutoResume] Start (enabled={cfg.Enabled}, firstRetry={cfg.FirstRetrySeconds}s, interval={cfg.IntervalSeconds}s, maxAttempts={cfg.MaxAttempts}, maxElapsed={cfg.MaxElapsedMinutes}min)");

        _sessionManager.OnSessionCreated += OnSessionCreated;
        _sessionManager.OnSessionRemoved += OnSessionRemoved;
        foreach (var s in _sessionManager.ListSessions())
            Wire(s);
    }

    private void OnSessionCreated(Session session) => Wire(session);

    private void OnSessionRemoved(Session session)
    {
        if (_watchers.TryRemove(session.Id, out var w))
            w.Dispose();
    }

    private void Wire(Session session)
    {
        if (session.Buffer is null) return;
        // v1 is Claude Code only (scope OUT: other agent CLIs). A non-Claude session never gets a
        // watcher, so it can never be auto-resumed.
        if (session.AgentKind != AgentKind.ClaudeCode) return;
        // Remote (GitHub Actions) sessions have no live PTY terminal grid to scan.
        if (session.BackendType == Backends.SessionBackendType.GitHubActions) return;
        if (_watchers.ContainsKey(session.Id)) return;

        var w = new Watcher(session, _configProvider);
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
        _sessionManager.OnSessionRemoved -= OnSessionRemoved;
        foreach (var w in _watchers.Values)
            w.Dispose();
        _watchers.Clear();
    }

    /// <summary>
    /// Per-session watcher: debounced content scan + the retry timer. The scan decides whether a
    /// transient error is on screen; the retry timer drives the auto-continue cadence and the
    /// give-up bound. The two share <see cref="_gate"/> so a scan and a timer fire never race on
    /// the attempt count.
    /// </summary>
    private sealed class Watcher : IDisposable
    {
        private readonly Session _session;
        private readonly CircularTerminalBuffer _buffer;
        private readonly AutoResumeLoop _loop;
        private readonly Action<byte[]> _onBytes;
        private readonly System.Threading.Timer _scanTimer;
        private readonly System.Threading.Timer _retryTimer;
        // One lock serializes the scan timer and the retry timer so they never race on the
        // loop's internal state.
        private readonly object _gate = new();
        private int _disposed;

        public Watcher(Session session, Func<AutoResumeConfig> configProvider)
        {
            _session = session;
            _buffer = session.Buffer ?? throw new InvalidOperationException(
                $"Session {session.Id} has no buffer (BackendType={session.BackendType})");
            _loop = new AutoResumeLoop(configProvider);
            _onBytes = OnBytes;
            _scanTimer = new System.Threading.Timer(_ => OnScan(), null, Timeout.Infinite, Timeout.Infinite);
            _retryTimer = new System.Threading.Timer(_ => OnRetry(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            _buffer.OnBytesWritten += _onBytes;
            ArmScan(); // scan whatever is already on screen (a restored session may already be stalled)
        }

        private void OnBytes(byte[] bytes)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            ArmScan();
        }

        private void ArmScan()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _scanTimer.Change(ScanDebounce, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* race with Dispose */ }
        }

        /// <summary>Debounced scan: the terminal settled, so look at the resolved grid.</summary>
        private void OnScan()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                var hasTransient = TransientErrorSignatures.IsRetryableTransient(SnapshotScreenText());
                AutoResumeStep step;
                lock (_gate)
                    step = _loop.OnScreenScan(hasTransient);
                Apply(step);
            }
            catch (Exception ex)
            {
                // Runs on a Timer thread; an escaped exception would terminate the process.
                FileLog.Write($"[TransientErrorAutoResume] scan failed session={_session.Id}: {ex.Message}");
            }
        }

        /// <summary>Retry timer fired: re-check the screen and let the loop decide.</summary>
        private void OnRetry()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                // Re-scan at fire time: only act if the transient error is STILL on screen.
                var stillTransient = TransientErrorSignatures.IsRetryableTransient(SnapshotScreenText());
                AutoResumeStep step;
                lock (_gate)
                    step = _loop.OnRetryDue(stillTransient);
                Apply(step);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TransientErrorAutoResume] retry failed session={_session.Id}: {ex.Message}");
            }
        }

        /// <summary>Carry out the loop's decision: arm the timer, auto-continue, or flag give-up.</summary>
        private void Apply(AutoResumeStep step)
        {
            switch (step.Kind)
            {
                case AutoResumeKind.None:
                    break;

                case AutoResumeKind.ArmFirstRetry:
                    FileLog.Write($"[TransientErrorAutoResume] session={_session.Id} state=TRANSIENT-ERROR detected; auto-resume armed, first continue in {step.Delay.TotalSeconds:F0}s");
                    ArmRetry(step.Delay);
                    break;

                case AutoResumeKind.Continue:
                    // The actual PTY write funnels through the single chokepoint (invariant 7).
                    var action = new WingmanAction
                    {
                        Action = WingmanAction.ActSubmit,
                        Text = ContinueNudge,
                        Reason = $"auto-resume transient API error (attempt {step.Attempt}/{step.MaxAttempts})",
                        Confidence = "high",
                    };
                    var result = WingmanActionExecutor.Execute(_session, action);
                    FileLog.Write($"[TransientErrorAutoResume] session={_session.Id} AUTO-CONTINUE attempt={step.Attempt}/{step.MaxAttempts} performed={result.Performed} status={result.Status} at={DateTime.UtcNow:o}");
                    ArmRetry(step.Delay); // re-arm for the steady interval
                    break;

                case AutoResumeKind.Recovered:
                    FileLog.Write($"[TransientErrorAutoResume] session={_session.Id} state=RECOVERED after {step.Attempt} auto-continue attempt(s); stopping retry loop");
                    StopRetry();
                    break;

                case AutoResumeKind.GiveUp:
                    FileLog.Write($"[TransientErrorAutoResume] session={_session.Id} GAVE-UP after {step.Attempt} attempt(s), {step.Elapsed.TotalMinutes:F1}min; flagging session needs-you");
                    StopRetry();
                    // Flag the session as needing the user. PositiveEvidence so it is sticky over the
                    // detector's plain activity-state mapping until the user acts.
                    _session.SetStatusColor(
                        StatusColor.Red,
                        "auto-resume gave up - transient API error did not clear",
                        source: StatusColorSource.PositiveEvidence);
                    break;
            }
        }

        private void ArmRetry(TimeSpan delay)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _retryTimer.Change(delay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* race with Dispose */ }
        }

        private void StopRetry()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _retryTimer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { /* race with Dispose */ }
        }

        private string SnapshotScreenText()
        {
            var rows = _session.SnapshotScreenRows();
            return rows.Length == 0 ? "" : string.Join("\n", rows);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _buffer.OnBytesWritten -= _onBytes;
            _scanTimer.Dispose();
            _retryTimer.Dispose();
        }
    }
}
