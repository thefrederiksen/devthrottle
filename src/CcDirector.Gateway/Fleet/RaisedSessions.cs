using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Fleet;

/// <summary>Why a session is on an account's list of raised sessions.</summary>
public static class RaisedSessionSources
{
    /// <summary>The owner raised it from his own device. It stays raised until he lowers it or it ends.</summary>
    public const string Owner = "owner";

    /// <summary>The owner set it up as the account's Fleet Manager. It is raised only while it IS the marked Fleet
    /// Manager: when the mark moves or clears, this entry stops counting at that moment.</summary>
    public const string FleetManagerMark = "fleet-manager-mark";
}

/// <summary>One entry on an account's list of raised sessions.</summary>
public sealed record RaisedSession(string SessionId, string Source, string RaisedBy, DateTime RaisedAtUtc);

/// <summary>
/// THE ONE ANSWER to "is this session raised?" (the Fleet Manager Improvement mission, phase 1). The guard, the
/// message route, the roster fold and the raise and lower routes all ask here, so the rule lives in one place -
/// the pattern <see cref="FleetManagerSessions"/> sets for the mark.
///
/// A RAISED SESSION acts with the owner's permissions inside the owner's own account: it may type into a session,
/// and call the Fleet Manager routes that are otherwise the owner's alone, and its messages are not held to the
/// relationship rule or the rates. What it still may not do is decided by <c>SessionKeyGuard</c>, which stays an
/// allow list.
///
/// AN ENTRY THE MARK GRANTED COUNTS ONLY WHILE ITS SESSION IS THE MARK. That is what makes "raised follows the mark"
/// true by construction rather than by every writer of the mark remembering to say so: the mark is written in six
/// places, and an entry that had to be deleted by each of them would stay raised the first time one forgot. Read
/// this way, a writer that forgets leaves a session NOT raised, which is the safe side to fail on.
/// </summary>
public static class RaisedSessions
{
    /// <summary>
    /// True when <paramref name="entry"/> makes its session raised now. <paramref name="markedSessionId"/> is the
    /// account's current Fleet Manager mark, or null when it has none.
    /// </summary>
    public static bool IsRaised(RaisedSession? entry, string? markedSessionId)
    {
        if (entry is null) return false;
        return entry.Source switch
        {
            RaisedSessionSources.Owner => true,
            RaisedSessionSources.FleetManagerMark => FleetManagerSessions.SameId(entry.SessionId, markedSessionId),
            // A source this code does not know is never a grant.
            _ => false,
        };
    }
}

/// <summary>What following the mark changed, so the caller can record it.</summary>
/// <param name="Lowered">The sessions whose mark entry was removed.</param>
/// <param name="Raised">The session whose mark entry was written, or null.</param>
public sealed record RaisedMarkChange(IReadOnlyList<string> Lowered, string? Raised);

