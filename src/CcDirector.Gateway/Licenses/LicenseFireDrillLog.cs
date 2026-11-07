using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE FIRE DRILL ROWS OF THE LICENSES LOG (mission "Licenses - Always Ready, Loud When Not", step 7). One row of the
/// switch log, of its own kind (<see cref="LicenseLogKinds.FireDrill"/>), for each daily drill: written
/// <see cref="LicenseLogStates.Open"/> BEFORE the drill does anything, so a Gateway that stops part-way leaves a row
/// that says so, and finished <see cref="LicenseLogStates.Passed"/> or <see cref="LicenseLogStates.Failed"/> with the
/// drill's finished sentence. The rows are also what says when the next drill is due, so a restart neither runs a
/// second drill the same day nor forgets the day's drill.
///
/// No table and no column is added. The drill machine is the row's machine; the account it was on and the spare it
/// went to are <see cref="LicenseSwitchLogEntity.FromAccountEmail"/> and <see cref="LicenseSwitchLogEntity.ToAccountEmail"/>
/// (empty when the drill did not get that far). A drill the Gateway stopped in the middle of is finished
/// <see cref="LicenseLogStates.Failed"/> with the reason word <see cref="InterruptedReason"/>.
/// </summary>
public sealed class LicenseFireDrillLog
{
    /// <summary>The kind of a fire drill row.</summary>
    public const string Kind = LicenseLogKinds.FireDrill;

    /// <summary>The reason word of a drill the Gateway stopped in the middle of. Such a drill does not count as the
    /// day's drill: the next one is due at once.</summary>
    public const string InterruptedReason = "interrupted";

    /// <summary>The reason word of a drill that found holes or whose switch failed.</summary>
    public const string FailedReason = "drill-failed";

    private readonly GatewayDatabase _db;

    public LicenseFireDrillLog(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>The newest drill rows first, at most <paramref name="take"/>. Quiet: read on every minute check.</summary>
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

    /// <summary>Write the row of a drill that is starting, open.</summary>
    /// <param name="machineId">The drill machine as the setting names it.</param>
    /// <param name="machineName">Its name on the Licenses page, or a sentence saying it is not there.</param>
    public LicenseSwitchLogEntity Start(TenantId tenant, Guid machineId, string machineName, string osUser, string? directorId,
        DateTime nowUtc)
    {
        FileLog.Write($"[LicenseFireDrillLog] Start: tenant={tenant.ToLogString()}, machine={machineId}");
        try
        {
            using var ctx = _db.CreateContext(tenant);
            var row = new LicenseSwitchLogEntity
            {
                Kind = Kind,
                MachineId = machineId,
                MachineName = machineName,
                OsUser = osUser,
                DirectorId = directorId,
                State = LicenseLogStates.Open,
                Message = "The daily fire drill is running.",
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

    /// <summary>Finish an open drill row. False when the row is not open (it was already finished).</summary>
    /// <param name="passed">The drill passed.</param>
    /// <param name="reasonCode">Null when it passed; <see cref="FailedReason"/> or <see cref="InterruptedReason"/> when not.</param>
    public bool Finish(TenantId tenant, Guid rowId, bool passed, string? reasonCode, string message, string fromAccountEmail,
        string toAccountEmail, DateTime nowUtc)
    {
        if (passed != (reasonCode is null))
            throw new ArgumentException("A drill that passed has no reason word, and one that did not has one.", nameof(reasonCode));
        if (reasonCode is not (null or FailedReason or InterruptedReason))
            throw new ArgumentException($"'{reasonCode}' is not a fire drill reason word.", nameof(reasonCode));
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var state = passed ? LicenseLogStates.Passed : LicenseLogStates.Failed;
        var text = Cap(message);
        var from = LicenseStore.NormalizeEmail(fromAccountEmail);
        var to = LicenseStore.NormalizeEmail(toAccountEmail);
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
                    .SetProperty(r => r.FromAccountEmail, from)
                    .SetProperty(r => r.ToAccountEmail, to)
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
