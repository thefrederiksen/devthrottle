using System.Reflection;

namespace CcDirector.Gateway;

/// <summary>
/// The HOSTED-mode signal for a Gateway running as a hosted/container deployment (for example Azure App
/// Service), reached by its PUBLIC URL rather than a tailnet. Set the environment variable
/// <c>CC_GATEWAY_HOSTED=1</c> to enable it. Fail-safe: any missing or other value is NOT hosted, so the
/// desktop/local behavior is byte-identical to today.
///
/// <see cref="IsHosted"/> is read in exactly two places: <see cref="GatewayHostOptions.FromEnvironment"/>, which
/// fixes the mode for a <see cref="GatewayHost"/> when it is constructed, and <see cref="GatewayEntryPoint"/>, which
/// picks the listen port. Everything else takes the mode from the host as a required value, so two hosts in one
/// process (the test suite) can run in different modes at once. An architecture test keeps it that way.
///
/// When hosted:
///  - Tailscale Serve auto-provisioning (<see cref="Tailscale.TailscaleServeProvisioner"/>) and the
///    tailscale-shelling network-diagnostics monitor (<see cref="Api.NetDiagMonitor"/>) are NOT started. A
///    hosted Gateway is reached by its public URL, not a tailnet, and the container image bundles no tailscale
///    binary, so starting them only produces noise and repeated errors.
///  - The listener honors the App Service port convention (<c>WEBSITES_PORT</c>, else <c>PORT</c>, else the
///    default), so the platform's front-end can route to the container's assigned port. The bind address is
///    all-interfaces in BOTH modes (that is unchanged - it is how a tailnet client reaches a desktop Gateway),
///    with the auth gate enforcing on every non-public route, so an all-interfaces bind is
///    reachable-but-authenticated, not open.
/// </summary>
public static class GatewayHostedMode
{
    /// <summary>The runtime toggle that turns hosted mode on: <c>CC_GATEWAY_HOSTED=1</c>. This is the value
    /// a slot swap or config restore can drop; the IMMUTABLE identity is <see cref="IsHostedImage"/>.</summary>
    public const string HostedEnvVar = "CC_GATEWAY_HOSTED";

    /// <summary>True when <c>CC_GATEWAY_HOSTED=1</c> - this Gateway is a hosted/container deployment.</summary>
    public static bool IsHosted =>
        string.Equals(Environment.GetEnvironmentVariable(HostedEnvVar), "1", StringComparison.Ordinal);

    /// <summary>
    /// The file name of the IMAGE-WIDE hosted identity marker. The hosted host project
    /// (<c>CcDirector.Gateway.Host</c>) bakes this file into its published output, so it sits in the
    /// application base directory of the hosted artifact ALONGSIDE every executable that image ships. It is
    /// what makes hosted identity image-wide rather than tied to one entry assembly: publishing the host also
    /// ships a runnable, UNMARKED <c>CcDirector.Gateway.dll</c> (with its own deps.json/runtimeconfig.json),
    /// and a compiled-in attribute only identifies the launcher it is stamped on - so overriding the
    /// container entrypoint to that unmarked executable would let the same hosted artifact boot as self-host
    /// with no contract. This marker closes that hole: it is read from the deployment directory the two
    /// executables SHARE, so <see cref="IsHostedImage"/> is true whichever one runs. Like the deps.json it
    /// ships next to, it is part of the deployed artifact, so a slot swap or config restore - which drop
    /// environment variables, not files in the image - cannot remove it.
    /// </summary>
    public const string HostedImageMarkerFileName = "hosted-gateway.image";

    /// <summary>
    /// True when the RUNNING deployment is the hosted container build - the IMMUTABLE hosted identity. Unlike
    /// <see cref="IsHosted"/> (a runtime environment toggle a slot swap or config restore can drop), this is
    /// part of the compiled/published artifact. <see cref="HostedStartupContract"/> uses it to decide that
    /// the FULL hosted contract must be proven at startup or the process must refuse to run - so a hosted
    /// image can never silently downgrade to Local single-tenant / no-auth semantics.
    ///
    /// It is true when EITHER signal holds (see <see cref="IsHostedImageDeployment"/>): the entry assembly
    /// carries the compiled-in <see cref="HostedGatewayImageAttribute"/> (the marked host launcher), OR the
    /// image-wide <see cref="HostedImageMarkerFileName"/> marker is present in the application base directory.
    /// The second signal is what the hosted image's OTHER, unmarked runnable executable
    /// (<c>CcDirector.Gateway.dll</c>) reads, so the contract cannot be bypassed by which entry executable
    /// the container runs.
    /// </summary>
    public static bool IsHostedImage =>
        IsHostedImageDeployment(System.Reflection.Assembly.GetEntryAssembly(), AppContext.BaseDirectory);

    /// <summary>
    /// Pure form of <see cref="IsHostedImage"/> for testing: the running deployment IS the hosted image when
    /// EITHER the entry assembly carries the compiled-in marker (<see cref="IsHostedImageAssembly"/>) OR the
    /// image-wide marker file is present in <paramref name="baseDirectory"/>
    /// (<see cref="HostedImageMarkerPresent"/>). Every executable the hosted image ships runs from the same
    /// base directory, so the second signal makes the identity image-wide: the unmarked
    /// <c>CcDirector.Gateway.dll</c> sees the marker exactly as the marked host does, and neither can boot the
    /// hosted artifact as self-host.
    /// </summary>
    internal static bool IsHostedImageDeployment(System.Reflection.Assembly? entryAssembly, string? baseDirectory)
        => IsHostedImageAssembly(entryAssembly) || HostedImageMarkerPresent(baseDirectory);

    /// <summary>
    /// Pure form of the entry-assembly half of the hosted identity: true when <paramref name="entryAssembly"/>
    /// is non-null and carries <see cref="HostedGatewayImageAttribute"/>. A null entry assembly (some test
    /// hosts) is treated as NOT the hosted image, so a test run is self-host by default.
    /// </summary>
    internal static bool IsHostedImageAssembly(System.Reflection.Assembly? entryAssembly)
        => entryAssembly?.GetCustomAttribute<HostedGatewayImageAttribute>() is not null;

    /// <summary>
    /// Pure form of the image-wide half of the hosted identity: true when the
    /// <see cref="HostedImageMarkerFileName"/> marker file exists in <paramref name="baseDirectory"/>. A null
    /// or blank base directory is treated as NOT the hosted image (self-host), so this never fabricates a
    /// hosted verdict from a missing path.
    /// </summary>
    internal static bool HostedImageMarkerPresent(string? baseDirectory)
        => !string.IsNullOrEmpty(baseDirectory)
           && File.Exists(Path.Combine(baseDirectory, HostedImageMarkerFileName));

    /// <summary>
    /// The port a hosted Gateway should listen on: the App Service <c>WEBSITES_PORT</c>, else <c>PORT</c>, else
    /// <paramref name="fallbackPort"/> (the port the process was started with). Only consulted in hosted mode;
    /// a missing/blank/invalid value falls through to the fallback, so it never fails the listener.
    /// </summary>
    public static int ResolveHostedPort(int fallbackPort)
    {
        foreach (var name in new[] { "WEBSITES_PORT", "PORT" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value) && int.TryParse(value, out var p) && p is > 0 and <= 65535)
                return p;
        }
        return fallbackPort;
    }
}
