using CcDirector.Core.Network;

namespace CcDirector.Gateway;

/// <summary>
/// Resolves the ONE public URL a client is handed for a Gateway SURFACE - the Cockpit
/// (<see cref="CockpitPath"/>) or the mobile app (<see cref="MobilePath"/>). There is ONE public base
/// URL for this Gateway, and every surface is a PATH under it (owner ruling 2026-07-20): the Cockpit URL
/// is <c>{base}/cockpit</c> and the mobile URL is <c>{base}/mobile</c>, derived here on the Gateway,
/// exactly as the same-machine case already works (a <c>host:port/cockpit</c> URL). One derivation rule,
/// hosted and local alike. The client is dumb: it opens whatever URL it is handed, so the whole hosted-vs-self-host
/// verdict AND the surface path are decided HERE, once, on the Gateway (CLAUDE.md rule 7).
///
/// Two modes, one gated on the hosted signal (<see cref="GatewayHostedMode.IsHosted"/>):
///  - HOSTED (<c>CC_GATEWAY_HOSTED=1</c>): a container reached by its public URL, with no tailscale in the
///    image. The public base URL is configuration, read once from <see cref="PublicBaseUrlEnvVar"/>
///    (set to <c>https://gateway.devthrottle.com</c> on the App Service). There is NO fallback: a hosted
///    Gateway with that variable unset is a deploy misconfiguration, so this FAILS LOUD
///    (<see cref="System.InvalidOperationException"/>) rather than paper over it with a null or a guess.
///  - SELF-HOST (anything else): the base is the tailnet front-door base from
///    <see cref="TailscaleIdentity.TryGetFrontDoorBaseUrl"/>, which is null when Tailscale is down. The
///    base-resolution branch is byte-identical to before this class existed - the tailscale CLI is shelled
///    ONLY on this branch, never in hosted mode - and only the surface PATH is now appended explicitly.
///
/// The value returned is a full URL (base + surface path, e.g. <c>https://gateway.devthrottle.com/cockpit</c>)
/// with no trailing slash, or null in self-host when Tailscale is down. A caller that needs to know the
/// surface is unavailable checks for null; a caller that always has a value (hosted) never sees one.
/// </summary>
public static class GatewayPublicUrl
{
    /// <summary>
    /// The environment variable carrying the hosted Gateway's ONE public base URL, e.g.
    /// <c>https://gateway.devthrottle.com</c>. Read once per request; consulted ONLY in hosted mode.
    /// </summary>
    public const string PublicBaseUrlEnvVar = "CC_GATEWAY_PUBLIC_URL";

    /// <summary>The Cockpit surface path under the base (the React shell fallback serves it).</summary>
    public const string CockpitPath = "/cockpit";

    /// <summary>The mobile app surface path under the base.</summary>
    public const string MobilePath = "/mobile";

    /// <summary>
    /// The prefix of the Cockpit's per-session screen, under the base: the full surface path is
    /// <c>/session/{session id}</c>. The Cockpit is served at the site root, so the session screen is a
    /// sibling of <see cref="CockpitPath"/> rather than a child of it.
    /// </summary>
    public const string SessionPathPrefix = "/session";

    /// <summary>
    /// Resolve the full public URL for the Cockpit surface (<c>{base}/cockpit</c>) against the live
    /// environment: the configured public base in hosted mode, the tailnet front door otherwise.
    /// </summary>
    /// <returns>The full Cockpit URL, or null in self-host mode when Tailscale is down.</returns>
    public static string? ResolveCockpit() => Resolve(CockpitPath);

    /// <summary>
    /// Cockpit live wrapper with the self-host front-door source injected, so the self-host branch can be
    /// driven against a KNOWN front door. The real tailnet provider returns null on a build host with no
    /// tailnet, so the pure fixture cannot pin the self-host LIVE path to an exact value; this seam can.
    /// The no-arg <see cref="ResolveCockpit()"/> passes the real
    /// <see cref="TailscaleIdentity.TryGetFrontDoorBaseUrl"/>. A self-host-only hardcode reddens the
    /// injected-front-door test because the result is no longer exactly <c>{frontDoor}/cockpit</c>.
    /// </summary>
    internal static string? ResolveCockpit(Func<string?> frontDoorProvider)
        => Resolve(CockpitPath, frontDoorProvider);

