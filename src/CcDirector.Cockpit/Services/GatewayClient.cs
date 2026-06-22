using System.Net.Http.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Cockpit.Services;

/// <summary>
/// Thin typed client over the CC Director Gateway. The Cockpit talks ONLY to the
/// Gateway for the roster: the Gateway already fans out to every registered Director
/// and returns the union. Per-session live actions (terminal stream, prompts) will
/// later go direct to each session's owning Director via its TailnetEndpoint.
/// </summary>
public sealed class GatewayClient
{
    private readonly HttpClient _http;
    private readonly ILogger<GatewayClient> _log;

    public GatewayClient(HttpClient http, ILogger<GatewayClient> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>The Gateway base URL this client is pointed at (for display/diagnostics).</summary>
    public string BaseUrl => _http.BaseAddress?.ToString() ?? "(unset)";

    /// <summary>
    /// Fetch the full cross-Director roster. Returns sessions + unreachable-Director
    /// errors. Throws on transport failure (the caller surfaces it as a banner) rather
    /// than masking it with an empty list -- an empty roster and a dead Gateway must
    /// look different.
    /// </summary>
    public async Task<SessionsEnvelope> GetSessionsAsync(CancellationToken ct = default)
    {
        _log.LogDebug("GetSessionsAsync: GET {Base}sessions?envelope=true", _http.BaseAddress);
        var env = await _http.GetFromJsonAsync<SessionsEnvelope>("sessions?envelope=true", ct);
        var result = env ?? new SessionsEnvelope();
        _log.LogDebug("GetSessionsAsync: {Sessions} sessions, {Errors} unreachable directors",
            result.Sessions.Count, result.MachineErrors.Count);
        return result;
    }

    /// <summary>
    /// Issue #531: drive ONE turn of a session through the wingman. Sends the person's message
    /// into the working session, waits for the agent to finish, and returns the agent's reply
    /// plus the wingman's faithful, speakable translation of it. Never throws on a handled error;
    /// the result carries <see cref="WingmanVoiceResult.Error"/> instead so the Voice tab can show it.
    /// </summary>
    public async Task<WingmanVoiceResult> WingmanVoiceTurnAsync(string sid, string text, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"sessions/{Uri.EscapeDataString(sid)}/wingman/voice-turn", new { text }, ct);
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<WingmanVoiceResult>(cancellationToken: ct)
                   ?? new WingmanVoiceResult { Error = "empty response from gateway" };
        var err = await resp.Content.ReadFromJsonAsync<WingmanVoiceResult>(cancellationToken: ct);
        return new WingmanVoiceResult { Error = err?.Error ?? $"gateway returned {(int)resp.StatusCode}" };
    }

    /// <summary>
    /// Issue #531: the direct-to-wingman path - the person talks to the wingman itself, not the
    /// working session. Returns the wingman's speakable answer.
    /// </summary>
    public async Task<WingmanVoiceResult> WingmanAskDirectAsync(string text, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("wingman/ask-direct", new { text }, ct);
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<WingmanVoiceResult>(cancellationToken: ct)
                   ?? new WingmanVoiceResult { Error = "empty response from gateway" };
        var err = await resp.Content.ReadFromJsonAsync<WingmanVoiceResult>(cancellationToken: ct);
        return new WingmanVoiceResult { Error = err?.Error ?? $"gateway returned {(int)resp.StatusCode}" };
    }

    /// <summary>
    /// Issue #472: the DevThrottle product/docs Q&amp;A path for the Cockpit Learning page. Posts a
    /// free-text question ABOUT THE PRODUCT to the Gateway and returns the wingman's answer. The
    /// Cockpit talks only to the Gateway here - never a Director. Never throws on a handled error;
    /// the result carries <see cref="WingmanVoiceResult.Error"/> instead so the page can show it.
    /// </summary>
    public async Task<WingmanVoiceResult> WingmanAskDevThrottleAsync(string text, CancellationToken ct = default)
    {
        _log.LogDebug("WingmanAskDevThrottleAsync: POST {Base}wingman/ask-devthrottle, len={Len}", _http.BaseAddress, text?.Length ?? 0);
        var resp = await _http.PostAsJsonAsync("wingman/ask-devthrottle", new { text }, ct);
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<WingmanVoiceResult>(cancellationToken: ct)
                   ?? new WingmanVoiceResult { Error = "empty response from gateway" };
        var err = await resp.Content.ReadFromJsonAsync<WingmanVoiceResult>(cancellationToken: ct);
        return new WingmanVoiceResult { Error = err?.Error ?? $"gateway returned {(int)resp.StatusCode}" };
    }

