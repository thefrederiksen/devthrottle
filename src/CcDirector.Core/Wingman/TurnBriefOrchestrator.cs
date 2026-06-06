using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// The turn lifecycle (TURN_BRIEFING.md section 3): watches every session for turn end
/// (Working -> waiting/idle), assembles the turn package, runs the generator, and stores the
/// TurnBrief. The yellow window lives here: BriefingState=Briefing while the wingman reads.
///
/// Watch-cancel (plan DT and D1): if the session re-enters Working while a brief is in
/// flight - the user replied, they were watching the terminal - the generation is cancelled;
/// briefing a decision already made is wasted tokens AND a stale UI. A result whose turn
/// count no longer matches the transcript is likewise discarded.
///
/// Opt-out: CC_TURNBRIEFS=0 disables the whole pipeline (the plan's kill switch).
/// </summary>
public sealed class TurnBriefOrchestrator : IDisposable
{
    /// <summary>Settle delay after turn end before reading the transcript (the detector can
    /// fire before claude flushes the JSONL - measured during the example captures).</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2.5);

    private readonly SessionManager _sessionManager;
    private readonly ITurnBriefGenerator _generator;
    private readonly TurnBriefStore _store;
    private readonly Func<Session, List<TurnWidgetDto>?> _transcriptReader;
    private readonly Func<Session, string> _screenReader;
    private readonly TimeSpan _settleDelay;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlight = new();
    private bool _disposed;

    public TurnBriefStore Store => _store;

