using System.Collections.Concurrent;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// Powers the mobile/voice "instant briefing" experience. For sessions flagged
/// <see cref="Session.MobileMode"/>, this regenerates the wingman "explain" briefing
/// (strong model / Opus) in the background at each decision-point turn-end - i.e. when the
/// session transitions into <see cref="ActivityState.WaitingForInput"/> - and caches the
/// result on the session via <see cref="Session.SetCachedExplain"/>.
///
/// The phone then reads the cache instantly on open (GET /sessions/{id}/wingman/explain)
/// rather than waiting on a live Opus call behind a spinner.
///
/// Design notes (from the remote-experience plan, P1.2):
/// - Gated to mobile-mode sessions only, so the Opus cost stays off the whole fleet.
/// - Triggered on the WaitingForInput transition (a decision point), NOT every byte/turn.
/// - Background job: it owns its own cancellation, nobody is waiting on it.
/// - Per-session in-flight guard: never run two generations for one session at once; a
///   turn-end that arrives mid-generation is skipped (the next one regenerates).
/// - <see cref="Session.SetCachedExplain"/> ignores empty results, so a failed or timed-out
///   generation preserves the last good briefing instead of blanking the screen.
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

    public ProactiveExplainService(SessionManager sessionManager, string claudePath, TurnSummaryCache? turnSummaryCache = null)
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
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s);
    }

    private void OnSessionCreated(Session session) => WireSession(session);

    private void WireSession(Session session)
    {
        if (_handlers.ContainsKey(session.Id)) return;

        Action<ActivityState, ActivityState> handler = (oldState, newState) =>
        {
            // Decision point: the agent finished its turn and is back at the prompt.
            if (newState != ActivityState.WaitingForInput) return;
            if (!session.MobileMode) return;
            TriggerBackgroundExplain(session);
        };
        _handlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;
    }

    /// <summary>
    /// Generate a briefing now, regardless of activity state. Used when mobile mode is
    /// switched on so the cache is populated immediately instead of waiting for the next turn.
    /// </summary>
    public void TriggerBackgroundExplain(Session session)
    {
        if (_disposed) return;
        // Per-session in-flight guard: skip if one is already running.
        if (!_inFlight.TryAdd(session.Id, 0)) return;

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
                    session.SetCachedExplain(result.Answer, result.Model, result.QuickReplies);
                    FileLog.Write($"[ProactiveExplainService] cached explain for {session.Id} (model={result.Model}, len={result.Answer?.Length ?? 0}, replies={result.QuickReplies?.Count ?? 0})");
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

/// <summary>Body of POST /sessions/{sid}/voice-mode.</summary>
internal sealed record VoiceModeRequest(bool Enabled);
