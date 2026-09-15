using System.Net;
using System.Net.Http;
using CcDirector.Core.Configuration;
using CcDirector.Core.Machine;
using CcDirector.Core.Network;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)

namespace CcDirector.ControlApi;

/// <summary>
/// Issue #1176 (Phase 1a): the Director's outbound client for the Gateway's DirectorHub. It dials the
/// Gateway (never the other way), authenticates with the same token the HTTP client uses, and pushes this
/// Director's session state UP the stream:
///
///   - on connect AND on every reconnect it sends <c>Hello</c> then a full <c>PushSnapshot</c> (the new
///     connection makes the snapshot authoritative at the Gateway, so a Director restart reseeds cleanly);
///   - <see cref="NotifyDelta"/> pushes one changed session; <see cref="NotifyRemove"/> pushes a removal.
///
/// It runs ALONGSIDE the existing <see cref="GatewayClient"/> in Phase 1a (additive): the heartbeat/
/// doorbell stay as the reconcile floor. It is inert unless the config has a Gateway URL and
/// <see cref="GatewayConfig.StreamMode"/> is on, so a Director with stream mode off behaves exactly as today.
///
/// Sends are best-effort and fire-and-forget: a dropped delta is harmless because the next snapshot (on
/// reconnect) re-establishes the full truth, mirroring the doorbell/heartbeat resilience model.
/// </summary>
public sealed class GatewayStreamClient : IAsyncDisposable
{
    private readonly GatewayConfig _config;
    private readonly string _directorId;
    private readonly string _version;
    private readonly Func<List<SessionDto>> _snapshot;

    /// <summary>
    /// The repository/worktree snapshot provider (issue devthrottle_internal#510, phase C). Null in
    /// older callers and tests: repo pushes are then simply skipped. Pushed as full snapshots only -
    /// repositories change slowly, so there is no delta path.
    /// </summary>
    private readonly Func<List<RepoStatusDto>>? _repoSnapshot;
    private readonly Func<DirectorCommand, Task<DirectorCommandResult>>? _commandDispatcher;
    private readonly TimeSpan _rePushInterval;

    /// <summary>
    /// Gateway Cleanup mission (tunnel-only): the connectivity indicator. The tunnel IS the Gateway
    /// connection now, so its up/down state IS the desktop's green/yellow. Null in tests/older callers.
    /// </summary>
    private readonly GatewayConnectionMonitor? _monitor;

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (up-stream): the handler for the four connection-bound stream verbs.
    /// Null when no <see cref="SessionManager"/> was supplied (older callers and tests that never drive a
    /// stream verb); in that case a stream verb returns a typed Error, exactly as an absent dispatcher does.
    /// </summary>
    private readonly DirectorUpStreamHandler? _upStreamHandler;

    // Gateway Cleanup mission (tunnel-only): the Hello now carries this Director's identity so the Gateway
    // registers it from the stream (HTTP register is gone). Captured once at construction.
    private readonly DateTime _startedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>
    /// devthrottle_internal#1176: provider of this instance's user-editable display name, consulted on
    /// EVERY reseed (not captured once) so a rename lands fleet-wide on the next ~10s Hello without a
    /// Director restart. Null in tests/older callers - the Hello then carries an empty name, which the
    /// Gateway's merge guard treats as "no statement" rather than an erase.
    /// </summary>
    private readonly Func<string?>? _displayName;

    /// <summary>
    /// Remove-the-network-port phase 1b: the registrations for every live session that holds a Gateway
    /// session key, re-sent on every reseed so a key survives a tunnel drop, a Gateway restart, and a
    /// Director reconnect. Null in tests and older callers - session keys are then simply never registered,
    /// which is the same state as a Director that has not been given the feature.
    /// </summary>
    private readonly Func<List<SessionKeyRegistration>>? _sessionKeys;

    /// <summary>Sessions whose keys were reaped here but not yet refused by the Gateway. Replayed on
    /// every reseed, so a revocation that could not be delivered is not simply lost.</summary>
    private readonly Func<List<string>>? _pendingRevocations;

    /// <summary>Called with a session id once the GATEWAY has accepted its revocation. Only an accepted
    /// invoke settles the debt - a logged-and-dropped failure must leave it owed.</summary>
    private readonly Action<string>? _onRevocationConfirmed;

    private HubConnection? _connection;
    private long _sequence;
    private int _started;
    private int _rePushInFlight;
    private Timer? _rePushTimer;
    private volatile bool _disposed;

    /// <summary>Reconnect backoff between long-outage restart attempts once auto-reconnect has given up.</summary>
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    /// <param name="commandDispatcher">
    /// Issue #1177 (Phase 1): handler for commands the Gateway sends DOWN this stream. When null (or in
    /// Phase 1a callers that pre-date it), the Director declines any command with an Error result; the
    /// Gateway then falls back to its HTTP command path, so behaviour is unchanged.
    /// </param>
    /// <param name="rePushInterval">
    /// Issue #1177 (Phase 4a): how often, while connected, the Director re-pushes its full snapshot so a
    /// QUIET session's pushed cache never ages past the Gateway's stale window. Null uses half the default
    /// stale window (comfortably under it). A test seam; production passes null.
    /// </param>
    /// <param name="sessionManager">
    /// Issue: Gateway Cleanup mission, Phase 0 (up-stream). When supplied, enables the four connection-bound
    /// stream verbs (open-terminal-stream / read-file / screenshot-file / close-stream) by building the
    /// up-stream handler, whose producer sends frames up this same connection. Null (older callers, tests)
    /// leaves stream verbs declined with a typed Error, so behaviour is unchanged for them.
    /// </param>
    public GatewayStreamClient(GatewayConfig config, string directorId, string version, Func<List<SessionDto>> snapshot,
        Func<DirectorCommand, Task<DirectorCommandResult>>? commandDispatcher = null,
        TimeSpan? rePushInterval = null,
        SessionManager? sessionManager = null,
        GatewayConnectionMonitor? monitor = null,
        Func<List<RepoStatusDto>>? repoSnapshot = null,
        Func<string?>? displayName = null,
        Func<List<SessionKeyRegistration>>? sessionKeys = null,
        Func<List<string>>? pendingRevocations = null,
        Action<string>? onRevocationConfirmed = null,
        Action<GatewayCapabilities>? onHello = null)
    {
        _onHello = onHello;
        _sessionKeys = sessionKeys;
        _pendingRevocations = pendingRevocations;
        _onRevocationConfirmed = onRevocationConfirmed;
        _displayName = displayName;
        _repoSnapshot = repoSnapshot;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _directorId = string.IsNullOrWhiteSpace(directorId) ? throw new ArgumentException("directorId is required", nameof(directorId)) : directorId;
        _version = version ?? "";
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _commandDispatcher = commandDispatcher;
        _monitor = monitor;
        _rePushInterval = rePushInterval ?? TimeSpan.FromSeconds(GatewayConfig.DefaultStreamStaleAfterSeconds / 2.0);
        _upStreamHandler = sessionManager is null
            ? null
            : new DirectorUpStreamHandler(sessionManager, (streamId, frames) => _connection!.SendAsync("StreamUp", streamId, frames));
    }

