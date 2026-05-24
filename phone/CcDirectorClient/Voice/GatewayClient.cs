using System.Net.Http.Headers;

namespace CcDirectorClient.Voice;

/// <summary>
/// Reads the session roster from the Gateway (GET /sessions), which aggregates
/// every session across all Directors and stamps each with the owning Director's
/// Tailnet base URL. This is the conductor's and the talk screen's source of
/// truth for what sessions exist and which need the user.
/// </summary>
public sealed class GatewayClient
{
    private readonly string _baseUrl;
    private readonly string _token;

    public GatewayClient(string baseUrl, string token = "")
    {
        _baseUrl = (baseUrl ?? "").TrimEnd('/');
        _token = token ?? "";
    }

    /// <summary>
    /// Fetch the current roster. Throws on a network or HTTP failure so the UI can
    /// show the real reason instead of a silently empty list. Exited/failed
    /// sessions are filtered out by <see cref="RosterParser"/>.
    /// </summary>
    public async Task<List<SessionInfo>> GetRosterAsync(CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] GetRoster: base={_baseUrl}");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!string.IsNullOrWhiteSpace(_token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        var resp = await http.GetAsync($"{_baseUrl}/sessions", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET /sessions failed: {(int)resp.StatusCode} {body}");

        var roster = RosterParser.Parse(body);
        ClientLog.Write($"[GatewayClient] GetRoster OK: count={roster.Count}");
        return roster;
    }
}
