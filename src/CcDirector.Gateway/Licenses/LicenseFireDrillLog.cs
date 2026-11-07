using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE DAILY LICENSE CHECK ROWS OF THE LICENSES LOG (mission "Licenses - Always Ready, Loud When Not"). One row of the
/// switch log, of its own kind (<see cref="LicenseLogKinds.FireDrill"/>, the stored word kept from the drill it replaced),
/// for each daily check: written <see cref="LicenseLogStates.Open"/>, then finished <see cref="LicenseLogStates.Passed"/>
/// or <see cref="LicenseLogStates.Failed"/> with the check's finished sentence. The rows are also what says when the next
/// check is due, so a restart neither runs a second check the same day nor forgets the day's check.
///
/// No table and no column is added. A check is about every machine, so its row names no machine
/// (<see cref="Guid.Empty"/>) and no account. A row the Gateway left open is finished <see cref="LicenseLogStates.Failed"/>
/// with the reason word <see cref="InterruptedReason"/>.
/// </summary>
public sealed class LicenseFireDrillLog
{
    /// <summary>The kind of a fire drill row.</summary>
    public const string Kind = LicenseLogKinds.FireDrill;

    /// <summary>The reason word of a check the Gateway stopped in the middle of. It counts as the day's check.</summary>
    public const string InterruptedReason = "interrupted";

    /// <summary>The reason word of a check that found a machine that will be stuck.</summary>
    public const string FailedReason = "drill-failed";

    private readonly GatewayDatabase _db;

    public LicenseFireDrillLog(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>The newest check rows first, at most <paramref name="take"/>. Quiet: read on every minute check.</summary>
    public IReadOnlyList<LicenseSwitchLogEntity> Newest(TenantId tenant, int take)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        using var ctx = _db.CreateContext(tenant);
        return ctx.LicenseSwitchLog.AsNoTracking()
            .Where(r => r.Kind == Kind)
            .ToList()
            .OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id)
            .Take(take)
            .ToList();
    }

    /// <summary>What a check's row names as its machine: every machine.</summary>
    public const string EveryMachine = "every machine";

    /// <summary>Write the row of a check that is starting, open.</summary>
    public LicenseSwitchLogEntity Start(TenantId tenant, DateTime nowUtc)
    {
        FileLog.Write($"[LicenseFireDrillLog] Start: tenant={tenant.ToLogString()}");
        try
        {
            using var ctx = _db.CreateContext(tenant);
            var row = new LicenseSwitchLogEntity
            {
                Kind = Kind,
                MachineId = Guid.Empty,
                MachineName = EveryMachine,
                OsUser = "",
                State = LicenseLogStates.Open,
                Message = "The daily license check is running.",
                CreatedAtUtc = Utc(nowUtc),
                TenantId = ctx.ActiveTenant!,
            };
            ctx.LicenseSwitchLog.Add(row);
            ctx.SaveChanges();
            FileLog.Write($"[LicenseFireDrillLog] Start: row={row.Id}");
            return row;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseFireDrillLog] Start FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Finish an open check row. False when the row is not open (it was already finished).</summary>
    /// <param name="passed">The check passed.</param>
    /// <param name="reasonCode">Null when it passed; <see cref="FailedReason"/> or <see cref="InterruptedReason"/> when not.</param>
    public bool Finish(TenantId tenant, Guid rowId, bool passed, string? reasonCode, string message, DateTime nowUtc)
    {
        if (passed != (reasonCode is null))
            throw new ArgumentException("A check that passed has no reason word, and one that did not has one.", nameof(reasonCode));
        if (reasonCode is not (null or FailedReason or InterruptedReason))
            throw new ArgumentException($"'{reasonCode}' is not a daily license check reason word.", nameof(reasonCode));
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var state = passed ? LicenseLogStates.Passed : LicenseLogStates.Failed;
        var text = Cap(message);
        var now = Utc(nowUtc);
        try
        {
            using var ctx = _db.CreateContext(tenant);
            var changed = ctx.LicenseSwitchLog
                .Where(r => r.Kind == Kind && r.Id == rowId && r.FinishedAtUtc == null)
                .ExecuteUpdate(u => u
                    .SetProperty(r => r.State, state)
                    .SetProperty(r => r.ReasonCode, reasonCode)
                    .SetProperty(r => r.Message, text)
                    .SetProperty(r => r.FinishedAtUtc, now)) > 0;
            FileLog.Write($"[LicenseFireDrillLog] Finish: tenant={tenant.ToLogString()}, row={rowId}, state={state}, reason={reasonCode ?? "(none)"}, changed={changed}");
            return changed;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseFireDrillLog] Finish FAILED: {ex.Message}");
            throw;
        }
    }

    private static string Cap(string message)
        => message.Length <= LicenseStore.MaxMessageLength ? message : message[..(LicenseStore.MaxMessageLength - 3)] + "...";

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
