using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Throttle;

/// <summary>
/// The only thing that feeds <see cref="ThrottleDefinition.Fold"/> from the store: the narrow projection
/// of the tenant's turn-submitted rows over a window, plus the session-history facts the repository split
/// joins on. Reads through <see cref="GatewayDatabase.CreateContext(TenantId)"/> with the tenant the ROUTE
/// resolved - explicit, never ambient - so on the hosted Gateway a figure can only ever be one account's
/// own.
///
/// It reads the Gateway database (the ledger lives there), not the statistics database the <c>stat_delta</c>
/// tally lives in. That is the point of ruling R9: the figure leaves that tally entirely.
/// </summary>
public sealed class ThrottleLedgerReader
{
    private readonly Func<TenantId, GatewayDbContext> _contextFor;

    /// <summary>Session ids are looked up in history in chunks this size, so a busy month never produces a
    /// query with more parameters than SQLite accepts.</summary>
    private const int HistoryChunk = 500;

    /// <summary>The Gateway's own reader: contexts come from the opened <see cref="GatewayDatabase"/>, scoped
    /// to the tenant the route resolved.</summary>
    public ThrottleLedgerReader(GatewayDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _contextFor = db.CreateContext;
    }

    /// <summary>
    /// A reader over a context source of the caller's choosing - the conformance check's way in. The check
    /// runs THIS SAME CODE against the hosted database from outside the Gateway, and it must open that
    /// database without <see cref="GatewayDatabase.Open"/>, which checks for and applies pending migrations:
    /// a read-only check must never be the thing that migrates the production schema. The source must hand
    /// back a context whose <see cref="GatewayDbContext.ActiveTenant"/> is already the requested tenant.
    /// </summary>
    public ThrottleLedgerReader(Func<TenantId, GatewayDbContext> contextFor)
    {
        _contextFor = contextFor ?? throw new ArgumentNullException(nameof(contextFor));
    }

    /// <summary>
    /// The figure for <paramref name="tenant"/> over [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).
    /// The window is the caller's to choose and to state; the ledger's own reach is reported beside it.
    /// </summary>
    public ThrottleFigureDto Compute(TenantId tenant, DateTime fromUtc, DateTime toUtc)
    {
        if (!tenant.IsValid) throw new ArgumentException("A valid TenantId is required.", nameof(tenant));
        if (toUtc <= fromUtc) throw new ArgumentException("The window must end after it starts.", nameof(toUtc));
        var from = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(toUtc, DateTimeKind.Utc);

        FileLog.Write($"[ThrottleLedgerReader] Compute: tenant={tenant.Value} from={from:O} to={to:O}");
        try
        {
            using var ctx = _contextFor(tenant);
            if (!string.Equals(ctx.ActiveTenant, tenant.Value, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The context source handed back a context scoped to a different tenant than the one asked " +
                    "for. The figure is refused: a wrong-tenant read is the one thing this reader must never do.");

            var rows = ctx.ActivityEvents.AsNoTracking()
                .Where(e => e.EventType == ThrottleDefinition.TurnSubmitted && e.OccurredUtc >= from && e.OccurredUtc < to)
                .Select(e => new { e.OccurredUtc, e.SessionId, e.AgentKind, e.InputOrigin, e.SendSource })
                .ToList()
                .Select(e => new ThrottleDefinition.LedgerSubmission(
                    DateTime.SpecifyKind(e.OccurredUtc, DateTimeKind.Utc), e.SessionId, e.AgentKind, e.InputOrigin, e.SendSource))
                .ToList();

            var earliest = ctx.ActivityEvents.AsNoTracking()
                .Where(e => e.EventType == ThrottleDefinition.TurnSubmitted)
                .Min(e => (DateTime?)e.OccurredUtc);

            var sessions = SessionFacts(ctx, rows.Select(r => r.SessionId).Distinct(StringComparer.Ordinal).ToList());

            var figure = ThrottleDefinition.Fold(rows, from, to, sessions);
            figure.Ledger = new ThrottleLedgerDto
            {
                RetentionDays = ThrottleDefinition.RetentionDays,
                EarliestUtc = earliest is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null,
            };
            FileLog.Write($"[ThrottleLedgerReader] Compute: tenant={tenant.Value} rows={rows.Count} counted={figure.Turns} " +
                          $"noOrigin={figure.Excluded.NoInputOrigin} sessions={figure.Sessions}");
            return figure;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ThrottleLedgerReader] Compute FAILED: tenant={tenant.Value} {ex.Message}");
            throw;
        }
    }

    private static Dictionary<string, ThrottleDefinition.SessionFacts> SessionFacts(GatewayDbContext ctx, List<string> sessionIds)
    {
        var found = new List<(string SessionId, string? RepoName, string? RepoPath, string? OriginKind, string? Machine, string? Os)>();
        for (var i = 0; i < sessionIds.Count; i += HistoryChunk)
        {
            var chunk = sessionIds.Skip(i).Take(HistoryChunk).ToList();
            found.AddRange(ctx.SessionHistory.AsNoTracking()
                .Where(s => chunk.Contains(s.SessionId))
                .Select(s => new { s.SessionId, s.RepoName, s.RepoPath, s.OriginKind, s.MachineName, s.Os })
                .ToList()
                .Select(s => (s.SessionId, s.RepoName, s.RepoPath, s.OriginKind, s.MachineName, s.Os)));
        }

        var machineOs = MachineSystems(ctx, found.Where(s => DirectorOs.Normalize(s.Os) is null).Select(s => s.Machine));

        var facts = new Dictionary<string, ThrottleDefinition.SessionFacts>(StringComparer.Ordinal);
        foreach (var s in found)
        {
            // The row's own report first. A row written before its Director reported a system takes the system that
            // machine HAS reported on another row - a machine's system does not change between sessions, and this is
            // what lets the Systems tab answer the whole retention window, not only the days since the report began.
            var os = DirectorOs.Normalize(s.Os)
                     ?? (s.Machine is { Length: > 0 } m && machineOs.TryGetValue(m, out var known) ? known : null);
            facts[s.SessionId] = new ThrottleDefinition.SessionFacts(s.RepoName, s.RepoPath, s.OriginKind, s.Machine, os);
        }
        return facts;
    }

    /// <summary>The system each named machine most recently reported, from this tenant's session history. A machine no
    /// row reports a system for is absent - its sessions are Unknown, never guessed.</summary>
    private static Dictionary<string, string> MachineSystems(GatewayDbContext ctx, IEnumerable<string?> machines)
    {
        var wanted = machines.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!)
            .Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return result;
        for (var i = 0; i < wanted.Count; i += HistoryChunk)
        {
            var chunk = wanted.Skip(i).Take(HistoryChunk).ToList();
            var reported = ctx.SessionHistory.AsNoTracking()
                .Where(s => s.MachineName != null && chunk.Contains(s.MachineName) && s.Os != null)
                .Select(s => new { s.MachineName, s.Os, s.LastSeenUtc })
                .ToList();
            foreach (var r in reported.OrderByDescending(r => r.LastSeenUtc))
                if (DirectorOs.Normalize(r.Os) is { } os) result.TryAdd(r.MachineName!, os);
        }
        return result;
    }
}
