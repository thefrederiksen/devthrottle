using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// The one seam every session-state observation must pass through, whichever plane it arrives on.
///
/// WHY THIS CLASS EXISTS (issue #3124, the 8-day empty ledger). The governance ledger's emitter is fed by a
/// funnel that, for its whole life, was invoked from exactly one place: the legacy same-machine HTTP discovery
/// legs (POST /directors/{id}/heartbeat, POST /directors/{id}/doorbell). Those legs return 403 on the HOSTED
/// gateway - a hosted Director reaches the Gateway over the tunnel, never HTTP - so on hosted, no session
/// transition could ever reach the ledger. Not for any account, not ever. The proof is in production: 8+
/// consecutive daily reports read "0 agent sessions ran yesterday" for a tenant whose Directors were
/// connected and working every day, while every feed that rides the tunnel (repo-state, dictation, rosters)
/// landed normally. The plane moved to the tunnel; the funnel stayed behind on the forbidden legs.
///
/// This sink is the shared instance the DirectorHub folds each ACCEPTED push into, handed the same delegate
/// the HTTP legs still pass on self-host - one funnel, two ingress planes, one ledger. A Director that is on
/// both planes at once (self-host with a tunnel configured) feeds the funnel twice; the emitter's per-session
/// dedup absorbs the repeat, exactly as it absorbs a heartbeat re-reporting the current state.
///
/// NEVER THROWS: a hub method that throws tears the Director's connection down, and a session-state
/// observation is never worth a tunnel. The delegate's own concerns (voice-cache clearing, the turn watcher,
/// the ledger append) each already tolerate failure inside; this seam makes the whole call contained so the
/// tunnel survives any of them.
/// </summary>
public sealed class SessionStateObservationSink
{
    private Action<string, string, string>? _onSessionState;

    /// <summary>
    /// Hand the sink THE funnel delegate - the same lambda the legacy HTTP legs receive, so both planes run
    /// one code path. Bound once, during endpoint mapping, before the listener binds and therefore before
    /// any Director can connect and push.
    /// </summary>
    public void Bind(Action<string, string, string> onSessionState)
    {
        _onSessionState = onSessionState ?? throw new ArgumentNullException(nameof(onSessionState));
    }

    /// <summary>
    /// One observed session state, from an ACCEPTED push. Null activity states and blank session ids are
    /// ignored here rather than downstream: the emitter would drop them anyway, and the caller should not
    /// have to know its vocabulary rules.
    /// </summary>
    public void Observe(string directorId, string sessionId, string? activityState)
    {
        if (_onSessionState is null)
            return;   // never bound (tests, or a push before mapping) - drop rather than guess
        if (string.IsNullOrWhiteSpace(directorId) || string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(activityState))
            return;

        try
        {
            _onSessionState(directorId, sessionId, activityState);
        }
        catch (Exception ex)
        {
            // Contained, never swallowed silently: the observation is dropped, the tunnel lives, and the
            // log says what was lost so an empty ledger is explainable rather than mysterious.
            FileLog.Write($"[SessionStateObservationSink] observation dropped (observer threw): director={directorId}, session={sessionId}, state={activityState}: {ex.Message}");
        }
    }
}
