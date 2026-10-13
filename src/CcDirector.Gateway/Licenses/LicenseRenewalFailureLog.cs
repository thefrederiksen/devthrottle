using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE FAILED-RENEWAL WARNINGS OF THE LICENSES LOG (License Switching, contract section 8.1). One row of the switch
/// log, of its own kind, for one FAILURE STREAK of one sign-in a machine holds: opened when the Gateway first sees the
/// sign-in's renewal failing with the sign-in about to end, open while the renewal keeps failing, and closed by the
/// report that says a renewal worked.
///
/// THE ROW IS WHAT MAKES THE OWNER EMAIL GO OUT ONCE. What became of the email is the row's reason word -
/// <see cref="LicenseGatewayReasonCodes.OwnerEmailed"/>, <see cref="LicenseGatewayReasonCodes.OwnerEmailFailed"/> or
/// <see cref="LicenseGatewayReasonCodes.NoOwnerAccount"/> - and a row recorded as emailed is never changed back. It is
/// stored, so a Gateway that restarts in the middle of a streak does not email again.
///
/// No table and no column is added: the row uses the switch log's own. The account is in
/// <see cref="LicenseSwitchLogEntity.ToAccountEmail"/>, when the sign-in ends in
/// <see cref="LicenseSwitchLogEntity.NotAfterUtc"/>, the state is <see cref="LicenseLogStates.Open"/> and then
/// <see cref="LicenseLogStates.Ended"/>. The sign-in's own time as the machine reported it when the row was opened is
/// kept in <see cref="LicenseSwitchLogEntity.DirectorStartedAtUtc"/> (read it with <see cref="SignedInWhenOpened"/>):
/// a later report with a newer one is how the Gateway knows a renewal worked.
/// </summary>
public sealed class LicenseRenewalFailureLog
{
    /// <summary>The kind of a failed-renewal row of the switch log.</summary>
    public const string Kind = LicenseLogKinds.RenewalFailed;

    private readonly GatewayDatabase _db;