    /// <summary>
    /// True when a Gateway is configured. Gateway Cleanup mission (tunnel-only): the streamMode gate is
    /// GONE - the tunnel is the ONLY Gateway connection, so a configured Director always dials it. When
    /// no gateway.url is set (local-only) this is false and <see cref="Start"/> is a no-op.
    /// </summary>
    public bool IsEnabled => _config.IsEnabled;

    /// <summary>Start dialing the Gateway. Idempotent; inert when <see cref="IsEnabled"/> is false.</summary>
    public void Start()
    {
        if (!IsEnabled) return;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        FileLog.Write($"[GatewayStreamClient] Start: dialing {_config.Url} for director {_directorId}");
        _ = SuperviseAsync();

        // Issue #1177 (Phase 4a): keep the Gateway's pushed cache fresh for a QUIET session. A portless
        // (remotely-unreachable) Director has no HTTP pull floor, so once the last push ages past the
        // Gateway's stale window its sessions vanish from the roster and can no longer be located. A periodic
        // full re-push - comfortably under that window - keeps TryGetFresh/TryLocate fresh. Best-effort, only
        // while connected. Armed only here (inside the IsEnabled guard), so it is inert when stream mode off.
        // Seed the lateness baseline at the moment the timer is ARMED, so the FIRST tick is measured
        // too (issue #2818). Without this a Director that comes up on a machine already under pressure
        // reports its first, worst tick as on time.
        Interlocked.Exchange(ref _lastRePushTickUtcTicks, DateTime.UtcNow.Ticks);
        _rePushTimer = new Timer(_ => RePushTick(), null, _rePushInterval, _rePushInterval);
    }

