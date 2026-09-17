using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// THE MARK MOVES TO THE NEW FLEET MANAGER IN ONE TRANSACTION (the steps 5 and 6 fixes, round 2, the Architect's
/// ruling). The account's mark, the history of marked sessions, the removal of the waiting replacement and the one
/// "you are now the Fleet Manager" event are written together: all of them are committed, or none is.
///
/// Before this, the event was a separate write after the mark had moved and the replacement had been forgotten. A
/// Gateway that stopped, or an event write that failed, in between left a marked Fleet Manager that was never told
/// it was one - and its first prompt tells it to do nothing until it is told. Now a failure leaves the replacement
/// recorded exactly as it was, and the next look of the replacement loop (which the sweep restarts after a Gateway
/// restart) promotes it again.
///
/// ONE EVENT PER PROMOTION: the event is stored only when that session has never been told, so promoting the same
/// session twice stores one event.
/// </summary>
public sealed class FleetManagerPromotionStore
{
    private readonly GatewayDatabase _db;

    public FleetManagerPromotionStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>For tests: runs after the mark has been saved inside the transaction and before the event is saved, so
    /// a test can fail the event write at exactly that point.</summary>
    internal Action? BeforeEventSaveForTest { get; set; }

    /// <summary>
    /// Mark <paramref name="successorSessionId"/> as the account's Fleet Manager, record it in the history, forget the
    /// waiting replacement and store the one event that tells it - in one transaction. True when the event was stored
    /// now; false when that session had already been told.
    /// </summary>
    /// <exception cref="ArgumentException">The id is not a session id.</exception>
    public bool Promote(TenantId tenant, string successorSessionId, DateTime nowUtc)
    {
        FileLog.Write($"[FleetManagerPromotionStore] Promote: tenant={tenant.ToLogString()}, successor={successorSessionId}");
        try
        {
            var sid = TenantSettingsResolver.CanonicalSessionId(successorSessionId, nameof(successorSessionId));
            var now = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();

            using var ctx = _db.CreateContext(tenant);
            using var tx = ctx.Database.BeginTransaction();

            var settings = TenantSettingsResolver.SuccessorCleared();
            settings[TenantSettingKeys.FleetManagerSessionId] = sid;
            TenantSettingsStore.ApplyIn(ctx, tenant, settings, now);
            ctx.SaveChanges();

            FleetManagerMarkHistory.UpsertIn(ctx, sid, now);
            var told = FleetManagerEventStore.AddMarkedIn(ctx, sid, now);
            BeforeEventSaveForTest?.Invoke();
            ctx.SaveChanges();
            tx.Commit();

            FileLog.Write($"[FleetManagerPromotionStore] Promote: committed mark={sid}, event={(told is null ? "already stored" : told.Id.ToString())}");
            return told is not null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerPromotionStore] Promote FAILED (nothing was written): {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
