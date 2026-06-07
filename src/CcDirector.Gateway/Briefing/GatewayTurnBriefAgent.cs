using System.Collections.Concurrent;
using CcDirector.AgentBrain;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.HostedAgent;

namespace CcDirector.Gateway.Briefing;

/// <summary>
/// The warm-brain stamping machine (issue #185, locked in #173): ONE queued agent for the
/// whole fleet. Each turn end pulls the truth from the owning Director over existing REST
/// (/turns widgets + screen tail - zero Director changes), assembles the TurnPackage with
/// the same pure builder the Director uses, asks the Gateway's warm brain with the frozen
/// v2.3 contract prompt, validates mechanically, stores the brief append-only, and /clear's
/// the brain - every brief starts from empty context. A stamping machine, not the wingman.
///
/// Queue semantics (locked): per-session coalescing - a newer turn end for a session simply
/// keeps that session marked dirty (one slot per session, never a backlog). Watch-cancel
/// preserved - the session re-entering Working cancels its in-flight generation and clears
/// its pending slot.
///
/// Degrade tier: a failed generation stores the honest stub marker, never invented content.
/// A dead brain triggers RestartAsync (the one recovery verb) and degrades the current brief.
///
/// Prompt changes land HERE (TurnBriefContract) and reach the fleet on a Gateway restart -
/// no Director deployment anywhere. Kill switch: CC_TURNBRIEFS=0.
/// </summary>
public sealed class GatewayTurnBriefAgent : IDisposable
{
    /// <summary>Settle delay after turn end before reading the transcript (the detector can
    /// fire before claude flushes the JSONL - measured during the example captures).</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>Per-brief generation budget (matches the Director path's 150s process timeout).</summary>
    public static readonly TimeSpan GenerationTimeout = TimeSpan.FromSeconds(150);

    /// <summary>Base generator identity. Production appends the pinned brain model
    /// (issue #204) - e.g. "gateway-brain/opus" - so every stored brief records which
    /// model actually wrote it.</summary>
    public const string GeneratorId = "gateway-brain";

    private readonly string _generatorId;

    private readonly GatewayTurnBriefStore _store;
    private readonly Func<CancellationToken, Task<IAgentBrain>> _brainProvider;
    private readonly Func<Task> _brainRecover;
    private readonly Func<string, string, CancellationToken, Task<TurnsResponse?>> _fetchTurns;
    private readonly Func<string, string, CancellationToken, Task<string>> _fetchScreenTail;
    private readonly Func<string, string, CancellationToken, Task<string?>> _fetchRepoPath;
    private readonly Func<string, string, CancellationToken, Task<Core.Sessions.SessionType>> _fetchSessionType;
    private readonly TimeSpan _settleDelay;

    /// <summary>
    /// Fired after a brief is stored: (sessionId, directorEndpoint, brief). The host wires
    /// the assessedState derivation + Director push-down here (issue #186); the agent
    /// itself stays a stamping machine.
    /// </summary>
    public Action<string, string, TurnBriefDto>? OnBriefStored { get; set; }

    private readonly ConcurrentDictionary<string, string> _pending = new();   // sid -> endpoint
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new();

    // Explain deep dives (issue #217): user-initiated, so they outrank background
    // stamping in the drain order and are NOT watch-cancelled - a session that starts
    // working again does not invalidate its story the way it invalidates a turn brief.
    private readonly ConcurrentDictionary<string, (string Endpoint, DateTime RequestedAtUtc)> _explainPending = new();
    private readonly ConcurrentDictionary<string, byte> _explainInFlight = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private int _recoveryInFlight;
    private int _consecutiveRejections;
    private bool _disposed;

    /// <summary>Consecutive validation rejections before the brain is presumed poisoned
    /// and restarted (the 2026-06-07 auth-banner outage: a broken brain REPLIES, so only
    /// a rejection streak reveals it). 3 = tolerant of a one-off model hiccup, still
    /// bounds a real outage to ~3 stub briefs instead of hours of them.</summary>
    public const int PoisonedBrainRejectionThreshold = 3;

    public GatewayTurnBriefStore Store => _store;

