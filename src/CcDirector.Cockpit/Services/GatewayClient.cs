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
    /// List every Director the Gateway knows about (the "on which Director?" picker for a new
    /// session). Reads aggregate through the Gateway, so this is a Gateway call.
    /// </summary>
    public async Task<List<DirectorDto>> GetDirectorsAsync(CancellationToken ct = default)
    {
        var d = await _http.GetFromJsonAsync<List<DirectorDto>>("directors", ct);
        return d ?? new List<DirectorDto>();
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

    /// <summary>"This brief is wrong" (D7) - stored Gateway-side as a labeled example for
    /// prompt iteration (#187: the feedback loop lives with the brief store).</summary>
    public async Task PostBriefFeedbackAsync(string sessionId, int turnNumber, string note, CancellationToken ct = default)
    {
        _log.LogDebug("BriefFeedback sid={Sid} turn={Turn}", sessionId, turnNumber);
        var resp = await _http.PostAsJsonAsync($"sessions/{Uri.EscapeDataString(sessionId)}/turnbriefs/feedback",
            new TurnBriefFeedbackRequest { TurnNumber = turnNumber, Note = note }, ct);
        resp.EnsureSuccessStatusCode();
    }
}
