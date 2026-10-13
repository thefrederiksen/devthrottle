using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE SIGN-IN ROWS OF THE LICENSES LOG (License Switching, contract section 8.4): one row for every sign-in started
/// from the Cockpit, of its own kind, in the table the switch log is kept in - so the log route lists it with no change.
///
/// A sign-in row uses the log's columns as a switch row does, with these meanings: <c>SwitchId</c> holds the sign-in's
/// identifier; <c>ToAccountEmail</c> the account signed in to (<c>FromAccountEmail</c> is empty); <c>Reason</c> one of
/// <see cref="LicenseSignInReasons"/>; <c>State</c> is <c>pending</c> until the Director answers, <c>unknown</c> when no
/// answer came back, then <c>added</c>, <c>renewed</c> or <c>failed</c>; <c>NotAfterUtc</c> is the time after which the
/// Director refuses to start it.
///
/// A row is written BEFORE the command is sent, and only an open row is ever changed: a row that has ended stays as it
/// ended. NEVER WHAT COMPLETES A SIGN-IN: a row carries an email, machine names, words and sentences.
/// </summary>
public sealed class LicenseSignInLog
{
    /// <summary>The kind of a sign-in row.</summary>
    public const string Kind = "sign-in";

    private readonly GatewayDatabase _db;

    public LicenseSignInLog(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Write a sign-in row in <c>pending</c>, with a new identifier and <c>NotAfterUtc</c> = now +
    /// <see cref="LicenseContract.SignInNotAfter"/>.</summary>
    public LicenseSwitchLogEntity CreatePending(TenantId tenant, LicenseMachineView machine, string accountEmail, string reason,
        string message, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (reason is not (LicenseSignInReasons.Manual or LicenseSignInReasons.Renewal))
            throw new ArgumentException($"'{reason}' is not a sign-in reason.", nameof(reason));
        var now = Utc(nowUtc);
        var email = LicenseStore.NormalizeEmail(accountEmail);
        using var ctx = _db.CreateContext(tenant);
        var row = new LicenseSwitchLogEntity
        {
            Kind = Kind,
            SwitchId = Guid.NewGuid().ToString("N"),
            MachineId = machine.MachineId,
            MachineName = machine.MachineName,
            OsUser = machine.OsUser,
            DirectorId = machine.DirectorId,
            FromAccountEmail = "",
            ToAccountEmail = email,
            Reason = reason,
            State = LicenseLogStates.Pending,
            Message = Cap(message),
            CreatedAtUtc = now,
            NotAfterUtc = now + LicenseContract.SignInNotAfter,
            TenantId = ctx.ActiveTenant!,
        };
        ctx.LicenseSwitchLog.Add(row);
        ctx.SaveChanges();
        FileLog.Write($"[LicenseSignInLog] CreatePending: tenant={tenant.ToLogString()}, signIn={row.SwitchId}, machine={machine.MachineId}, account={email}, reason={reason}");
        return row;
    }

    /// <summary>One sign-in row by its identifier; null when this account has none.</summary>
    public LicenseSwitchLogEntity? Find(TenantId tenant, string signInId)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.LicenseSwitchLog.AsNoTracking().FirstOrDefault(r => r.SwitchId == signInId && r.Kind == Kind);
    }

    /// <summary>Every sign-in row still <c>pending</c> or <c>unknown</c>, oldest first.</summary>
    /// <remarks>Quiet: the minute check reads this for every tenant every minute, and a read that finds nothing to do
    /// leaves no line.</remarks>
    public IReadOnlyList<LicenseSwitchLogEntity> ListOpen(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.LicenseSwitchLog.AsNoTracking()
            .Where(r => r.Kind == Kind && (r.State == LicenseLogStates.Pending || r.State == LicenseLogStates.Unknown))
            .ToList()
            .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.SwitchId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Record that the command of an OPEN sign-in went out. False when the row is no longer open.</summary>
    public bool RecordSend(TenantId tenant, string signInId, string directorId, DateTime nowUtc)
    {
        DateTime? now = Utc(nowUtc);
        using var ctx = _db.CreateContext(tenant);
        var changed = Open(ctx, signInId).ExecuteUpdate(u => u
            .SetProperty(r => r.SendCount, r => r.SendCount + 1)
            .SetProperty(r => r.LastSentAtUtc, now)
            .SetProperty(r => r.DirectorId, directorId)) == 1;
        FileLog.Write($"[LicenseSignInLog] RecordSend: signIn={signInId}, director={directorId}, changed={changed}");
        return changed;
    }

    /// <summary>Move an OPEN sign-in to <c>unknown</c>: its command went out and no answer came back. It stays open
    /// until <see cref="TryFinish"/>. False when the row is no longer open.</summary>
    public bool TryMarkUnknown(TenantId tenant, string signInId, string? message)
    {
        var text = Cap(message);
        using var ctx = _db.CreateContext(tenant);
        var changed = Open(ctx, signInId).ExecuteUpdate(u => u
            .SetProperty(r => r.State, LicenseLogStates.Unknown)
            .SetProperty(r => r.ReasonCode, (string?)null)
            .SetProperty(r => r.Message, text)) == 1;
        FileLog.Write($"[LicenseSignInLog] TryMarkUnknown: signIn={signInId}, changed={changed}");
        return changed;
    }

    /// <summary>
    /// End an OPEN sign-in as <c>added</c>, <c>renewed</c> or <c>failed</c>. A row that has ended is never changed
    /// again. True when this call ended it.
    /// </summary>
    /// <param name="finishedOnMachineAtUtc">When the Director finished it, by its own clock; null when it gave no answer.</param>
    /// <param name="replayed">The answer was the Director's stored one for a repeated identifier; null when it gave no answer.</param>
    public bool TryFinish(TenantId tenant, string signInId, string state, string? reasonCode, string? message, DateTime nowUtc,
        DateTime? finishedOnMachineAtUtc = null, bool? replayed = null)
    {
        if (state is not (LicenseSignInOutcomes.Added or LicenseSignInOutcomes.Renewed or LicenseSignInOutcomes.Failed))
            throw new ArgumentException($"'{state}' is not how a sign-in ends.", nameof(state));
        DateTime? now = Utc(nowUtc);
        var reason = state == LicenseSignInOutcomes.Failed ? Trimmed(reasonCode, 32) : null;
        var text = Cap(message);
        var onMachine = finishedOnMachineAtUtc is { } at ? Utc(at) : (DateTime?)null;
        using var ctx = _db.CreateContext(tenant);
        var changed = Open(ctx, signInId).ExecuteUpdate(u => u
            .SetProperty(r => r.State, state)
            .SetProperty(r => r.ReasonCode, reason)
            .SetProperty(r => r.Message, text)
            .SetProperty(r => r.DirectorFinishedAtUtc, onMachine)
            .SetProperty(r => r.Replayed, replayed)
            .SetProperty(r => r.FinishedAtUtc, now)) == 1;
        FileLog.Write($"[LicenseSignInLog] TryFinish: signIn={signInId}, state={state}, reason={reason ?? "(none)"}, changed={changed}");
        return changed;
    }

    private static IQueryable<LicenseSwitchLogEntity> Open(GatewayDbContext ctx, string signInId)
        => ctx.LicenseSwitchLog.Where(r => r.SwitchId == signInId && r.Kind == Kind
                                           && (r.State == LicenseLogStates.Pending || r.State == LicenseLogStates.Unknown));

    private static string? Trimmed(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }

    private static string? Cap(string? message) => Trimmed(message, LicenseStore.MaxMessageLength);

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
