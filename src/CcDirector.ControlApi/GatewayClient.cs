using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Phase-1 Director-to-Gateway client. If a <see cref="GatewayConfig"/> is enabled
/// (gateway.url present in config.json), this client:
///
///   1. POSTs /directors/register on start.
///   2. POSTs /directors/{id}/heartbeat every <see cref="HeartbeatInterval"/>, carrying a
///      snapshot of every session's mechanical state (issue #186: the heartbeat doubles
///      as the reconcile channel for lost doorbell pings).
///   3. POSTs /directors/{id}/doorbell on every session activity-state change
///      (<see cref="NotifySessionState"/>) - fire-and-forget, payload announces THAT a
///      state changed, never WHAT happened. No retries, no outbox: a lost ping is
///      harmless because the heartbeat reconciles.
///   4. DELETEs /directors/{id}/registration on stop.
///   5. Reacts to 410 Gone on heartbeat by re-registering automatically.
///   6. Retries failed register and heartbeat calls with exponential backoff.
///
/// When the config is disabled (no gateway.url) the client is inert - every method
/// is a no-op so the Director boots normally in local-only mode.
/// </summary>
public sealed class GatewayClient : IDisposable
{
    /// <summary>How often the heartbeat fires.</summary>
    public static TimeSpan HeartbeatInterval { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Max delay between failed register/heartbeat retries.</summary>
    public static TimeSpan MaxBackoff { get; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often a VERIFIED two-way connection re-proves itself (issues #223/#224). While
    /// unverified or failed, re-verification instead rides every heartbeat tick (15s) so
    /// recovery is fast; once green, this slower cadence keeps the proof fresh without
    /// dialing the callback loop four times a minute.
    /// </summary>
    public static TimeSpan ReverifyInterval { get; } = TimeSpan.FromSeconds(60);

    private readonly GatewayConfig _config;
    private readonly string _directorId;
    private readonly int _port;
    private readonly string _version;
    private readonly Func<List<SessionStateSnapshot>>? _sessionStates;
    private readonly GatewayConnectionMonitor? _monitor;
    private readonly HttpClient _http;
    private DateTime _lastVerifyStartedUtc = DateTime.MinValue;
    private int _verifying; // re-entrancy guard: never stack a second handshake on a slow one

    private Timer? _heartbeat;
    private CancellationTokenSource? _cts;
    private bool _registered;
    private bool _disposed;

    // The endpoint the Gateway currently knows for this Director: set on every successful
    // register POST ("" for a flagged no-endpoint registration), null while never registered.
    // Issue #324: there is deliberately NO forever-cache of the MagicDNS name anymore - the
    // identity is re-resolved on every register attempt and every heartbeat tick, so a
    // Tailscale daemon that comes up (or goes away) after Director start heals/degrades the
    // advertisement within one heartbeat cycle, no restart.
    private volatile string? _advertisedEndpoint;
    private int _reRegistering; // guard: never stack heartbeat-triggered re-registrations

    /// <summary>
    /// The plan-1A detection ladder (LocalAPI -> CLI -> config override). One instance per
    /// client so its log-dedup state spans the heartbeat re-resolves. Internal so tests can
    /// pin the environment-dependent probes (<see cref="TailnetIdentityResolver.LocalApiProbe"/>,
    /// <see cref="TailnetIdentityResolver.CliProbe"/>) while exercising the REAL ladder.
    /// </summary>
    internal TailnetIdentityResolver IdentityResolver { get; } = new();

    /// <summary>
    /// The LAN-IP resolver used when <see cref="GatewayConfig.AddressingMode"/> is
    /// <see cref="AddressingMode.Lan"/> (issue #457). One instance per client so its log-dedup
    /// state spans the heartbeat re-resolves, mirroring <see cref="IdentityResolver"/>.
    /// </summary>
    internal LanIdentityResolver LanResolver { get; } = new();

    /// <summary>
    /// Test seam (issue #324): some tests pin the whole resolution to an exact endpoint
    /// (e.g. a loopback callback host) that the production ladder would refuse. Production
    /// always resolves through <see cref="IdentityResolver"/> with this Director's port and
    /// configured override (wired in the constructor).
    /// </summary>
    internal Func<TailnetEndpointResolution> ResolveAdvertisedEndpoint { get; set; }

    /// <summary>
    /// Verify-before-advertise (issue #197): called with the advertised endpoint before
    /// every register POST. Returns null when the endpoint is confirmed reachable from
    /// the outside; otherwise a human-readable reason, which SKIPS the registration -
    /// a dead endpoint is never advertised, and <see cref="RegisterLoop"/>'s backoff
    /// retries later (covering the first-serve TLS cert issuance window and a Tailscale
    /// daemon that comes up after the Director). Null (the default) disables verification:
    /// direct constructions (tests, ephemeral-port hosts) register exactly as before;
    /// ControlApiHost wires it for real fixed-port Directors.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? EndpointVerifier { get; set; }

    /// <summary>Reason the last register attempt refused to advertise, or null when the
    /// endpoint verified (or verification is disabled). Surfaced for diagnostics.</summary>
    public string? LastVerifyError { get; private set; }

    public bool IsEnabled => _config.IsEnabled;
    public bool IsRegistered => _registered;

    /// <param name="sessionStates">Snapshot provider for the heartbeat's per-session state
    /// map (issue #186). Null (old callers, tests) sends a body-less heartbeat.</param>
    /// <param name="monitor">Two-way handshake state home (issues #223/#224). Owned by the
    /// HOST, not this client, so it survives client replacement on settings changes. Null
    /// (old callers, tests) disables verification entirely - registration and heartbeat
    /// behave exactly as before.</param>
    public GatewayClient(GatewayConfig config, string directorId, int port, string version, Func<List<SessionStateSnapshot>>? sessionStates = null, GatewayConnectionMonitor? monitor = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _directorId = directorId ?? throw new ArgumentNullException(nameof(directorId));
        _port = port;
        _version = version ?? "0.0.0";
        _sessionStates = sessionStates;
        _monitor = monitor;
        // Pick the resolver by addressing mode (issue #457). Tailscale mode advertises the
        // Serve front door; LAN mode advertises this machine's LAN IP. Neither ever advertises
        // loopback. The mode is fixed for the life of this client (a change applies on restart,
        // same as the bind interface that pairs with it).
        ResolveAdvertisedEndpoint = () => _config.AddressingMode == AddressingMode.Lan
            ? LanResolver.ResolveEndpoint(_port, _config.TailnetEndpoint)
            : IdentityResolver.ResolveEndpoint(_port, _config.TailnetEndpoint);

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        if (_config.IsEnabled)
        {
            _http.BaseAddress = new Uri(_config.Url.TrimEnd('/') + "/");
            if (!string.IsNullOrEmpty(_config.Token))
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        }
    }

    /// <summary>
    /// Fetch the latest Gateway turn brief for a session - the desktop Wingman tab's source
    /// (the rich per-turn brief the warm brain stamps, same content the Cockpit renders).
    /// Returns null when the Gateway is disabled, has no brief yet (404), or is unreachable;
    /// the caller then shows the local explain instead. Best-effort, never throws - same
    /// posture as the rest of this network client.
    /// </summary>
    public async Task<TurnBriefDto?> GetLatestTurnBriefAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_config.IsEnabled || string.IsNullOrWhiteSpace(sessionId)) return null;
        try
        {
            using var resp = await _http.GetAsync($"sessions/{sessionId}/turnbriefs/latest", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;   // no brief stamped yet
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayClient] GetLatestTurnBriefAsync {sessionId}: HTTP {(int)resp.StatusCode}");
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<TurnBriefDto>(ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayClient] GetLatestTurnBriefAsync {sessionId} FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Start the registration lifecycle. Fire-and-forget: the first register attempt
    /// runs in the background so a slow or unreachable Gateway never blocks Director
    /// startup. The heartbeat timer is set up regardless.
    /// </summary>
    public void Start()
    {
        if (!_config.IsEnabled)
        {
            FileLog.Write("[GatewayClient] Start: disabled (no gateway.url), running in local-only mode");
            _monitor?.Reset(gatewayConfigured: false);
            return;
        }
        if (_disposed) throw new ObjectDisposedException(nameof(GatewayClient));
        _monitor?.Reset(gatewayConfigured: true);

        _cts = new CancellationTokenSource();
        FileLog.Write($"[GatewayClient] Start: gateway={_config.Url}, directorId={_directorId}, port={_port}");

        // Kick off the first registration in the background. RegisterLoop retries on failure.
        _ = Task.Run(() => RegisterLoop(_cts.Token));

        _heartbeat = new Timer(_ => HeartbeatTick(), null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>
    /// Gracefully unregister. Best-effort: a failing DELETE is logged but does not
    /// throw - the Gateway will sweep the stale entry within 60 s anyway.
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed) return;
        if (!_config.IsEnabled) return;

        FileLog.Write($"[GatewayClient] StopAsync: directorId={_directorId}");
        try { _cts?.Cancel(); } catch { }
        _heartbeat?.Dispose();
        _heartbeat = null;

        if (_registered)
        {
            try
            {
                var resp = await _http.DeleteAsync($"directors/{_directorId}/registration");
                FileLog.Write($"[GatewayClient] DELETE registration -> {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayClient] DELETE registration FAILED (best-effort): {ex.Message}");
            }
        }
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        _heartbeat?.Dispose();
        _cts?.Dispose();
        _http.Dispose();
    }

    // ===== Internals =====

    private async Task RegisterLoop(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested && !_registered)
        {
            try
            {
                if (await TryRegisterAsync(ct))
                {
                    _registered = true;
                    // Registration only proves leg 1. Run the two-way handshake right away
                    // (issues #223/#224) so the indicator earns its green - or names the
                    // broken return leg - within seconds of connecting, not at the next tick.
                    // A flagged no-endpoint registration (issue #324) has nothing the Gateway
                    // could call back, so the handshake is skipped until identity heals.
                    if (!string.IsNullOrEmpty(_advertisedEndpoint))
                        _ = Task.Run(() => VerifyAsync(ct), ct);
                    return;
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayClient] Register attempt failed: {ex.Message}");
                _monitor?.ReportRegistrationFailure($"Cannot reach the Gateway at {_config.Url}: {ex.Message}");
            }

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }

            // Exponential backoff, capped at MaxBackoff - except while the tailnet identity
            // itself is unresolved (issue #324): then the retry stays at heartbeat cadence so
            // a Tailscale daemon that comes up is picked up within ~15s, not after a minute.
            var capMs = _lastResolutionFailed ? HeartbeatInterval.TotalMilliseconds : MaxBackoff.TotalMilliseconds;
            var nextMs = Math.Min(delay.TotalMilliseconds * 2, capMs);
            delay = TimeSpan.FromMilliseconds(nextMs);
        }
    }

    private async Task<bool> TryRegisterAsync(CancellationToken ct)
    {
        var req = BuildRegistrationRequest();
        if (string.IsNullOrWhiteSpace(req.TailnetEndpoint))
        {
            // No tailnet identity resolved (issue #324). FAIL LOUDLY - an explicit monitor
            // state (painted by the desktop indicator) plus a log line naming the fix - and
            // still register, flagged unreachable, so the fleet can see this machine exists
            // (an invisible Director is harder to diagnose remotely than a flagged one).
            var reason = req.EndpointUnreachableReason
                ?? "No tailnet identity resolved and no gateway.tailnetEndpoint override configured - start Tailscale on this machine or set the override.";
            FileLog.Write($"[GatewayClient] TryRegisterAsync: NO TAILNET IDENTITY - {reason}");
            _monitor?.ReportTailnetIdentityFailure(reason);

            var flaggedResp = await _http.PostAsJsonAsync("directors/register", req, ct);
            if (flaggedResp.IsSuccessStatusCode)
            {
                _advertisedEndpoint = "";
                FileLog.Write($"[GatewayClient] Registered FLAGGED (no reachable endpoint): status={(int)flaggedResp.StatusCode}; heartbeat re-resolves identity every {HeartbeatInterval.TotalSeconds:F0}s");
                return true;
            }

            // An old Gateway rejects the flagged shape (400 tailnetEndpoint required) - which
            // truthfully preserves its old behavior. Keep the identity-failure state (the
            // actionable, LOCAL truth) and let RegisterLoop retry at heartbeat cadence.
            FileLog.Write($"[GatewayClient] Flagged register returned {(int)flaggedResp.StatusCode} {flaggedResp.ReasonPhrase} (Gateway predates issue #324 or refused); will retry");
            return false;
        }

        // Verify-before-advertise (issue #197): never register an endpoint that does not
        // demonstrably answer. The verifier (composed in ControlApiHost) asserts the
        // Director's own tailscale serve mapping and probes the advertised URL from the
        // outside-in. On failure the precise reason lands in THIS machine's log - the one
        // place someone can act on it - instead of an eternal unreachable flap on the Gateway.
        if (EndpointVerifier is not null)
        {
            var reason = await EndpointVerifier(req.TailnetEndpoint, ct);
            if (reason is not null)
            {
                LastVerifyError = reason;
                FileLog.Write($"[GatewayClient] NOT registering {req.TailnetEndpoint}: {reason}");
                _monitor?.ReportRegistrationFailure($"This Director's own front door failed verification: {reason}");
                return false;
            }
            LastVerifyError = null;
        }

        FileLog.Write($"[GatewayClient] POST /directors/register: endpoint={req.TailnetEndpoint}");
        var resp = await _http.PostAsJsonAsync("directors/register", req, ct);
        if (resp.IsSuccessStatusCode)
        {
            _advertisedEndpoint = req.TailnetEndpoint;
            FileLog.Write($"[GatewayClient] Registered: status={(int)resp.StatusCode}, endpoint={req.TailnetEndpoint}");
            return true;
        }

        FileLog.Write($"[GatewayClient] Register returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
        _monitor?.ReportRegistrationFailure($"Gateway refused registration: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        return false;
    }

    private void HeartbeatTick()
    {
        if (_disposed || _cts is null || _cts.IsCancellationRequested) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!_registered)
                {
                    // Still trying to do the initial registration. Let RegisterLoop handle it.
                    return;
                }

                // Issue #324: re-resolve the tailnet identity every cycle (no forever-cache).
                // If the resolvable endpoint differs from what the Gateway knows - Tailscale
                // came up after Director start, went away, or the MagicDNS name changed -
                // re-register so the advertisement heals (or truthfully degrades) within one
                // heartbeat, no restart.
                MaybeReRegisterOnIdentityChange();

                // Issues #223/#224: keep the two-way proof fresh. Re-verifies every
                // ReverifyInterval while green, every tick while failed/unverified - so
                // killing the return path flips the indicator red within one cycle and
                // a repaired path flips it back green within one heartbeat.
                MaybeKickVerify();

                // The per-session state snapshot rides the heartbeat (issue #186): it lets
                // the Gateway reconcile any doorbell ping it missed. Old Gateways ignore
                // the body, so this is compatible in both directions.
                var body = new DirectorHeartbeatRequest { Sessions = _sessionStates?.Invoke() ?? new List<SessionStateSnapshot>() };
                var resp = await _http.PostAsJsonAsync($"directors/{_directorId}/heartbeat", body, _cts.Token);
                if (resp.StatusCode == HttpStatusCode.Gone)
                {
                    // Gateway forgot about us (it restarted or swept us as stale).
                    // Drop registered=false so the next call to RegisterLoop re-registers.
                    FileLog.Write("[GatewayClient] Heartbeat returned 410 Gone, re-registering");
                    _registered = false;
                    _ = Task.Run(() => RegisterLoop(_cts.Token));
                    return;
                }
                if (!resp.IsSuccessStatusCode)
                    FileLog.Write($"[GatewayClient] Heartbeat returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayClient] Heartbeat FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// The turn-end doorbell (issue #186): announce THAT a session's mechanical state
    /// changed - {sessionId, newState}, nothing else. Issue #330 extends the same channel
    /// with an optional event-vocabulary tag (<see cref="DoorbellEvents"/>): session-created,
    /// session-exited, prompt-detected ride the very same fire-and-forget ping. Failures are
    /// logged and dropped (the heartbeat snapshot reconciles within 15s); not sent while
    /// unregistered (the registration itself triggers the Gateway's catch-up).
    /// </summary>
    /// <param name="eventName">Optional <see cref="DoorbellEvents"/> name when this ping
    /// announces a lifecycle moment; null = a plain activity-transition ping (pre-#330 shape).</param>
    public void NotifySessionState(string sessionId, string newState, string? eventName = null)
    {
        if (!_config.IsEnabled || _disposed || !_registered) return;
        var cts = _cts;
        if (cts is null || cts.IsCancellationRequested) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var req = new DoorbellRequest { SessionId = sessionId, NewState = newState, Event = eventName };
                var resp = await _http.PostAsJsonAsync($"directors/{_directorId}/doorbell", req, cts.Token);
                if (!resp.IsSuccessStatusCode)
                    FileLog.Write($"[GatewayClient] doorbell {sessionId} -> {(int)resp.StatusCode} (dropped; heartbeat reconciles)");
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayClient] doorbell {sessionId} FAILED (dropped; heartbeat reconciles): {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Heartbeat-cycle identity re-resolution (issue #324): compare the freshly-resolved
    /// endpoint against what the Gateway currently knows and re-register on any difference.
    /// Runs the actual re-registration on a background task with a re-entrancy guard so a
    /// slow verify-before-advertise never stacks registrations across ticks.
    /// </summary>
    private void MaybeReRegisterOnIdentityChange()
    {
        var resolution = ResolveAdvertisedEndpoint();
        var current = resolution.IsResolved ? resolution.Endpoint : "";
        if (string.Equals(current, _advertisedEndpoint, StringComparison.Ordinal)) return;

        var cts = _cts;
        if (cts is null || cts.IsCancellationRequested) return;
        if (Interlocked.CompareExchange(ref _reRegistering, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                FileLog.Write($"[GatewayClient] Tailnet identity changed: advertised='{_advertisedEndpoint}' resolved='{current}' - re-registering");
                await TryRegisterAsync(cts.Token);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayClient] Identity-change re-register FAILED: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _reRegistering, 0);
            }
        });
    }

    /// <summary>Kick a background handshake when one is due (see <see cref="ReverifyInterval"/>).</summary>
    private void MaybeKickVerify()
    {
        if (_monitor is null) return;
        // A flagged no-endpoint registration (issue #324) has nothing the Gateway could call
        // back; a handshake would only overwrite the precise identity-failure message with a
        // generic callback error. The heartbeat identity re-resolve restores verification
        // the moment a real endpoint registers.
        if (string.IsNullOrEmpty(_advertisedEndpoint)) return;
        if (_monitor.Status == GatewayConnectionStatus.Verified
            && _lastVerifyStartedUtc + ReverifyInterval > DateTime.UtcNow)
            return;
        var cts = _cts;
        if (cts is null || cts.IsCancellationRequested) return;
        _ = Task.Run(() => VerifyAsync(cts.Token));
    }

    /// <summary>
    /// Run ONE two-way handshake (issues #223/#224): POST a fresh nonce to the Gateway's
    /// /directors/{id}/verify (this call arriving proves leg 1), which makes the Gateway
    /// dial GET /verify/{nonce} back on the advertised endpoint (leg 2). The verdict -
    /// including the cross-check that the callback actually landed HERE - goes to the
    /// <see cref="GatewayConnectionMonitor"/>; the return value is for on-demand callers
    /// (the troubleshooting dialog's Re-test). Null when verification is disabled, a
    /// handshake is already in flight, or the verdict never round-tripped.
    /// </summary>
    public async Task<DirectorVerifyResultDto?> VerifyAsync(CancellationToken ct = default)
    {
        if (!_config.IsEnabled || _monitor is null || _disposed) return null;
        if (Interlocked.CompareExchange(ref _verifying, 1, 0) != 0) return null;
        var nonce = _monitor.BeginHandshake();
        try
        {
            _lastVerifyStartedUtc = DateTime.UtcNow;
            var resp = await _http.PostAsJsonAsync($"directors/{_directorId}/verify", new DirectorVerifyRequest { Nonce = nonce }, ct);

            if (resp.StatusCode == HttpStatusCode.Gone)
            {
                // Same contract as the heartbeat: the Gateway forgot us, re-register first.
                _monitor.CompleteHandshake(nonce, null, "Gateway no longer knows this Director - re-registering");
                _registered = false;
                var cts = _cts;
                if (cts is not null && !cts.IsCancellationRequested)
                    _ = Task.Run(() => RegisterLoop(cts.Token));
                return null;
            }
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                _monitor.CompleteHandshake(nonce, null,
                    $"The Gateway at {_config.Url} does not support the verify handshake - update the Gateway");
                return null;
            }
            if (!resp.IsSuccessStatusCode)
            {
                _monitor.CompleteHandshake(nonce, null, $"Gateway verify returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }

            var result = await resp.Content.ReadFromJsonAsync<DirectorVerifyResultDto>(cancellationToken: ct);
            if (result is null)
            {
                _monitor.CompleteHandshake(nonce, null, "Gateway verify answered 2xx with an unparsable body");
                return null;
            }

            string? summary = null;
            if (!result.Verified)
            {
                // The leg that broke in #197/#223: heartbeats land, callbacks die.
                summary = $"The Gateway cannot call this Director back: {result.CallbackError}";
            }
            else if (!_monitor.CallbackReceived(nonce))
            {
                // The nonce correlation earning its keep: the Gateway believes its callback
                // succeeded, but it never arrived here - whatever answered the advertised
                // URL was not this process. One leg alone can never fake a green.
                result.Verified = false;
                summary = $"The Gateway's callback was answered by something that is not this Director (at {result.CallbackEndpoint})";
            }
            // Stream leg (the Cockpit terminal path): HTTP control can be fully verified while the
            // WebSocket UPGRADE the terminal needs is dead. Don't flip Verified (the advertise gate
            // is HTTP-based, issue #197) - but record it loudly. result is stored as LastResult, so
            // any director UI reading the monitor sees StreamOk/StreamError too.
            if (result.Verified && !result.StreamOk && result.StreamError is not null)
                FileLog.Write($"[GatewayClient] verify: HTTP callback OK but TERMINAL STREAM leg failed: {result.StreamError}");

            _monitor.CompleteHandshake(nonce, result, summary);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _monitor.AbandonHandshake(nonce); // shutdown: no verdict, no state flip
            return null;
        }
        catch (Exception ex)
        {
            // Includes the HttpClient timeout (TaskCanceledException without ct signaled):
            // leg 1 itself is down or crawling.
            _monitor.CompleteHandshake(nonce, null, $"Cannot reach the Gateway at {_config.Url}: {ex.Message}");
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _verifying, 0);
        }
    }

    // Stamped by BuildRegistrationRequest: true while the last resolution found no tailnet
    // identity. RegisterLoop reads it to keep identity retries at heartbeat cadence (#324).
    private volatile bool _lastResolutionFailed;

    /// <summary>
    /// Build the registration body from a FRESH identity resolution (issue #324 - the
    /// detection ladder runs every time, never a forever-cache).
    ///
    /// The Director binds Kestrel to LOOPBACK only; the address advertised here is the
    /// Tailscale Serve front door (HTTPS, this node's MagicDNS name, THIS Director's own
    /// port, e.g. https://&lt;machine&gt;.&lt;tailnet&gt;.ts.net:7879). It is NEVER loopback - a remote
    /// Gateway or the Cockpit could never reach loopback - and never empty-but-claimed-
    /// reachable: when nothing resolves, <see cref="DirectorRegistrationRequest.TailnetEndpoint"/>
    /// is empty AND <see cref="DirectorRegistrationRequest.EndpointUnreachableReason"/> carries
    /// the reason (the regression the issue-#324 acceptance criteria pin).
    /// </summary>
    internal DirectorRegistrationRequest BuildRegistrationRequest()
    {
        var resolution = ResolveAdvertisedEndpoint();
        _lastResolutionFailed = !resolution.IsResolved;
        return new DirectorRegistrationRequest
        {
            DirectorId = _directorId,
            TailnetEndpoint = resolution.Endpoint,
            EndpointUnreachableReason = resolution.FailureReason,
            Pid = Environment.ProcessId,
            MachineName = Environment.MachineName,
            User = Environment.UserName,
            Version = _version,
            StartedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
        };
    }

    /// <summary>
    /// One outside-in probe of an advertised Director endpoint: GET &lt;endpoint&gt;/healthz
    /// through the real HTTPS front door (the tailscale serve mapping + tailnet cert), NOT
    /// loopback. Returns null when it answers 2xx, else the reason. Deliberately a single
    /// short attempt: <see cref="RegisterLoop"/>'s exponential backoff supplies the long
    /// retry horizon (first-ever serve on a node can take seconds to get its TLS cert).
    /// </summary>
    public static async Task<string?> ProbeAdvertisedEndpointAsync(string endpoint, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var resp = await http.GetAsync($"{endpoint.TrimEnd('/')}/healthz", ct);
            return resp.IsSuccessStatusCode ? null : $"healthz answered HTTP {(int)resp.StatusCode}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "healthz probe timed out after 5s";
        }
        catch (Exception ex)
        {
            return $"healthz probe failed: {ex.Message}";
        }
    }
}