    // Timer callback (a boundary): re-push the full snapshot so a quiet session's pushed cache stays fresh.
    // Skips when disposed, when disconnected, or when a prior re-push is still in flight (no overlap).
    //
    // Issue #2188: every skip and every slow push is now LOGGED. This tick is the heartbeat that keeps the
    // Gateway's pushed cache fresh, and a missed one is what makes an entire Director's sessions read as
    // non-existent - on 2026-07-26 two missed ticks refused every action on fourteen live sessions for about
    // ten seconds. Both skip paths used to return in silence, so the single event that caused the outage
    // left no trace at all and the investigation had to infer it from a thirty-second hole between two
    // success lines. An event that can take the fleet offline must never be invisible.
    private void RePushTick()
    {
        if (_disposed) return;

        // HOW LATE WAS THIS CALLBACK (issue #2818). Measured FIRST, before any path below can return,
        // and measured from the SCHEDULE rather than from the moment this method began running. Every
        // other timing in this class starts counting after the callback is already executing, so the
        // failure that takes a Director's sessions off the air is invisible to all of them: a tick
        // queued for longer than the staleness window behind blocked session workers can start, find a
        // healthy connection and an idle slot, push in eighty milliseconds, and log nothing but success
        // - while the Gateway had already stopped believing in this Director's sessions.
        var tickAt = DateTime.UtcNow;
        var previousTicks = Interlocked.Exchange(ref _lastRePushTickUtcTicks, tickAt.Ticks);
        var previousTick = previousTicks == 0 ? (DateTime?)null : new DateTime(previousTicks, DateTimeKind.Utc);
        var lateness = TimerLateness.Of(_rePushInterval, previousTick, tickAt);

        // Elapsed since the last FULL snapshot. Deliberately NOT called the Gateway's cache age: an
        // accepted delta refreshes that cache too and is not counted here, so this is an upper bound,
        // and TimerLateness.Describe reports it as one rather than drawing a verdict from it.
        var sinceFullSnapshot = _lastAcceptedSnapshotUtc is null
            ? (TimeSpan?)null
            : tickAt - _lastAcceptedSnapshotUtc.Value;

        if (TimerLateness.Describe(
                lateness,
                _rePushInterval,
                TimeSpan.FromSeconds(GatewayConfig.DefaultStreamStaleAfterSeconds),
                sinceFullSnapshot)
            is { } latenessLine)
        {
            FileLog.Write($"[GatewayStreamClient] re-push tick LATE: {latenessLine} {MemoryNow()}");
        }

        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected)
        {
            // Expected while the tunnel is re-dialing; logged because it is also how a long silence begins.
            FileLog.Write($"[GatewayStreamClient] re-push tick SKIPPED: connection state={conn?.State.ToString() ?? "none"} " +
                          MemoryNow());
            return;
        }
        if (Interlocked.Exchange(ref _rePushInFlight, 1) == 1)
        {
            var running = _rePushStartedUtc == DateTime.MinValue
                ? "unknown"
                : $"{(DateTime.UtcNow - _rePushStartedUtc).TotalSeconds:F1}s";
            // An ON-TIME callback can still skip here because the PREVIOUS build is stalled, and that is
            // one of the ways memory pressure takes a Director's sessions off the air without producing
            // a late-tick line at all. The reading belongs on this message too (issue #2818).
            FileLog.Write($"[GatewayStreamClient] re-push tick SKIPPED: previous push still in flight after {running} " +
                          MemoryNow());
            return;
        }
        _ = RePushAsync();
    }

    /// <summary>
    /// The machine's memory right now, as a clause for a log line, stating plainly that it is a reading
    /// taken at the moment of logging (issue #2818).
    ///
    /// It says WHAT WAS MEASURED and deliberately does not say that memory CAUSED the outcome it is
    /// attached to. A skipped tick on a machine that happens to be short of memory is a correlation;
    /// writing it as a cause would put a conclusion in the log that nobody verified.
    /// </summary>
    private static string MemoryNow()
    {
        var reading = MachineMemoryProbe.Shared.Read();
        return $"[memory at this moment: {reading}, {MemoryPressure.Describe(MemoryPressure.Level(reading))}]";
    }

    /// <summary>When the in-flight re-push started, so a skipped tick can report how long it has been
    ///  waiting. Written only by the single re-push allowed in flight at a time.</summary>
    private DateTime _rePushStartedUtc = DateTime.MinValue;

    /// <summary>
    /// When the previous re-push TICK ran, as UTC ticks, so this one can say how late it was to start
    /// (issue #2818). Zero means "not set yet".
    ///
    /// SEEDED WHEN THE TIMER IS ARMED rather than left empty until the first callback, because a
    /// Director whose very FIRST tick arrives thirty seconds late is precisely the case worth
    /// reporting, and measuring only from the previous actual tick would score it as perfectly on time.
    ///
    /// Held as a long and exchanged atomically because <see cref="Timer"/> callbacks CAN overlap: when
    /// one runs past its period the next fires on another pool thread while it is still going. An
    /// earlier draft of this field asserted the runtime prevented that. It does not, and on a starved
    /// machine - where a callback running long is the normal case - two threads reading and writing a
    /// DateTime here would have produced a torn value and a nonsense lateness in the log.
    /// </summary>
    private long _lastRePushTickUtcTicks;

    /// <summary>
    /// When the Gateway last accepted a FULL snapshot from this Director.
    ///
    /// THIS IS NOT THE GATEWAY'S FRESHNESS CLOCK, and the distinction cost a false alarm before it was
    /// written down. <c>PushedSessionStore.ApplyDelta</c> and <c>ApplyRemove</c> stamp
    /// <c>ReceivedAtUtc</c> exactly as <c>ApplySnapshot</c> does, so an accepted delta refreshes the
    /// Gateway's cache without ever touching this field. It is stamped after <c>InvokeAsync</c> returns,
    /// too, which is later than the Gateway's own stamp. Elapsed time measured from here is therefore
    /// not a bound on that cache's age in either direction - it is simply time since this Director's
    /// last full-snapshot acknowledgement, which is all <see cref="TimerLateness.Describe"/> claims.
    /// </summary>
    private DateTime? _lastAcceptedSnapshotUtc;

    /// <summary>A re-push that takes longer than this is reported. The cadence is <c>_rePushInterval</c>, so
    ///  anything at or past one whole interval has already cost the next tick - and two lost ticks is what
    ///  ages the Gateway's cache past its staleness cut (issue #2188).</summary>
    private static readonly TimeSpan SlowRePushThreshold = TimeSpan.FromSeconds(
        GatewayConfig.DefaultStreamStaleAfterSeconds / 2.0);

    private async Task RePushAsync()
    {
        var startedUtc = DateTime.UtcNow;
        _rePushStartedUtc = startedUtc;
        var report = ReseedReport.NotConnected;
        try
        {
            // ReseedAsync sends Hello + a full PushSnapshot with an incrementing sequence (the same path used
            // on connect/reconnect) and already swallows its own send faults, so a re-push is best-effort.
            report = await ReseedAsync();
        }
        finally
        {
            // Report a SLOW push, not only a failed one. A push that succeeds but takes a whole cadence has
            // already eaten the following tick, which is the observed path to the cache going stale - and it
            // is otherwise indistinguishable from a healthy push in the log.
            //
            // WHAT CHANGED AND WHY IT MATTERS (issue #1153): this used to report one elapsed number for every
            // outcome, including a push the connection's own server-timeout had CANCELLED - so an abandoned
            // wait was logged in the same words as a slow Gateway, and the two are not the same event and do
            // not have the same fix. The outcome now decides the wording, and a wait that never finished
            // says so in the line rather than being counted as a duration the Gateway is answerable for.
            var elapsed = DateTime.UtcNow - startedUtc;
            if (!report.Completed)
            {
                // NOT called slow. Nobody can say how long the push would have taken, because it did not
                // finish - all that is known is how long we waited before it was abandoned.
                FileLog.Write($"[GatewayStreamClient] re-push DID NOT COMPLETE after {elapsed.TotalSeconds:F1}s: "
                    + $"{report.Failure ?? "no reason recorded"} - this is how long the WAIT lasted, "
                    + "NOT how long the Gateway took. " + MemoryNow());
            }
            else if (elapsed >= SlowRePushThreshold)
            {
                FileLog.Write($"[GatewayStreamClient] re-push SLOW: took {elapsed.TotalSeconds:F1}s "
                    + $"(cadence {SlowRePushThreshold.TotalSeconds:F0}s) - the next tick was likely skipped. "
                    + $"Of that, building the snapshot here took {report.BuildSnapshot.TotalSeconds:F1}s and "
                    + $"waiting for the Gateway to accept it took {report.AwaitGateway.TotalSeconds:F1}s. "
                    + MemoryNow());
            }
            _rePushStartedUtc = DateTime.MinValue;
            Interlocked.Exchange(ref _rePushInFlight, 0);
        }
    }

    // Owns the connection for its whole life: build once, keep it connected, and reseed on every
    // (re)connection. Auto-reconnect handles transient drops fast; when it gives up (Closed), this loop
    // restarts the connection so a long Gateway outage self-heals without a Director restart.
    private async Task SuperviseAsync()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(_config.Url.TrimEnd('/') + "/director-stream", options =>
            {
                var token = _config.Token;
                if (!string.IsNullOrEmpty(token))
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                // Local-name-friendly dialing (see GatewayHttp): a gateway named soren_north.local
                // resolves to a link-local IPv6 address first, and the default connect hangs on it.
                // The handler covers negotiate and the fallback transports; the websocket factory
                // covers the websocket itself, which dials outside that handler by default.
                options.HttpMessageHandlerFactory = _ => GatewayHttp.Handler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                    await GatewayHttp.ConnectWebSocketAsync(context.Uri, token, cancellationToken);
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
            })
            // Gateway Cleanup mission: MessagePack (binary) so a full-cap 48KB up-stream byte[] frame stays
            // ~48KB on the wire. Under JSON it base64-inflates to ~64KB and exceeds the hub's receive limit,
            // dropping the tunnel on any file read >48KB or a terminal burst. The hub also speaks JSON, so this
            // is safe to roll out per-Director.
            .AddMessagePackProtocol()
            .Build();

        // Tunnel liveness (issue #1153) - the client half of the pair the hub sets in GatewayHost, read from
        // the SAME shared constants so the two can never drift. ServerTimeout is what actually decides whether
        // this Director hangs up: it fires on SILENCE from the Gateway, not on a slow call, so the fix for a
        // Gateway that is alive but busy is a more frequent ping and an UNCHANGED tolerance. Both must be set
        // before StartAsync.
        _connection.ServerTimeout = DirectorStreamLimits.SilenceTolerance;
        _connection.KeepAliveInterval = DirectorStreamLimits.KeepAlivePing;

        _connection.Reconnecting += ex =>
        {
            FileLog.Write($"[GatewayStreamClient] reconnecting: {ex?.Message}");
            _capabilities = null;   // unknown until the reconnect's Hello answers
            // Gateway Cleanup Phase 0 (Architect ruling A): the tunnel dropped, so no frame can reach the
            // Gateway - tear down every live up-stream instead of leaving a producer sending into a dead socket.
            _upStreamHandler?.CancelAll();
            _monitor?.MarkTunnelConnecting();
            return Task.CompletedTask;
        };
        _connection.Reconnected += async _ => { await ReseedAsync(); _monitor?.MarkTunnelConnected(); };
        // Also tear streams down on a full close (auto-reconnect gave up); the supervise loop then re-dials.
        // The capabilities go with the connection: the next dial may reach a different (older) Gateway, and a
        // caller must not act on the last one's answer until the new Hello has answered.
        _connection.Closed += _ => { _capabilities = null; _upStreamHandler?.CancelAll(); _monitor?.MarkTunnelConnecting(); return Task.CompletedTask; };

        // Issue #1176 (Phase 1b): the down-channel proof. The Gateway can call this and await the reply
        // over the same connection (SignalR client results), demonstrating request-both-ways on one
        // outbound-dialed stream. A synthetic proof, not a production command handler.
        _connection.On<string, string>("Ping", message => $"pong:{message}");

        // Issue #1177 (Phase 1): the production down-channel. The Gateway invokes "Command" with a
        // DirectorCommand and awaits the DirectorCommandResult over the same connection (SignalR client
        // results). This handler is a boundary, so it catches: a dispatcher fault becomes an Error result
        // the Gateway can fall back on, never a faulted hub invocation.
        _connection.On<DirectorCommand, DirectorCommandResult>("Command", async cmd =>
        {
            try
            {
                if (cmd is null)
                    return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "command is required");

                // Gateway Cleanup Phase 0 (Architect ruling A): the four connection-bound stream verbs branch
                // here - they need this live connection and the per-stream cancellation registry, so they are
                // NOT in the unary verb-to-area dictionary. Everything else forwards to the unary dispatcher.
                if (DirectorUpStreamHandler.IsStreamVerb(cmd.Verb))
                {
                    if (_upStreamHandler is null)
                    {
                        FileLog.Write($"[GatewayStreamClient] stream verb declined (no up-stream handler): verb={cmd.Verb}, cmdId={cmd.CommandId}");
                        return DirectorCommandResult.Fail(DirectorCommandStatus.Error, "director has no up-stream handler");
                    }
                    FileLog.Write($"[GatewayStreamClient] stream verb received: verb={cmd.Verb}, sid={cmd.SessionId}, cmdId={cmd.CommandId}");
                    var streamResult = _upStreamHandler.Handle(cmd);
                    streamResult.CommandId = cmd.CommandId;
                    return streamResult;
                }

                if (_commandDispatcher is null)
                {
                    FileLog.Write($"[GatewayStreamClient] Command declined (no dispatcher): verb={cmd.Verb}, cmdId={cmd.CommandId}");
                    return DirectorCommandResult.Fail(DirectorCommandStatus.Error, "director has no command dispatcher");
                }

                FileLog.Write($"[GatewayStreamClient] Command received: verb={cmd.Verb}, sid={cmd.SessionId}, cmdId={cmd.CommandId}");
                return await _commandDispatcher(cmd);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayStreamClient] Command FAILED: verb={cmd?.Verb}, cmdId={cmd?.CommandId}, error={ex.Message}");
                return DirectorCommandResult.Fail(DirectorCommandStatus.Error, ex.Message);
            }
        });

        while (!_disposed)
        {
            var outcome = await TryConnectAsync();
            if (outcome == ConnectOutcome.Connected)
            {
                await ReseedAsync();
                _monitor?.MarkTunnelConnected();   // tunnel up = the two-way connection is proven (green)
                await WaitUntilClosedAsync();      // returns when auto-reconnect has exhausted its attempts
            }
            else if (outcome == ConnectOutcome.SubscriptionRequired)
            {
                // The Gateway refused the device key (subscription lapsed / revoked). STOP - do not hammer a
                // locked door. The status names the fix (renew / re-enroll); a settings change or a fresh
                // enrollment rebuilds this client and re-dials.
                _monitor?.MarkSubscriptionRequired("This gateway needs an active subscription - renew your subscription or re-enroll this device.");
                break;
            }
            if (_disposed) break;
            _monitor?.MarkTunnelConnecting();      // dropped / dialing again (yellow) until the next connect
            await Task.Delay(RestartDelay);        // long-outage restart
        }
    }

    private enum ConnectOutcome
    {
        /// <summary>The tunnel is up.</summary>
        Connected,
        /// <summary>A retryable failure (Gateway down, network blip) - keep re-dialing.</summary>
        Retry,
        /// <summary>A TERMINAL refusal: the Gateway rejected the device key with 401/402 (the hosted
        /// subscription lapsed or the device was revoked). Do NOT keep hammering - stop and surface it.</summary>
        SubscriptionRequired,
    }

    private async Task<ConnectOutcome> TryConnectAsync()
    {
        if (_connection is null) return ConnectOutcome.Retry;
        try
        {
            await _connection.StartAsync();
            FileLog.Write($"[GatewayStreamClient] connected to {_config.Url}");
            return ConnectOutcome.Connected;
        }
        catch (Exception ex) when (IsSubscriptionRequired(ex))
        {
            // Terminal: the per-device key is no longer accepted (revoked / subscription lapsed). Re-dialing
            // would just get refused again forever, so stop and let the connection status say why (the fix is
            // to renew the subscription / re-enroll, which restarts the client).
            FileLog.Write("[GatewayStreamClient] connect REFUSED (subscription required / device revoked) - stopping reconnect");
            return ConnectOutcome.SubscriptionRequired;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayStreamClient] connect failed (will retry): {ex.Message}");
            return ConnectOutcome.Retry;
        }
    }

    /// <summary>
    /// A tunnel-connect exception is a TERMINAL "subscription required" when the Gateway refused the device
    /// key with 401 (credential revoked) or 402 (hosted subscription required). The key is a fixed per-device
    /// credential, never refreshed, so a 401/402 means it is no longer valid - not a transient blip - and the
    /// only fix is renewing the subscription or re-enrolling. Walks the inner-exception chain because SignalR
    /// wraps the negotiate failure.
    /// </summary>
    private static bool IsSubscriptionRequired(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is HttpRequestException hre
                && hre.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired)
                return true;
        return false;
    }

    private async Task WaitUntilClosedAsync()
    {
        if (_connection is null) return;
        var closed = new TaskCompletionSource();
        Task OnClosed(Exception? _) { closed.TrySetResult(); return Task.CompletedTask; }
        _connection.Closed += OnClosed;
        try
        {
            if (_connection.State == HubConnectionState.Disconnected) return;
            await closed.Task;
        }
        finally
        {
            _connection.Closed -= OnClosed;
        }
    }

    /// <summary>
    /// What one reseed's SESSION leg actually did, so a duration can never be reported as something it did
    /// not measure.
    ///
    /// THE DEFECT THIS TYPE EXISTS TO KILL (issue #1153). The re-push timing used to sit in a
    /// <c>finally</c> wrapped around the whole send, so when the connection's own server-timeout cancelled an
    /// in-flight push, the length of the WAIT was written to the log as <c>re-push SLOW: took 83.4s</c>. That
    /// number is perfectly true about how long the wait lasted and says NOTHING about how long the push took -
    /// a true record of the wrong thing - and it sent a day's investigation after the Gateway's speed when
    /// what had actually happened was that the tunnel went silent and the client hung up. The tell, only
    /// visible once someone looked, was that the slow-push line and the server-timeout line carried IDENTICAL
    /// timestamps.
    ///
    /// So the phases are separated and the outcome is named: how long WE spent building the snapshot on this
    /// machine, how long the GATEWAY took to accept it, and whether the wait finished at all. They fail for
    /// different reasons and have different fixes, and one combined number cannot tell them apart.
    /// </summary>
    private readonly record struct ReseedReport(TimeSpan BuildSnapshot, TimeSpan AwaitGateway, bool Completed, string? Failure)
    {
        /// <summary>Nothing was attempted: there was no connected tunnel to reseed down.</summary>
        public static ReseedReport NotConnected { get; } =
            new(TimeSpan.Zero, TimeSpan.Zero, Completed: false, Failure: "the tunnel was not connected");
    }

    /// <summary>
    /// devthrottle_internal#1176: the display name for this reseed's Hello. A cosmetic label must never
    /// take the registration down, so a throwing provider (corrupt named-instances.json) is logged and
    /// reported as "no statement" (empty) - the Gateway's merge guard keeps whatever it already holds.
    /// </summary>
    private string ReadDisplayName()
    {
        if (_displayName is null) return "";
        try
        {
            return _displayName()?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayStreamClient] display-name provider FAILED (Hello proceeds unnamed): {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// The hub methods this Director will actually call on a Gateway, and therefore the ones whose
    /// absence it should say something about. Kept deliberately SHORT: this is not an inventory of
    /// the hub, it is the list whose absence has a consequence a person needs to hear about.
    /// </summary>
    private static readonly string[] MethodsThisDirectorNeeds =
    {
        "RegisterSessionKey", "RevokeSessionKey", "PushSnapshot", "PushDelta", "PushRepoSnapshot", "PushTurns",
    };

    /// <summary>What the Gateway answered on the last Hello, so callers can ask whether it has a hub method
    /// before calling it. Null until the first Hello has answered.</summary>
    private GatewayCapabilities? _capabilities;

    /// <summary>Told every time a Hello answers (turn-push mission: the TurnPusher seeds its watermarks from
    /// the answer and backfills). Called after the capability line is logged.</summary>
    private readonly Action<GatewayCapabilities>? _onHello;

    /// <summary>Whether the Gateway on the other end has this hub method. False before the first Hello and
    /// against a Gateway built before the method existed - the caller then does not call it.</summary>
    public bool GatewaySupports(string hubMethod)
        => _capabilities?.HubMethods.Contains(hubMethod, StringComparer.Ordinal) == true;

    /// <summary>
    /// Push one batch of a session's conversation (turn-push mission) and return the watermark the Gateway
    /// holds afterwards. Null when the Gateway refused the batch. Throws when the tunnel is not connected -
    /// the caller (TurnPusher) logs and resumes from the Gateway's watermark on the next trigger.
    /// </summary>
    public async Task<TurnWatermark?> PushTurnsAsync(TurnPushBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected)
            throw new InvalidOperationException("the Gateway stream is not connected");
        var seq = Interlocked.Increment(ref _sequence);
        return await conn.InvokeAsync<TurnWatermark?>("PushTurns", seq, batch, ct).ConfigureAwait(false);
    }

    /// <summary>The capability line already reported, so a ten-second reseed does not repeat it forever.</summary>
    private string _lastCapabilityReport = "";

    /// <summary>
    /// Say ONCE, plainly, what the Gateway on the other end cannot do.
    ///
    /// This is the line that did not exist on 2026-08-05. The hosted Gateway predated
    /// <c>RegisterSessionKey</c>, so this Director kept minting session keys and sending registrations
    /// that could never be accepted, and every agent it launched was handed a credential answering 401.
    /// The only trace was a transport-level "Method does not exist" logged every ten seconds by the
    /// recovery path - true, but describing one failed call rather than the state of the connection
    /// (#2457, #2459).
    ///
    /// A NULL argument is the important case, not an error: a Gateway built before capabilities were
    /// reported returns nothing from Hello, and that alone dates it before this work.
    ///
    /// It only LOGS. Nothing here refuses to mint a key or short-circuits a call - the recovery paths
    /// already retry every reseed, and a second mechanism deciding the same question would give two
    /// answers to it. The fix for an out-of-date Gateway is to deploy it, and the point of this line
    /// is that someone reading the log can tell that is what is needed.
    ///
    /// Reported on CHANGE only. The reseed runs every ten seconds and an unchanged fact repeated
    /// forever is how a log stops being read.
    /// </summary>
    private void ReportGatewayCapabilities(GatewayCapabilities? capabilities)
    {
        var report = DescribeGatewayCapabilities(capabilities);
        if (string.Equals(report, _lastCapabilityReport, StringComparison.Ordinal)) return;
        _lastCapabilityReport = report;
        FileLog.Write($"[GatewayStreamClient] {report}");
    }

    /// <summary>
    /// The sentence <see cref="ReportGatewayCapabilities"/> writes. Pure and separate from the logging
    /// so the wording - which is the entire value of this feature - can be tested directly rather than
    /// inferred from a log file.
    /// </summary>
    internal static string DescribeGatewayCapabilities(GatewayCapabilities? capabilities)
    {
        if (capabilities is null)
            return "the Gateway did not report its capabilities, so it was built before capability "
                + "reporting existed - it is OLDER than this Director. If session keys are being refused, "
                + "this is why, and deploying the Gateway is the fix.";

        var missing = MethodsThisDirectorNeeds
            .Where(m => !capabilities.HubMethods.Contains(m, StringComparer.Ordinal))
            .ToArray();
        var identity = $"Gateway v{capabilities.Version}"
            + (capabilities.Commit.Length > 0 ? $" ({capabilities.Commit})" : "");
        return missing.Length == 0
            ? $"{identity}: has every hub method this Director needs"
            : $"{identity} is MISSING the hub method(s) this Director needs: {string.Join(", ", missing)}. "
              + "Calls to them will fail until the Gateway is deployed; a missing RegisterSessionKey "
              + "means EVERY session's command line will answer 401.";
    }

    private async Task<ReseedReport> ReseedAsync()
    {
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return ReseedReport.NotConnected;

        var build = TimeSpan.Zero;
        var awaitGateway = TimeSpan.Zero;
        var completed = false;
        string? failure = null;
        try
        {
            var seq = Interlocked.Increment(ref _sequence);

            var helloStarted = DateTime.UtcNow;
            // Invoked GENERICALLY so the Gateway's answer is read. A Gateway too old to return
            // capabilities returns nothing, which SignalR gives us as null - and null is the answer
            // that matters. See ReportGatewayCapabilities.
            var capabilities = await conn.InvokeAsync<GatewayCapabilities?>("Hello", new DirectorStreamHello
            {
                DirectorId = _directorId,
                Version = _version,
                MachineName = Environment.MachineName,
                User = Environment.UserName,
                Pid = Environment.ProcessId,
                StartedAt = _startedAt,
                DisplayName = ReadDisplayName(),
                PushesTurns = true,   // this build carries the TurnPusher (turn-push mission)
            });
            awaitGateway += DateTime.UtcNow - helloStarted;
            ReportGatewayCapabilities(capabilities);
            _capabilities = capabilities;
            if (capabilities is not null && _onHello is not null)
            {
                try { _onHello(capabilities); }
                catch (Exception ex) { FileLog.Write($"[GatewayStreamClient] Hello callback FAILED: {ex.Message}"); }
            }

            // OUR work, on this machine: assembling the roster. Timed apart from the send because a slow build
            // is a local problem (a starved machine, a lock held too long) and a slow send is the Gateway's -
            // opposite diagnoses, opposite owners, and the single combined number could name neither.
            var buildStarted = DateTime.UtcNow;
            var snapshot = _snapshot().ToArray();
            build = DateTime.UtcNow - buildStarted;

            // The Gateway's work: InvokeAsync does not return when the frame is written, it returns when the
            // hub method has FINISHED, so this measures the Gateway's own per-push processing.
            var sendStarted = DateTime.UtcNow;
            await conn.InvokeAsync("PushSnapshot", seq, snapshot);
            awaitGateway += DateTime.UtcNow - sendStarted;

            completed = true;
            // The moment the Gateway's cache was made fresh, which is what a late tick reports against.
            _lastAcceptedSnapshotUtc = DateTime.UtcNow;
            FileLog.Write($"[GatewayStreamClient] reseeded full snapshot seq={seq} "
                + $"(built in {build.TotalMilliseconds:F0}ms, Gateway accepted it in {awaitGateway.TotalMilliseconds:F0}ms)");
        }
        catch (Exception ex)
        {
            // Kept LOUD and kept here. This catch is the only place a hub-side throw becomes visible to the
            // Director, and it is the one thing the Director would lose if this ever moved to a
            // fire-and-forget send - so if that change is made, the surfacing has to be replaced, not dropped.
            failure = ex.Message;
            FileLog.Write($"[GatewayStreamClient] reseed failed (auto-reconnect will retry): {ex.Message}");
        }

        // Remove-the-network-port phase 1b: re-register every live session's Gateway key, in its OWN
        // try/catch for the same reason as the repository leg below.
        //
        // THIS IS THE RECOVERY PATH, not an optimisation. The tunnel is how a key reaches the Gateway, and
        // the tunnel drops - a Gateway restart wipes nothing (the registry is durable) but a Director that
        // reconnects after one may have minted keys nobody received. Re-sending them on every reseed makes
        // the new connection authoritative for credentials exactly as the snapshot above makes it
        // authoritative for the roster, so a lost registration heals within one reseed instead of leaving an
        // agent permanently unable to call the Gateway.
        //
        // It also EXTENDS the expiry (RegistrationFor recomputes it), which is what lets a key be short-lived
        // without a long-running session ever losing it.
        if (_sessionKeys is not null && conn.State == HubConnectionState.Connected)
        {
            // ONE BAD REGISTRATION MUST NOT TAKE THE OTHERS DOWN WITH IT.
            //
            // This loop used to sit inside a single try, so the FIRST registration that threw abandoned
            // every key after it. One session with an unregisterable key therefore cost every other
            // session on the Director its credential, and each reseed retried in the same order and
            // failed at the same place - a fleet-wide agent lockout produced by one row. Observed on
            // 2026-09-14: 2,310 consecutive failures, every session key on the machine refused.
            //
            // Each registration is now independent. A failure is counted, named, and the loop continues.
            // THE OUTER CATCH STAYS. Restructuring the loop is not a licence to drop it: the comment
            // above promises this leg has its own try/catch so a reseed is never broken by it, and
            // _sessionKeys() is a caller-supplied delegate that can throw before the loop is even
            // entered. Removing it would trade one fault for a worse one - a failed registration pass
            // taking down the snapshot push that carries the entire roster.
            try
            {
                // ONE BAD REGISTRATION MUST NOT TAKE THE OTHERS DOWN WITH IT.
                //
                // This loop used to sit inside a single try, so the FIRST registration that threw
                // abandoned every key after it. One session with an unregisterable key therefore cost
                // every other session on the Director its credential, and each reseed retried in the
                // same order and failed at the same place - a fleet-wide agent lockout produced by one
                // row. Observed 2026-09-14: 2,310 consecutive failures, every session key refused.
                var registrations = _sessionKeys();
                var registered = 0;
                var failures = new List<string>();

                foreach (var registration in registrations)
                {
                    try
                    {
                        await conn.InvokeAsync("RegisterSessionKey", registration);
                        registered++;
                    }
                    catch (Exception ex)
                    {
                        // NAME THE SESSION. The old line reported only the exception message, so 2,310
                        // identical lines never once said WHICH registration was failing - the single
                        // fact that would have turned the outage into a five-minute fix.
                        failures.Add($"{registration.SessionId}: {ex.Message}");
                    }
                }

                if (failures.Count > 0)
                {
                    // AND DO NOT GUESS AT THE CAUSE. This line used to say "(older Gateway?)" - a guess,
                    // and on 2026-09-14 a wrong one that sent an investigation toward a production
                    // deploy. The Director logs the Gateway's version and a PASSING capability check
                    // moments earlier in this same method, so an old Gateway is the one explanation
                    // already ruled out. Report what was measured; the Gateway now supplies the reason.
                    FileLog.Write("[GatewayStreamClient] session key re-registration INCOMPLETE: " +
                                  $"{registered}/{registrations.Count} registered, {failures.Count} failed. " +
                                  $"Each failure, by session: {string.Join(" | ", failures)}");
                }
                else if (registrations.Count > 0)
                {
                    FileLog.Write($"[GatewayStreamClient] re-registered {registered}/{registrations.Count} session key(s)");
                }
            }
            catch (Exception ex)
            {
                // The PASS itself failed - building the list, or something outside any one registration.
                // Worded distinctly from the per-session failures above, because this one says nothing
                // about any individual key.
                FileLog.Write("[GatewayStreamClient] session key re-registration pass FAILED before it " +
                              $"could report per-session results: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Replay every revocation still owed. This is the other half of the recovery path above, and the
        // half that was missing: registrations healed on reseed while revocations did not, so the one
        // direction that matters for SECURITY was the one with no retry. Each is confirmed individually,
        // so a partial failure leaves exactly the undelivered ones owed for the next reseed.
        if (_pendingRevocations is not null && conn.State == HubConnectionState.Connected)
        {
            try
            {
                var owed = _pendingRevocations();
                foreach (var sessionId in owed)
                {
                    try
                    {
                        await conn.InvokeAsync("RevokeSessionKey", sessionId);
                        _onRevocationConfirmed?.Invoke(sessionId);
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[GatewayStreamClient] replayed revocation for {sessionId} failed: {ex.Message} - still owed");
                    }
                }
                if (owed.Count > 0)
                    FileLog.Write($"[GatewayStreamClient] replayed {owed.Count} owed session key revocation(s)");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayStreamClient] revocation replay incomplete: {ex.Message}");
            }
        }

        // The repository snapshot rides the same reseed cadence, in its OWN try/catch: an old Gateway
        // without the PushRepoSnapshot hub method throws a HubException here, and that must never take
        // the session reseed down with it (best-effort, no capability negotiation - see phase C notes).
        if (_repoSnapshot != null && conn.State == HubConnectionState.Connected)
        {
            try
            {
                var repoSeq = Interlocked.Increment(ref _sequence);
                await conn.InvokeAsync("PushRepoSnapshot", repoSeq, _repoSnapshot().ToArray());
                FileLog.Write($"[GatewayStreamClient] reseeded repository snapshot seq={repoSeq}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayStreamClient] repository reseed skipped (older Gateway?): {ex.Message}");
            }
        }

        return new ReseedReport(build, awaitGateway, completed, failure);
    }

    /// <summary>
    /// Push the current full repository snapshot. Fire-and-forget; callers debounce. A drop is
    /// reconciled by the next reseed/re-push tick.
    /// </summary>
    public void NotifyRepoSnapshot()
    {
        if (_repoSnapshot is null) return;
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        var seq = Interlocked.Increment(ref _sequence);
        _ = SendAsync(() => conn.InvokeAsync("PushRepoSnapshot", seq, _repoSnapshot().ToArray()), "PushRepoSnapshot");
    }

    /// <summary>Push one changed session. Fire-and-forget; a drop is reconciled by the next snapshot.</summary>
    public void NotifyDelta(SessionDto session)
    {
        if (session is null || string.IsNullOrEmpty(session.SessionId)) return;
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        var seq = Interlocked.Increment(ref _sequence);
        _ = SendAsync(() => conn.InvokeAsync("PushDelta", seq, session), "PushDelta");
    }

    /// <summary>
    /// Register ONE session's Gateway key (Remove-the-network-port mission, phase 1b), sent the instant the
    /// key is minted - before the agent process is launched, let alone booted - so it is accepted by the
    /// time the agent's first command reaches the Gateway.
    ///
    /// It is awaited, and its failure is REPORTED rather than swallowed: an unregistered key is a session
    /// whose command line answers 401 on every call, and that must be one findable line in the log rather
    /// than an agent reporting that DevThrottle is broken. The next reseed re-registers it, which is what
    /// makes this survivable rather than fatal.
    /// </summary>
    public async Task<bool> RegisterSessionKeyAsync(SessionKeyRegistration registration)
    {
        if (registration is null || string.IsNullOrEmpty(registration.SessionId)) return false;
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected)
        {
            FileLog.Write($"[GatewayStreamClient] session key NOT registered (tunnel not connected): session={registration.SessionId} - the next reseed will register it");
            return false;
        }

        try
        {
            await conn.InvokeAsync("RegisterSessionKey", registration);
            FileLog.Write($"[GatewayStreamClient] registered session key: session={registration.SessionId}, expires={registration.ExpiresAtUtc:O}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayStreamClient] session key registration FAILED: session={registration.SessionId}, {ex.Message} - the next reseed will retry");
            return false;
        }
    }

    /// <summary>
    /// End one session's Gateway key (Remove-the-network-port mission, phase 1b) - sent when the session is
    /// reaped. Fire-and-forget: a revocation that does not land is backstopped by the key's expiry, and by
    /// the fact that a reaped session is no longer re-registered, so the key lapses rather than living on.
    /// </summary>
    public void RevokeSessionKey(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected)
        {
            // NOT "it lapses at its expiry" - that was the old reasoning and it was wrong. The debt is
            // already recorded by SessionGatewayKeys.Forget and the next reseed replays it, so a tunnel
            // that is down at this instant delays the revocation rather than discarding it.
            FileLog.Write($"[GatewayStreamClient] session key revocation DEFERRED (tunnel not connected): session={sessionId} - owed, and replayed on the next reseed");
            return;
        }
        _ = RevokeAndConfirmAsync(conn, sessionId);
    }

    /// <summary>
    /// Send one revocation and settle its debt ONLY if the Gateway accepted it.
    ///
    /// The old code passed this through the shared fire-and-forget SendAsync, which caught the failure,
    /// logged it and dropped it. That is right for a roster push - the next snapshot re-states the truth -
    /// and wrong for a revocation, because nothing re-stated it: the hash had already been forgotten, so
    /// the reseed had nothing to replay and the key stayed valid on the Gateway until it expired.
    /// </summary>
    private async Task RevokeAndConfirmAsync(HubConnection conn, string sessionId)
    {
        try
        {
            await conn.InvokeAsync("RevokeSessionKey", sessionId);
            _onRevocationConfirmed?.Invoke(sessionId);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayStreamClient] RevokeSessionKey({sessionId}) failed: {ex.Message} - still owed, and replayed on the next reseed");
        }
    }

    /// <summary>Push a session removal. Fire-and-forget; a drop is reconciled by the next snapshot.</summary>
    public void NotifyRemove(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        var seq = Interlocked.Increment(ref _sequence);
        _ = SendAsync(() => conn.InvokeAsync("RemoveSession", seq, sessionId), "RemoveSession");
    }

    private static async Task SendAsync(Func<Task> send, string what)
    {
        try { await send(); }
        catch (Exception ex) { FileLog.Write($"[GatewayStreamClient] {what} dropped (mid-reconnect?): {ex.Message}"); }
    }

    /// <summary>Force the outbound tunnel to bounce: stop the current connection so the supervise
    /// loop re-dials (within RestartDelay). Idempotent; inert when the tunnel is disabled. This is the
    /// Director floor's POST /reconnect capability (Gateway Cleanup mission).</summary>
    public async Task ReconnectAsync()
    {
        if (!IsEnabled) return;
        var conn = _connection;
        if (conn is null) return;
        FileLog.Write("[GatewayStreamClient] ReconnectAsync: bouncing tunnel on request");
        try { await conn.StopAsync(); }
        catch (Exception ex) { FileLog.Write($"[GatewayStreamClient] ReconnectAsync stop error: {ex.Message}"); }
    }

    /// <summary>
    /// The clean-shutdown farewell (issue #2194, the #1862 ending design): tell the Gateway this
    /// Director is stopping ON PURPOSE, so its work-history rows are ruled "Director stopped" instead
    /// of being concluded "interrupted" from silence. Called at the START of the Director's shutdown
    /// routine - BEFORE the sessions are killed - so the ruling covers every session the shutdown
    /// takes with it (a per-session remove arriving later keeps this first ruling). Best-effort and
    /// time-boxed: shutdown must never hang on it, and an older Gateway without the hub method just
    /// throws into the catch. Safe to call more than once.
    /// </summary>
    public async Task NotifyDirectorStoppingAsync()
    {
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await conn.InvokeAsync("DirectorStopping", timeout.Token);
            FileLog.Write("[GatewayStreamClient] sent DirectorStopping farewell");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayStreamClient] DirectorStopping farewell not delivered (older Gateway?): {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        _disposed = true;
        if (_rePushTimer is not null)
        {
            await _rePushTimer.DisposeAsync();
            _rePushTimer = null;
        }
        if (_connection is not null)
        {
            // Issue #2194: the farewell backstop for stop paths that never ran the app-level shutdown
            // routine. When that routine DID run, the Gateway already ruled these rows and this second
            // call stamps nothing.
            await NotifyDirectorStoppingAsync();
            try { await _connection.StopAsync(); }
            catch (Exception ex) { FileLog.Write($"[GatewayStreamClient] StopAsync error: {ex.Message}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_rePushTimer is not null)
        {
            await _rePushTimer.DisposeAsync();
            _rePushTimer = null;
        }
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); }
            catch (Exception ex) { FileLog.Write($"[GatewayStreamClient] DisposeAsync error: {ex.Message}"); }
            _connection = null;
        }
    }
}
