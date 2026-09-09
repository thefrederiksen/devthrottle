using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
public sealed class GatewayClient : IGatewayHold, IDisposable
{
    /// <summary>How often the heartbeat fires.</summary>
    public static TimeSpan HeartbeatInterval { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Max delay between failed register/heartbeat retries.</summary>
    public static TimeSpan MaxBackoff { get; } = TimeSpan.FromSeconds(60);

    private readonly GatewayConfig _config;
    private readonly string _directorId;
    private readonly string _version;
    private readonly Func<List<SessionStateSnapshot>>? _sessionStates;
    private readonly GatewayConnectionMonitor? _monitor;
    private readonly HttpClient _http;

    // The gateway address currently in use (issue #1233). Starts as _config.Url and is narrowed at
    // Start() to the first reachable entry of _config.CandidateUrls (machine name -> Tailscale -> IP).
    // volatile: read by the fleet-relay one-off clients on other threads.
    private volatile string _activeUrl = "";

    private CancellationTokenSource? _cts;
    private bool _registered;
    private bool _disposed;

    /// <summary>
    /// The gateway address currently in use (issue #1233): <see cref="GatewayConfig.Url"/> until
    /// <see cref="Start"/> narrows it to the first reachable entry of
    /// <see cref="GatewayConfig.CandidateUrls"/>. Exposed for tests and diagnostics.
    /// </summary>
    internal string ActiveUrl => _activeUrl;

    /// <summary>
    /// Reachability probe for a single gateway candidate (issue #1233): returns null when the
    /// address answers GET /healthz, otherwise a reason. Injectable so candidate selection is
    /// unit-tested without a live gateway; production probes /healthz with a short timeout.
    /// </summary>
    internal Func<string, CancellationToken, Task<string?>> ProbeGatewayCandidate { get; set; }

    public bool IsEnabled => _config.IsEnabled;
    public bool IsRegistered => _registered;

    /// <param name="sessionStates">Snapshot provider for the heartbeat's per-session state
    /// map (issue #186). Null (old callers, tests) sends a body-less heartbeat.</param>
    /// <param name="monitor">Two-way handshake state home (issues #223/#224). Owned by the
    /// HOST, not this client, so it survives client replacement on settings changes. Null
    /// (old callers, tests) disables verification entirely - registration and heartbeat
    /// behave exactly as before.</param>
    public GatewayClient(GatewayConfig config, string directorId, string version, Func<List<SessionStateSnapshot>>? sessionStates = null, GatewayConnectionMonitor? monitor = null)
        : this(config, directorId, version, sessionStates, monitor, handler: null)
    {
    }

    /// <summary>
    /// The same client with its message handler supplied, so a test can stand a stub Gateway in front of
    /// it and assert the route it calls, the body it sends and the answer it parses - with no live
    /// Gateway and no port. Internal and test-only for exactly the reason
    /// <see cref="ProbeGatewayCandidate"/> is: production always dials through
    /// <see cref="GatewayHttp.Handler"/>, which is what the parameterless path passes.
    /// </summary>
    internal GatewayClient(GatewayConfig config, string directorId, string version, HttpMessageHandler handler)
        : this(config, directorId, version, sessionStates: null, monitor: null,
               handler ?? throw new ArgumentNullException(nameof(handler)))
    {
    }

    private GatewayClient(GatewayConfig config, string directorId, string version, Func<List<SessionStateSnapshot>>? sessionStates, GatewayConnectionMonitor? monitor, HttpMessageHandler? handler)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _directorId = directorId ?? throw new ArgumentNullException(nameof(directorId));
        _version = version ?? "0.0.0";
        _sessionStates = sessionStates;
        _monitor = monitor;

        _activeUrl = _config.Url;
        ProbeGatewayCandidate = (url, ct) => ProbeGatewayHealthzAsync(url, ct);

        _http = new HttpClient(handler ?? GatewayHttp.Handler())
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        if (_config.IsEnabled)
        {
            _http.BaseAddress = new Uri(_activeUrl.TrimEnd('/') + "/");
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
    /// Turn a failed Gateway relay response into the exception the /fleet/* endpoint will word for the human,
    /// CARRYING the Gateway's own message instead of throwing it away.
    ///
    /// This is the second of two places the same explanation used to die. The Gateway's TunnelFailure dropped
    /// a Director-sent failure into a bodyless 502; these relays then threw "returned HTTP 502 Bad Gateway"
    /// without ever reading the body, so even once the Gateway carried the words, the Director discarded them
    /// one hop before the person who typed the command. Fixing either alone lands nowhere - the CLI's own
    /// error path already parses an "error" key and already tolerates an empty body, so with both legs carried
    /// the Director's real explanation reaches the terminal with no client change at all.
    ///
    /// Falls back to the status line ONLY when there genuinely is no message to show - an empty or non-JSON
    /// body. That is not papering over a failure; it is reporting the only fact available.
    /// </summary>
    private static async Task<InvalidOperationException> RelayFailureAsync(
        HttpResponseMessage resp, string what, CancellationToken ct)
    {
        string? message = null;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out var err)
                    && err.ValueKind == JsonValueKind.String)
                {
                    message = err.GetString();
                }
            }
        }
        catch (JsonException) { /* not JSON - fall through to the status line below */ }

        var statusLine = $"Gateway {what} returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}".TrimEnd();
        FileLog.Write($"[GatewayClient] {what} FAILED: {(string.IsNullOrWhiteSpace(message) ? statusLine : message)}");
        return new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? statusLine : message);
    }

    // ===== The restart cycle (issue #2725) =====
    // Three calls a Director makes ON ITS OWN CREDENTIAL while running the restart cycle the owner
    // accepted: the capability re-check, the guarded restart of itself, and the report against the
    // request. None of them is a session key, so the admission-scoped routes they reach stay refused to
    // session keys exactly as before.

    /// <summary>The Phase 1 capability answer for a machine (GET /machines/{machine}/restart-capability).
    /// Throws when the Gateway is disabled or cannot answer - a check that cannot be made is not a yes.</summary>
    /// <param name="machine">The machine, as the request named it.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MachineRestartCapabilityDto> GetRestartCapabilityAsync(string machine, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; the machine's restart capability cannot be asked for.");
        FileLog.Write($"[GatewayClient] GetRestartCapabilityAsync: GET /machines/{machine}/restart-capability");
        using var resp = await _http.GetAsync($"machines/{Uri.EscapeDataString(machine)}/restart-capability", ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"GET /machines/{machine}/restart-capability", ct);
        var dto = await resp.Content.ReadFromJsonAsync<MachineRestartCapabilityDto>(ct);
        if (dto is null)
            throw new InvalidOperationException($"Gateway GET /machines/{machine}/restart-capability returned an unparsable body.");
        FileLog.Write($"[GatewayClient] GetRestartCapabilityAsync: verdict={dto.Verdict} guardedRestart={dto.GuardedRestart}");
        return dto;
    }

    /// <summary>
    /// Ask this Director's OWN launcher to restart it ONLY IF it is empty, through the Gateway's restart
    /// route (POST /machines/{machine}/director/restart with onlyIfEmpty). The route is admission-scoped;
    /// this Director's credential is not a session key, so nothing is widened.
    ///
    /// confirmProtected is sent TRUE because the owner's accept IS the confirmation the slot guard asks
    /// for, and the executable path is sent as evidence for that guard. Does not throw on a refusal - the
    /// status and body come back verbatim for the cycle to report.
    /// </summary>
    /// <param name="machine">This Director's machine, as the request named it.</param>
    /// <param name="exePath">This process's executable, for the slot guard.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(int Status, string Body)> RequestOwnRestartOnlyIfEmptyAsync(string machine, string? exePath, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; this Director cannot reach its launcher through it.");
        FileLog.Write($"[GatewayClient] RequestOwnRestartOnlyIfEmptyAsync: POST /machines/{machine}/director/restart onlyIfEmpty=true exePath={exePath ?? "(none)"}");
        using var resp = await _http.PostAsJsonAsync($"machines/{Uri.EscapeDataString(machine)}/director/restart",
            new { onlyIfEmpty = true, confirmProtected = true, exePath }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        FileLog.Write($"[GatewayClient] RequestOwnRestartOnlyIfEmptyAsync: HTTP {(int)resp.StatusCode}");
        return ((int)resp.StatusCode, body);
    }

    /// <summary>Report progress or the outcome of the cycle against its request. Throws on failure: a
    /// report that did not land must never look reported.</summary>
    /// <param name="machine">The machine on the request.</param>
    /// <param name="requestId">The request.</param>
    /// <param name="report">The state and the sentence.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task ReportRestartProgressAsync(string machine, string requestId, DirectorRestartProgressReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; the restart request cannot be reported to.");
        FileLog.Write($"[GatewayClient] ReportRestartProgressAsync: request={requestId} state={report.State}: {report.Progress}");
        using var resp = await _http.PostAsJsonAsync(
            $"machines/{Uri.EscapeDataString(machine)}/director/restart-requests/{Uri.EscapeDataString(requestId)}/report", report, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"POST /machines/{machine}/director/restart-requests/{requestId}/report", ct);
    }

    // ===== Fleet relay (issue #705) =====
    // A session can only reach its OWN Director (it is given CC_DIRECTOR_API, never the
    // Gateway URL or the fleet token). These three methods let the Director relay a session's
    // request on to the Gateway using the authenticated _http it already holds, so the fleet
    // token stays server-side and never enters an agent process. They THROW on failure (no
    // best-effort null like GetLatestTurnBriefAsync): the /fleet/* endpoints are the boundary
    // that turns a failure into a clear error, per the no-fallback rule.

    /// <summary>
    /// Relay the Gateway's aggregated fleet session list (GET /sessions) so a session can
    /// discover every other session across the fleet. Throws when the Gateway is disabled or
    /// the call fails.
    /// </summary>
    public async Task<List<SessionDto>> ListFleetSessionsAsync(CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot list the fleet.");

        FileLog.Write("[GatewayClient] ListFleetSessionsAsync: GET /sessions");
        using var resp = await _http.GetAsync("sessions", ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "GET /sessions", ct);

        var list = await resp.Content.ReadFromJsonAsync<List<SessionDto>>(ct);
        if (list is null)
            throw new InvalidOperationException("Gateway GET /sessions returned an unparsable body.");

        FileLog.Write($"[GatewayClient] ListFleetSessionsAsync: {list.Count} session(s)");
        return list;
    }

    /// <summary>
    /// Like <see cref="ListFleetSessionsAsync"/> but asks for the envelope (GET /sessions?envelope=true),
    /// which also carries per-Director reachability (Online / Wobbly / Offline). The plain list
    /// silently DROPS an unreachable Director's sessions while still returning 200, so a caller that
    /// must not act on a partial roster - the destructive worktree reaper - needs the reachability to
    /// tell whether the fleet view is complete. Throws when the Gateway is disabled or the call fails.
    /// </summary>
    public async Task<(List<SessionDto> Sessions, List<DirectorReachabilityDto> Reachability)> ListFleetSessionsWithReachabilityAsync(CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot list the fleet.");

        FileLog.Write("[GatewayClient] ListFleetSessionsWithReachabilityAsync: GET /sessions?envelope=true");
        using var resp = await _http.GetAsync("sessions?envelope=true", ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "GET /sessions?envelope=true", ct);

        var env = await resp.Content.ReadFromJsonAsync<SessionsEnvelope>(ct);
        if (env is null)
            throw new InvalidOperationException("Gateway GET /sessions?envelope=true returned an unparsable body.");

        // FAIL CLOSED on MISSING completeness metadata (inspection): a legacy or version-skewed Gateway
        // may return a 200 envelope WITHOUT the reachability ('directors') array - or without 'sessions'.
        // A destructive caller must distinguish "the server authoritatively returned an empty array"
        // (present but empty) from "the field was absent" (unknown). Coalescing absent-to-empty would
        // make the reaper trust a roster whose completeness it cannot confirm, so an absent field throws.
        if (env.Directors is null)
            throw new InvalidOperationException(
                "Gateway GET /sessions?envelope=true omitted per-Director reachability; cannot confirm the roster is complete.");
        if (env.Sessions is null)
            throw new InvalidOperationException(
                "Gateway GET /sessions?envelope=true omitted the session list; cannot confirm the roster is complete.");

        FileLog.Write($"[GatewayClient] ListFleetSessionsWithReachabilityAsync: {env.Sessions.Count} session(s), {env.Directors.Count} reachability record(s)");
        return (env.Sessions, env.Directors);
    }


    /// <summary>The /sessions?envelope=true response shape: the roster plus per-Director reachability.</summary>
    private sealed class SessionsEnvelope
    {
        public List<SessionDto>? Sessions { get; set; }
        public List<DirectorReachabilityDto>? Directors { get; set; }
    }

    // ===== Workspaces (issue #2722) =====
    // A workspace - a named set of seats - lives on the GATEWAY, not on this machine. The desktop used to
    // keep them as files under its own configuration directory, which meant they were readable only while
    // this machine was up and editable only at this keyboard. The one moment a workspace is worth having
    // is the moment the machine has been restarted out from under its fleet, so the store moved and these
    // four calls are how the desktop reaches it. They THROW on failure, like the fleet relays above: a
    // workspace that was not saved must never look saved.

    /// <summary>Every workspace on the Gateway, newest first. Summaries only.</summary>
    /// <param name="ct">Cancellation.</param>
    public async Task<List<WorkspaceSummaryDto>> ListWorkspacesAsync(CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException(
                "Gateway is not configured; workspaces are stored on the Gateway, so there is nowhere to " +
                "read them from. Connect this Director to a Gateway in Settings.");

        FileLog.Write("[GatewayClient] ListWorkspacesAsync: GET /gateway/workspaces");
        using var resp = await _http.GetAsync("gateway/workspaces", ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "GET /gateway/workspaces", ct);

        var env = await resp.Content.ReadFromJsonAsync<WorkspaceListEnvelope>(ct);
        if (env?.Workspaces is null)
            throw new InvalidOperationException("Gateway GET /gateway/workspaces returned an unparsable body.");

        FileLog.Write($"[GatewayClient] ListWorkspacesAsync: {env.Workspaces.Count} workspace(s)");
        return env.Workspaces;
    }

    /// <summary>One workspace document, or null when the Gateway has no workspace with that id.</summary>
    /// <param name="id">The workspace slug.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<WorkspaceDocument?> GetWorkspaceAsync(string id, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot read a workspace.");

        FileLog.Write($"[GatewayClient] GetWorkspaceAsync: GET /gateway/workspaces/{id}");
        using var resp = await _http.GetAsync($"gateway/workspaces/{Uri.EscapeDataString(id)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"GET /gateway/workspaces/{id}", ct);

        var doc = await resp.Content.ReadFromJsonAsync<WorkspaceDocument>(ct);
        if (doc is null)
            throw new InvalidOperationException(
                $"Gateway GET /gateway/workspaces/{id} returned an unparsable body.");
        return doc;
    }

    /// <summary>Create or replace a workspace. Returns the stored document with the Gateway's timestamps.</summary>
    /// <param name="doc">The workspace to store. Its Id is the route.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<WorkspaceDocument> SaveWorkspaceAsync(WorkspaceDocument doc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!_config.IsEnabled)
            throw new InvalidOperationException(
                "Gateway is not configured; workspaces are stored on the Gateway, so there is nowhere to " +
                "save this. Connect this Director to a Gateway in Settings.");

        FileLog.Write($"[GatewayClient] SaveWorkspaceAsync: PUT /gateway/workspaces/{doc.Id}, seats={doc.Seats.Count}");
        using var resp = await _http.PutAsJsonAsync(
            $"gateway/workspaces/{Uri.EscapeDataString(doc.Id)}", doc, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"PUT /gateway/workspaces/{doc.Id}", ct);

        var saved = await resp.Content.ReadFromJsonAsync<WorkspaceDocument>(ct);
        if (saved is null)
            throw new InvalidOperationException(
                $"Gateway PUT /gateway/workspaces/{doc.Id} returned an unparsable body.");
        return saved;
    }

    /// <summary>
    /// CAPTURE: ask the Gateway to fold a Director's live sessions into a new workspace, which is what a
    /// drain produces (issue #2723). The Gateway does the fold because the seat facts - the agent, the
    /// resolved role, the controller, the model, the transcript path - are ones it already holds
    /// firsthand, and this document is read after the sessions are gone, when nobody can check.
    ///
    /// It CREATES and never replaces, so a second drain cannot silently overwrite a record somebody is
    /// halfway through; the Gateway answers 409 and this throws.
    /// </summary>
    /// <param name="request">What to call it, which Director to fold, and why.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<WorkspaceDocument> CaptureWorkspaceAsync(
        WorkspaceCaptureRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_config.IsEnabled)
            throw new InvalidOperationException(
                "Gateway is not configured; a drain's record is stored on the Gateway, so there is " +
                "nowhere to capture this fleet to. Connect this Director to a Gateway in Settings.");

        FileLog.Write(
            $"[GatewayClient] CaptureWorkspaceAsync: POST /gateway/workspaces, id={request.Id}, " +
            $"directorId={request.DirectorId}");
        using var resp = await _http.PostAsJsonAsync("gateway/workspaces", request, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "POST /gateway/workspaces", ct);

        var doc = await resp.Content.ReadFromJsonAsync<WorkspaceDocument>(ct);
        if (doc is null)
            throw new InvalidOperationException("Gateway POST /gateway/workspaces returned an unparsable body.");

        FileLog.Write($"[GatewayClient] CaptureWorkspaceAsync: id={doc.Id}, seats={doc.Seats.Count}");
        return doc;
    }

    /// <summary>
    /// Create a workspace, and REFUSE if one with that id already exists.
    ///
    /// This is the whole point of the header. Without it the only way not to clobber somebody is to
    /// list, check the answer, and then PUT - three operations with two gaps in them, and a workspace
    /// created inside either gap is destroyed by a caller that had honestly checked. "If-None-Match: *"
    /// is the standard way to say "write this only if it does not exist", and the Gateway decides it
    /// under the same lock it writes under, so there is no gap left to lose anything in.
    /// </summary>
    /// <param name="doc">The workspace to create. Its Id is the route.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="WorkspaceAlreadyExistsException">A workspace with that id is already there.
    /// This is a normal answer, not a transport failure: the caller decides whether to leave it alone
    /// or to overwrite it deliberately.</exception>
    public async Task<WorkspaceDocument> CreateWorkspaceAsync(
        WorkspaceDocument doc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!_config.IsEnabled)
            throw new InvalidOperationException(
                "Gateway is not configured; workspaces are stored on the Gateway, so there is nowhere " +
                "to create this. Connect this Director to a Gateway in Settings.");

        FileLog.Write($"[GatewayClient] CreateWorkspaceAsync: PUT (create-only) /gateway/workspaces/{doc.Id}");
        using var req = new HttpRequestMessage(
            HttpMethod.Put, $"gateway/workspaces/{Uri.EscapeDataString(doc.Id)}")
        {
            Content = JsonContent.Create(doc),
        };
        req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            FileLog.Write($"[GatewayClient] CreateWorkspaceAsync: '{doc.Id}' already exists");
            throw new WorkspaceAlreadyExistsException(doc.Id);
        }

        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"PUT /gateway/workspaces/{doc.Id} (create-only)", ct);

        var saved = await resp.Content.ReadFromJsonAsync<WorkspaceDocument>(ct);
        if (saved is null)
            throw new InvalidOperationException(
                $"Gateway PUT /gateway/workspaces/{doc.Id} returned an unparsable body.");
        return saved;
    }

    /// <summary>Delete a workspace. False when the Gateway had none with that id.</summary>
    /// <param name="id">The workspace slug.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> DeleteWorkspaceAsync(string id, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot delete a workspace.");

        FileLog.Write($"[GatewayClient] DeleteWorkspaceAsync: DELETE /gateway/workspaces/{id}");
        using var resp = await _http.DeleteAsync($"gateway/workspaces/{Uri.EscapeDataString(id)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"DELETE /gateway/workspaces/{id}", ct);
        return true;
    }

    /// <summary>The /gateway/workspaces response shape.</summary>
    private sealed class WorkspaceListEnvelope
    {
        public List<WorkspaceSummaryDto>? Workspaces { get; set; }
    }



    // The fleet-relay legs that used to live here - SendPromptToFleetAsync, RequestDeletionFleetAsync,
    // FanoutToFleetAsync, GetMissionAsync and the workflow-run lookups - are GONE with their only
    // callers, the Director's /fleet/* loopback routes (Remove-the-network-port mission, phase 5).
    // The command line presents its session key to the Gateway directly; nothing relays through the
    // Director any more.

    /// <summary>
    /// Push captured prompts and replies to the Gateway's prompt log (issue #1551): POST /prompts.
    /// Returns how many the Gateway stored, or null when it could not be reached or refused - the
    /// Director keeps no copy, so a null must be treated as "not recorded" and retried, never as done.
    /// Unlike most calls here this does not throw: a logging failure must not break a turn.
    /// </summary>
    public async Task<int?> PushPromptsAsync(IReadOnlyList<PromptRecord> records, CancellationToken ct = default)
    {
        if (!_config.IsEnabled) return null;
        if (records.Count == 0) return 0;

        try
        {
            var body = new PromptIngestRequest { Records = records };
            using var resp = await _http.PostAsJsonAsync("prompts", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayClient] PushPromptsAsync: POST /prompts returned HTTP {(int)resp.StatusCode}");
                return null;
            }

            var parsed = await resp.Content.ReadFromJsonAsync<PromptIngestResponse>(ct);
            return parsed?.Written;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayClient] PushPromptsAsync FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Push one activity-event batch to the Gateway ledger (docs/PLAN-trustworthy-working-start-
    /// 2026-07-24.md). Returns the Gateway's acknowledgement, or null when the push did not land
    /// (disabled, HTTP failure, transport fault) - the caller keeps the outbox records and retries.
    /// A duplicate in the response is a SUCCESSFUL replay: the Gateway already durably holds that
    /// event, which acknowledges it exactly as a fresh write does.
    /// </summary>
    public async Task<ActivityEventIngestResponse?> PushActivityEventsAsync(
        IReadOnlyList<ActivityEventRecord> events, CancellationToken ct = default)
    {
        if (!_config.IsEnabled) return null;
        if (events.Count == 0) return new ActivityEventIngestResponse { Written = 0, Duplicates = 0 };

        try
        {
            var body = new ActivityEventIngestRequest { Events = events };
            using var resp = await _http.PostAsJsonAsync("activity-events/batch", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayClient] PushActivityEventsAsync: POST /activity-events/batch returned HTTP {(int)resp.StatusCode}");
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<ActivityEventIngestResponse>(ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayClient] PushActivityEventsAsync FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Push this Director's repo-state snapshots to the Gateway (issue #2118) - the branches and worktrees
    /// of its registered repositories, which is the one git-hygiene fact the Gateway cannot observe for
    /// itself. Returns the Gateway's acknowledgement, or NULL when the push did not land (Gateway disabled,
    /// HTTP failure, transport fault).
    ///
    /// FAIL-SAFE, exactly like <see cref="PushActivityEventsAsync"/>: a failed push is logged and returns
    /// null, and the caller simply tries again on its next cycle. It never throws into the Director, because
    /// a hygiene feed for a morning email must not be able to disturb the sessions a person is working in.
    /// There is no outbox and no retry queue: this pushes the CURRENT state of the repositories, so a lost
    /// push is not lost data - the next cycle's snapshot supersedes it entirely.
    /// </summary>
    public async Task<RepoStatePushResponse?> PushRepoStateAsync(
        RepoStatePushRequest request, CancellationToken ct = default)
    {
        if (!_config.IsEnabled) return null;
        ArgumentNullException.ThrowIfNull(request);
        if (request.Repositories.Count == 0) return new RepoStatePushResponse { Stored = 0 };

        try
        {
            using var resp = await _http.PostAsJsonAsync("gateway/repostate", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayClient] PushRepoStateAsync: POST /gateway/repostate returned HTTP {(int)resp.StatusCode}");
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<RepoStatePushResponse>(ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayClient] PushRepoStateAsync FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Report whether the skills the Gateway serves could actually be READ on this machine.
    ///
    /// The Gateway can see that it served a skill and still be wrong about whether anything can read it -
    /// only the machine observes that. Without this feed, publishing a skill fleet-wide is done blind, and
    /// a machine where nothing lands looks exactly like a machine where everything is fine.
    ///
    /// FAIL-SAFE, exactly like <see cref="PushRepoStateAsync"/>: a failed push is logged and returns null,
    /// and the caller tries again on its next cycle. No outbox and no retry queue - this pushes the CURRENT
    /// outcome, so a lost push is superseded by the next one rather than lost.
    /// </summary>
    public async Task<SkillPlacementPushResponse?> PushSkillPlacementAsync(
        SkillPlacementPushRequest request, CancellationToken ct = default)
    {
        if (!_config.IsEnabled) return null;
        ArgumentNullException.ThrowIfNull(request);
        if (request.Reports.Count == 0) return new SkillPlacementPushResponse { Stored = 0 };

        try
        {
            using var resp = await _http.PostAsJsonAsync("gateway/skills/placement", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayClient] PushSkillPlacementAsync: POST /gateway/skills/placement " +
                              $"returned HTTP {(int)resp.StatusCode}");
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<SkillPlacementPushResponse>(ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayClient] PushSkillPlacementAsync FAILED: {ex.Message}");
            return null;
        }
    }


    /// <summary>
    /// Snooze Length mission (Phase 3): record or clear a Gateway-owned snooze/hold for a session by
    /// driving the Gateway's <c>POST /sessions/{sid}/hold</c> with the SAME authenticated client (already
    /// pointed at the resolved Gateway address and carrying the fleet token) the other Director-to-Gateway
    /// relays use - so the desktop reuses the Director's existing Gateway connection, never binding its own
    /// URL or token. The Gateway records the snooze-until AND forwards the hold back DOWN to the owning
    /// Director before answering, so on success <c>Session.OnHold</c> is already set.
    ///
    /// This is the <see cref="IGatewayHold"/> seam - the ONE method the Gateway Cleanup migration re-points
    /// from HTTP to the tunnel. Fail-loud (no fallback): THROWS when the Gateway is disabled or the call
    /// does not confirm, so the desktop shows a clear error and sets no local hold.
    /// </summary>
    public async Task RecordHoldAsync(string sessionId, bool onHold, int? snoozeMinutes = null, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("The Gateway is not configured; snooze needs a Gateway connection.");
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required", nameof(sessionId));

        FileLog.Write(
            $"[GatewayClient] RecordHoldAsync: POST /sessions/{sessionId}/hold onHold={onHold}, "
            + $"snoozeMinutes={(snoozeMinutes is null ? "default" : snoozeMinutes.ToString())}");
        // A null SnoozeMinutes is what the plain Snooze click sends: the Gateway then applies the user's
        // default length. Only an explicit "Snooze for" choice carries a value.
        var body = new HoldRequest { OnHold = onHold, SnoozeMinutes = onHold ? snoozeMinutes : null };
        using var resp = await _http.PostAsJsonAsync($"sessions/{sessionId}/hold", body, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"hold for {sessionId}", ct);
    }

    /// <summary>
    /// End a session through THE one stop route (<c>POST /sessions/{sid}/stop</c>) - mission "Stop a
    /// session", Ruling 5. Every surface that ends a session goes through this route: the command line,
    /// the Cockpit, the phone, and this Director's own window. The Gateway records the reason, asks the
    /// owning Director to end the process, and folds the finished words; the caller renders them.
    ///
    /// The Director calling this to stop one of ITS OWN sessions is deliberate and it is not a detour.
    /// The Gateway sends the stop back down this Director's own tunnel, so the session manager removes the
    /// session exactly as it would for a stop asked for from anywhere else, and the rail row goes with it.
    /// The alternative - the window killing the process in-process - is a second stop that records no
    /// reason, and Ruling 4 makes the reason mandatory.
    ///
    /// FAIL LOUD, NO FALLBACK. It throws when the Gateway is not configured and it throws when the call
    /// does not succeed, carrying the Gateway's own sentence where there is one. It NEVER falls back to a
    /// local kill: Ruling 5 names that as the wrong fix in terms, because a stop that quietly worked
    /// without the Gateway would be a stop with no recorded reason, and a silent second path is how two
    /// surfaces come to describe the same event two different ways.
    /// </summary>
    /// <param name="sessionId">The session to stop.</param>
    /// <param name="reason">Why it is being stopped. Required (Ruling 4) and recorded with the stop.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<SessionStopResponse> StopSessionAsync(string sessionId, string reason, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException(
                "This Director is not connected to a Gateway, so a session cannot be stopped from here. "
                + "The reason for a stop is recorded on the Gateway, and a stop that recorded nothing is "
                + "the thing this route exists to replace.");
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required", nameof(sessionId));
        // Refused before the round trip, the way the command line refuses it: there is no point asking the
        // Gateway to tell us what we already know. The Gateway refuses a blank reason too, in its own
        // words, and that refusal is what the failure branch below carries.
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to stop a session", nameof(reason));

        FileLog.Write($"[GatewayClient] StopSessionAsync: POST /sessions/{sessionId}/stop");
        using var resp = await _http.PostAsJsonAsync(
            $"sessions/{sessionId}/stop", new SessionStopRequest { Reason = reason }, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"stop for {sessionId}", ct);

        var answer = await resp.Content.ReadFromJsonAsync<SessionStopResponse>(ct);

        // A BROKEN INSTRUMENT, NOT A FOURTH VERDICT - the same rule the command line applies to the same
        // answer. Every answer this route gives carries a headline; one that does not is a Gateway that
        // did not understand the request, and returning it would leave a surface with nothing to render
        // and no idea that anything was wrong. Said as ignorance, never as an outcome.
        if (answer is null || string.IsNullOrWhiteSpace(answer.Headline))
            throw new InvalidOperationException(
                $"The Gateway returned nothing that says what happened to {sessionId}, so this cannot "
                + "report whether it was stopped. Check the session list to see whether it is still there.");

        FileLog.Write(
            $"[GatewayClient] StopSessionAsync: {sessionId} verdict={answer.Verdict}, "
            + $"processId={(answer.ProcessId?.ToString() ?? "none")}, processEnded={answer.ProcessEnded}, "
            + $"rowRemoved={answer.RowRemoved}, detailLines={answer.Details.Count}");
        return answer;
    }

    /// <summary>
    /// Read the user's snooze lengths and default from the Gateway (<c>GET /gateway/snooze-presets</c>).
    /// Returns null when the Gateway is not configured. Throws when it is configured but the call fails -
    /// the caller decides whether that is fatal; the desktop's cache treats it as "keep the last-known
    /// list" so a right-click never waits on the network.
    /// </summary>
    public async Task<SnoozeOptionsResponse?> GetSnoozeOptionsAsync(CancellationToken ct = default)
    {
        if (!_config.IsEnabled) return null;

        using var resp = await _http.GetAsync("gateway/snooze-presets", ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "snooze lengths", ct);

        var options = await resp.Content.ReadFromJsonAsync<SnoozeOptionsResponse>(ct);
        if (options is null || options.Presets.Length == 0)
            throw new InvalidOperationException("The Gateway returned no snooze lengths.");

        FileLog.Write(
            $"[GatewayClient] GetSnoozeOptionsAsync: presets=[{string.Join(", ", options.Presets)}], "
            + $"default={options.DefaultMinutes}");
        return options;
    }

    // ===== Fleet session numbers (issue #1292) =====
    // The Gateway is the authority for the short three-digit session number (100-999) so it is
    // unique across every Director on every machine. The Director asks here when it creates a
    // session and frees the number when the session ends. Best-effort like the turn-brief fetch:
    // a null / failed allocate means the Director falls back to a local offline number, so these
    // never throw - an unreachable Gateway must not block session creation.

    /// <summary>
    /// Ask the Gateway to hand out this session's fleet-unique three-digit number (issue #1292).
    /// Returns the number when the Gateway answered; null when the Gateway is disabled (not
    /// configured), unreachable, or its pool is exhausted - the caller then assigns a local offline
    /// number instead. Idempotent on the Gateway: asking again for the same session id returns the
    /// same number.
    /// </summary>
    public async Task<int?> AllocateSessionNumberAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_config.IsEnabled || string.IsNullOrWhiteSpace(sessionId)) return null;
        try
        {
            var body = new SessionNumberAllocateRequest { SessionId = sessionId, DirectorId = _directorId };
            using var resp = await _http.PostAsJsonAsync("session-numbers/allocate", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayClient] AllocateSessionNumberAsync {sessionId}: HTTP {(int)resp.StatusCode}");
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<SessionNumberAllocateResponse>(ct);
            return parsed?.Number;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayClient] AllocateSessionNumberAsync {sessionId} FAILED (offline fallback): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Tell the Gateway to free this session's number back to the pool when the session ends
    /// (issue #1292). Fire-and-forget: a lost release is reconciled anyway - the Gateway frees a
    /// number when its owning Director is removed from the registry. No-op while the Gateway is
    /// disabled or unregistered.
    /// </summary>
    public void ReleaseSessionNumber(string sessionId)
    {
        if (!_config.IsEnabled || _disposed || string.IsNullOrWhiteSpace(sessionId)) return;
        var cts = _cts;
        _ = Task.Run(async () =>
        {
            try
            {
                using var resp = await _http.DeleteAsync($"session-numbers/{sessionId}", cts?.Token ?? default);
                if (!resp.IsSuccessStatusCode)
                    FileLog.Write($"[GatewayClient] ReleaseSessionNumber {sessionId} -> {(int)resp.StatusCode} (dropped; director-removal reconciles)");
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayClient] ReleaseSessionNumber {sessionId} FAILED (dropped; director-removal reconciles): {ex.Message}");
            }
        });
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
        FileLog.Write($"[GatewayClient] Start: candidates=[{string.Join(", ", _config.CandidateUrls)}], directorId={_directorId} (tunnel-only: no HTTP register/heartbeat/verify)");

        // Gateway Cleanup mission (tunnel-only): the HTTP register/heartbeat/verify connection loop is GONE.
        // The tunnel (GatewayStreamClient) is the ONLY Gateway connection - its Hello registers this Director
        // with the Gateway and its live state drives the connectivity light. This client survives ONLY as the
        // on-demand caller for the Director's outbound Gateway operations (fleet send/ask/fanout/spawn, hold,
        // session-number). Select the reachable Gateway address once so those calls have a base address; do
        // NOT register, heartbeat, or run the two-way verify handshake (the Gateway no longer dials back).
        _ = Task.Run(async () =>
        {
            try { await SelectActiveUrlAsync(_cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { FileLog.Write($"[GatewayClient] candidate selection failed, using {_activeUrl}: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Gracefully unregister over the LEGACY same-machine discovery plane. Best-effort: a failing DELETE is
    /// logged and does not throw.
    ///
    /// DO NOT READ THIS AS THE SHUTDOWN GOODBYE - it is not, on any Gateway a Director talks to today. The
    /// endpoint is refused outright on a hosted Gateway (GatewayEndpoints returns
    /// LegacyDiscoveryPlaneUnavailable), and even where it is allowed it resolves the id in the Local tenant
    /// partition only. The tunnel-era goodbye is <c>GatewayStreamClient.NotifyDirectorStoppingAsync</c>, which
    /// retires the registration through <c>DirectorRegistry.MarkStopped</c>.
    ///
    /// This comment used to say the Gateway "will sweep the stale entry within 60 s anyway", which read as
    /// "an unregistration that fails costs nothing". The eviction horizon is TWENTY-FOUR HOURS
    /// (DirectorRegistry.DefaultEvictionHorizon), so for a full day the Gateway went on expecting a Director
    /// that had already gone - and every roster read reported it unreachable. A stale reassurance in a comment
    /// is how a hole stays open: it answers the question the next reader was about to ask.
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed) return;
        if (!_config.IsEnabled) return;

        FileLog.Write($"[GatewayClient] StopAsync: directorId={_directorId}");
        try { _cts?.Cancel(); } catch { }

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
        _cts?.Dispose();
        _http.Dispose();
    }

    // ===== Internals =====

    /// <summary>
    /// Walk <see cref="GatewayConfig.CandidateUrls"/> in priority order (machine name, then Tailscale,
    /// then IP) and switch the active address to the first that answers GET /healthz (issue #1233).
    /// Setting the shared client's base address here is safe: this runs before the first outbound call.
    /// With a single candidate (older installs, or a manual override with no discovered fallbacks) there
    /// is nothing to choose and the method is a no-op. When nothing answers yet the active address is
    /// left as-is, so the on-demand Gateway calls still attempt it.
    /// Internal so the selection wiring is unit-tested through <see cref="ProbeGatewayCandidate"/>.
    /// </summary>
    internal async Task SelectActiveUrlAsync(CancellationToken ct)
    {
        var candidates = _config.CandidateUrls;
        if (candidates.Count <= 1) return;

        var selection = await GatewayEndpointSelector.SelectAsync(candidates, ProbeGatewayCandidate, ct);
        if (selection.Found)
        {
            if (!string.Equals(selection.ChosenUrl, _activeUrl, StringComparison.OrdinalIgnoreCase))
            {
                FileLog.Write($"[GatewayClient] selected reachable gateway {selection.ChosenUrl} from {candidates.Count} candidate(s)");
                SetActiveUrl(selection.ChosenUrl!);
            }
        }
        else
        {
            FileLog.Write($"[GatewayClient] no gateway candidate answered among {candidates.Count}; will attempt {_activeUrl} and retry");
        }
    }

    // Point the shared client at a newly chosen gateway address. Only call before the first request is
    // sent on _http - HttpClient forbids changing BaseAddress afterwards - which is the Start-time
    // selection above, before RegisterLoop.
    private void SetActiveUrl(string url)
    {
        _activeUrl = url;
        _http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
    }

    // Default candidate probe: GET <url>/healthz with a short timeout. /healthz is unauthenticated so
    // no token is presented. Never throws - a transport failure or timeout becomes a reason string,
    // which makes GatewayEndpointSelector move to the next candidate.
    private static async Task<string?> ProbeGatewayHealthzAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(5) };
        return await GatewayEndpointSelector.ProbeHealthzAsync(url, http, ct);
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
        // Gateway Cleanup mission (tunnel-only): no longer gated on HTTP registration (which is gone) - the
        // doorbell is an outbound Director->Gateway front-door notify that fires whenever a Gateway is
        // configured. Failures are dropped; the tunnel's periodic snapshot re-push reconciles roster state.
        if (!_config.IsEnabled || _disposed) return;
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

    // Gateway Cleanup mission (tunnel-only): the two-way verify handshake is GONE from this client.
    // MaybeKickVerify and VerifyAsync used to POST directors/{id}/verify and ask the Gateway to dial this
    // Director back on its advertised endpoint. The Gateway deleted that route - and its whole dial-back -
    // at the cut ("Liveness is now the tunnel connection itself"), and the Director's own GET /verify/{nonce}
    // callback door went with it, so the handshake could never pass again by two independent counts. What it
    // could still do was LIE: on the 404 it told the owner "the Gateway does not support the verify handshake
    // - update the Gateway", which sent him to fix a Gateway that was already correct, and flipped a healthy
    // green light to red for doing nothing but opening diagnostics. Liveness is the tunnel: GatewayStreamClient
    // marks the monitor connected/reconnecting directly.

    /// <summary>
    /// Build the registration body. The Remove-the-network-port mission ended the advertised
    /// inbound endpoint: this Director listens on nothing, so there is no address to resolve, no
    /// Tailscale Serve front door to name, and no unreachable-endpoint reason to carry - the
    /// registration is identity only (who, where, which version, which process), and reachability
    /// is the tunnel connection itself. Older Directors still send an endpoint; the contract
    /// fields stay for them.
    /// </summary>
    internal DirectorRegistrationRequest BuildRegistrationRequest()
    {
        return new DirectorRegistrationRequest
        {
            DirectorId = _directorId,
            TailnetEndpoint = "",
            EndpointUnreachableReason = null,
            Pid = Environment.ProcessId,
            MachineName = Environment.MachineName,
            User = Environment.UserName,
            Version = _version,
            StartedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
        };
    }
}
