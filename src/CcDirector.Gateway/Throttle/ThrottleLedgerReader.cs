using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
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
        var facts = new Dictionary<string, ThrottleDefinition.SessionFacts>(StringComparer.Ordinal);
        for (var i = 0; i < sessionIds.Count; i += HistoryChunk)
        {
            var chunk = sessionIds.Skip(i).Take(HistoryChunk).ToList();
            var found = ctx.SessionHistory.AsNoTracking()
                .Where(s => chunk.Contains(s.SessionId))
                .Select(s => new { s.SessionId, s.RepoName, s.RepoPath, s.OriginKind })
                .ToList();
            foreach (var s in found)
                facts[s.SessionId] = new ThrottleDefinition.SessionFacts(s.RepoName, s.RepoPath, s.OriginKind);
        }
        return facts;
    }
}