    /// <summary>Production wiring: the Gateway's brain supervisor + Director REST client.</summary>
    public GatewayTurnBriefAgent(BrainSupervisor brain, GatewayTurnBriefStore store, DirectorEndpointClient client, string? generatorId = null)
        : this(
            store,
            (brain ?? throw new ArgumentNullException(nameof(brain))).GetAsync,
            () => brain.RestartAsync(),
            (ep, sid, ct) => (client ?? throw new ArgumentNullException(nameof(client))).GetTurnsAsync(ep, sid, ct),
            async (ep, sid, ct) => (await client.GetBufferAsync(ep, sid, lines: 80, ct: ct))?.Text ?? "",
            fetchRepoPath: async (ep, sid, ct) => (await client.GetSessionAsync(ep, sid, ct))?.RepoPath,
            fetchSessionType: async (ep, sid, ct) =>
                Enum.TryParse<Core.Sessions.SessionType>((await client.GetSessionAsync(ep, sid, ct))?.Type, out var t)
                    ? t : Core.Sessions.SessionType.Implement,
            generatorId: generatorId)
    {
    }

    /// <summary>Seam constructor for tests: fake brain, fake fetchers, fast settle.</summary>
    internal GatewayTurnBriefAgent(
        GatewayTurnBriefStore store,
        Func<CancellationToken, Task<IAgentBrain>> brainProvider,
        Func<Task> brainRecover,
        Func<string, string, CancellationToken, Task<TurnsResponse?>> fetchTurns,
        Func<string, string, CancellationToken, Task<string>> fetchScreenTail,
        Func<string, string, CancellationToken, Task<string?>>? fetchRepoPath = null,
        Func<string, string, CancellationToken, Task<Core.Sessions.SessionType>>? fetchSessionType = null,
        TimeSpan? settleDelay = null,
        string? generatorId = null)
    {
        _generatorId = string.IsNullOrWhiteSpace(generatorId) ? GeneratorId : generatorId;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _brainProvider = brainProvider;
        _brainRecover = brainRecover;
        _fetchTurns = fetchTurns;
        _fetchScreenTail = fetchScreenTail;
        // Tests that don't care about @file substitution get the no-repo answer: the
        // resolver then keeps prompts verbatim (same behavior as a remote Director).
        _fetchRepoPath = fetchRepoPath ?? ((_, _, _) => Task.FromResult<string?>(null));
        // Issue #236: untyped fetch (tests, old callers) defaults to Implement - no mission
        // clause, identical brief to before.
        _fetchSessionType = fetchSessionType ?? ((_, _, _) => Task.FromResult(Core.Sessions.SessionType.Implement));
        _settleDelay = settleDelay ?? SettleDelay;
        _worker = Task.Run(WorkerLoopAsync);
    }

    /// <summary>True when the pipeline is disabled via CC_TURNBRIEFS=0 (the kill switch).</summary>
    public static bool Disabled =>
        Environment.GetEnvironmentVariable("CC_TURNBRIEFS") == "0";

    /// <summary>Coarse pipeline state for one session, for the read endpoints: "Explaining"
    /// while a user-initiated deep dive is queued or in flight (issue #217 - it outranks
    /// background stamping for display), "Briefing" while a turn brief is queued or in
    /// flight, "Briefed" once the store has anything, else "None".</summary>
    public string BriefingStateFor(string sessionId)
    {
        if (_explainPending.ContainsKey(sessionId) || _explainInFlight.ContainsKey(sessionId)) return "Explaining";
        if (_pending.ContainsKey(sessionId) || _inFlight.ContainsKey(sessionId)) return "Briefing";
        return _store.Latest(sessionId) is not null ? "Briefed" : "None";
    }