/// <summary>
/// THE LIST OF RAISED SESSIONS, per account, over the <c>raised_sessions</c> table - durable, so it survives a
/// Gateway restart. Tenant-partitioned by construction, like <see cref="FleetManagerMarkHistory"/>.
///
/// WHO WRITES IT. Only the owner's own device (<see cref="Raise"/>, <see cref="Lower"/>), setting up the Fleet
/// Manager (<see cref="FollowMark"/>, <see cref="CarryToSuccessor"/>), and a session ending
/// (<see cref="EndWithSession"/>). No session key reaches any of them: the routes that call the first two refuse a
/// session key, and <c>SessionKeyGuard</c> refuses it before that.
/// </summary>
public sealed class RaisedSessionStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly Func<TenantId, string?> _markedSessionId;

    /// <param name="markedSessionId">Reads the account's current Fleet Manager mark.</param>
    public RaisedSessionStore(GatewayDatabase db, Func<TenantId, string?> markedSessionId)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _markedSessionId = markedSessionId ?? throw new ArgumentNullException(nameof(markedSessionId));
    }

    /// <summary>Is this session raised now? Reads the stored entry and the account's mark, and asks
    /// <see cref="RaisedSessions.IsRaised"/>. A session id that is not a session id is not raised.</summary>
    public bool IsRaised(TenantId tenant, string? sessionId)
    {
        if (!Guid.TryParse(sessionId, out var parsed)) return false;
        var sid = parsed.ToString("D");
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.RaisedSessions.AsNoTracking().FirstOrDefault(r => r.SessionId == sid);
        var raised = RaisedSessions.IsRaised(row is null ? null : ToRecord(row), _markedSessionId(tenant));
        FileLog.Write($"[RaisedSessionStore] IsRaised: tenant={tenant.ToLogString()}, session={sid}, raised={raised}");
        return raised;
    }

    /// <summary>The ids of every session of the account that is raised now - one read, for the roster fold.</summary>
    public IReadOnlySet<string> RaisedIds(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.RaisedSessions.AsNoTracking().ToList();
        var marked = rows.Count == 0 ? null : _markedSessionId(tenant);
        return rows.Select(ToRecord)
            .Where(r => RaisedSessions.IsRaised(r, marked))
            .Select(r => r.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every stored entry of the account, counted or not, oldest first.</summary>
    public IReadOnlyList<RaisedSession> List(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.RaisedSessions.AsNoTracking()
            .OrderBy(r => r.RaisedAtUtc).ThenBy(r => r.SessionId)
            .ToList()
            .Select(ToRecord)
            .ToList();
    }

    /// <summary>The owner raises a session. An entry the mark granted becomes the owner's, so it no longer depends on
    /// the mark.</summary>
    /// <exception cref="ArgumentException">The id is not a session id.</exception>
    public RaisedSession Raise(TenantId tenant, string sessionId, string raisedBy, DateTime nowUtc)
    {
        FileLog.Write($"[RaisedSessionStore] Raise: tenant={tenant.ToLogString()}, session={sessionId}, by={raisedBy}");
        try
        {
            var sid = Canonical(sessionId);
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                var row = UpsertIn(ctx, sid, RaisedSessionSources.Owner, raisedBy, Utc(nowUtc));
                ctx.SaveChanges();
                FileLog.Write($"[RaisedSessionStore] Raise: session={sid} is raised by the owner");
                return ToRecord(row);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RaisedSessionStore] Raise FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>The owner lowers a session: its entry is removed, whatever granted it. True when one was removed.</summary>
    /// <exception cref="ArgumentException">The id is not a session id.</exception>
    public bool Lower(TenantId tenant, string sessionId)
    {
        FileLog.Write($"[RaisedSessionStore] Lower: tenant={tenant.ToLogString()}, session={sessionId}");
        try
        {
            var removed = Remove(tenant, Canonical(sessionId));
            FileLog.Write($"[RaisedSessionStore] Lower: session={sessionId}, removed={removed}");
            return removed;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RaisedSessionStore] Lower FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>The session has ended, so its entry goes with it. True when one was removed. An id that is not a
    /// session id has no entry.</summary>
    public bool EndWithSession(TenantId tenant, string sessionId)
    {
        FileLog.Write($"[RaisedSessionStore] EndWithSession: tenant={tenant.ToLogString()}, session={sessionId}");
        try
        {
            if (!Guid.TryParse(sessionId, out var parsed)) return false;
            var removed = Remove(tenant, parsed.ToString("D"));
            FileLog.Write($"[RaisedSessionStore] EndWithSession: session={sessionId}, removed={removed}");
            return removed;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RaisedSessionStore] EndWithSession FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// THE MARK WAS SET OR CLEARED BY HAND. Every entry the mark granted to another session is removed, and - only
    /// when <paramref name="raise"/> is true, which the caller sets only for the owner's own device - the newly marked
    /// session gets one. Removing the others here is what stops an old entry coming back to life when a session key
    /// later marks that session again.
    /// </summary>
    /// <param name="markedSessionId">The session now marked, or null when the mark was cleared.</param>
    public RaisedMarkChange FollowMark(TenantId tenant, string? markedSessionId, bool raise, string raisedBy, DateTime nowUtc)
    {
        FileLog.Write($"[RaisedSessionStore] FollowMark: tenant={tenant.ToLogString()}, marked={markedSessionId ?? "(none)"}, raise={raise}");
        try
        {
            var sid = markedSessionId is null ? null : Canonical(markedSessionId);
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                using var tx = ctx.Database.BeginTransaction();

                var stale = ctx.RaisedSessions
                    .Where(r => r.Source == RaisedSessionSources.FleetManagerMark && (!raise || r.SessionId != sid))
                    .ToList();
                ctx.RaisedSessions.RemoveRange(stale);

                string? raisedId = null;
                if (raise && sid is not null)
                {
                    var existing = ctx.RaisedSessions.FirstOrDefault(r => r.SessionId == sid);
                    // An entry the owner made himself is stronger than one the mark grants, and is left as it is.
                    if (existing is null)
                    {
                        UpsertIn(ctx, sid, RaisedSessionSources.FleetManagerMark, raisedBy, Utc(nowUtc));
                        raisedId = sid;
                    }
                }

                ctx.SaveChanges();
                tx.Commit();
                var lowered = stale.Select(r => r.SessionId).ToList();
                FileLog.Write($"[RaisedSessionStore] FollowMark: lowered=[{string.Join(", ", lowered)}], raised={raisedId ?? "(none)"}");
                return new RaisedMarkChange(lowered, raisedId);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RaisedSessionStore] FollowMark FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// A NEW FLEET MANAGER WAS STARTED TO REPLACE A RUNNING ONE (a restart or a move). When the running one is raised
    /// now, the new one is given a mark entry at once - which counts for nothing until the mark actually moves to it,
    /// because <see cref="RaisedSessions.IsRaised"/> reads the mark. It is written now, and not when the mark moves,
    /// because by then the old session has usually ended and taken its entry with it. Raised is carried over, never
    /// multiplied: the old one's mark entry stops counting the moment the mark leaves it. True when an entry was written.
    /// </summary>
    public bool CarryToSuccessor(TenantId tenant, string predecessorSessionId, string successorSessionId, DateTime nowUtc)
    {
        FileLog.Write($"[RaisedSessionStore] CarryToSuccessor: tenant={tenant.ToLogString()}, from={predecessorSessionId}, to={successorSessionId}");
        try
        {
            var to = Canonical(successorSessionId);
            if (!IsRaised(tenant, predecessorSessionId))
            {
                FileLog.Write($"[RaisedSessionStore] CarryToSuccessor: {predecessorSessionId} is not raised; nothing carried");
                return false;
            }
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                if (ctx.RaisedSessions.Any(r => r.SessionId == to)) return false;
                UpsertIn(ctx, to, RaisedSessionSources.FleetManagerMark,
                    $"gateway: the Fleet Manager mark moves here from {predecessorSessionId}", Utc(nowUtc));
                ctx.SaveChanges();
            }
            FileLog.Write($"[RaisedSessionStore] CarryToSuccessor: {to} is raised once the mark moves to it");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RaisedSessionStore] CarryToSuccessor FAILED: {ex.Message}");
            throw;
        }
    }

    private bool Remove(TenantId tenant, string sid)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.RaisedSessions.Where(r => r.SessionId == sid).ExecuteDelete() > 0;
        }
    }

    private static RaisedSessionEntity UpsertIn(GatewayDbContext ctx, string sid, string source, string raisedBy, DateTime now)
    {
        var row = ctx.RaisedSessions.FirstOrDefault(r => r.SessionId == sid);
        if (row is null)
        {
            row = new RaisedSessionEntity { SessionId = sid };
            row.TenantId = ctx.ActiveTenant!;
            ctx.RaisedSessions.Add(row);
        }
        row.Source = source;
        row.RaisedBy = raisedBy.Length > 256 ? raisedBy[..256] : raisedBy;
        row.RaisedAtUtc = now;
        return row;
    }

    private static string Canonical(string sessionId)
        => Guid.TryParse(sessionId, out var parsed)
            ? parsed.ToString("D")
            : throw new ArgumentException($"'{sessionId}' is not a session id.", nameof(sessionId));

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static RaisedSession ToRecord(RaisedSessionEntity e) => new(e.SessionId, e.Source, e.RaisedBy, e.RaisedAtUtc);
}