    /// <summary>
    /// The cross-fleet "Interrupted sessions" list (issue #212 W3): sessions whose Director
    /// died abnormally, enriched with last-known rail line + headline. Empty list when there
    /// is nothing to recover. Throws on transport failure (surfaced as a banner).
    /// </summary>
    public async Task<List<InterruptedSessionDto>> GetInterruptedAsync(CancellationToken ct = default)
    {
        var list = await _http.GetFromJsonAsync<List<InterruptedSessionDto>>("interrupted", ct);
        return list ?? new List<InterruptedSessionDto>();
    }

    /// <summary>
    /// Dismiss one interrupted session from the Interrupted sessions list (issue #212 W3). Routed by the Gateway
    /// to the live Director that surfaced it (<paramref name="reportedByDirectorId"/>).
    /// </summary>
    public async Task DismissInterruptedAsync(string deadDirectorId, int deadPid, string reportedByDirectorId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync(
            $"interrupted/{Uri.EscapeDataString(deadDirectorId)}/{deadPid}?via={Uri.EscapeDataString(reportedByDirectorId)}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"dismiss failed ({(int)resp.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// Restore one interrupted session (issue #212 W4): the Gateway creates a continuation
    /// session in the dead session's repo, seeded with its surviving turn-brief context, and
    /// removes the row from the Interrupted sessions list. Returns where the continuation ended up.
    /// </summary>
    public async Task<RestoreInterruptedResponse> RestoreInterruptedAsync(
        string deadDirectorId, int deadPid, string sessionId, string reportedByDirectorId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"interrupted/{Uri.EscapeDataString(deadDirectorId)}/{deadPid}/restore",
            new RestoreInterruptedRequest { SessionId = sessionId, Via = reportedByDirectorId }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"restore failed ({(int)resp.StatusCode}): {body}");
        }
        var result = await resp.Content.ReadFromJsonAsync<RestoreInterruptedResponse>(cancellationToken: ct);
        return result ?? throw new HttpRequestException("restore returned an empty body");
    }

    /// <summary>
    /// Dismiss ONE session from an interrupted journal (issue #212 W4), keeping its siblings
    /// in the Interrupted sessions list. The group-level sweep stays <see cref="DismissInterruptedAsync"/>.
    /// </summary>
    public async Task DismissInterruptedSessionAsync(
        string deadDirectorId, int deadPid, string sessionId, string reportedByDirectorId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync(
            $"interrupted/{Uri.EscapeDataString(deadDirectorId)}/{deadPid}/sessions/{Uri.EscapeDataString(sessionId)}?via={Uri.EscapeDataString(reportedByDirectorId)}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"dismiss session failed ({(int)resp.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// List every Director the Gateway knows about (the "on which Director?" picker for a new
    /// session). Reads aggregate through the Gateway, so this is a Gateway call.
    /// </summary>
    public async Task<List<DirectorDto>> GetDirectorsAsync(CancellationToken ct = default)
    {
        var d = await _http.GetFromJsonAsync<List<DirectorDto>>("directors", ct);
        return d ?? new List<DirectorDto>();
    }

    /// <summary>
    /// One session by id (<c>GET /sessions/{sid}</c>), enriched by the Gateway aggregator
    /// (MachineName/TailnetEndpoint/ViewUrl) exactly like the roster rows. Null when no
    /// Director owns it (deleted, or its Director is unreachable). Throws on transport
    /// failure - the detail page surfaces it as a banner.
    /// </summary>
    public async Task<SessionDto?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"sessions/{Uri.EscapeDataString(sessionId)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessionDto>(cancellationToken: ct);
    }

    /// <summary>
    /// A read-only snapshot of the ONE-brain wingman pipeline (<c>GET /wingman/queue</c>, issue #239):
    /// in-flight session, ordered queue, recent briefs, and brain health. Throws on transport failure
    /// (the Wingman Pipeline page surfaces it as a banner) so a dead Gateway never looks like an idle
    /// pipeline.
    /// </summary>
    public async Task<WingmanQueueDto> GetWingmanQueueAsync(CancellationToken ct = default)
    {
        var snapshot = await _http.GetFromJsonAsync<WingmanQueueDto>("wingman/queue", ct);
        return snapshot ?? throw new HttpRequestException("wingman/queue returned an empty body");
    }

    /// <summary>
    /// Gateway health summary (<c>GET /healthz</c>): version, server time, fleet counts.
    /// Throws on transport failure - the dashboard surfaces it as a banner.
    /// </summary>
    public async Task<HealthDto> GetHealthAsync(CancellationToken ct = default)
    {
        var h = await _http.GetFromJsonAsync<HealthDto>("healthz", ct);
        return h ?? throw new HttpRequestException("healthz returned an empty body");
    }

    /// <summary>
    /// Gateway About/diagnostics (<c>GET /about</c>): product, version, build date, install root,
    /// the one Cockpit URL, and the installed component versions on the Gateway box. Throws on
    /// transport failure - the About page surfaces it as a banner.
    /// </summary>
    public async Task<AboutDto> GetAboutAsync(CancellationToken ct = default)
    {
        var a = await _http.GetFromJsonAsync<AboutDto>("gateway/about", ct);
        return a ?? throw new HttpRequestException("about returned an empty body");
    }

    // ===== DevThrottle account (issue #648): the Cockpit account surface is a pure client of the
    // Gateway account endpoints. The credential lives on the Gateway, so identity is READ from
    // GET /account/status (#638) and logout CLEARS it via POST /account/logout (#648). The Cockpit
    // never sees, stores, or displays the raw token - the contract carries only the boolean + identity.

    /// <summary>
    /// The Gateway's signed-in DevThrottle status and identity (<c>GET /account/status</c>, issue #638):
    /// <c>{ signedIn, email?, provider? }</c>, computed locally on the Gateway with no cloud call. Throws
    /// on transport failure (the Account page surfaces it as a banner) so a dead Gateway never looks like
    /// a signed-out one.
    /// </summary>
    public async Task<AccountStatusDto> GetAccountStatusAsync(CancellationToken ct = default)
    {
        _log.LogDebug("GetAccountStatusAsync: GET {Base}account/status", _http.BaseAddress);
        var status = await _http.GetFromJsonAsync<AccountStatusDto>("account/status", ct);
        return status ?? throw new HttpRequestException("account/status returned an empty body");
    }

    /// <summary>
    /// Clear the Gateway's DevThrottle credential (<c>POST /account/logout</c>, issue #648). After this
    /// the Gateway reports <c>signedIn:false</c> and returns to its sign-in prompt. User action: throws
    /// with the server error on failure so the Account page can show it. Returns the post-logout status
    /// the Gateway echoes back (signed-out) so the page can confirm without a second round-trip.
    /// </summary>
    public async Task<AccountStatusDto> LogoutAccountAsync(CancellationToken ct = default)
    {
        _log.LogInformation("LogoutAccountAsync: POST {Base}account/logout", _http.BaseAddress);
        var resp = await _http.PostAsync("account/logout", content: null, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"logout failed ({(int)resp.StatusCode}): {ExtractError(body)}");
        }
        return await resp.Content.ReadFromJsonAsync<AccountStatusDto>(cancellationToken: ct)
            ?? throw new HttpRequestException("account/logout returned an empty body");
    }

    // ===== Telemetry consent (issue #649): the Cockpit telemetry surface is a pure client of the
    // Gateway telemetry-consent endpoints. The consent setting lives on the Gateway (fleet-wide), so
    // the Cockpit READS it from GET /gateway/telemetry-consent and toggles it via
    // PUT /gateway/telemetry-consent. The toggle gates only the richer usage telemetry; the always-on
    // login/startup auth-floor events are never gated by it.

    /// <summary>
    /// The Gateway's fleet-wide richer-usage-telemetry consent (<c>GET /gateway/telemetry-consent</c>,
    /// issue #649): <c>{ enabled }</c>, default ON. Throws on transport failure (the Telemetry page
    /// surfaces it as a banner) so a dead Gateway never looks like a consented-off fleet.
    /// </summary>
    public async Task<TelemetryConsentDto> GetTelemetryConsentAsync(CancellationToken ct = default)
    {
        _log.LogDebug("GetTelemetryConsentAsync: GET {Base}gateway/telemetry-consent", _http.BaseAddress);
        var dto = await _http.GetFromJsonAsync<TelemetryConsentDto>("gateway/telemetry-consent", ct);
        return dto ?? throw new HttpRequestException("gateway/telemetry-consent returned an empty body");
    }

    /// <summary>
    /// Set the Gateway's fleet-wide richer-usage-telemetry consent (<c>PUT /gateway/telemetry-consent</c>,
    /// issue #649). Turning it off stops the richer usage events fleet-wide. User action: throws with the
    /// server error on failure so the Telemetry page can show it. Returns the post-set value the Gateway
    /// echoes back.
    /// </summary>
    public async Task<TelemetryConsentDto> SetTelemetryConsentAsync(bool enabled, CancellationToken ct = default)
    {
        _log.LogInformation("SetTelemetryConsentAsync: PUT {Base}gateway/telemetry-consent enabled={Enabled}", _http.BaseAddress, enabled);
        var resp = await _http.PutAsJsonAsync("gateway/telemetry-consent", new { enabled }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"set telemetry consent failed ({(int)resp.StatusCode}): {ExtractError(body)}");
        }
        return await resp.Content.ReadFromJsonAsync<TelemetryConsentDto>(cancellationToken: ct)
            ?? throw new HttpRequestException("telemetry-consent set returned an empty body");
    }

    /// <summary>
    /// The repositories a given Director offers for a new session. The Gateway proxies this to
    /// the Director's <c>GET /repos</c>, so the Cockpit never needs that Director's endpoint.
    /// </summary>
    public async Task<List<RepositoryDto>> GetReposAsync(string directorId, CancellationToken ct = default)
    {
        var r = await _http.GetFromJsonAsync<List<RepositoryDto>>($"directors/{directorId}/repos", ct);
        return r ?? new List<RepositoryDto>();
    }

    /// <summary>
    /// Create a session on a specific Director via the Gateway proxy
    /// (<c>POST /directors/{id}/sessions</c>). Returns the created session.
    /// </summary>
    public async Task<SessionDto?> CreateSessionAsync(string directorId, NewSessionRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"directors/{directorId}/sessions", req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessionDto>(cancellationToken: ct);
    }

    /// <summary>Remove a repo from a Director's recent list (<c>DELETE /directors/{id}/repos</c>).</summary>
    public async Task RemoveRepoAsync(string directorId, string path, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"directors/{directorId}/repos?path={Uri.EscapeDataString(path)}", ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// List sub-directories of <paramref name="path"/> on a Director's filesystem (Browse... button).
    /// A null/empty path lists the drive roots. Returns null on transport failure.
    /// </summary>
    public async Task<DirectoryListingDto?> ListDirectoryAsync(string directorId, string? path, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(path)
            ? $"directors/{directorId}/fs/list"
            : $"directors/{directorId}/fs/list?path={Uri.EscapeDataString(path)}";
        return await _http.GetFromJsonAsync<DirectoryListingDto>(url, ct);
    }

    /// <summary>
    /// Hand a session's context over to a new or existing session (<c>POST /handover</c>). The
    /// Gateway orchestrates: same-Director it proxies; cross-Director it reads the prose context
    /// and spawns the target with it as a pre-prompt. Throws with the server error on failure.
    /// </summary>
    public async Task<HandoverResponse?> HandoverAsync(HandoverRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("handover", req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"handover failed ({(int)resp.StatusCode}): {body}");
        }
        return await resp.Content.ReadFromJsonAsync<HandoverResponse>(cancellationToken: ct);
    }

    /// <summary>
    /// Rename a session (<c>PATCH /sessions/{sid}</c>; the Gateway proxies to the owning
    /// Director). Returns the updated DTO. Throws with the server error on failure.
    /// </summary>
    public async Task<SessionDto?> RenameSessionAsync(string sessionId, string name, CancellationToken ct = default)
    {
        _log.LogInformation("RenameSessionAsync: {SessionId} -> \"{Name}\"", sessionId, name);
        var resp = await _http.PatchAsJsonAsync($"sessions/{Uri.EscapeDataString(sessionId)}", new { name }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"rename failed ({(int)resp.StatusCode}): {body}");
        }
        return await resp.Content.ReadFromJsonAsync<SessionDto>(cancellationToken: ct);
    }

    /// <summary>
    /// All Gateway-stored TurnBriefs for a session, newest first (issue #185: the warm-brain
    /// stamping machine writes briefs on the GATEWAY; the Cockpit reads them from here first
    /// and falls back to the owning Director's store only while that pipeline still exists).
    /// Null on transport failure - the caller falls back, never renders an error for this.
    /// </summary>
    public async Task<TurnBriefsResponse?> GetTurnBriefsAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"sessions/{Uri.EscapeDataString(sessionId)}/turnbriefs", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<TurnBriefsResponse>(cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            _log.LogDebug(ex, "GetTurnBriefsAsync transport failure for {Sid}", sessionId);
            return null;
        }
    }

    /// <summary>The latest Gateway-stored TurnBrief; null on 404 (none yet) or transport failure.</summary>
    public async Task<TurnBriefDto?> GetLatestTurnBriefAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"sessions/{Uri.EscapeDataString(sessionId)}/turnbriefs/latest", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<TurnBriefDto>(cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            _log.LogDebug(ex, "GetLatestTurnBriefAsync transport failure for {Sid}", sessionId);
            return null;
        }
    }

