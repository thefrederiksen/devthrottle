using System.Net.Http.Json;

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
}
