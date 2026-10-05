using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Tenant-indexed live Director tunnel connections, each with a server-side abort callback. DirectorHub
/// populates it on Hello and clears it on disconnect. The cancellation cutoff (MTR-15) calls
/// <see cref="AbortForTenant"/> to sever every live tunnel a just-revoked tenant holds ON THIS Gateway
/// instance: the durable device tombstone already denies any NEW authentication, and this ends the
/// connections that are already up so a cancelled customer's active session actually stops.
///
/// Per-instance: each Gateway replica tracks only its own connections and aborts only those; a tenant whose
/// tunnels are spread across replicas has each replica's own monitor read NotEntitled and abort its local
/// connections within the sweep bound. Idempotent and race-safe: a disconnect that removes an entry, or a
/// second abort, is a harmless no-op. No PII is logged - only counts and the fixed reason.
/// </summary>
public sealed class DirectorConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _byConnection = new(StringComparer.Ordinal);

    private readonly record struct Entry(string Tenant, Action Abort, string? AccountSubject, string? DirectorId);

    /// <summary>
    /// Index a live connection under its tenant with the server-side abort to end it later. <paramref name="accountSubject"/>
    /// is the person the connection's device key was issued to and <paramref name="directorId"/> the Director that said
    /// Hello on it, so a team can cut ONE person's tunnels (<see cref="AbortForTenantMember"/>) and a moved Director's
    /// old tunnel can be cut on its own (<see cref="AbortForDirector"/>) (devthrottle_internal#2311). The subject is
    /// personally identifying and is never logged.
    /// </summary>
    public void Register(TenantId tenant, string connectionId, Action abort, string? accountSubject = null, string? directorId = null)
    {
        if (!tenant.IsValid || string.IsNullOrEmpty(connectionId) || abort is null)
            return;
        _byConnection[connectionId] = new Entry(tenant.Value, abort,
            string.IsNullOrWhiteSpace(accountSubject) ? null : accountSubject, string.IsNullOrWhiteSpace(directorId) ? null : directorId);
    }

    /// <summary>Drop a connection's entry (on disconnect). Idempotent.</summary>
    public void Unregister(string connectionId)
    {
        if (!string.IsNullOrEmpty(connectionId))
            _byConnection.TryRemove(connectionId, out _);
    }

    /// <summary>Abort every live connection this instance holds for the tenant. Returns the count aborted.
    /// Idempotent: connections already gone are simply not present.</summary>
    public int AbortForTenant(TenantId tenant, string reason)
    {
        if (!tenant.IsValid)
            return 0;

        var key = tenant.Value;
        var aborted = AbortWhere(e => string.Equals(e.Tenant, key, StringComparison.Ordinal));
        if (aborted > 0)
            FileLog.Write($"[DirectorConnectionRegistry] aborted {aborted} live Director connection(s) for a tenant (reason={reason})");
        return aborted;
    }

    /// <summary>
    /// Abort every live connection this instance holds for ONE person in ONE tenant - their Directors on a team they
    /// were removed from or made a Collaborator in (devthrottle_internal#2311). Never the team's other members' tunnels,
    /// the person's tunnels on their other teams, or on their personal tenant. Returns the count aborted.
    /// </summary>
    public int AbortForTenantMember(TenantId tenant, string accountSubject, string reason)
    {
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(accountSubject))
            return 0;

        var key = tenant.Value;
        var subject = accountSubject.Trim();
        var aborted = AbortWhere(e => string.Equals(e.Tenant, key, StringComparison.Ordinal)
                                      && string.Equals(e.AccountSubject, subject, StringComparison.Ordinal));
        FileLog.Write($"[DirectorConnectionRegistry] AbortForTenantMember: aborted {aborted} live Director connection(s) of one member of tenant {tenant.ToLogString()} (reason={reason})");
        return aborted;
    }

    /// <summary>
    /// Abort every live connection this instance holds for ONE Director in ONE tenant - the old tunnel of a Director
    /// that has just moved to another team, whose key is already revoked (devthrottle_internal#2311). Returns the count
    /// aborted.
    /// </summary>
    public int AbortForDirector(TenantId tenant, string directorId, string reason)
    {
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(directorId))
            return 0;

        var key = tenant.Value;
        var id = directorId.Trim();
        var aborted = AbortWhere(e => string.Equals(e.Tenant, key, StringComparison.Ordinal)
                                      && string.Equals(e.DirectorId, id, StringComparison.OrdinalIgnoreCase));
        FileLog.Write($"[DirectorConnectionRegistry] AbortForDirector: aborted {aborted} live connection(s) of director={id} in tenant {tenant.ToLogString()} (reason={reason})");
        return aborted;
    }

    private int AbortWhere(Func<Entry, bool> matches)
    {
        var aborted = 0;
        foreach (var kv in _byConnection)
        {
            if (!matches(kv.Value))
                continue;
            if (_byConnection.TryRemove(kv.Key, out var entry))
            {
                try { entry.Abort(); aborted++; }
                catch (Exception ex) { FileLog.Write($"[DirectorConnectionRegistry] abort failed for a connection, continuing ({ex.GetType().Name})"); }
            }
        }
        return aborted;
    }
}
