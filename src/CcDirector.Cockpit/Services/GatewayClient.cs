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

    /// <summary>
    /// Create a GitHub Actions remote session on a Director via the Gateway proxy
    /// (<c>POST /directors/{id}/sessions/github</c>). Returns the created session.
    /// </summary>
    public async Task<SessionDto?> CreateGitHubSessionAsync(string directorId, GitHubSessionRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"directors/{directorId}/sessions/github", req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"github session failed ({(int)resp.StatusCode}): {body}");
        }
        return await resp.Content.ReadFromJsonAsync<SessionDto>(cancellationToken: ct);
    }

    /// <summary>Remove a repo from a Director's recent list (<c>DELETE /directors/{id}/repos</c>).</summary>
    public async Task RemoveRepoAsync(string directorId, string path, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"directors/{directorId}/repos?path={Uri.EscapeDataString(path)}", ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>The Assistant/Coach quick-launch categories a Director offers, with resolved paths.</summary>
    public async Task<List<CoachingCategoryDto>> GetCoachingCategoriesAsync(string directorId, CancellationToken ct = default)
    {
        var c = await _http.GetFromJsonAsync<List<CoachingCategoryDto>>($"directors/{directorId}/coaching/categories", ct);
        return c ?? new List<CoachingCategoryDto>();
    }

    /// <summary>Resumable Claude Code sessions on a Director (Resume Session tab).</summary>
    public async Task<List<ClaudeSessionDto>> GetClaudeSessionsAsync(string directorId, CancellationToken ct = default)
    {
        var s = await _http.GetFromJsonAsync<List<ClaudeSessionDto>>($"directors/{directorId}/claude-sessions", ct);
        return s ?? new List<ClaudeSessionDto>();
    }

    /// <summary>Handover documents on a Director (Handovers tab).</summary>
    public async Task<List<HandoverDto>> GetHandoversAsync(string directorId, CancellationToken ct = default)
    {
        var h = await _http.GetFromJsonAsync<List<HandoverDto>>($"directors/{directorId}/handovers", ct);
        return h ?? new List<HandoverDto>();
    }

    /// <summary>Full text of one handover document for preview.</summary>
    public async Task<HandoverContentDto?> GetHandoverContentAsync(string directorId, string path, CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<HandoverContentDto>(
            $"directors/{directorId}/handovers/content?path={Uri.EscapeDataString(path)}", ct);
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
}