    /// <summary>
    /// Resolve the full public URL for ONE session's Cockpit screen (<c>{base}/session/{session id}</c>)
    /// against the live environment. Same base rule as <see cref="ResolveCockpit()"/>; only the surface
    /// path differs.
    ///
    /// This exists so that a client wanting "this session in the Cockpit" still receives ONE finished URL
    /// and opens it verbatim. The alternative - handing out the front door and letting the caller append
    /// the session path - is the client composing a URL, which rule 7 forbids and which a desktop test
    /// already reddens on.
    /// </summary>
    /// <param name="sessionId">The session id. Percent-encoded into the path segment, so an id carrying a
    /// character that is not path-safe cannot change the shape of the URL.</param>
    /// <returns>The full session URL, or null in self-host mode when Tailscale is down.</returns>
    /// <exception cref="ArgumentException">The session id is missing or blank. There is no session screen
    /// without a session, and silently handing back the front door instead would send the caller somewhere
    /// it did not ask for.</exception>
    public static string? ResolveCockpitSession(string sessionId)
        => ResolveCockpitSession(sessionId, TailscaleIdentity.TryGetFrontDoorBaseUrl);

    /// <summary>
    /// Session-screen resolver with the self-host front-door source injected, so the self-host branch can
    /// be driven against a KNOWN front door - the same seam, and for the same reason, as
    /// <see cref="ResolveCockpit(Func{string?})"/>.
    /// </summary>
    internal static string? ResolveCockpitSession(string sessionId, Func<string?> frontDoorProvider)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException(
                "A session id is required to resolve the Cockpit's session screen.", nameof(sessionId));

