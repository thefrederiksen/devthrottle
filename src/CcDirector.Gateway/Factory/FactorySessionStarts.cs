using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// Which sessions a factory agent started (Website Business Factory, Screen 6): the factory activity record's
/// "started" rows for the sessions the roster is showing, and no others. The roster fold stamps its chip from this.
/// </summary>
public static class FactorySessionStarts
{
    /// <summary>For an account and the session ids on screen: the row that started each one a factory agent started.</summary>
    public delegate IReadOnlyDictionary<string, FactoryActivityDto> Reader(TenantId tenant, IReadOnlyCollection<string> sessionIds);

    /// <summary>No session started by a factory agent: what the roster reads for an account the switch is off for.</summary>
    public static readonly IReadOnlyDictionary<string, FactoryActivityDto> None =
        new Dictionary<string, FactoryActivityDto>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Read the "started" rows for <paramref name="sessionIds"/>, oldest first, keeping the FIRST per session: a
    /// session started once, and a later "started" row naming it (a copy, a retry) never renames its origin.
    /// </summary>
    public static IReadOnlyDictionary<string, FactoryActivityDto> Read(
        FactoryActivityRecord record, TenantId tenant, IReadOnlyCollection<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(sessionIds);
        var ids = sessionIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count == 0) return None;

        var started = new Dictionary<string, FactoryActivityDto>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (true)
        {
            var page = record.Query(tenant, outcome: FactoryActivityOutcome.Started, oldestFirst: true,
                offset: offset, limit: FactoryActivityRecord.MaxPageSize, sessionIds: ids);
            foreach (var row in page.Rows)
                if (!string.IsNullOrEmpty(row.SessionId))
                    started.TryAdd(row.SessionId, row);
            if (!page.HasMore) break;
            offset += page.Rows.Count;
        }
        FileLog.Write($"[FactorySessionStarts] Read: sessions={ids.Count}, started by a factory agent={started.Count}");
        return started;
    }
}