    public TurnBriefOrchestrator(
        SessionManager sessionManager,
        ITurnBriefGenerator generator,
        TurnBriefStore? store = null,
        Func<Session, List<TurnWidgetDto>?>? transcriptReader = null,
        Func<Session, string>? screenReader = null,
        TimeSpan? settleDelay = null)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(generator);
        _sessionManager = sessionManager;
        _generator = generator;
        _store = store ?? new TurnBriefStore();
        _transcriptReader = transcriptReader ?? TryParseTranscript;
        _screenReader = screenReader ?? ReadScreenTail;
        _settleDelay = settleDelay ?? SettleDelay;
    }

    /// <summary>True when the pipeline is disabled via CC_TURNBRIEFS=0.</summary>
    public static bool Disabled =>
        Environment.GetEnvironmentVariable("CC_TURNBRIEFS") == "0";

    public void Start()
    {
        if (Disabled)
        {
            FileLog.Write("[TurnBriefOrchestrator] disabled (CC_TURNBRIEFS=0)");
            return;
        }
        FileLog.Write($"[TurnBriefOrchestrator] Start: generator={_generator.Id}");
        _sessionManager.OnSessionCreated += Attach;
        foreach (var s in _sessionManager.ListSessions())
            Attach(s);
    }

    internal void Attach(Session session)
    {
        session.OnActivityStateChanged += (oldState, newState) => OnStateChanged(session, oldState, newState);
        // Restore the rail line from the durable store after a Director restart.
        var latest = _store.Latest(session.Id);
        if (latest is not null)
        {
            session.LatestBriefRailLine = latest.NeedsYou?.RailLine;
            session.SetBriefingState(BriefingState.Briefed);
        }
    }

    private void OnStateChanged(Session session, ActivityState oldState, ActivityState newState)
    {
        if (_disposed) return;

        // Watch-cancel: the user replied (or the agent resumed) - the in-flight brief is moot.
        if (newState == ActivityState.Working && _inFlight.TryRemove(session.Id, out var inflight))
        {
            FileLog.Write($"[TurnBriefOrchestrator] watch-cancel: sid={session.Id} re-entered Working");
            inflight.Cancel();
        }

        // Turn end: Working -> waiting/idle.
        if (oldState != ActivityState.Working) return;
        if (newState is not (ActivityState.WaitingForInput or ActivityState.Idle)) return;

        var cts = new CancellationTokenSource();
        if (!_inFlight.TryAdd(session.Id, cts)) { cts.Dispose(); return; } // one at a time per session
        _ = BriefTurnAsync(session, cts);
    }

    private async Task BriefTurnAsync(Session session, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            // The yellow window opens AT turn end, not after the settle delay (issue #192):
            // until the brief lands we do not know whether the session needs you, so the
            // badge must not flash red for the settle+read duration. The early-skip paths
            // below close the window again (None / Briefed) within the settle delay.
            session.SetBriefingState(BriefingState.Briefing);

            await Task.Delay(_settleDelay, ct);

            var widgets = _transcriptReader(session);
            if (widgets is null || widgets.Count == 0)
            {
                FileLog.Write($"[TurnBriefOrchestrator] sid={session.Id}: no transcript yet; skipping (boot gotcha)");
                session.SetBriefingState(BriefingState.None);
                return;
            }

            var prior = _store.Latest(session.Id);
            if (prior is not null && prior.TurnNumber == widgets.Count && !prior.Degraded)
            {
                // Already briefed this exact turn at full quality.
                session.SetBriefingState(BriefingState.Briefed);
                return;
            }

            var package = TurnPackageBuilder.Build(
                session.Id, widgets, _screenReader(session), prior, _store.List(session.Id));

            FileLog.Write($"[TurnBriefOrchestrator] briefing sid={session.Id} turn={package.TurnCount}");

            var brief = await _generator.GenerateAsync(package, ct);

            // Degrade tier: a failed wingman read still leaves an honest marker.
            if (brief is null)
                brief = await new StubTurnBriefGenerator().GenerateAsync(package, ct);

            ct.ThrowIfCancellationRequested();

            // Staleness: if the transcript moved on while we were reading, discard.
            var nowWidgets = _transcriptReader(session);
            if (nowWidgets is not null && nowWidgets.Count != package.TurnCount)
            {
                FileLog.Write($"[TurnBriefOrchestrator] sid={session.Id}: turn advanced ({package.TurnCount} -> {nowWidgets.Count}); discarding brief");
                session.SetBriefingState(BriefingState.None);
                return;
            }

            if (brief is not null)
            {
                _store.Append(session.Id, brief);
                session.LatestBriefRailLine = brief.NeedsYou?.RailLine;
                session.SetBriefingState(brief.Degraded ? BriefingState.Failed : BriefingState.Briefed);
                FileLog.Write($"[TurnBriefOrchestrator] stored sid={session.Id} turn={brief.TurnNumber} model={brief.Model} railLine={brief.NeedsYou?.RailLine ?? "(none)"}");
            }
        }
        catch (OperationCanceledException)
        {
            session.SetBriefingState(BriefingState.None);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnBriefOrchestrator] BriefTurnAsync FAILED: sid={session.Id} {ex.Message}");
            session.SetBriefingState(BriefingState.Failed);
        }
        finally
        {
            _inFlight.TryRemove(session.Id, out _);
            cts.Dispose();
        }
    }

    private static List<TurnWidgetDto>? TryParseTranscript(Session session)
    {
        if (string.IsNullOrEmpty(session.ClaudeSessionId)) return null;
        try
        {
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            if (!File.Exists(jsonl)) return null;
            var messages = StreamMessageParser.ParseFile(jsonl);
            return WidgetBuilder.BuildFromMessages(messages);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnBriefOrchestrator] transcript parse failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The current screen as plain text rows (mechanical tag-strip of the parsed
    /// grid - display plumbing, not interpretation).</summary>
    private static string ReadScreenTail(Session session)
    {
        try
        {
            var (_, gridHtml, _) = session.GetHtmlSnapshotSplit();
            if (string.IsNullOrEmpty(gridHtml)) return "";
            var rows = gridHtml
                .Split("<div class=\"line\">", StringSplitOptions.RemoveEmptyEntries)
                .Select(r => System.Net.WebUtility.HtmlDecode(
                    Regex.Replace(r, "<[^>]+>", "", RegexOptions.None, TimeSpan.FromMilliseconds(100))).TrimEnd())
                .Where(t => !string.IsNullOrWhiteSpace(t));
            return string.Join("\n", rows);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnBriefOrchestrator] screen snapshot failed: {ex.Message}");
            return "";
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var cts in _inFlight.Values)
        {
            try { cts.Cancel(); } catch { /* teardown */ }
        }
        _inFlight.Clear();
    }
}
