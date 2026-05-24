using System.Net.Http.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// HTTP wrapper for the Director's internal Control API. One short-lived
/// HttpClient per call is fine here; the Gateway is not high-throughput.
/// </summary>
public sealed class DirectorEndpointClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _token;

    public DirectorEndpointClient(string? token = null)
    {
        _token = token;
        _http = new HttpClient
        {
            // Keep this short. The Gateway aggregates over every Director on every request,
            // so a single unreachable Director should not stall the whole /healthz response.
            Timeout = TimeSpan.FromSeconds(2),
        };
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<HealthDto?> GetHealthAsync(string endpoint, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<HealthDto>($"{endpoint}/healthz", ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] GetHealthAsync FAILED: endpoint={endpoint}, error={ex.Message}");
            return null;
        }
    }

    public async Task<List<SessionDto>?> ListSessionsAsync(string endpoint, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<SessionDto>>($"{endpoint}/sessions", ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] ListSessionsAsync FAILED: endpoint={endpoint}, error={ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Same as <see cref="ListSessionsAsync"/> but distinguishes "no sessions"
    /// (empty list, error=null) from "couldn't reach Director" (sessions=null,
    /// error=&lt;reason&gt;). Used by the Gateway aggregator to surface unreachable
    /// Directors to the UI as <c>machineErrors</c> entries instead of silently
    /// hiding them. Forwards <paramref name="includeExited"/> to the Director,
    /// which hides exited sessions by default (Phase 3).
    /// </summary>
    public async Task<(List<SessionDto>? sessions, string? error)> ListSessionsWithStatusAsync(string endpoint, bool includeExited = false, CancellationToken ct = default)
    {
        try
        {
            var url = includeExited
                ? $"{endpoint}/sessions?includeExited=true"
                : $"{endpoint}/sessions";
            var sessions = await _http.GetFromJsonAsync<List<SessionDto>>(url, ct);
            return (sessions ?? new List<SessionDto>(), null);
        }
        catch (TaskCanceledException)
        {
            return (null, "timeout");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] ListSessionsWithStatusAsync FAILED: endpoint={endpoint}, error={ex.Message}");
            return (null, ex.Message);
        }
    }

    public async Task<SessionDto?> GetSessionAsync(string endpoint, string sessionId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{endpoint}/sessions/{sessionId}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<SessionDto>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] GetSessionAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return null;
        }
    }

    public async Task<WingmanViewDto?> GetWingmanAsync(string endpoint, string sessionId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{endpoint}/sessions/{sessionId}/wingman", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<WingmanViewDto>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] GetWingmanAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Phase 5: forward a wingman "ask" call through the Gateway to the owning Director.
    /// Uses a longer per-call timeout (45 s) than the chatty aggregate endpoints because
    /// Haiku side-calls can take 5-30 s.
    /// </summary>
    public async Task<WingmanAskResult?> AskWingmanAsync(string endpoint, string sessionId, WingmanAskRequest req, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        if (!string.IsNullOrEmpty(_token))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        try
        {
            var resp = await http.PostAsJsonAsync($"{endpoint}/sessions/{sessionId}/wingman/ask", req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                FileLog.Write($"[DirectorEndpointClient] AskWingmanAsync HTTP {(int)resp.StatusCode}: {Truncate(body, 200)}");
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<WingmanAskResult>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] AskWingmanAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Forward a "set the session goal" call to the owning Director. Returns the raw
    /// JSON body the Director produced (goal + state) on success, or null on failure.
    /// </summary>
    public async Task<string?> SetWingmanGoalAsync(string endpoint, string sessionId, WingmanGoalRequest req, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"{endpoint}/sessions/{sessionId}/wingman/goal", req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                FileLog.Write($"[DirectorEndpointClient] SetWingmanGoalAsync HTTP {(int)resp.StatusCode}: {Truncate(body, 200)}");
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] SetWingmanGoalAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Forward a "kill this session" (DELETE) to the owning Director. Returns true
    /// when the Director reports the session was killed.
    /// </summary>
    public async Task<bool> KillSessionAsync(string endpoint, string sessionId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.DeleteAsync($"{endpoint}/sessions/{sessionId}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                FileLog.Write($"[DirectorEndpointClient] KillSessionAsync HTTP {(int)resp.StatusCode}: {Truncate(body, 200)}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] KillSessionAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return false;
        }
    }

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

    public async Task<(bool ok, SessionDto? body, string? error)> PatchSessionAsync(string endpoint, string sessionId, SessionUpdateRequest req, CancellationToken ct = default)
    {
        try
        {
            var http = new HttpRequestMessage(HttpMethod.Patch, $"{endpoint}/sessions/{sessionId}")
            {
                Content = JsonContent.Create(req),
            };
            var resp = await _http.SendAsync(http, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (false, null, $"director returned {(int)resp.StatusCode}: {body}");
            }
            var dto = await resp.Content.ReadFromJsonAsync<SessionDto>(cancellationToken: ct);
            return (true, dto, null);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] PatchSessionAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return (false, null, ex.Message);
        }
    }

    public async Task<BufferResponse?> GetBufferAsync(string endpoint, string sessionId, int? lines = null, bool raw = false, long? since = null, CancellationToken ct = default)
    {
        try
        {
            var qs = new List<string>();
            if (lines.HasValue) qs.Add($"lines={lines.Value}");
            if (raw) qs.Add("raw=true");
            if (since.HasValue) qs.Add($"since={since.Value}");
            var url = $"{endpoint}/sessions/{sessionId}/buffer" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");

            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<BufferResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] GetBufferAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return null;
        }
    }

    public async Task<(bool ok, PromptResponse? body, string? error)> PostPromptAsync(string endpoint, string sessionId, PromptRequest req, CancellationToken ct = default)
    {
        try
        {
            // Director side ignores WaitForIdle - that's a Gateway-side concern.
            var resp = await _http.PostAsJsonAsync($"{endpoint}/sessions/{sessionId}/prompt", req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (false, null, $"director returned {(int)resp.StatusCode}: {body}");
            }
            var dto = await resp.Content.ReadFromJsonAsync<PromptResponse>(cancellationToken: ct);
            return (true, dto, null);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] PostPromptAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return (false, null, ex.Message);
        }
    }

    public async Task<bool> PostInterruptAsync(string endpoint, string sessionId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync($"{endpoint}/sessions/{sessionId}/interrupt", null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] PostInterruptAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return false;
        }
    }

    public async Task<bool> PostEscapeAsync(string endpoint, string sessionId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync($"{endpoint}/sessions/{sessionId}/escape", null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] PostEscapeAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Forward an uploaded image to the owning Director, which files it into the
    /// screenshots folder on its machine (where the session runs) and returns the
    /// saved absolute path. Returns (path, fileName) on success, or an error string.
    /// </summary>
    public async Task<(bool ok, string? path, string? fileName, string? error)> UploadImageAsync(
        string endpoint, string sessionId, byte[] bytes, string fileName, string contentType, CancellationToken ct = default)
    {
        // Phone images can be a few MB; the shared client's 2 s timeout is too tight.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrEmpty(_token))
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        try
        {
            using var content = new MultipartFormDataContent();
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
            content.Add(part, "file", fileName);

            var resp = await http.PostAsync($"{endpoint}/sessions/{sessionId}/upload-image", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (false, null, null, $"director returned {(int)resp.StatusCode}: {Truncate(body, 200)}");
            }

            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
            var name = root.TryGetProperty("fileName", out var n) ? n.GetString() : null;
            return (true, path, name, null);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] UploadImageAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return (false, null, null, ex.Message);
        }
    }

    public async Task<List<RepositoryDto>?> ListReposAsync(string endpoint, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<RepositoryDto>>($"{endpoint}/repos", ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] ListReposAsync FAILED: endpoint={endpoint}, error={ex.Message}");
            return null;
        }
    }

    public async Task<(bool ok, SessionDto? body, string? error)> CreateSessionAsync(string endpoint, NewSessionRequest req, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"{endpoint}/sessions", req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (false, null, $"director returned {(int)resp.StatusCode}: {body}");
            }
            var dto = await resp.Content.ReadFromJsonAsync<SessionDto>(cancellationToken: ct);
            return (true, dto, null);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] CreateSessionAsync FAILED: endpoint={endpoint}, error={ex.Message}");
            return (false, null, ex.Message);
        }
    }

    public async Task<SessionSummaryDto?> GetSummaryAsync(string endpoint, string sessionId, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<SessionSummaryDto>($"{endpoint}/sessions/{sessionId}/summary", ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] GetSummaryAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return null;
        }
    }

    public async Task<(bool ok, HandoverResponse? body, string? error)> PostHandoverAsync(string endpoint, HandoverRequest req, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"{endpoint}/handover", req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (false, null, $"director returned {(int)resp.StatusCode}: {body}");
            }
            var dto = await resp.Content.ReadFromJsonAsync<HandoverResponse>(cancellationToken: ct);
            return (true, dto, null);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] PostHandoverAsync FAILED: endpoint={endpoint}, error={ex.Message}");
            return (false, null, ex.Message);
        }
    }

    public async Task<RecapResponse?> GetRecapAsync(string endpoint, string sessionId, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<RecapResponse>($"{endpoint}/sessions/{sessionId}/recap", ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] GetRecapAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return null;
        }
    }

    public async Task<(bool ok, RecapResponse? body, string? error)> PostRecapAsync(string endpoint, string sessionId, string? model = null, CancellationToken ct = default)
    {
        // The recap POST runs a side-claude (--print) which may take 10-60s on a long
        // session. Use a per-call HttpClient with a generous timeout instead of the
        // shared short-timeout client used for the chatty aggregate endpoints.
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        if (!string.IsNullOrEmpty(_token))
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        try
        {
            var url = $"{endpoint}/sessions/{sessionId}/recap";
            if (!string.IsNullOrEmpty(model)) url += "?model=" + Uri.EscapeDataString(model);
            var resp = await http.PostAsync(url, content: null, ct);
            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 201)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (false, null, $"director returned {(int)resp.StatusCode}: {body}");
            }
            var dto = await resp.Content.ReadFromJsonAsync<RecapResponse>(cancellationToken: ct);
            return (true, dto, null);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] PostRecapAsync FAILED: endpoint={endpoint}, sid={sessionId}, error={ex.Message}");
            return (false, null, ex.Message);
        }
    }

    public async Task<bool> PostShutdownAsync(string endpoint, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync($"{endpoint}/shutdown", null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorEndpointClient] PostShutdownAsync FAILED: endpoint={endpoint}, error={ex.Message}");
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
