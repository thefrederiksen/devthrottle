using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Fleet;

/// <summary>One session this account has marked as its Fleet Manager, at some time.</summary>
public sealed record FleetManagerMark(string SessionId, DateTime FirstMarkedAtUtc, DateTime LastMarkedAtUtc);

/// <summary>
/// THE HISTORY OF WHICH SESSIONS AN ACCOUNT HAS MARKED AS ITS FLEET MANAGER, over the <c>fleet_manager_marks</c>
/// table (the Fleet Manager mission, step 3). The current mark itself stays the <c>fleet_manager_session_id</c>
/// tenant setting (<see cref="FleetManagerSessions"/>); every time it is set, the session is recorded here too.
///
/// CLEARING OR REPLACING THE MARK DOES NOT REMOVE A ROW: a session that was once the Fleet Manager may still
/// control sessions it started, and the digest must still find them.
///
/// BOUNDED. An account keeps the <see cref="MaxRememberedPerAccount"/> sessions it marked most recently; every
/// write prunes the older ones, so repeated resets never grow the table or the digest without limit. A session
/// pruned this way is no longer found as a former Fleet Manager, even if it still controls sessions.
///
/// WHAT THIS DOES NOT DO: it does not hand those sessions over. A session an earlier Fleet Manager started is
/// still controlled by that earlier session; the digest shows it with that owner's id. Transferring ownership
/// to the new Fleet Manager is a later step of the mission (hand over).
///
/// Tenant-partitioned by construction, like <see cref="FleetOutcomeStore"/>.
/// </summary>
public sealed class FleetManagerMarkHistory
{
    /// <summary>How many marked sessions one account keeps, the most recently marked first.</summary>
    public const int MaxRememberedPerAccount = 20;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FleetManagerMarkHistory(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Record that <paramref name="sessionId"/> has just been marked as this account's Fleet Manager.
    /// A session marked before keeps its first time and moves its last time.</summary>
    /// <exception cref="ArgumentException">The session id is not a session id.</exception>
    public FleetManagerMark Record(TenantId tenant, string sessionId, DateTime nowUtc)
    {
        FileLog.Write($"[FleetManagerMarkHistory] Record: tenant={tenant}, session={sessionId}");
        try
        {
            if (!Guid.TryParse(sessionId, out var parsed))
                throw new ArgumentException($"'{sessionId}' is not a session id.", nameof(sessionId));
            var sid = parsed.ToString("D");
            var now = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();

            lock (_gate)
            {
                try
                {
                    return Upsert(tenant, sid, now);
                }
                catch (DbUpdateException ex)
                {
                    // Another Gateway instance inserted the same session between our read and our write; the
                    // unique index refused the second row. The row now exists, so the second attempt updates it.
                    FileLog.Write($"[FleetManagerMarkHistory] Record: concurrent insert for session={sid} ({ex.Message}); updating");
                    return Upsert(tenant, sid, now);
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerMarkHistory] Record FAILED: {ex.Message}");
            throw;
        }
    }

    private FleetManagerMark Upsert(TenantId tenant, string sid, DateTime now)
    {
        using var ctx = _db.CreateContext(tenant);
        return UpsertIn(ctx, sid, now);
    }

    /// <summary>
    /// Record the mark on a context the caller owns: the row is saved and the account pruned on that context, so
    /// inside the caller's transaction both are committed or rolled back with everything else it writes.
    /// </summary>
    internal static FleetManagerMark UpsertIn(GatewayDbContext ctx, string sid, DateTime now)
    {
        var row = ctx.FleetManagerMarks.FirstOrDefault(m => m.SessionId == sid);
        if (row is null)
        {
            row = new FleetManagerMarkEntity { SessionId = sid, FirstMarkedAtUtc = now, LastMarkedAtUtc = now };
            row.TenantId = ctx.ActiveTenant!;
            ctx.FleetManagerMarks.Add(row);
        }
        else
        {
            row.LastMarkedAtUtc = now;
        }
        ctx.SaveChanges();
        var pruned = Prune(ctx);
        FileLog.Write($"[FleetManagerMarkHistory] Record: session={sid}, first={row.FirstMarkedAtUtc:O}, pruned={pruned}");
        return new FleetManagerMark(row.SessionId, row.FirstMarkedAtUtc, row.LastMarkedAtUtc);
    }

    /// <summary>Delete every row of the context's account beyond the <see cref="MaxRememberedPerAccount"/> most
    /// recently marked. Returns how many were deleted.</summary>
    private static int Prune(GatewayDbContext ctx)
    {
        var stale = ctx.FleetManagerMarks.AsNoTracking()
            .OrderByDescending(m => m.LastMarkedAtUtc).ThenByDescending(m => m.SessionId)
            .Skip(MaxRememberedPerAccount)
            .Select(m => m.Id)
            .ToList();
        if (stale.Count == 0) return 0;
        return ctx.FleetManagerMarks.Where(m => stale.Contains(m.Id)).ExecuteDelete();
    }

    /// <summary>The sessions this account has marked (at most <see cref="MaxRememberedPerAccount"/>, the most
    /// recent), oldest first mark first.</summary>
    public IReadOnlyList<FleetManagerMark> List(TenantId tenant)
    {
        FileLog.Write($"[FleetManagerMarkHistory] List: tenant={tenant}");
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.FleetManagerMarks.AsNoTracking()
            .OrderBy(m => m.FirstMarkedAtUtc).ThenBy(m => m.SessionId)
            .Select(m => new FleetManagerMark(m.SessionId, m.FirstMarkedAtUtc, m.LastMarkedAtUtc))
            .ToList();
        FileLog.Write($"[FleetManagerMarkHistory] List: returned={rows.Count}");
        return rows;
    }
}
