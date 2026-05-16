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
