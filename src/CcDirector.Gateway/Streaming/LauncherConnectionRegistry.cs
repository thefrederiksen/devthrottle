using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// One launcher's live command stream: the connection a command is addressed down, and what that
/// launcher declared about itself when it joined.
///
/// THE TWO ARE ONE OBJECT ON PURPOSE. They were going to be two entries - a connection map and a
/// declaration map - and that shape can express a state that must never exist: a declaration with no
/// connection, which is a launcher that has gone still vouching for itself out of a record nobody
/// cleared. A capability query reading that would certify a machine that is not there. Stored together,
/// the declaration is created with the connection and destroyed with it, and the impossible state cannot
/// be written.
/// </summary>
/// <param name="ConnectionId">The SignalR connection a command is pushed down.</param>
/// <param name="Declaration">What the launcher declared on <c>Hello</c>, or null when it declared
/// nothing - which is a REACHABLE launcher older than the capability handshake, and a different state
/// from a launcher that never joined at all.</param>
public sealed record LauncherStreamConnection(string ConnectionId, LauncherCapabilityDeclaration? Declaration);

/// <summary>
/// launcher-persistent-join: the Gateway's map of which machine's cc-launcher is currently connected over a
/// persistent stream, keyed by <see cref="MachineKey"/> - the OWNING TENANT plus the machine name
/// (case-insensitive) - with the SignalR connection id as the value.
///
/// It is the launcher twin of the connection-tracking half of <see cref="PushedSessionStore"/>, but far
/// simpler: a launcher pushes no session state, so there is nothing to cache - only the live connection id
/// the Gateway addresses a command DOWN. One (tenant, machine) -> one active connection. A new connection
/// from the same machine under the same tenant supersedes the prior one; a different tenant's launcher for a
/// machine of the same bare name is a DIFFERENT entry and cannot supersede it - machine names are not unique
/// across tenants, so the tenant is half the key.
///
/// Thread-safe via a <see cref="ConcurrentDictionary{TKey,TValue}"/>. Registered as a DI singleton so the
/// hub (constructed per-invocation by SignalR's container) and <c>GatewayHost.SendLauncherCommandAsync</c>
/// share the one instance.
/// </summary>
public sealed class LauncherConnectionRegistry
{
    /// <summary>The composite key: a machine name is unique only WITHIN a tenant, so the tenant is part of
    /// the key. The machine name is canonicalized (trimmed, lower-cased) so keying is case-insensitive.</summary>
    private readonly record struct MachineKey(TenantId Tenant, string Machine);

    private static MachineKey Key(TenantId tenant, string machineName) =>
        new(tenant, machineName.Trim().ToLowerInvariant());

    private readonly ConcurrentDictionary<MachineKey, LauncherStreamConnection> _byMachine = new();

    /// <summary>
    /// Mark <paramref name="connectionId"/> as the active stream connection for the owning tenant's
    /// <paramref name="machineName"/>, superseding any prior connection for that (tenant, machine).
    /// </summary>
    /// <param name="declaration">What this launcher declared about itself on Hello, or null when it
    /// declared nothing. Stored WITH the connection so it can never outlive it - see
    /// <see cref="LauncherStreamConnection"/>. A superseding connection replaces the declaration too,
    /// which matters when a launcher is updated in place: the new process's Hello must not be answered
    /// with the old build's promises.</param>
    public void RegisterConnection(TenantId tenant, string machineName, string connectionId,
        LauncherCapabilityDeclaration? declaration = null)
    {
        if (string.IsNullOrWhiteSpace(machineName))
            throw new ArgumentException("machineName is required", nameof(machineName));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("connectionId is required", nameof(connectionId));

        _byMachine[Key(tenant, machineName)] = new LauncherStreamConnection(connectionId, declaration);
        FileLog.Write($"[LauncherConnectionRegistry] RegisterConnection: tenant={tenant.Value}, machine={machineName}, conn={Short(connectionId)} is now the active connection (declares: {Describe(declaration)})");
    }

    /// <summary>One plain phrase for the log, telling a launcher that declared nothing from one that did.</summary>
    private static string Describe(LauncherCapabilityDeclaration? declaration)
        => declaration is null
            ? "nothing - a launcher older than the capability handshake"
            : declaration.Commands.Count == 0
                ? "an empty capability list"
                : string.Join(" ", declaration.Commands);

    /// <summary>
    /// Clear the entry whose active connection is <paramref name="connectionId"/>. A late disconnect from a
    /// superseded connection removes nothing (the newer connection owns the (tenant, machine)), because the
    /// atomic key/value remove only succeeds when the stored connection id still matches. Scanning by the
    /// connection id needs no tenant - a connection id belongs to exactly one entry.
    /// </summary>
    public void Unregister(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;

        foreach (var kv in _byMachine)
        {
            if (!string.Equals(kv.Value.ConnectionId, connectionId, StringComparison.Ordinal))
                continue;

            if (((ICollection<KeyValuePair<MachineKey, LauncherStreamConnection>>)_byMachine).Remove(kv))
                FileLog.Write($"[LauncherConnectionRegistry] Unregister: tenant={kv.Key.Tenant.Value}, machine={kv.Key.Machine}, conn={Short(connectionId)} cleared");
            else
                FileLog.Write($"[LauncherConnectionRegistry] Unregister IGNORED (superseded): tenant={kv.Key.Tenant.Value}, machine={kv.Key.Machine}, conn={Short(connectionId)}");
            return;
        }
    }

    /// <summary>
    /// The active stream connection id for the tenant's machine launcher, or null when none. The Gateway
    /// uses it to address a command DOWN the stream to that launcher - and only ever finds the caller's own.
    /// </summary>
    public string? GetActiveConnectionId(TenantId tenant, string machineName) =>
        GetActiveConnection(tenant, machineName)?.ConnectionId;

    /// <summary>
    /// The tenant's launcher connection for that machine - the connection id AND what that launcher
    /// declared about itself - or null when it holds no stream.
    ///
    /// ONE READ, so the capability query cannot be handed a declaration and a connection that disagree.
    /// Asking two questions of two maps has a window between them in which a launcher disconnects, and a
    /// caller that read the declaration on the far side of it would report a machine that has gone as one
    /// that answered.
    /// </summary>
    public LauncherStreamConnection? GetActiveConnection(TenantId tenant, string machineName) =>
        _byMachine.TryGetValue(Key(tenant, machineName), out var connection) ? connection : null;

    /// <summary>True when this tenant's launcher for the machine currently has an active stream connection.</summary>
    public bool IsStreamConnected(TenantId tenant, string machineName) =>
        _byMachine.ContainsKey(Key(tenant, machineName));

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);
}
