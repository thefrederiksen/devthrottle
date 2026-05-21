using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Optional configuration for the Director's HTTP registration with a CC Director
/// Gateway. Read from <c>%LOCALAPPDATA%\cc-director\config\config.json</c> under
/// the <c>gateway</c> block:
///
/// <code>
/// {
///   "gateway": {
///     "url": "http://gateway.tailnet.example:7878",
///     "token": "...",
///     "tailnetEndpoint": "http://machine-b.tailnet.example:7879"
///   }
/// }
/// </code>
///
/// If <c>url</c> is missing or empty, the Director runs in local-only mode and
/// no HTTP registration happens. Same-machine Gateways on the box can still
/// discover the Director via the filesystem-watch path.
/// </summary>
public sealed class GatewayConfig
{
    /// <summary>Gateway base URL, e.g. <c>http://gateway.tailnet.example:7878</c>.</summary>
    public string Url { get; init; } = "";

    /// <summary>Shared bearer token. Empty means no auth header is sent.</summary>
    public string Token { get; init; } = "";

    /// <summary>
    /// Optional override for the Director's own routable URL. If unset, the
    /// <see cref="GatewayClient"/> falls back to <c>http://{MachineName}:{port}</c>.
    /// </summary>
    public string? TailnetEndpoint { get; init; }

    /// <summary>True when <see cref="Url"/> is configured.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Url);

    /// <summary>
    /// Read the gateway block from <c>config.json</c>. Returns a disabled config
    /// (IsEnabled = false) when the file is missing, malformed, or has no gateway block.
    /// </summary>
    public static GatewayConfig Load()
    {
        var path = CcStorage.ConfigJson();
        try
        {
            if (!File.Exists(path)) return new GatewayConfig();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new GatewayConfig();

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("gateway", out var gw)) return new GatewayConfig();

            var url = gw.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var token = gw.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
            var tailnet = gw.TryGetProperty("tailnetEndpoint", out var te) ? te.GetString() : null;

            return new GatewayConfig
            {
                Url = url.Trim(),
                Token = token.Trim(),
                TailnetEndpoint = string.IsNullOrWhiteSpace(tailnet) ? null : tailnet.Trim(),
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConfig] Load FAILED, treating as disabled: {ex.Message}");
            return new GatewayConfig();
        }
    }
}