        return Resolve($"{SessionPathPrefix}/{Uri.EscapeDataString(sessionId.Trim())}", frontDoorProvider);
    }

    /// <summary>
    /// Resolve the full public URL for the mobile-app surface (<c>{base}/mobile</c>) against the live
    /// environment. Same base rule as <see cref="ResolveCockpit"/>; the surface path is the only difference.
    ///
    /// The <c>/m</c>-&gt;<c>/mobile</c> app re-base has LANDED (Phase D): the mobile app now serves at
    /// <c>/mobile</c> (Vite base, React Router basename, service-worker scope, and the sign-in seam all
    /// re-rooted there), and the Gateway 301-redirects the old <c>/m</c> mount to it. What is still
    /// DEFERRED is a CONSUMER of this method: nothing hands out a mobile URL / QR / "open on phone" link
    /// yet, so <c>ResolveMobile()</c> is resolved and unit-tested but not yet wired into a caller. The
    /// first consumer lands with whatever feature needs to hand a phone its URL.
    /// </summary>
    /// <returns>The full mobile URL, or null in self-host mode when Tailscale is down.</returns>
    public static string? ResolveMobile() => Resolve(MobilePath);

    /// <summary>
    /// The public BASE address of this Gateway (no surface path appended), or null in self-host when
    /// Tailscale is down. This is the read-only "address" the About page shows now that manual network
    /// addressing is retired (issue #2022) - the same one derivation as every surface URL, with the surface
    /// path stripped so it reads as a host address rather than a page link.
    /// </summary>
    public static string? ResolveBase()
    {
        var cockpit = ResolveCockpit();
        if (cockpit is null) return null;
        return cockpit.EndsWith(CockpitPath, StringComparison.Ordinal)
            ? cockpit[..^CockpitPath.Length]
            : cockpit;
    }

    /// <summary>
    /// Resolve the full public URL for a surface PATH against the live environment: the configured public
    /// base in hosted mode, the tailnet front door otherwise. Fails loud when hosted and the base is unset.
    /// Shells the tailscale CLI only on the self-host branch (never in hosted mode).
    /// </summary>
    /// <param name="surfacePath">The surface path, e.g. <see cref="CockpitPath"/> or <see cref="MobilePath"/>.</param>
    /// <returns>The full URL (base + path), or null in self-host mode when Tailscale is down.</returns>
    public static string? Resolve(string surfacePath)
        => Resolve(surfacePath, TailscaleIdentity.TryGetFrontDoorBaseUrl);

    /// <summary>
    /// Live surface resolver with the self-host front-door source injected. Hosted mode reads the
    /// configured public base from the environment (and fails loud when unset); self-host mode uses
    /// <paramref name="frontDoorProvider"/>, which the no-arg wrapper supplies as the real
    /// <see cref="TailscaleIdentity.TryGetFrontDoorBaseUrl"/>. Shells the tailscale CLI only via the
    /// provider on the self-host branch (never in hosted mode). The seam exists so a test can drive the
    /// self-host LIVE path with a KNOWN front door and assert the exact <c>{frontDoor}{path}</c> result.
    /// </summary>
    internal static string? Resolve(string surfacePath, Func<string?> frontDoorProvider)
        => GatewayHostedMode.IsHosted
            ? Resolve(true, Environment.GetEnvironmentVariable(PublicBaseUrlEnvVar), selfHostFrontDoor: null, surfacePath)
            : Resolve(false, hostedConfiguredBase: null, frontDoorProvider(), surfacePath);

    /// <summary>
    /// Pure resolver - fully unit-testable without touching the real environment or shelling tailscale.
    /// </summary>
    /// <param name="isHosted"><see cref="GatewayHostedMode.IsHosted"/>.</param>
    /// <param name="hostedConfiguredBase">The <see cref="PublicBaseUrlEnvVar"/> value; may be null or blank.</param>
    /// <param name="selfHostFrontDoor">The self-host front door from
    /// <see cref="TailscaleIdentity.TryGetFrontDoorBaseUrl"/>; null when Tailscale is down.</param>
    /// <param name="surfacePath">The surface path to append to the base (leading slash optional).</param>
    /// <returns>The full URL (base + path) with no trailing slash, or null in self-host mode when the
    /// front door is null.</returns>
    /// <exception cref="InvalidOperationException">Hosted mode with <paramref name="hostedConfiguredBase"/>
    /// missing or blank - a deploy misconfiguration that must not be papered over.</exception>
    public static string? Resolve(bool isHosted, string? hostedConfiguredBase, string? selfHostFrontDoor, string surfacePath)
    {
        var path = NormalizeSurfacePath(surfacePath);

        if (isHosted)
        {
            if (string.IsNullOrWhiteSpace(hostedConfiguredBase))
                throw new InvalidOperationException(
                    $"{PublicBaseUrlEnvVar} is not set. A hosted Gateway (CC_GATEWAY_HOSTED=1) must be " +
                    "configured with its public base URL (for example https://gateway.devthrottle.com). " +
                    "This is a deploy misconfiguration - refusing to serve a missing or guessed public URL.");

            return NormalizeBase(hostedConfiguredBase) + path;
        }

        // Self-host: the tailnet front door base (null when down), with the surface path appended. The
        // base-resolution branch is unchanged; only the explicit surface path is new.
        return string.IsNullOrWhiteSpace(selfHostFrontDoor) ? null : NormalizeBase(selfHostFrontDoor) + path;
    }

    /// <summary>Trim whitespace and any trailing slash so the appended surface path never doubles it.</summary>
    private static string NormalizeBase(string baseUrl) => baseUrl.Trim().TrimEnd('/');

    /// <summary>Ensure the surface path has a single leading slash and no trailing slash.</summary>
    private static string NormalizeSurfacePath(string surfacePath)
    {
        var trimmed = (surfacePath ?? "").Trim().Trim('/');
        return "/" + trimmed;
    }
}