    public LicenseRenewalFailureLog(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>The account's warnings whose streak has not ended, oldest first.</summary>
    /// <remarks>Quiet: the minute check reads this for every account every minute, and a check that finds nothing
    /// to do leaves no line. A failure is logged by the check that asked.</remarks>
    public IReadOnlyList<LicenseSwitchLogEntity> ListOpen(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.LicenseSwitchLog.AsNoTracking()
            .Where(r => r.Kind == Kind && r.FinishedAtUtc == null)
            .ToList()
            .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.MachineName, StringComparer.Ordinal)
            .ThenBy(r => r.ToAccountEmail, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Record that a failure streak of one sign-in reached the point where the owner is told. A sign-in has at most
    /// one open warning: when one is already open it is returned unchanged.
    /// </summary>
    /// <param name="signInExpiresAtUtc">When the sign-in ends, as the machine reported it.</param>
    /// <param name="signedInAtUtc">When the sign-in was made, as the machine reported it; null when it did not say.</param>
    public LicenseSwitchLogEntity Open(TenantId tenant, LicenseMachineView machine, string accountEmail, DateTime signInExpiresAtUtc,
        DateTime? signedInAtUtc, string message, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var email = LicenseStore.NormalizeEmail(accountEmail);
        FileLog.Write($"[LicenseRenewalFailureLog] Open: tenant={tenant.ToLogString()}, machine={machine.MachineId}, account={email}");
        try
        {
            using var ctx = _db.CreateContext(tenant);
            var open = ctx.LicenseSwitchLog.AsNoTracking()
                .FirstOrDefault(r => r.Kind == Kind && r.MachineId == machine.MachineId && r.ToAccountEmail == email && r.FinishedAtUtc == null);
            if (open is not null)
            {
                FileLog.Write($"[LicenseRenewalFailureLog] Open: already open since {open.CreatedAtUtc:O}");
                return open;
            }
            var row = new LicenseSwitchLogEntity
            {
                Kind = Kind,
                MachineId = machine.MachineId,
                MachineName = machine.MachineName,
                OsUser = machine.OsUser,
                DirectorId = machine.DirectorId,
                ToAccountEmail = email,
                State = LicenseLogStates.Open,
                Message = Cap(message),
                CreatedAtUtc = Utc(nowUtc),
                NotAfterUtc = Utc(signInExpiresAtUtc),
                DirectorStartedAtUtc = signedInAtUtc is { } signedIn ? Utc(signedIn) : null,
                TenantId = ctx.ActiveTenant!,
            };
            ctx.LicenseSwitchLog.Add(row);
            ctx.SaveChanges();
            FileLog.Write($"[LicenseRenewalFailureLog] Open: row={row.Id}");
            return row;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseRenewalFailureLog] Open FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Record what became of the owner email of one open warning: <paramref name="emailState"/> is
    /// <see cref="LicenseGatewayReasonCodes.OwnerEmailed"/> (with <paramref name="sentAtUtc"/>),
    /// <see cref="LicenseGatewayReasonCodes.OwnerEmailFailed"/> or <see cref="LicenseGatewayReasonCodes.NoOwnerAccount"/>.
    /// A warning already recorded as emailed is never changed: one email for each streak. True when this call changed it.
    /// </summary>
    public bool RecordEmail(TenantId tenant, Guid rowId, string emailState, string message, DateTime? sentAtUtc)
    {
        if (emailState is not (LicenseGatewayReasonCodes.OwnerEmailed or LicenseGatewayReasonCodes.OwnerEmailFailed
            or LicenseGatewayReasonCodes.NoOwnerAccount))
            throw new ArgumentException($"'{emailState}' is not an owner email state.", nameof(emailState));
        if ((emailState == LicenseGatewayReasonCodes.OwnerEmailed) != sentAtUtc.HasValue)
            throw new ArgumentException("A sent time is given exactly when the email was sent.", nameof(sentAtUtc));
        DateTime? sent = sentAtUtc is { } at ? Utc(at) : null;
        var sends = sent is null ? 0 : 1;
        var text = Cap(message);
        try
        {
            using var ctx = _db.CreateContext(tenant);
            var changed = ctx.LicenseSwitchLog
                .Where(r => r.Kind == Kind && r.Id == rowId && r.FinishedAtUtc == null)
                .Where(r => r.ReasonCode == null || r.ReasonCode != LicenseGatewayReasonCodes.OwnerEmailed)
                .ExecuteUpdate(u => u
                    .SetProperty(r => r.ReasonCode, emailState)
                    .SetProperty(r => r.Message, text)
                    .SetProperty(r => r.LastSentAtUtc, sent)
                    .SetProperty(r => r.SendCount, r => r.SendCount + sends)) > 0;
            FileLog.Write($"[LicenseRenewalFailureLog] RecordEmail: tenant={tenant.ToLogString()}, row={rowId}, state={emailState}, changed={changed}");
            return changed;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseRenewalFailureLog] RecordEmail FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>End one warning: its sign-in was renewed, or the machine no longer holds it. What became of the owner
    /// email stays on the row. False when the row is not open.</summary>
    public bool Close(TenantId tenant, Guid rowId, string message, DateTime nowUtc)
    {
        DateTime? now = Utc(nowUtc);
        var text = Cap(message);
        try
        {
            using var ctx = _db.CreateContext(tenant);
            var changed = ctx.LicenseSwitchLog
                .Where(r => r.Kind == Kind && r.Id == rowId && r.FinishedAtUtc == null)
                .ExecuteUpdate(u => u
                    .SetProperty(r => r.State, LicenseLogStates.Ended)
                    .SetProperty(r => r.Message, text)
                    .SetProperty(r => r.FinishedAtUtc, now)) > 0;
            FileLog.Write($"[LicenseRenewalFailureLog] Close: tenant={tenant.ToLogString()}, row={rowId}, changed={changed}");
            return changed;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseRenewalFailureLog] Close FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>When the sign-in of a failed-renewal row was made, as the machine had reported it when the row was
    /// opened; null when it had not said.</summary>
    public static DateTime? SignedInWhenOpened(LicenseSwitchLogEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.DirectorStartedAtUtc is { } at ? Utc(at) : null;
    }

    private static string Cap(string message)
    {
        var text = (message ?? "").Trim();
        return text.Length > LicenseStore.MaxMessageLength ? text[..LicenseStore.MaxMessageLength] : text;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
