namespace CcDirector.Setup.Engine;

/// <summary>A single external command to run, as exe + argument list.</summary>
public sealed record ServiceCommand(string Exe, IReadOnlyList<string> Args)
{
    /// <summary>A display string (for logs / dry-run), not for shell execution.</summary>
    public string Display => $"{Exe} {string.Join(' ', Args)}";
}

/// <summary>
/// Builds the command sequences a Gateway-role machine uses to apply its own
/// updates. The Gateway runs as LocalSystem, so it has the rights to stop/start
/// its service and write C:\cc-tools - no UAC after the first install (decision
/// D7). These builders are pure so the exact sequence is unit-testable; actual
/// execution + the detached-helper handoff is integration-only (deferred to the
/// empty-machine wizard run).
///
/// The running service exe is file-locked, so the swap cannot happen while the
/// service is up. The ordered flow is therefore: stop -> swap files -> start.
/// </summary>
public static class GatewayServiceCommands
{
    public const string ServiceName = "cc-gateway-service";

    /// <summary>nssm stop &lt;svc&gt; (used before swapping the Gateway exe).</summary>
    public static ServiceCommand Stop(string nssmPath, string serviceName = ServiceName) =>
        new(nssmPath, ["stop", serviceName]);

    /// <summary>nssm start &lt;svc&gt; (used after swapping the Gateway exe).</summary>
    public static ServiceCommand Start(string nssmPath, string serviceName = ServiceName) =>
        new(nssmPath, ["start", serviceName]);

    /// <summary>nssm status &lt;svc&gt; (used to confirm the service came back up).</summary>
    public static ServiceCommand Status(string nssmPath, string serviceName = ServiceName) =>
        new(nssmPath, ["status", serviceName]);

    /// <summary>
    /// The full ordered restart sequence around a Gateway-exe swap. The file swap
    /// itself (via <see cref="InstallSwapper.Place"/>) happens between Stop and
    /// Start; it is not a command, so it is not in this list.
    /// </summary>
    public static IReadOnlyList<ServiceCommand> RestartSequence(string nssmPath, string serviceName = ServiceName) =>
    [
        Stop(nssmPath, serviceName),
        Start(nssmPath, serviceName),
        Status(nssmPath, serviceName),
    ];
}
