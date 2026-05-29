using System.Collections.Concurrent;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// Powers the auto-explain experience. For sessions with <see cref="Session.WingmanEnabled"/>
/// (default ON), this regenerates the wingman "explain" briefing (strong model / Opus) in
/// the background at each decision-point turn-end - i.e. when the session transitions into
/// <see cref="ActivityState.WaitingForInput"/> - and caches the result on the session via
/// <see cref="Session.SetCachedExplain"/>.
///
/// While the briefing call is in flight, the session is flagged
/// <see cref="Session.IsExplaining"/>=true so the <c>SessionStatusWingman</c> can paint the
/// dot Yellow ("Wingman is reading") instead of jumping straight to Red. The flag is cleared
/// in <c>finally</c> so a failure / timeout / shutdown still releases the Yellow state.
///
/// The phone then reads the cache instantly on open (GET /sessions/{id}/wingman/explain)
/// rather than waiting on a live Opus call behind a spinner.
///
/// Design notes:
/// - Gated to <see cref="Session.WingmanEnabled"/> sessions, so the Opus cost stays off the
///   whole fleet when a user disables Wingman for a session.
/// - Triggered on the WaitingForInput transition (a decision point), NOT every byte/turn.
/// - Background job: it owns its own cancellation, nobody is waiting on it.
/// - Per-session in-flight guard: never run two generations for one session at once; a
///   turn-end that arrives mid-generation is skipped (the next one regenerates).
/// - <see cref="Session.SetCachedExplain"/> ignores empty results, so a failed or timed-out
///   generation preserves the last good briefing instead of blanking the screen.
/// - TEXT ONLY. We do NOT pre-render TTS audio here. Auto-narration on every turn-end would
///   spend OpenAI tokens on briefings nobody will hear (and our experiment showed the work is
///   wasted - the phone is fast at the live /tts call against the cached briefing). The
///   phone's voice mode hits /tts on demand against the briefing's spoken-version field.
/// </summary>
public sealed class ProactiveExplainService : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly TurnSummaryCache? _turnSummaryCache;
    private readonly string _claudePath;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _handlers = new();
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _started;
    private bool _disposed;

    /// <summary>Let the terminal buffer settle into its final frame before briefing.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(600);

    public ProactiveExplainService(
        SessionManager sessionManager,
        string claudePath,
        TurnSummaryCache? turnSummaryCache = null)
    {
        _sessionManager = sessionManager;
        _claudePath = claudePath;
        _turnSummaryCache = turnSummaryCache;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[ProactiveExplainService] Start");

        _sessionManager.OnSessionCreated += OnSessionCreated;
        _sessionManager.OnSessionRemoved += OnSessionRemoved;
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s);
    }

    private void OnSessionCreated(Session session) => WireSession(session);

    /// <summary>Unhook the activity-state handler and clear the in-flight guard
    /// when a session is removed, so closed sessions do not leak handlers.</summary>
    private void OnSessionRemoved(Session session)
    {
        if (_handlers.TryRemove(session.Id, out var h))
            session.OnActivityStateChanged -= h;
        _inFlight.TryRemove(session.Id, out _);
    }

    private void WireSession(Session session)
    {
        if (_handlers.ContainsKey(session.Id)) return;

        Action<ActivityState, ActivityState> handler = (oldState, newState) =>
        {
            // Decision point: the agent finished its turn and is back at the prompt.
            if (newState != ActivityState.WaitingForInput) return;
            if (!session.WingmanEnabled) return;
            TriggerBackgroundExplain(session);
        };
        _handlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;
    }

    /// <summary>
    /// Generate a briefing now, regardless of activity state. Used when the user flips
    /// Wingman on so the cache is populated immediately instead of waiting for the next
    /// turn-end. No-op for Wingman-off sessions even if a stale code path calls in.
    /// </summary>
    public void TriggerBackgroundExplain(Session session)
    {
        if (_disposed) return;
        // Defensive: never spend an Opus turn on a session the user has disabled Wingman on.
        if (!session.WingmanEnabled) return;
        // Brand-new session: nothing useful to summarize yet (the agent has just printed
        // its splash banner). The Wingman tab is already showing the canned greeting set
        // by SessionStatusWingman.WireSession; running Opus on the banner is wasted work.
        // The flag clears on the user's first real submit, so the next turn-end will run
        // the wingman normally.
        if (session.IsBrandNew)
        {
            FileLog.Write($"[ProactiveExplainService] skip brand-new session {session.Id} (no user input yet)");
            return;
        }
        // Per-session in-flight guard: skip if one is already running.
        if (!_inFlight.TryAdd(session.Id, 0)) return;

        // Flip the Yellow overlay on now so SessionStatusWingman can repaint the dot
        // BEFORE the strong-model call starts; clear it in finally regardless of outcome.
        session.IsExplaining = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SettleDelay, _cts.Token);
                var ctx = await WingmanContextBuilder.BuildAsync(session, _turnSummaryCache, _cts.Token);
                var result = await WingmanService.AskAboutSessionAsync(
                    question: "", ctx, _claudePath, _cts.Token, explain: true);

                if (result is not null && string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    // Set the structured fields (headline, what-happened, etc.) FIRST, then
                    // SetCachedExplain - because SetCachedExplain is what fires
                    // OnCachedExplainChanged. If the structured fields were set after the event,
                    // every consumer that reads them on that event (the Wingman tab, the session
                    // list headline) would see the PREVIOUS briefing's fields and only catch up
                    // on the next regeneration. Order matters: populate, then notify.
                    session.SetCachedExplainStructured(result.Headline, result.WhatHappened, result.LongDescription, result.WhatClaudeWants, result.Say);
                    session.SetCachedExplain(result.Answer, result.Model, result.QuickReplies);
                    FileLog.Write($"[ProactiveExplainService] cached explain for {session.Id} (model={result.Model}, headline=\"{result.Headline}\", len={result.Answer?.Length ?? 0}, longLen={result.LongDescription?.Length ?? 0}, replies={result.QuickReplies?.Count ?? 0}, sayLen={result.Say?.Length ?? 0})");
                }
                else
                {
                    FileLog.Write($"[ProactiveExplainService] explain not ok for {session.Id}: status={result?.Status}, err={result?.Error}");
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                FileLog.Write($"[ProactiveExplainService] background explain FAILED for {session.Id}: {ex.Message}");
            }
            finally
            {
                session.IsExplaining = false;
                _inFlight.TryRemove(session.Id, out _);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }

        _sessionManager.OnSessionCreated -= OnSessionCreated;
        _sessionManager.OnSessionRemoved -= OnSessionRemoved;
        foreach (var s in _sessionManager.ListSessions())
        {
            if (_handlers.TryRemove(s.Id, out var h))
                s.OnActivityStateChanged -= h;
        }
        _cts.Dispose();
    }
}

/// <summary>Body of POST /sessions/{sid}/mobile-mode.</summary>
internal sealed record MobileModeRequest(bool Enabled);

/// <summary>Body of POST /sessions/{sid}/state-vote - a user correction of the detected state.</summary>
internal sealed record StateVoteRequest(string? CorrectState, string? Note, string? DetectedState, string? DetectedReason);

/// <summary>Body of POST /sessions/{sid}/voice-mode.</summary>
internal sealed record VoiceModeRequest(bool Enabled);

/// <summary>Body of POST /sessions/{sid}/wingman-enabled.</summary>
internal sealed record WingmanEnabledRequest(bool Enabled);
