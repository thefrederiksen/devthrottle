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

    /// <summary>Generator identity recorded on briefs produced by this agent.</summary>
    public const string GeneratorId = "gateway-brain";

    private readonly GatewayTurnBriefStore _store;
    private readonly Func<CancellationToken, Task<IAgentBrain>> _brainProvider;
    private readonly Func<Task> _brainRecover;
    private readonly Func<string, string, CancellationToken, Task<TurnsResponse?>> _fetchTurns;
    private readonly Func<string, string, CancellationToken, Task<string>> _fetchScreenTail;
    private readonly TimeSpan _settleDelay;

    /// <summary>
    /// Fired after a brief is stored: (sessionId, directorEndpoint, brief). The host wires
    /// the assessedState derivation + Director push-down here (issue #186); the agent
    /// itself stays a stamping machine.
    /// </summary>
    public Action<string, string, TurnBriefDto>? OnBriefStored { get; set; }

    private readonly ConcurrentDictionary<string, string> _pending = new();   // sid -> endpoint
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private int _recoveryInFlight;
    private bool _disposed;

    public GatewayTurnBriefStore Store => _store;

    /// <summary>Production wiring: the Gateway's brain supervisor + Director REST client.</summary>
    public GatewayTurnBriefAgent(BrainSupervisor brain, GatewayTurnBriefStore store, DirectorEndpointClient client)
        : this(
            store,
            (brain ?? throw new ArgumentNullException(nameof(brain))).GetAsync,
            () => brain.RestartAsync(),
            (ep, sid, ct) => (client ?? throw new ArgumentNullException(nameof(client))).GetTurnsAsync(ep, sid, ct),
            async (ep, sid, ct) => (await client.GetBufferAsync(ep, sid, lines: 80, ct: ct))?.Text ?? "")
    {
    }

    /// <summary>Seam constructor for tests: fake brain, fake fetchers, fast settle.</summary>
    internal GatewayTurnBriefAgent(
        GatewayTurnBriefStore store,
        Func<CancellationToken, Task<IAgentBrain>> brainProvider,
        Func<Task> brainRecover,
        Func<string, string, CancellationToken, Task<TurnsResponse?>> fetchTurns,
        Func<string, string, CancellationToken, Task<string>> fetchScreenTail,
        TimeSpan? settleDelay = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _brainProvider = brainProvider;
        _brainRecover = brainRecover;
        _fetchTurns = fetchTurns;
        _fetchScreenTail = fetchScreenTail;
        _settleDelay = settleDelay ?? SettleDelay;
        _worker = Task.Run(WorkerLoopAsync);
    }

    /// <summary>True when the pipeline is disabled via CC_TURNBRIEFS=0 (the kill switch).</summary>
    public static bool Disabled =>
        Environment.GetEnvironmentVariable("CC_TURNBRIEFS") == "0";

    /// <summary>Coarse pipeline state for one session, for the read endpoints: "Briefing"
    /// while queued or in flight, "Briefed" once the store has anything, else "None".</summary>
    public string BriefingStateFor(string sessionId)
    {
        if (_pending.ContainsKey(sessionId) || _inFlight.ContainsKey(sessionId)) return "Briefing";
        return _store.Latest(sessionId) is not null ? "Briefed" : "None";
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
        var package = TurnPackageBuilder.Build(
            Guid.Parse(sid), widgets, screenTail, prior, _store.List(sid));

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

            brief = TurnBriefContract.ParseAndValidate(ask.Text, package, GeneratorId);
            if (brief is null)
                FileLog.Write($"[GatewayTurnBriefAgent] sid={package.SessionId}: REJECTED by validation; degrading");
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
