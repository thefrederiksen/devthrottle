using System.Net.Http;
using System.Text.Json;
using CcDirector.Core.Network;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Settings;

/// <summary>Result of probing a gateway's /healthz.</summary>
public sealed record GatewayTestResult(bool Ok, string Message, string? Version, int Directors, int Sessions);

/// <summary>Result of scanning for a gateway: the first reachable URL (or null) and every URL tried.</summary>
public sealed record GatewayDetectResult(string? Url, IReadOnlyList<string> Scanned);

/// <summary>Result of building this Director's advertised "public URL".</summary>
public sealed record PublicUrlResult(string? Url, string Kind);

/// <summary>Result of locating the OS screenshots folder.</summary>
public sealed record ScreenshotsDetectResult(string? Directory);

/// <summary>
/// UI-free orchestration for the Settings page detections and the gateway reachability test.
/// This is the single implementation shared by the Avalonia dialog and the REST Control API,
/// so an agent can drive settings/detection over HTTP without opening the dialog. Builds on the
/// pure helpers (<see cref="EndpointProbe"/>, <see cref="TailscaleIdentity"/>,
/// <see cref="ScreenshotLocator"/>); this layer adds the HTTP probing and the scan ordering.
/// </summary>
public sealed class SettingsDetectionService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly HttpClient _http;

    /// <param name="http">Override the HTTP client (tests inject a stub); defaults to a shared short-timeout client.</param>
    public SettingsDetectionService(HttpClient? http = null) => _http = http ?? SharedHttp;

    /// <summary>
    /// GET {baseUrl}/healthz and summarize what answered. Never throws - a transport error or a
    /// non-gateway response comes back as Ok=false with a human-readable reason.
    /// </summary>
    public async Task<GatewayTestResult> TestGatewayAsync(string baseUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new GatewayTestResult(false, "No gateway URL provided.", null, 0, 0);

        var healthz = baseUrl.TrimEnd('/') + "/healthz";
        FileLog.Write($"[SettingsDetectionService] TestGateway: {healthz}");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(healthz, ct);
        }
        catch (Exception ex)
        {
            return new GatewayTestResult(false, $"Cannot reach {baseUrl}: {ex.Message}", null, 0, 0);
        }

        if (!resp.IsSuccessStatusCode)
            return new GatewayTestResult(false, $"Gateway returned {(int)resp.StatusCode} {resp.ReasonPhrase} for {healthz}.", null, 0, 0);

        var json = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (GetStringProp(root, "status") is null)
                return new GatewayTestResult(false, $"{baseUrl} answered but does not look like a CC Director gateway.", null, 0, 0);

            var version = GetStringProp(root, "version") ?? "?";
            var directors = GetIntProp(root, "directors");
            var sessions = GetIntProp(root, "sessions");
            return new GatewayTestResult(true, $"OK: gateway v{version}, {directors} director(s), {sessions} session(s).", version, directors, sessions);
        }
        catch (JsonException)
        {
            return new GatewayTestResult(false, $"{baseUrl} answered but the response was not valid gateway JSON.", null, 0, 0);
        }
    }

    /// <summary>
    /// Find a gateway, Tailscale-first: probe every online, non-mobile tailnet device by its
    /// MagicDNS name (parallel), then this machine's loopback. Returns the first reachable URL
    /// (or null) plus every URL tried.
    /// </summary>
    public async Task<GatewayDetectResult> DetectGatewayAsync(CancellationToken ct = default)
    {
        var hosts = await Task.Run(() => TailscaleIdentity.ListGatewayHostCandidates(), ct);
        var scanned = new List<string>();

        var tailnetUrls = hosts.Select(h => $"http://{h}:{EndpointProbe.DefaultGatewayPort}").ToList();
        scanned.AddRange(tailnetUrls);
        var found = await ProbeFirstReachableAsync(tailnetUrls, ct);

        if (found is null)
        {
            foreach (var candidate in EndpointProbe.LocalGatewayCandidates())
            {
                scanned.Add(candidate);
                if ((await TestGatewayAsync(candidate, ct)).Ok)
                {
                    found = candidate;
                    break;
                }
            }
        }

        FileLog.Write($"[SettingsDetectionService] DetectGateway: scanned={scanned.Count}, found={found ?? "(none)"}");
        return new GatewayDetectResult(found, scanned);
    }

    /// <summary>
    /// Build this Director's advertised public URL: the Tailscale MagicDNS name + control port
    /// when available, else the best local IP + port. Returns Url=null when neither exists or
    /// the port is unknown.
    /// </summary>
    public async Task<PublicUrlResult> DetectPublicUrlAsync(int controlPort, CancellationToken ct = default)
    {
        if (controlPort <= 0)
            return new PublicUrlResult(null, "");

        return await Task.Run(() =>
        {
            var dnsName = TailscaleIdentity.TryGetMagicDnsName();
            if (!string.IsNullOrWhiteSpace(dnsName))
                return new PublicUrlResult(EndpointProbe.BuildAdvertisedUrl(dnsName, controlPort), "Tailscale name");

            var address = EndpointProbe.BestLocalAddress();
            if (address is null)
                return new PublicUrlResult(null, "");

            var kind = EndpointProbe.IsTailscaleAddress(address) ? "Tailscale IP" : "LAN IP";
            return new PublicUrlResult(EndpointProbe.BuildAdvertisedUrl(address.ToString(), controlPort), kind);
        }, ct);
    }

    /// <summary>Locate the OS screenshots folder (Windows Pictures\Screenshots, macOS screencapture / Desktop).</summary>
    public Task<ScreenshotsDetectResult> DetectScreenshotsAsync(CancellationToken ct = default)
        => Task.Run(() => new ScreenshotsDetectResult(ScreenshotLocator.Detect()), ct);

    /// <summary>Probe URLs concurrently, return the first (in input order) that answers like a gateway, else null.</summary>
    private async Task<string?> ProbeFirstReachableAsync(IReadOnlyList<string> baseUrls, CancellationToken ct)
    {
        var results = await Task.WhenAll(baseUrls.Select(async url => (url, ok: (await TestGatewayAsync(url, ct)).Ok)));
        return results.FirstOrDefault(r => r.ok).url;
    }

    private static string? GetStringProp(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }

    private static int GetIntProp(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number)
                return p.Value.GetInt32();
        return 0;
    }
}
