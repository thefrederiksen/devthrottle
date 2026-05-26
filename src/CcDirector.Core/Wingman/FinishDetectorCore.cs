namespace CcDirector.Core.Wingman;

/// <summary>
/// The pure finish-detection state machine (docs/wingman/REDESIGN.md section 2).
///
/// TERMINAL-ONLY. CC Director runs hook-free by design: in the default terminal-driven
/// mode the Director deliberately does NOT install Claude Code hooks (App.axaml.cs), so
/// there is no Stop hook to lean on. Finish detection therefore comes entirely from the
/// resolved terminal screen, classified by <see cref="ClaudeScreenReader"/>.
///
/// A turn is declared over when the screen has been positively PARKED (the agent is waiting
/// on the user - <see cref="ScreenParkState.ParkedForInput"/> or
/// <see cref="ScreenParkState.ParkedForPermission"/>) for a short confirm window, AND we
/// had previously seen the agent WORKING in this turn. Requiring prior work is what stops a
/// freshly-booted idle session from fabricating a turn-end, and the confirm window is what
/// stops a momentary parked-looking frame mid-turn from firing. Never inferred from silence.
///
/// Deliberately pure: fed discrete screen states and ticks with an explicit clock, no
/// timers/I/O/Session. The live <see cref="FinishDetector"/> drives it from real screen
/// reads + a timer; the capture/replay harness drives it from recordings - same brain.
/// </summary>
public sealed class FinishDetectorCore
{
    /// <summary>How long a parked screen must persist before the turn-end fires. Guards
    /// against a transient parked-looking frame in the middle of a turn.</summary>
    private readonly TimeSpan _confirmWindow;

    private bool _turnActive;          // we have seen the agent working in this turn
    private bool _finishedFired;       // already emitted TurnFinished for the current parked state
    private DateTime? _parkedSinceUtc; // when the screen first looked parked in the current candidate; null when not parked

    public FinishDetectorCore(TimeSpan? confirmWindow = null)
        => _confirmWindow = confirmWindow ?? TimeSpan.FromMilliseconds(800);

    /// <summary>Feed the current resolved screen state. Returns <c>true</c> when this input
    /// completes a turn-end and the caller should emit TurnFinished now.</summary>
    public bool OnScreen(ScreenParkState screen, DateTime nowUtc)
    {
        switch (screen)
        {
            case ScreenParkState.Working:
                // Positive evidence of work: a turn is active and any parked candidate is void.
                // Observing work also resets a prior finish so the NEXT turn can fire.
                _turnActive = true;
                _finishedFired = false;
                _parkedSinceUtc = null;
                return false;

            case ScreenParkState.ParkedForInput:
            case ScreenParkState.ParkedForPermission:
                _parkedSinceUtc ??= nowUtc;
                return TryFire(nowUtc);

            // Unknown: we cannot see; do not change the candidate (a pending debounce may still
            // elapse via OnTick) and never claim finished from no evidence.
            default:
                return false;
        }
    }

    /// <summary>Advance the clock with no new screen read, so the confirm window can expire.
    /// The live wrapper calls this from a timer; the harness calls it with recorded time.</summary>
    public bool OnTick(DateTime nowUtc) => TryFire(nowUtc);

    private bool TryFire(DateTime nowUtc)
    {
        if (!_turnActive || _finishedFired || _parkedSinceUtc is null)
            return false;
        if (nowUtc - _parkedSinceUtc.Value < _confirmWindow)
            return false;

        _finishedFired = true;
        _turnActive = false;
        _parkedSinceUtc = null;
        return true;
    }
}