    /// <summary>The "I am lost - explain" button was pressed (issue #217): queue a
    /// session-level deep dive (coalescing - one slot per session, a second press while
    /// one is pending is a no-op).</summary>
    public bool RequestExplain(string sessionId, string directorEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directorEndpoint);
        if (_disposed) return false;
        var isNew = !_explainPending.ContainsKey(sessionId) && !_explainInFlight.ContainsKey(sessionId);
        if (isNew)
        {
            _explainPending[sessionId] = (directorEndpoint, DateTime.UtcNow);
            _signal.Release();
        }
        FileLog.Write($"[GatewayTurnBriefAgent] explain requested sid={sessionId} (coalesced={!isNew})");
        return true;
    }

    /// <summary>A turn ended: mark the session dirty (coalescing - one slot per session).</summary>
    public void OnTurnEnd(TurnEndSignal signal)
    {
        if (_disposed) return;
        var isNew = !_pending.ContainsKey(signal.SessionId);
        _pending[signal.SessionId] = signal.DirectorEndpoint;
        if (isNew) _signal.Release();
        FileLog.Write($"[GatewayTurnBriefAgent] queued sid={signal.SessionId} (coalesced={!isNew})");
    }

    /// <summary>Watch-cancel: the user replied (or the agent resumed) - the brief is moot.</summary>
    public void OnSessionWorking(string sessionId)
    {
        if (_disposed) return;
        _pending.TryRemove(sessionId, out _);
        if (_inFlight.TryGetValue(sessionId, out var cts))
        {
            FileLog.Write($"[GatewayTurnBriefAgent] watch-cancel: sid={sessionId} re-entered Working");
            cts.Cancel();
        }
    }

    private async Task WorkerLoopAsync()
    {
        var lifetime = _lifetime.Token;
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(lifetime);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // User-initiated explains drain FIRST (issue #217): someone is sitting at the
            // screen waiting for them, while turn briefs are background stamping.
            while (!lifetime.IsCancellationRequested && TryTakeExplain(out var esid, out var eEndpoint, out var requestedAt))
            {
                try
                {
                    await ExplainOneAsync(esid, eEndpoint, requestedAt, lifetime);
                }
                catch (OperationCanceledException)
                {
                    FileLog.Write($"[GatewayTurnBriefAgent] explain cancelled: sid={esid}");
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayTurnBriefAgent] ExplainOneAsync FAILED: sid={esid} {ex.Message}");
                }
                finally
                {
                    _explainInFlight.TryRemove(esid, out _);
                }
            }

            // One signal may cover several dirty sessions (or none after watch-cancel);
            // drain everything currently marked dirty, one at a time - the ONE brain.
            while (!lifetime.IsCancellationRequested && TryTakePending(out var sid, out var endpoint))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                _inFlight[sid] = cts;
                try
                {
                    await BriefOneAsync(sid, endpoint, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    FileLog.Write($"[GatewayTurnBriefAgent] brief cancelled: sid={sid}");
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayTurnBriefAgent] BriefOneAsync FAILED: sid={sid} {ex.Message}");
                }
                finally
                {
                    _inFlight.TryRemove(sid, out _);
                }
            }
        }
    }

    private bool TryTakePending(out string sessionId, out string endpoint)
    {
        foreach (var sid in _pending.Keys)
        {
            if (_pending.TryRemove(sid, out var ep))
            {
                sessionId = sid;
                endpoint = ep;
                return true;
            }
        }
        sessionId = "";
        endpoint = "";
        return false;
    }

    private bool TryTakeExplain(out string sessionId, out string endpoint, out DateTime requestedAtUtc)
    {
        foreach (var sid in _explainPending.Keys)
        {
            // Mark in-flight BEFORE removing from pending: BriefingStateFor must never
            // observe the gap and report the deep dive as finished while it runs.
            _explainInFlight[sid] = 1;
            if (_explainPending.TryRemove(sid, out var req))
            {
                sessionId = sid;
                endpoint = req.Endpoint;
                requestedAtUtc = req.RequestedAtUtc;
                return true;
            }
            _explainInFlight.TryRemove(sid, out _);
        }
        sessionId = "";
        endpoint = "";
        requestedAtUtc = default;
        return false;
    }

    /// <summary>
    /// One deep-dive cycle (issue #217): assemble the ExplainPackage from the session's
    /// own stored story + live transcript + screen, ask the warm brain with the explain
    /// contract, validate mechanically, store append-only. Failure stores the honest
    /// degraded stub - the user pressed a button and must get an answer, never silence.
    /// </summary>
    private async Task ExplainOneAsync(string sid, string endpoint, DateTime requestedAtUtc, CancellationToken ct)
    {
        var turns = await _fetchTurns(endpoint, sid, ct);
        var widgets = turns?.Widgets ?? new List<TurnWidgetDto>();
        var screenTail = await _fetchScreenTail(endpoint, sid, ct);

        // Reuse the turn-package builder for the transcript span (prior=null = the full
        // recent widget budget), then lift the explain-specific material around it.
        var briefs = _store.List(sid);
        var turnPackage = widgets.Count > 0
            ? TurnPackageBuilder.Build(Guid.Parse(sid), widgets, screenTail, priorBrief: null, briefs)
            : null;

        // Bare @file prompts (dictation) carry no readable words - substitute the file
        // content so the deep dive sees what the user actually said (issue #208).
        if (turnPackage is not null && DictatedPromptResolver.NeedsResolution(turnPackage))
            turnPackage = DictatedPromptResolver.Resolve(turnPackage, await _fetchRepoPath(endpoint, sid, ct));

        var package = new ExplainPackage(
            Guid.Parse(sid),
            turnPackage?.TurnCount ?? 0,
            turnPackage?.FirstUserPrompt,
            ExplainContract.BuildStoryLines(briefs),
            turnPackage?.TranscriptDelta ?? "",
            turnPackage?.ScreenTail ?? "");

        FileLog.Write($"[GatewayTurnBriefAgent] explaining sid={sid} turn={package.TurnCount} storyLines={package.StoryLines.Count}");

        ExplainReportDto? report = null;
        var prompt = ExplainContract.BuildPrompt(package);
        var cleared = false;
        try
        {
            var brain = await _brainProvider(ct);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(GenerationTimeout);
            AskResult ask;
            try
            {
                ask = await brain.AskAsync(prompt, budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"warm-brain explain did not finish within {GenerationTimeout.TotalSeconds}s");
            }

            // Same stamping-machine hygiene as briefs: clear BEFORE validation.
            await brain.ClearAsync(CancellationToken.None);
            cleared = true;

            report = ExplainContract.ParseAndValidate(ask.Text, package, _generatorId, requestedAtUtc);
            if (report is null)
                FileLog.Write($"[GatewayTurnBriefAgent] explain sid={sid}: REJECTED by validation; degrading");
        }
        catch (OperationCanceledException)
        {
            if (!cleared) FireBrainRecovery("explain cancelled mid-ask");
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTurnBriefAgent] warm-brain explain FAILED: {ex.Message}; degrading + recovering the brain");
            if (!cleared) FireBrainRecovery(ex.Message);
        }

        // Honest stub on failure: the user pressed a button - silence is not an answer.
        report ??= new ExplainReportDto
        {
            SessionId = sid,
            TurnNumber = package.TurnCount,
            RequestedAtUtc = requestedAtUtc,
            GeneratedAtUtc = DateTime.UtcNow,
            Model = _generatorId,
            Degraded = true,
            WhatHappened = "The wingman could not read this session right now (generation failed or timed out).",
            WhatWeDid = new List<string> { "No deep dive was produced - this is the honest failure marker, not a summary." },
            WhatNext = "Press the explain button again in a minute; if it keeps failing, check the Gateway brain in Settings.",
        };

        _store.AppendExplain(sid, report);
        FileLog.Write($"[GatewayTurnBriefAgent] explain stored sid={sid} turn={report.TurnNumber} degraded={report.Degraded}");
    }

    private async Task BriefOneAsync(string sid, string endpoint, CancellationToken ct)
    {
        // Settle: the detector can fire before claude flushes the JSONL.
        await Task.Delay(_settleDelay, ct);

        var turns = await _fetchTurns(endpoint, sid, ct);
        if (turns?.Widgets is not { Count: > 0 } widgets)
        {
            FileLog.Write($"[GatewayTurnBriefAgent] sid={sid}: no transcript ({turns?.Status ?? "unreachable"}); skipping");
            return;
        }

        var prior = _store.Latest(sid);
        if (prior is not null && prior.TurnNumber == widgets.Count && !prior.Degraded)
            return; // already briefed this exact turn at full quality

        var screenTail = await _fetchScreenTail(endpoint, sid, ct);
        // Issue #236: the type drives the brief's per-type mission clause (a BugReport
        // session whose issue is filed should suggest close).
        var sessionType = await _fetchSessionType(endpoint, sid, ct);
        var package = TurnPackageBuilder.Build(
            Guid.Parse(sid), widgets, screenTail, prior, _store.List(sid), sessionType);

        // Bare @file prompts (dictation drops "@.temp/input_*.txt") render YOU ASKED as
        // an opaque path - substitute the file content BEFORE the package is stored, so
        // the brain, the saved package, and the review corpus all see the user's words
        // (issue #208, review rounds 3-5's last recurring defect). Repo path is fetched
        // only when a bare reference is actually present.
        if (DictatedPromptResolver.NeedsResolution(package))
            package = DictatedPromptResolver.Resolve(package, await _fetchRepoPath(endpoint, sid, ct));

        _store.SavePackage(sid, package);

        FileLog.Write($"[GatewayTurnBriefAgent] briefing sid={sid} turn={package.TurnCount}");
        var brief = await GenerateAsync(package, ct);

        ct.ThrowIfCancellationRequested();

        // Staleness: if the transcript moved on while the brain was reading, discard.
        var nowTurns = await _fetchTurns(endpoint, sid, ct);
        if (nowTurns?.Widgets is { } nowWidgets && nowWidgets.Count != package.TurnCount)
        {
            FileLog.Write($"[GatewayTurnBriefAgent] sid={sid}: turn advanced ({package.TurnCount} -> {nowWidgets.Count}); discarding brief");
            return;
        }

        _store.Append(sid, brief);
        FileLog.Write($"[GatewayTurnBriefAgent] stored sid={sid} turn={brief.TurnNumber} model={brief.Model} railLine={brief.NeedsYou?.RailLine ?? "(none)"}");
        OnBriefStored?.Invoke(sid, endpoint, brief);
    }

    /// <summary>
    /// One warm-brain stamping cycle: ask -> validate -> ALWAYS /clear (empty context per
    /// brief). Generation failure degrades to the stub; a dead brain additionally fires
    /// RestartAsync so the NEXT brief gets a healthy brain.
    /// </summary>
    private async Task<TurnBriefDto> GenerateAsync(TurnPackage package, CancellationToken ct)
    {
        var prompt = TurnBriefContract.BuildPrompt(package);

        TurnBriefDto? brief = null;
        var cleared = false;
        try
        {
            var brain = await _brainProvider(ct);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(GenerationTimeout);
            AskResult ask;
            try
            {
                ask = await brain.AskAsync(prompt, budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"warm-brain brief did not finish within {GenerationTimeout.TotalSeconds}s");
            }

            // Stamping-machine hygiene: clear BEFORE validation so a rejected brief can
            // never leak its turn into the next session's context.
            await brain.ClearAsync(CancellationToken.None);
            cleared = true;

            brief = TurnBriefContract.ParseAndValidate(ask.Text, package, _generatorId);
            if (brief is null)
            {
                FileLog.Write($"[GatewayTurnBriefAgent] sid={package.SessionId}: REJECTED by validation; degrading");

                // POISONED-BRAIN DETECTION (2026-06-07 outage, issue #208): a brain whose
                // auth/subscription broke REPLIES successfully - with an error banner, not
                // JSON - so the exception path never fires and it stamps stubs forever
                // (6 hours live). Consecutive validation rejections are the mechanical
                // signal: one is a model hiccup; a streak means the brain itself answers
                // garbage. Restart it - the one recovery verb, same as a dead brain.
                if (Interlocked.Increment(ref _consecutiveRejections) >= PoisonedBrainRejectionThreshold)
                {
                    Interlocked.Exchange(ref _consecutiveRejections, 0);
                    FireBrainRecovery($"{PoisonedBrainRejectionThreshold} consecutive validation rejections - brain presumed poisoned");
                }
            }
            else
            {
                Interlocked.Exchange(ref _consecutiveRejections, 0);
            }
        }
        catch (OperationCanceledException)
        {
            // Watch-cancel or shutdown: the partial context dies with the recovery below.
            if (!cleared) FireBrainRecovery("ask cancelled mid-turn");
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTurnBriefAgent] warm-brain FAILED: {ex.Message}; degrading + recovering the brain");
            if (!cleared) FireBrainRecovery(ex.Message);
        }

        if (brief is not null) return brief;

        // Degrade tier: a failed read still leaves an honest marker (never invented content).
        var stub = await new StubTurnBriefGenerator().GenerateAsync(package, CancellationToken.None);
        return stub!;
    }

    /// <summary>The one recovery verb, fire-and-forget: a wedged/dirty brain is replaced,
    /// not debugged. The current brief already degraded; this is about the NEXT one.
    /// Concurrent failures collapse into ONE restart - several asks failing against the
    /// same dying brain must not queue a restart each (the live #185 restart storm).</summary>
    private void FireBrainRecovery(string reason)
    {
        if (Interlocked.CompareExchange(ref _recoveryInFlight, 1, 0) != 0)
        {
            FileLog.Write($"[GatewayTurnBriefAgent] brain recovery already in flight; collapsing ({reason})");
            return;
        }
        FileLog.Write($"[GatewayTurnBriefAgent] recovering the brain (RestartAsync): {reason}");
        _ = Task.Run(async () =>
        {
            try { await _brainRecover(); }
            catch (Exception ex) { FileLog.Write($"[GatewayTurnBriefAgent] brain recovery FAILED: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _recoveryInFlight, 0); }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        foreach (var cts in _inFlight.Values)
        {
            try { cts.Cancel(); } catch { /* teardown */ }
        }
        try { _worker.Wait(TimeSpan.FromSeconds(5)); } catch { /* teardown */ }
        _signal.Dispose();
        _lifetime.Dispose();
    }
}
