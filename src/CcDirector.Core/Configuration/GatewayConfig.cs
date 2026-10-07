using System.Net;
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
    /// <summary>Gateway base URL, e.g. <c>http://gateway.tailnet.example:7878</c>. This is the
    /// ACTIVE / preferred address: the one a manual override writes, and the first candidate tried
    /// when connecting (a manually set address wins over the auto-discovered fallbacks).</summary>
    public string Url { get; init; } = "";

    /// <summary>
    /// The ordered list of FALLBACK gateway addresses discovered from the account (issue #1233):
    /// machine name plus port, the Tailscale address when Tailscale is available, and the local
    /// network IP plus port, in priority order. Read from the <c>gateway.urls</c> array in
    /// config.json. Empty on older installs that only ever wrote <c>gateway.url</c>, in which case
    /// behaviour is unchanged (one candidate). Use <see cref="CandidateUrls"/> to get the full
    /// ordered set to try, which puts <see cref="Url"/> first.
    /// </summary>
    public IReadOnlyList<string> Urls { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The full ordered set of addresses to try when connecting to the gateway (issue #1233):
    /// <see cref="Url"/> first (the active/manual address wins), then each entry of
    /// <see cref="Urls"/>, de-duplicated (case-insensitive) so the active address is not probed
    /// twice when it also appears in the discovered list. A client walks these in order and
    /// connects through the first that answers (see <see cref="Network.GatewayEndpointSelector"/>).
    /// </summary>
    public IReadOnlyList<string> CandidateUrls
    {
        get
        {
            var list = new List<string>();
            AddCandidate(list, Url);
            foreach (var u in Urls)
                AddCandidate(list, u);
            return list;
        }
    }

    // Append a trimmed, non-blank candidate unless an equal one (case-insensitive) is already present.
    private static void AddCandidate(List<string> list, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        var trimmed = candidate.Trim();
        foreach (var existing in list)
        {
            if (string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase))
                return;
        }
        list.Add(trimmed);
    }

    /// <summary>
    /// Bearer token the Director/launcher present to the Gateway. Normally the enrolled per-device key
    /// or shared fleet token from config.json's <c>gateway.token</c>. When that is empty AND the
    /// configured Gateway is THIS machine's own Gateway, <see cref="Load"/> resolves it to the local
    /// shared machine token from <c>gateway-token.txt</c> (see <see cref="IsLocalGatewayHost"/>), so a
    /// Gateway-role install - which never runs the pairing step that would write a token - still
    /// authenticates its own Director. Empty means no auth header is sent.
    /// </summary>
    public string Token { get; init; } = "";

    /// <summary>
    /// Optional override for the Director's own routable URL. If unset, the
    /// <see cref="GatewayClient"/> falls back to <c>http://{MachineName}:{port}</c>.
    /// </summary>
    public string? TailnetEndpoint { get; init; }

    /// <summary>
    /// The fleet network addressing mode (issue #457). Read from the top-level
    /// <c>addressing_mode</c> key in config.json (NOT the gateway block), so it is the same
    /// value <see cref="AddressingModeConfig"/> exposes. Decides whether a Director advertises
    /// its Tailscale front door or its LAN IP. Default <see cref="AddressingMode.Tailscale"/>.
    /// </summary>
    public AddressingMode AddressingMode { get; init; } = AddressingModeConfig.Default;

    /// <summary>
    /// Issue #1176 (Phase 1a): when true, the Director opens a persistent stream to the Gateway and
    /// pushes its session state, and the Gateway serves <c>GET /sessions</c> from that pushed cache
    /// when it is fresh. Default false: the Director uses only the existing pull/heartbeat path and the
    /// Gateway ignores the pushed cache, so behaviour is byte-identical to today. Read from the gateway
    /// block's <c>streamMode</c> key in config.json.
    /// </summary>
    public bool StreamMode { get; init; }

    /// <summary>
    /// Issue #1176 (Phase 1a): how many seconds a Director's most recent stream push may age before the
    /// Gateway treats its pushed cache as stale and falls back to pulling that Director for
    /// <c>GET /sessions</c>. Guards against a wedged stream serving frozen data. Read from the gateway
    /// block's <c>staleAfterSeconds</c> key; default <see cref="DefaultStreamStaleAfterSeconds"/>.
    /// </summary>
    public int StreamStaleAfterSeconds { get; init; } = DefaultStreamStaleAfterSeconds;

    /// <summary>Default value for <see cref="StreamStaleAfterSeconds"/>.</summary>
    public const int DefaultStreamStaleAfterSeconds = 20;

    /// <summary>
    /// Epic #1159 step A: how long a machine may be gone before the Gateway forgets it and its sessions
    /// leave the roster. This is the ONE elapsed-time rule allowed to remove a session; every shorter
    /// staleness window now decides only what the roster SAYS about a machine, never whether it is served.
    ///
    /// It is deliberately far longer than any push or heartbeat interval. The value it replaced was sixty
    /// seconds - one minute of a closed tunnel and the machine, its sessions and its colours were deleted
    /// from the roster, which is the defect this step exists to end. A day is long enough that a laptop
    /// shut overnight is still there in the morning, and short enough that a machine genuinely retired
    /// does not haunt the roster for a week.
    /// </summary>
    public const int DefaultDirectorEvictionHorizonHours = 24;

    /// <summary>
    /// How long a machine may be gone before the Gateway forgets it and its sessions leave the roster. Read
    /// from the gateway block's <c>directorEvictionHorizonHours</c> key; default
    /// <see cref="DefaultDirectorEvictionHorizonHours"/>.
    ///
    /// It is configurable because the right answer depends on how the fleet is used, and nobody has measured
    /// that yet - a machine that sleeps overnight wants a day, a shared build farm churning through short-lived
    /// Directors may want an hour. The DEFAULT is the ruling; this key exists so changing it does not need a
    /// release. A value of zero or less is refused rather than honoured: a zero horizon would evict every
    /// machine on the next thirty-second sweep, which is the deleting roster this whole read model exists to
    /// end, and it would arrive as a silent configuration typo rather than a visible change.
    /// </summary>
    public int DirectorEvictionHorizonHours { get; init; } = DefaultDirectorEvictionHorizonHours;

    /// <summary>True when <see cref="Url"/> is configured.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Url);

    /// <summary>
    /// Has this machine signed in: a Gateway to talk to AND a credential to present to it. A machine
    /// that installed but never signed in has neither. The error reporter decides where errors go by
    /// this, and the launcher decides whether to open the Director at sign-in to Windows by it.
    /// </summary>
    public bool HasCredential => IsEnabled && !string.IsNullOrWhiteSpace(Token);

    /// <summary>
    /// Read the gateway block from <c>config.json</c>. Returns a disabled config
    /// (IsEnabled = false) when the file is missing, malformed, or has no gateway block.
    /// </summary>
    public static GatewayConfig Load() => LoadFrom(CcStorage.Root());

    /// <summary>
    /// <see cref="Load"/> for the Director whose storage home is <paramref name="storageRoot"/>, rather
    /// than this process's own. The setup command line's <c>enroll</c> asks this of the default
    /// Director's home before connecting it, so "already connected" means the Director is connected -
    /// not that some other folder holds a gateway block (issue #3506).
    /// </summary>
    public static GatewayConfig LoadFrom(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new ArgumentException("storageRoot is required", nameof(storageRoot));
        var configDir = Path.Combine(storageRoot, "config");
        var path = Path.Combine(configDir, "config.json");
        try
        {
            if (!File.Exists(path)) return new GatewayConfig();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new GatewayConfig();

            using var doc = JsonDocument.Parse(json);

            // The addressing mode is a TOP-LEVEL key (issue #457), read whether or not a
            // gateway block is present so a standalone Director still binds per the chosen mode.
            var mode = doc.RootElement.TryGetProperty("addressing_mode", out var am) && am.ValueKind == JsonValueKind.String
                ? AddressingModeExtensions.Parse(am.GetString())
                : AddressingModeConfig.Default;

            if (!doc.RootElement.TryGetProperty("gateway", out var gw))
                return new GatewayConfig { AddressingMode = mode };

            var url = (gw.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "").Trim();
            var token = (gw.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "").Trim();
            var tailnet = gw.TryGetProperty("tailnetEndpoint", out var te) ? te.GetString() : null;

            // Issue #1233: the ordered fallback candidate list discovered from the account. A missing
            // or malformed key leaves it empty so the install behaves exactly as before (one candidate,
            // gateway.url). Only non-blank string entries are kept, trimmed, in the order given.
            var urls = new List<string>();
            if (gw.TryGetProperty("urls", out var urlsEl) && urlsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in urlsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        urls.Add(s.Trim());
                }
            }

            // Issue #1176 (Phase 1a). streamMode is opt-in (only a JSON boolean true enables it), so a
            // missing or malformed key leaves the Director on the existing pull path. staleAfterSeconds
            // must be a positive integer or the default stands.
            var streamMode = gw.TryGetProperty("streamMode", out var sm) && sm.ValueKind == JsonValueKind.True;
            var staleAfterSeconds = gw.TryGetProperty("staleAfterSeconds", out var sa)
                && sa.ValueKind == JsonValueKind.Number && sa.TryGetInt32(out var sav) && sav > 0
                    ? sav
                    : DefaultStreamStaleAfterSeconds;
            // Epic #1159 step A. Must be a POSITIVE integer or the default stands - see the property's note on
            // why a zero is refused rather than honoured.
            var evictionHorizonHours = gw.TryGetProperty("directorEvictionHorizonHours", out var eh)
                && eh.ValueKind == JsonValueKind.Number && eh.TryGetInt32(out var ehv) && ehv > 0
                    ? ehv
                    : DefaultDirectorEvictionHorizonHours;

            // Same-machine credential resolution. A Gateway-role install never runs the pairing step
            // (that step, and only that step, writes gateway.token - it is a Workstation-only step), so
            // the Gateway host's own Director/launcher have no configured token and, once host-wide auth
            // is enforced, get 401 on every Gateway call. When the configured Gateway is THIS machine's
            // own Gateway, present the local shared machine token from gateway-token.txt - the exact file
            // GatewayAuth writes on the Gateway host, and the same file the Director-side callers read for the
            // inbound Control API. It is scoped to a LOCAL Gateway URL so a remote Workstation never sends
            // this machine's token to a different Gateway. This is a credential RESOLUTION, not a fallback
            // that hides an error: it is the correct token for the local Gateway, sourced from the one
            // file that holds it.
            if (string.IsNullOrEmpty(token) && url.Length > 0 && IsLocalGatewayHost(url))
            {
                var localToken = TryReadLocalMachineToken(configDir);
                if (!string.IsNullOrEmpty(localToken))
                {
                    token = localToken;
                    FileLog.Write("[GatewayConfig] gateway.token empty and gateway.url targets this machine's own Gateway; using the local shared machine token from gateway-token.txt");
                }
            }

            return new GatewayConfig
            {
                Url = url,
                Urls = urls,
                Token = token,
                TailnetEndpoint = string.IsNullOrWhiteSpace(tailnet) ? null : tailnet.Trim(),
                AddressingMode = mode,
                StreamMode = streamMode,
                StreamStaleAfterSeconds = staleAfterSeconds,
                DirectorEvictionHorizonHours = evictionHorizonHours,
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConfig] Load FAILED, treating as disabled: {ex.Message}");
            return new GatewayConfig();
        }
    }

    /// <summary>
    /// True when <paramref name="url"/> addresses THIS machine's own Gateway: loopback, "localhost",
    /// this machine's name, or its Tailscale MagicDNS name. Tailscale lowercases the hostname and turns
    /// '_' into '-' (MACHINE_A -> machine-a), so the first DNS label is compared against the
    /// normalized machine name. Used to scope the empty-token same-machine credential resolution to a
    /// local Gateway so a remote Workstation never presents this machine's token to a different Gateway.
    /// Pure and side-effect free so it is unit-tested directly.
    /// </summary>
    internal static bool IsLocalGatewayHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        if (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
            return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;

        // Tailscale MagicDNS: <normalized-hostname>.<tailnet>.ts.net - match the first label.
        var firstLabel = host.Split('.', 2)[0];
        var normalizedMachine = Environment.MachineName.ToLowerInvariant().Replace('_', '-');
        return firstLabel.Equals(normalizedMachine, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Read the local shared machine token that <c>GatewayAuth</c> generates on the Gateway host
    /// (<c>%LOCALAPPDATA%\cc-director\config\director\gateway-token.txt</c> - the same file
    /// <c>GatewayAuth.TokenFile</c> / <see cref="GatewayCredentialStore.CredentialFile"/>
    /// name). The path is computed fresh (not a cached static) so it honors the current config root.
    /// Null when the file is absent or empty.
    /// </summary>
    private static string? TryReadLocalMachineToken(string configDir)
    {
        try
        {
            var path = Path.Combine(configDir, "director", "gateway-token.txt");
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConfig] TryReadLocalMachineToken failed: {ex.Message}");
            return null;
        }
    }
}