    /// <summary>Trigger the "I am lost - explain" deep dive (#217). User action: throws on
    /// failure so the button can show the error.</summary>
    public async Task<ExplainAcceptedResponse> PostExplainAsync(string sessionId, CancellationToken ct = default)
    {
        _log.LogDebug("Explain sid={Sid}", sessionId);
        var resp = await _http.PostAsync($"sessions/{Uri.EscapeDataString(sessionId)}/explain", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ExplainAcceptedResponse>(cancellationToken: ct)
            ?? new ExplainAcceptedResponse { Accepted = true, State = "Explaining" };
    }

    /// <summary>The newest explain report (#217); null on 404 (none yet) or transport failure
    /// - same render-without-it idiom as the brief getters.</summary>
    public async Task<ExplainReportDto?> GetLatestExplainAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"sessions/{Uri.EscapeDataString(sessionId)}/explain/latest", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<ExplainReportDto>(cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            _log.LogDebug(ex, "GetLatestExplainAsync transport failure for {Sid}", sessionId);
            return null;
        }
    }

    /// <summary>Brief vote/reason feedback (#207) - stored Gateway-side as a replayable
    /// labeled example with the full TurnPackage.</summary>
    public async Task<TurnBriefFeedbackResponse> PostBriefFeedbackAsync(
        string sessionId,
        int turnNumber,
        string vote,
        string note,
        string? feedbackId = null,
        CancellationToken ct = default)
    {
        _log.LogDebug("BriefFeedback sid={Sid} turn={Turn} vote={Vote} id={FeedbackId}", sessionId, turnNumber, vote, feedbackId);
        var resp = await _http.PostAsJsonAsync($"sessions/{Uri.EscapeDataString(sessionId)}/turnbriefs/feedback",
            new TurnBriefFeedbackRequest { TurnNumber = turnNumber, Vote = vote, Note = note, FeedbackId = feedbackId }, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TurnBriefFeedbackResponse>(cancellationToken: ct)
            ?? new TurnBriefFeedbackResponse { Saved = true };
    }

    public async Task<TurnBriefFeedbackListResponse?> GetBriefFeedbackAsync(int count = 50, CancellationToken ct = default)
    {
        // No-fallback: a Gateway error must surface on the Feedback page (the caller's
        // catch shows it), never render as an empty "no feedback yet" list.
        var resp = await _http.GetAsync($"turnbriefs/feedback?count={count}", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TurnBriefFeedbackListResponse>(cancellationToken: ct);
    }

    // ===== Named work lists (issue #275, client of #273's /lists surface) =====
    // The Cockpit is a CLIENT of the shared Gateway list object: every create/append/reorder/
    // remove goes through these calls, so the Cockpit never owns a copy or its own ordering.

    /// <summary>
    /// Every named work list the Gateway holds (<c>GET /lists</c>). Throws on transport failure
    /// (the Lists page surfaces it as a banner) so a dead Gateway never looks like an empty fleet.
    /// </summary>
    public async Task<List<WorkListDto>> GetWorkListsAsync(CancellationToken ct = default)
    {
        _log.LogDebug("GetWorkListsAsync: GET {Base}lists", _http.BaseAddress);
        var env = await _http.GetFromJsonAsync<WorkListsEnvelope>("lists", ct);
        return env?.Lists ?? new List<WorkListDto>();
    }

    /// <summary>One named list by name (<c>GET /lists/{name}</c>); null on 404 (no such list).</summary>
    public async Task<WorkListDto?> GetWorkListAsync(string name, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"lists/{Uri.EscapeDataString(name)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WorkListDto>(cancellationToken: ct);
    }

    /// <summary>
    /// Create a named list (<c>POST /lists</c>). User action: throws on failure (incl. 409 when the
    /// name is taken) so the create dialog can show the server's message.
    /// </summary>
    public async Task CreateWorkListAsync(string name, CancellationToken ct = default)
    {
        _log.LogInformation("CreateWorkListAsync: \"{Name}\"", name);
        var resp = await _http.PostAsJsonAsync("lists", new { name }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"create list failed ({(int)resp.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// Append one structured item ref to a list (<c>POST /lists/{name}/items</c>). User action:
    /// throws on failure so the add-item form can show the server's message.
    /// </summary>
    public async Task AppendWorkListItemAsync(string name, WorkListItemRef item, CancellationToken ct = default)
    {
        _log.LogInformation("AppendWorkListItemAsync: list=\"{Name}\" source={Source} id={Id}", name, item.Source, item.Id);
        var resp = await _http.PostAsJsonAsync($"lists/{Uri.EscapeDataString(name)}/items", item, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"append item failed ({(int)resp.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// Replace a list's items with a full ordered array (<c>PATCH /lists/{name}/items</c>) - this is
    /// how reorder is committed to the shared object (never a local-only reorder). User action:
    /// throws on failure so the caller can show the server's message.
    /// </summary>
    public async Task ReorderWorkListItemsAsync(string name, IReadOnlyList<WorkListItemRef> items, CancellationToken ct = default)
    {
        _log.LogInformation("ReorderWorkListItemsAsync: list=\"{Name}\" count={Count}", name, items.Count);
        var resp = await _http.PatchAsJsonAsync($"lists/{Uri.EscapeDataString(name)}/items", items, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"reorder failed ({(int)resp.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// Remove the item addressed by source + id (<c>DELETE /lists/{name}/items/{source}/{id}</c>).
    /// User action: throws on failure so the caller can show the server's message.
    /// </summary>
    public async Task RemoveWorkListItemAsync(string name, string source, string id, CancellationToken ct = default)
    {
        _log.LogInformation("RemoveWorkListItemAsync: list=\"{Name}\" source={Source} id={Id}", name, source, id);
        var resp = await _http.DeleteAsync(
            $"lists/{Uri.EscapeDataString(name)}/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"remove item failed ({(int)resp.StatusCode}): {body}");
        }
    }

    // ===== Cron jobs (epic #479): the Schedule page is a pure client of /cron/jobs (#482-#484) =====

    /// <summary>All cron jobs (<c>GET /cron/jobs</c>). Read path: returns an empty list on a null body.</summary>
    public async Task<List<CronJobDto>> GetCronJobsAsync(CancellationToken ct = default)
    {
        _log.LogDebug("GetCronJobsAsync: GET {Base}cron/jobs", _http.BaseAddress);
        var env = await _http.GetFromJsonAsync<CronJobsEnvelope>("cron/jobs", ct);
        return env?.Jobs ?? new List<CronJobDto>();
    }

    /// <summary>
    /// Create a cron job (<c>POST /cron/jobs</c>). User action: throws on failure (incl. 400 for an
    /// invalid schedule) so the create form can show the server's message inline.
    /// </summary>
    public async Task<CronJobDto?> CreateCronJobAsync(CronJobDto job, CancellationToken ct = default)
    {
        _log.LogInformation("CreateCronJobAsync: name=\"{Name}\" kind={Kind}", job.Name, job.ScheduleKind);
        var resp = await _http.PostAsJsonAsync("cron/jobs", job, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"create cron job failed ({(int)resp.StatusCode}): {body}");
        }
        return await resp.Content.ReadFromJsonAsync<CronJobDto>(cancellationToken: ct);
    }

    /// <summary>Update a cron job (<c>PUT /cron/jobs/{id}</c>). Throws on failure so the caller can show the message.</summary>
    public async Task<CronJobDto?> UpdateCronJobAsync(string id, CronJobDto job, CancellationToken ct = default)
    {
        _log.LogInformation("UpdateCronJobAsync: id={Id}", id);
        var resp = await _http.PutAsJsonAsync($"cron/jobs/{Uri.EscapeDataString(id)}", job, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"update cron job failed ({(int)resp.StatusCode}): {body}");
        }
        return await resp.Content.ReadFromJsonAsync<CronJobDto>(cancellationToken: ct);
    }

    /// <summary>Delete a cron job (<c>DELETE /cron/jobs/{id}</c>). Throws on failure.</summary>
    public async Task DeleteCronJobAsync(string id, CancellationToken ct = default)
    {
        _log.LogInformation("DeleteCronJobAsync: id={Id}", id);
        var resp = await _http.DeleteAsync($"cron/jobs/{Uri.EscapeDataString(id)}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"delete cron job failed ({(int)resp.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// Fire a cron job now (<c>POST /cron/jobs/{id}/run</c>). Throws on failure (incl. 409 when a
    /// prior run is still in flight) so the caller can show the server's message.
    /// </summary>
    public async Task<CronRunRecord?> RunCronJobNowAsync(string id, CancellationToken ct = default)
    {
        _log.LogInformation("RunCronJobNowAsync: id={Id}", id);
        var resp = await _http.PostAsync($"cron/jobs/{Uri.EscapeDataString(id)}/run", content: null, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"run cron job failed ({(int)resp.StatusCode}): {body}");
        }
        return await resp.Content.ReadFromJsonAsync<CronRunRecord>(cancellationToken: ct);
    }

    /// <summary>One cron job's run history (<c>GET /cron/jobs/{id}/runs</c>), newest first.</summary>
    public async Task<List<CronRunRecord>> GetCronRunsAsync(string id, CancellationToken ct = default)
    {
        var env = await _http.GetFromJsonAsync<CronRunsEnvelope>($"cron/jobs/{Uri.EscapeDataString(id)}/runs", ct);
        return env?.Runs ?? new List<CronRunRecord>();
    }

    // ===== Tool pages (issue #183): exes / transcripts / dictionary =====
    // These three pages were static HTML fetching the Gateway same-origin; they are now Blazor
    // components that reach the same endpoints through this server-side client. No endpoint
    // contract changes - the calls below mirror exactly what the static pages issued.

    /// <summary>
    /// Local Directors on this machine + build-slot status (<c>GET /exes/list</c>). Throws on
    /// transport failure so the Exes page surfaces it as an error banner (no fallback empty list).
    /// </summary>
    public async Task<ExesListDto> GetExesAsync(CancellationToken ct = default)
    {
        var dto = await _http.GetFromJsonAsync<ExesListDto>("exes/list", ct);
        return dto ?? throw new HttpRequestException("exes/list returned an empty body");
    }

    /// <summary>Kill a Director and all its sessions (<c>DELETE /directors/{id}</c> with
    /// <c>{force:true}</c>). User action: throws with the server error on failure.</summary>
    public async Task KillDirectorAsync(string directorId, CancellationToken ct = default)
    {
        _log.LogInformation("KillDirectorAsync: {DirectorId}", directorId);
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"directors/{Uri.EscapeDataString(directorId)}")
        {
            Content = JsonContent.Create(new { force = true }),
        };
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"kill director failed ({(int)resp.StatusCode}): {body}");
        }
    }

    /// <summary>Build a slot then launch it (<c>POST /exes/slots/{n}/build-start</c>). User action:
    /// throws with the server's detail message (build output tail) on failure.</summary>
    public async Task<BuildStartResultDto> BuildStartSlotAsync(int slot, CancellationToken ct = default)
    {
        _log.LogInformation("BuildStartSlotAsync: slot {Slot}", slot);
        var resp = await _http.PostAsync($"exes/slots/{slot}/build-start", content: null, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"build-start failed ({(int)resp.StatusCode}): {ExtractError(body)}");
        }
        return await resp.Content.ReadFromJsonAsync<BuildStartResultDto>(cancellationToken: ct)
            ?? throw new HttpRequestException("build-start returned an empty body");
    }

    /// <summary>Delete a slot's built exe (<c>DELETE /exes/slots/{n}</c>). User action: throws with
    /// the server error on failure.</summary>
    public async Task DeleteSlotAsync(int slot, CancellationToken ct = default)
    {
        _log.LogInformation("DeleteSlotAsync: slot {Slot}", slot);
        var resp = await _http.DeleteAsync($"exes/slots/{slot}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"delete slot failed ({(int)resp.StatusCode}): {ExtractError(body)}");
        }
    }

    /// <summary>Every local recording/transcript (<c>GET /ingest/recordings</c>). Throws on transport
    /// failure so the Voice Recorder page surfaces it (no fallback to a misleading empty list).</summary>
    public async Task<List<RecordingListItem>> GetRecordingsAsync(CancellationToken ct = default)
    {
        var list = await _http.GetFromJsonAsync<List<RecordingListItem>>("ingest/recordings", ct);
        return list ?? new List<RecordingListItem>();
    }

    /// <summary>The cleaned transcript text for a recording (<c>GET /ingest/recording/{id}/transcript</c>);
    /// null on 404 (none stored) or transport failure - the page renders a placeholder instead.</summary>
    public async Task<string?> GetTranscriptAsync(string recordingId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"ingest/recording/{Uri.EscapeDataString(recordingId)}/transcript", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            _log.LogDebug(ex, "GetTranscriptAsync transport failure for {Id}", recordingId);
            return null;
        }
    }

    /// <summary>Delete one transient local recording (<c>DELETE /ingest/recording/{id}</c>). User
    /// action: throws with the server error on failure.</summary>
    public async Task DeleteRecordingAsync(string recordingId, CancellationToken ct = default)
    {
        _log.LogInformation("DeleteRecordingAsync: {Id}", recordingId);
        var resp = await _http.DeleteAsync($"ingest/recording/{Uri.EscapeDataString(recordingId)}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"delete recording failed ({(int)resp.StatusCode}): {ExtractError(body)}");
        }
    }

    /// <summary>Copy a recording's transcript + audio into the vault (<c>POST /ingest/recording/{id}/promote</c>).
    /// User action: throws with the server error on failure.</summary>
    public async Task PromoteRecordingAsync(string recordingId, CancellationToken ct = default)
    {
        _log.LogInformation("PromoteRecordingAsync: {Id}", recordingId);
        var resp = await _http.PostAsync($"ingest/recording/{Uri.EscapeDataString(recordingId)}/promote", content: null, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"promote failed ({(int)resp.StatusCode}): {ExtractError(body)}");
        }
    }

    /// <summary>Update a recording's title/subtitle/summary (<c>PATCH /ingest/recording/{id}/meta</c>);
    /// returns the updated record. User action: throws with the server error on failure.</summary>
    public async Task<RecordingListItem> UpdateRecordingMetaAsync(
        string recordingId, string? title, string? subtitle, string? summary, CancellationToken ct = default)
    {
        _log.LogInformation("UpdateRecordingMetaAsync: {Id}", recordingId);
        var resp = await _http.PatchAsJsonAsync(
            $"ingest/recording/{Uri.EscapeDataString(recordingId)}/meta",
            new { title, subtitle, summary }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"save details failed ({(int)resp.StatusCode}): {ExtractError(body)}");
        }
        return await resp.Content.ReadFromJsonAsync<RecordingListItem>(cancellationToken: ct)
            ?? throw new HttpRequestException("meta update returned an empty body");
    }

    /// <summary>The copy-paste agent API guide (<c>GET /ingest/agent-info</c>), plain text. User
    /// action: throws with the server error on failure.</summary>
    public async Task<string> GetAgentInfoAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("ingest/agent-info", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>The dictation glossary (<c>GET /ingest/dictionary</c>). Throws on transport failure so
    /// the Dictionary page surfaces it (no fallback to an empty glossary that could be saved back).</summary>
    public async Task<DictionaryDto> GetDictionaryAsync(CancellationToken ct = default)
    {
        var dict = await _http.GetFromJsonAsync<DictionaryDto>("ingest/dictionary", ct);
        return dict ?? throw new HttpRequestException("ingest/dictionary returned an empty body");
    }

    /// <summary>Replace the whole glossary (<c>PUT /ingest/dictionary</c>); returns the re-read
    /// dictionary. User action: throws with the server error on failure.</summary>
    public async Task<DictionaryDto> SaveDictionaryAsync(DictionaryDto dict, CancellationToken ct = default)
    {
        _log.LogInformation("SaveDictionaryAsync: {Vocab} terms, {Mistrans} patterns, {Profiles} profiles",
            dict.Vocabulary.Count, dict.CommonMistranscriptions.Count, dict.Profiles.Count);
        var resp = await _http.PutAsJsonAsync("ingest/dictionary", dict, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"save dictionary failed ({(int)resp.StatusCode}): {ExtractError(body)}");
        }
        return await resp.Content.ReadFromJsonAsync<DictionaryDto>(cancellationToken: ct)
            ?? throw new HttpRequestException("dictionary save returned an empty body");
    }

    /// <summary>Pull the <c>error</c>/<c>detail</c> field out of a Gateway JSON error body when
    /// present, so the page shows the server's message rather than a raw JSON blob.</summary>
    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(no detail)";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == System.Text.Json.JsonValueKind.String)
                    return detail.GetString() ?? body;
                if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == System.Text.Json.JsonValueKind.String)
                    return err.GetString() ?? body;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON - return as-is (it may be a plain-text problem detail).
        }
        return body;
    }
}

/// <summary>The <c>GET /cron/jobs</c> envelope: <c>{ "jobs": [ CronJobDto, ... ] }</c> (epic #479).</summary>
public sealed class CronJobsEnvelope
{
    public List<CronJobDto> Jobs { get; set; } = new();
}

/// <summary>The <c>GET /cron/jobs/{id}/runs</c> envelope: <c>{ "jobId": "...", "runs": [ CronRunRecord, ... ] }</c> (epic #479).</summary>
public sealed class CronRunsEnvelope
{
    public string JobId { get; set; } = "";
    public List<CronRunRecord> Runs { get; set; } = new();
}

/// <summary>The <c>GET /lists</c> envelope: <c>{ "lists": [ WorkListDto, ... ] }</c> (issue #273).</summary>
public sealed class WorkListsEnvelope
{
    public List<WorkListDto> Lists { get; set; } = new();
}

