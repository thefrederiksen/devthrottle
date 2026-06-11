using CcDirector.Core.Utilities;

namespace CcDirector.Core.Network;

/// <summary>
/// Outcome of one tailnet-endpoint resolution (issue #324). Exactly one of two shapes:
///   - Resolved: <see cref="Endpoint"/> is a non-loopback https URL, <see cref="Source"/>
///     names the rung that produced it, <see cref="FailureReason"/> is null.
///   - Unresolved: <see cref="Endpoint"/> is empty, <see cref="FailureReason"/> is a
///     human-readable line that NAMES THE FIX (start Tailscale / set the override).
/// A loopback or empty endpoint is never reported as resolved - that is the regression
/// the acceptance criteria pin (never claim a reachable advertised endpoint that is
/// empty or loopback).
/// </summary>
public sealed class TailnetEndpointResolution
{
    /// <summary>Advertisable endpoint, e.g. <c>https://machine.tailnet.ts.net:7879</c>. Empty when unresolved.</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>Which rung resolved it: "local-api", "cli", or "config-override". Null when unresolved.</summary>
    public string? Source { get; init; }

    /// <summary>Why nothing resolved, naming the remediation. Null when resolved.</summary>
    public string? FailureReason { get; init; }

    /// <summary>True when a reachable (non-empty, non-loopback) endpoint was resolved.</summary>
    public bool IsResolved => FailureReason is null;
}

/// <summary>
/// Resolves the tailnet endpoint a Director advertises to the Gateway, in the plan-1A
/// detection order (issue #324): tailscaled LocalAPI -> <c>tailscale status --json</c> CLI ->
/// explicit <c>gateway.tailnetEndpoint</c> config override. First success wins. The override
/// deliberately ranks LAST so a stale shared override can never poison a node whose real
/// MagicDNS identity resolves (the long-standing ResolveTailnetEndpoint policy).
///
/// Loopback is NEVER advertised: it stays correct for local Kestrel binding only, and a
/// loopback override is rejected with a reason instead of being passed through.
///
/// The probe funcs are seams so the detection order is unit-testable without Tailscale.
/// The DEFAULT probes honor the product's existing <c>CC_GATEWAY_NO_TAILSCALE=1</c> kill
/// switch (same contract as TailscaleServeSelfProvisioner): a process that must not touch
/// Tailscale must not advertise a Tailscale endpoint it refuses to provision. Pinned test
/// probes bypass the switch by construction.
/// </summary>
public sealed class TailnetIdentityResolver
{
    /// <summary>First rung: the tailscaled LocalAPI. Returns the MagicDNS name or null.</summary>
    public Func<string?> LocalApiProbe { get; set; } = () =>
        TailscaleDisabledByEnvironment ? null : TailscaleIdentity.TryGetMagicDnsNameViaLocalApi();

    /// <summary>Second rung: shelling <c>tailscale status --json</c>. Returns the MagicDNS name or null.</summary>
    public Func<string?> CliProbe { get; set; } = () =>
        TailscaleDisabledByEnvironment ? null : TailscaleIdentity.TryGetMagicDnsName();

    /// <summary>The product-wide Tailscale kill switch (dev/test isolation), see class doc.</summary>
    public static bool TailscaleDisabledByEnvironment =>
        string.Equals(Environment.GetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE"), "1", StringComparison.Ordinal);

    // Log-dedup state: this resolver runs every heartbeat cycle (issue #324), so identical
    // outcomes are logged ONCE per transition, not 4x/minute (the #197 log-drowning lesson).
    private string? _lastLoggedFailure;
    private string? _lastLoggedSuccess;

    /// <summary>
    /// Run the detection ladder for this Director's <paramref name="port"/>.
    /// <paramref name="configOverride"/> is the optional <c>gateway.tailnetEndpoint</c> value.
    /// Never throws; an unresolvable identity is an EXPECTED state reported via
    /// <see cref="TailnetEndpointResolution.FailureReason"/> so the caller can fail loudly.
    /// </summary>
    public TailnetEndpointResolution ResolveEndpoint(int port, string? configOverride)
    {
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 1-65535");

        var dnsName = LocalApiProbe();
        if (!string.IsNullOrWhiteSpace(dnsName))
            return Resolved(TailscaleIdentity.BuildFrontDoorUrlForPort(dnsName, port), "local-api");

        dnsName = CliProbe();
        if (!string.IsNullOrWhiteSpace(dnsName))
            return Resolved(TailscaleIdentity.BuildFrontDoorUrlForPort(dnsName, port), "cli");

        if (!string.IsNullOrWhiteSpace(configOverride))
        {
            if (IsLoopback(configOverride))
            {
                // The plan bans the silent loopback fallback for the ADVERTISED address: a
                // loopback URL is a lie to every remote caller. Refuse it, name the fix.
                return Unresolved($"gateway.tailnetEndpoint override '{configOverride}' is a loopback address and is never advertised - "
                                + "set it to a tailnet-routable URL, or start Tailscale so the MagicDNS identity resolves.");
            }
            return Resolved(configOverride, "config-override");
        }

        return Unresolved("No tailnet identity: the Tailscale LocalAPI and CLI both failed to resolve this machine's MagicDNS name "
                        + "and no gateway.tailnetEndpoint override is configured. Start Tailscale (and log in) on this machine, "
                        + "or set gateway.tailnetEndpoint in config.json.");
    }

    private TailnetEndpointResolution Resolved(string endpoint, string source)
    {
        var line = $"{endpoint} via {source}";
        if (_lastLoggedSuccess != line)
        {
            _lastLoggedSuccess = line;
            _lastLoggedFailure = null;
            FileLog.Write($"[TailnetIdentityResolver] ResolveEndpoint: resolved {line}");
        }
        return new TailnetEndpointResolution { Endpoint = endpoint, Source = source };
    }

    private TailnetEndpointResolution Unresolved(string reason)
    {
        if (_lastLoggedFailure != reason)
        {
            _lastLoggedFailure = reason;
            _lastLoggedSuccess = null;
            FileLog.Write($"[TailnetIdentityResolver] ResolveEndpoint FAILED: {reason}");
        }
        return new TailnetEndpointResolution { FailureReason = reason };
    }

    /// <summary>True when the URL (or bare host) points at loopback. Pure - unit-tested.</summary>
    public static bool IsLoopback(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return false; // unparsable is handled (and refused) by the verifier, not mislabeled as loopback
        return uri.IsLoopback;
    }
}
