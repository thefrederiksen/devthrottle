using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.SignalR;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// launcher-persistent-join: the SignalR hub each cc-launcher dials OUT to, so the always-on launcher stays
/// joined to the Gateway and the Gateway can PUSH a lifecycle command (start/stop/restart a Director, or a
/// generic launch) DOWN the open stream instead of dialing the launcher's loopback REST API.
///
/// This is the launcher twin of <see cref="DirectorHub"/>, but one-directional up: a launcher only sends
/// <see cref="Hello"/> (the identity binding) - it pushes no session state. Every command flows the other
/// way, invoked by the Gateway on the client. So the only inbound method here is Hello.
///
/// Identity binding: the first message MUST be <see cref="Hello"/>, which binds this connection to one
/// machine name in <see cref="Microsoft.AspNetCore.SignalR.HubCallerContext.Items"/> and registers it in the
/// <see cref="LauncherConnectionRegistry"/>. A re-claim to a different machine on the same connection is
/// rejected. The transport itself is already authenticated by the host-wide token middleware (the shared
/// token or a per-device key), exactly like <see cref="DirectorHub"/>.
///
/// The hub holds no state of its own; it is a thin adapter onto <see cref="LauncherConnectionRegistry"/>.
/// SignalR constructs it per invocation via dependency injection.
/// </summary>
public sealed class LauncherHub : Hub
{
    private const string MachineNameItemKey = "cc.launcherMachine";
    private const string TenantIdItemKey = "cc.tenantId";

    private readonly LauncherConnectionRegistry _registry;
    private readonly HostedTenantBoundary? _tenantBoundary;

    // REQUIRED AND NON-NULLABLE (finding I1-01): the boundary is a security argument, so constructing this
    // hub without one must be a compile error, never a silent default. In production SignalR resolves it from
    // dependency injection (GatewayHost registers the singleton); a self-host process registers a boundary
    // built over the SingleTenantContext, which always resolves Local. The FIELD stays nullable because a
    // miswire is still expressible with a forced null, and the runtime gate in ResolveConnectionTenant must
    // hold even then.
    public LauncherHub(LauncherConnectionRegistry registry, HostedTenantBoundary tenantBoundary)
    {
        _registry = registry;
        _tenantBoundary = tenantBoundary;
    }

    /// <summary>Bind this connection to the calling tenant's machine launcher. Must be the first message;
    /// aborts on a bad name or - on hosted - a device key that resolves to no tenant (deny by default,
    /// exactly like <see cref="DirectorHub"/>). The machine name is client-supplied; the TENANT is not - it
    /// comes from the authenticated device key, so one account cannot register a launcher into another's
    /// partition.</summary>
    public void Hello(LauncherStreamHello hello)
    {
        if (hello is null || string.IsNullOrWhiteSpace(hello.MachineName))
        {
            FileLog.Write($"[LauncherHub] Hello REJECTED (missing machineName): conn={Short(Context.ConnectionId)}");
            Context.Abort();
            return;
        }

        var machine = hello.MachineName.Trim();
        var alreadyBound = BoundMachine();
        if (alreadyBound is not null && !string.Equals(alreadyBound, machine, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[LauncherHub] Hello REJECTED (conn already bound to {alreadyBound}, cannot re-claim {machine}): conn={Short(Context.ConnectionId)}");
            Context.Abort();
            return;
        }

        var resolved = ResolveConnectionTenant();
        if (resolved is not { } tenant)
        {
            FileLog.Write($"[LauncherHub] Hello REJECTED (hosted: the authenticated device key resolves to no tenant): conn={Short(Context.ConnectionId)}");
            Context.Abort();
            return;
        }

        Context.Items[MachineNameItemKey] = machine;
        Context.Items[TenantIdItemKey] = tenant;
        // The declaration is stored WITH the connection and dies with it (see LauncherStreamConnection),
        // so a launcher can never be found vouching for itself after it has gone. A null here is a
        // launcher older than the capability handshake - reachable, and silent about itself - which the
        // capability query reports as its own state rather than folding into "cannot".
        _registry.RegisterConnection(tenant, machine, Context.ConnectionId, hello.Capability);
        // ToLogString, never Value: on hosted the raw account tenant id must not reach a log (the data
        // map promises hashed tenant ids in service logs, and this line was one of two that broke it).
        FileLog.Write($"[LauncherHub] Hello: tenant={tenant.ToLogString()}, machine={machine} bound to conn={Short(Context.ConnectionId)} (version={hello.Version})");
    }

    /// <summary>The tenant that owns this connection, from the authenticated device key (never the payload).
    /// On self-host this is <see cref="TenantId.Local"/>; on hosted a key with no bound tenant returns null
    /// and the connection is aborted.
    ///
    /// GATED ON <see cref="GatewayHostedMode.IsHosted"/> ITSELF, never on whether a boundary was wired
    /// (finding I1-01, the same shape <c>GatewayEndpoints.ResolveReadTenant</c> carries). Deciding on the
    /// field fails OPEN: a hosted process whose hub was constructed without a boundary would resolve
    /// <see cref="TenantId.Local"/> and register the launcher into the shared partition. On hosted, a missing
    /// or non-hosted-wired boundary resolves to null, and null is a REFUSAL - Hello aborts the connection.
    /// The second defence is the required non-nullable constructor parameter.</summary>
    private TenantId? ResolveConnectionTenant()
    {
        var key = Context.GetHttpContext()?.Items.TryGetValue(AuthMiddleware.DeviceKeyItemKey, out var value) == true
            ? value as string
            : null;
        if (!GatewayHostedMode.IsHosted)
            return _tenantBoundary is null ? TenantId.Local : _tenantBoundary.ResolveForDeviceKey(key);
        if (_tenantBoundary is null || !_tenantBoundary.IsHosted)
            return null;
        return _tenantBoundary.ResolveForDeviceKey(key);
    }

    public override Task OnConnectedAsync()
    {
        FileLog.Write($"[LauncherHub] connected: conn={Short(Context.ConnectionId)}");
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.Unregister(Context.ConnectionId);
        FileLog.Write($"[LauncherHub] disconnected: conn={Short(Context.ConnectionId)}, machine={BoundMachine() ?? "(unbound)"} ({exception?.Message ?? "clean"})");
        return base.OnDisconnectedAsync(exception);
    }

    private string? BoundMachine() =>
        Context.Items.TryGetValue(MachineNameItemKey, out var value) && value is string name ? name : null;

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);
}
