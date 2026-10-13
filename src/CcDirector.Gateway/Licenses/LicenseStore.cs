using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Licenses;

/// <summary>What a switch log row records (License Switching).</summary>
public static class LicenseLogKinds
{
    public const string Switch = "switch";
    public const string SpareAdded = "spare-added";
    public const string SpareRemoved = "spare-removed";

    /// <summary>An exhaustion event: an account in use reached the threshold and no account had room, so nothing was
    /// switched. One row per event, open while its <c>FinishedAtUtc</c> is null.</summary>
    public const string NoRoom = "no-room";
}

/// <summary>Where a switch log row stands. <see cref="Pending"/> and <see cref="Unknown"/> are open; the rest are final.</summary>
public static class LicenseLogStates
{
    /// <summary>Written before the command is sent, and kept while the Director is offline (contract section 5.1).</summary>
    public const string Pending = "pending";

    /// <summary>Sent, and no answer came back (timeout or tunnel dropped); settled by a resend or a report.</summary>
    public const string Unknown = "unknown";

    public const string Switched = LicenseSwitchOutcomes.Switched;
    public const string AlreadyActive = LicenseSwitchOutcomes.AlreadyActive;
    public const string Failed = LicenseSwitchOutcomes.Failed;

    /// <summary>A spare row: final when written.</summary>
    public const string Recorded = "recorded";

    /// <summary>A no-room row while its event lasts.</summary>
    public const string Open = "open";

    /// <summary>A no-room row whose event is over.</summary>
    public const string Ended = "ended";

    public static bool IsOpen(string state) => state is Pending or Unknown;
}

/// <summary>The reason codes the GATEWAY writes on a switch row (contract section 3.5), beside the Director's own
/// <see cref="LicenseSwitchReasonCodes"/>.</summary>
public static class LicenseGatewayReasonCodes
{
    public const string DirectorOffline = "director-offline";
    public const string DirectorError = "director-error";

    /// <summary>On a no-room row: the one owner email for the event was sent.</summary>
    public const string OwnerEmailed = "owner-emailed";

    /// <summary>On a no-room row: the owner email has not gone out yet; it is tried again on a later minute check.</summary>
    public const string OwnerEmailFailed = "owner-email-failed";

    /// <summary>On a no-room row: this Gateway has no DevThrottle account to email, so no email is sent for the event.</summary>
    public const string NoOwnerAccount = "no-owner-account";
}

/// <summary>The two usage windows, as the bar table names them.</summary>
public static class LicenseWindows
{
    public const string FiveHour = "five-hour";
    public const string SevenDay = "seven-day";
}

/// <summary>What applying one accepted report changed: the accounts that appeared on and left the machine.</summary>
public sealed record LicenseReportApplied(Guid MachineId, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, bool FirstReport);

/// <summary>
/// THE LICENSE STORE, per account, over the <c>license_*</c> tables (the License Switching mission). It applies an
/// ACCEPTED usage report as a complete snapshot of one machine, keeps the switch log, and keeps the accounts barred
/// as targets. Whether a report is accepted at all (bound Director, active connection, sequence) is decided before it
/// reaches here, by <see cref="LicenseUsageIngest"/>.
///
/// NEVER A KEY. Nothing passed to this store can carry a sign-in: emails, plan words, numbers, times, words, sentences.
/// </summary>
public sealed class LicenseStore
{
    public const int MaxMessageLength = 500;

    private readonly GatewayDatabase _db;

    public LicenseStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>The form every email is stored and compared in.</summary>
    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Apply one accepted report: the machine row, the accounts, and the machine's holdings replaced by exactly the
    /// report's. Compared with the machine's previous holdings, every account that appeared or left writes a
    /// <c>spare-added</c> / <c>spare-removed</c> row (contract section 4). A machine's FIRST report has nothing to compare
    /// with, so it writes none. <paramref name="readAtShift"/> is added to every holding's <c>ReadAtUtc</c> to get the
    /// time staleness is judged by (zero unless the report's clock was skewed, contract section 5.2). One save: the
    /// whole report lands, or none of it.
    /// </summary>
    public LicenseReportApplied ApplyReport(TenantId tenant, string directorId, long sequence, LicenseUsageReport report,
        DateTime receivedAtUtc, TimeSpan readAtShift)
    {
        ArgumentNullException.ThrowIfNull(report);
        FileLog.Write($"[LicenseStore] ApplyReport: tenant={tenant.ToLogString()}, director={directorId}, seq={sequence}, holdings={report.Holdings.Count}");
        try
        {
            var now = Utc(receivedAtUtc);
            var machineKey = report.MachineName.Trim().ToLowerInvariant();
            var userKey = report.OsUser.Trim().ToLowerInvariant();
            using var ctx = _db.CreateContext(tenant);

            var machine = ctx.LicenseMachines.FirstOrDefault(m => m.MachineKey == machineKey && m.OsUserKey == userKey);
            var first = machine is null;
            if (machine is null)
            {
                machine = new LicenseMachineEntity { MachineKey = machineKey, OsUserKey = userKey, TenantId = ctx.ActiveTenant! };
                ctx.LicenseMachines.Add(machine);
            }
            var active = report.Holdings.FirstOrDefault(h => h.IsActive);
            machine.MachineName = report.MachineName.Trim();
            machine.OsUser = report.OsUser.Trim();
            machine.DirectorId = directorId;
            machine.LastSequence = sequence;
            machine.LastReportAtUtc = now;
            machine.LastSentAtUtc = Utc(report.SentAtUtc);
            machine.LastTrigger = Cap((report.Trigger ?? "").Trim(), 32);
            machine.ActiveAccountEmail = active is null ? null : NormalizeEmail(active.AccountEmail);
            machine.SwitchInProgressId = string.IsNullOrWhiteSpace(report.SwitchInProgressId) ? null : report.SwitchInProgressId.Trim();
            machine.ClockSkewSeconds = readAtShift.TotalSeconds;

            var previous = first
                ? new List<LicenseHoldingEntity>()
                : ctx.LicenseHoldings.Where(h => h.MachineId == machine.Id).ToList();
            var byEmail = report.Holdings.ToDictionary(h => NormalizeEmail(h.AccountEmail), StringComparer.Ordinal);

            var removed = previous.Where(h => !byEmail.ContainsKey(h.AccountEmail)).ToList();
            ctx.LicenseHoldings.RemoveRange(removed);
            var added = new List<string>();
            foreach (var (email, holding) in byEmail)
            {
                var row = previous.FirstOrDefault(h => h.AccountEmail == email);
                if (row is null)
                {
                    row = new LicenseHoldingEntity { MachineId = machine.Id, AccountEmail = email, TenantId = ctx.ActiveTenant! };
                    ctx.LicenseHoldings.Add(row);
                    added.Add(email);
                }
                CopyReading(holding, row, readAtShift);
            }

            UpsertAccounts(ctx, report.Holdings, now);

            if (!first)
            {
                foreach (var email in added)
                    ctx.LicenseSwitchLog.Add(SpareRow(ctx, machine, directorId, LicenseLogKinds.SpareAdded, email, byEmail[email].IsActive, now));
                foreach (var row in removed)
                    ctx.LicenseSwitchLog.Add(SpareRow(ctx, machine, directorId, LicenseLogKinds.SpareRemoved, row.AccountEmail, row.IsActive, now));
            }

            ctx.SaveChanges();
            var removedEmails = removed.Select(r => r.AccountEmail).ToList();
            FileLog.Write($"[LicenseStore] ApplyReport: machine={machine.Id}, first={first}, active={machine.ActiveAccountEmail ?? "(none)"}, "
                          + $"added={added.Count}, removed={removedEmails.Count}");
            return new LicenseReportApplied(machine.Id, first ? Array.Empty<string>() : added,
                first ? Array.Empty<string>() : removedEmails, first);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseStore] ApplyReport FAILED: {ex.Message}");
            throw;
        }
    }

    private static void CopyReading(LicenseHolding holding, LicenseHoldingEntity row, TimeSpan readAtShift)
    {
        row.IsActive = holding.IsActive;
        row.Plan = Trimmed(holding.Plan, 64);
        row.RateLimitTier = Trimmed(holding.RateLimitTier, 128);
        row.FiveHourUtilization = holding.FiveHour?.Utilization;
        row.FiveHourResetsAtUtc = UtcOrNull(holding.FiveHour?.ResetsAtUtc);
        row.SevenDayUtilization = holding.SevenDay?.Utilization;
        row.SevenDayResetsAtUtc = UtcOrNull(holding.SevenDay?.ResetsAtUtc);
        row.ExtraUsageEnabled = holding.ExtraUsage?.IsEnabled;
        row.ExtraUsageUtilization = holding.ExtraUsage?.Utilization;
        row.ReadAtUtc = UtcOrNull(holding.ReadAtUtc);
        row.JudgedReadAtUtc = row.ReadAtUtc is { } read ? read + readAtShift : null;
        row.ReadError = Trimmed(holding.ReadError, 32);
        row.ReadErrorDetail = Trimmed(holding.ReadErrorDetail, 500);
        row.SignedInAtUtc = UtcOrNull(holding.SignedInAtUtc);
        row.SignInExpiresAtUtc = UtcOrNull(holding.SignInExpiresAtUtc);
    }

    private static void UpsertAccounts(GatewayDbContext ctx, IEnumerable<LicenseHolding> holdings, DateTime now)
    {
        var emails = holdings.Select(h => NormalizeEmail(h.AccountEmail)).ToList();
        var existing = ctx.LicenseAccounts.Where(a => emails.Contains(a.AccountEmail)).ToDictionary(a => a.AccountEmail, StringComparer.Ordinal);
        foreach (var holding in holdings)
        {
            var email = NormalizeEmail(holding.AccountEmail);
            if (!existing.TryGetValue(email, out var account))
            {
                account = new LicenseAccountEntity { AccountEmail = email, FirstSeenAtUtc = now, TenantId = ctx.ActiveTenant! };
                ctx.LicenseAccounts.Add(account);
                existing[email] = account;
            }
            account.LastSeenAtUtc = now;
            // A sign-in that does not name its plan does not erase what another sign-in of the account said.
            if (Trimmed(holding.Plan, 64) is { } plan) account.Plan = plan;
            if (Trimmed(holding.RateLimitTier, 128) is { } tier) account.RateLimitTier = tier;
        }
    }

    private static LicenseSwitchLogEntity SpareRow(GatewayDbContext ctx, LicenseMachineEntity machine, string directorId,
        string kind, string email, bool wasActive, DateTime now)
    {
        var added = kind == LicenseLogKinds.SpareAdded;
        var what = wasActive ? "active sign-in" : "spare";
        return new LicenseSwitchLogEntity
        {
            Kind = kind,
            MachineId = machine.Id,
            MachineName = machine.MachineName,
            OsUser = machine.OsUser,
            DirectorId = directorId,
            FromAccountEmail = added ? "" : email,
            ToAccountEmail = added ? email : "",
            State = LicenseLogStates.Recorded,
            Message = added
                ? $"The machine's report shows a new {what} for {email}."
                : $"The machine's report no longer shows the {what} for {email}.",
            CreatedAtUtc = now,
            FinishedAtUtc = now,
            TenantId = ctx.ActiveTenant!,
        };
    }

    // ---- The switch log (written by the decision engine) ----

    /// <summary>
    /// Write a switch row in <c>pending</c> BEFORE the command is sent (contract section 5.5), with a fresh switch identifier and
    /// <c>NotAfterUtc</c> = now + <see cref="LicenseContract.SwitchNotAfter"/>. <paramref name="explanation"/> is the
    /// engine's sentence saying why, kept on the row until an outcome replaces it. Null when the machine is unknown to
    /// this account, or when it already has a switch row in <c>pending</c> or <c>unknown</c>: a machine is never sent a
    /// second switch identifier while one is open (contract section 5.4).
    /// </summary>
    public LicenseSwitchLogEntity? CreatePendingSwitch(TenantId tenant, Guid machineId, string fromEmail, string toEmail,
        string reason, DateTime nowUtc, string? explanation = null)
    {
        FileLog.Write($"[LicenseStore] CreatePendingSwitch: tenant={tenant.ToLogString()}, machine={machineId}, from={fromEmail}, to={toEmail}, reason={reason}");
        if (reason is not (LicenseSwitchReasons.Threshold or LicenseSwitchReasons.Manual))
            throw new ArgumentException($"'{reason}' is not a switch reason.", nameof(reason));
        var now = Utc(nowUtc);
        using var ctx = _db.CreateContext(tenant);
        var machine = ctx.LicenseMachines.AsNoTracking().FirstOrDefault(m => m.Id == machineId);
        if (machine is null)
        {
            FileLog.Write($"[LicenseStore] CreatePendingSwitch: REFUSED, no machine {machineId} in this account");
            return null;
        }
        var open = ctx.LicenseSwitchLog.AsNoTracking().FirstOrDefault(r => r.MachineId == machineId && r.Kind == LicenseLogKinds.Switch
            && (r.State == LicenseLogStates.Pending || r.State == LicenseLogStates.Unknown));
        if (open is not null)
        {
            FileLog.Write($"[LicenseStore] CreatePendingSwitch: REFUSED, machine {machineId} already has open switch {open.SwitchId}");
            return null;
        }
        var row = new LicenseSwitchLogEntity
        {
            Kind = LicenseLogKinds.Switch,
            SwitchId = Guid.NewGuid().ToString("N"),
            MachineId = machine.Id,
            MachineName = machine.MachineName,
            OsUser = machine.OsUser,
            DirectorId = machine.DirectorId,
            FromAccountEmail = NormalizeEmail(fromEmail),
            ToAccountEmail = NormalizeEmail(toEmail),
            Reason = reason,
            State = LicenseLogStates.Pending,
            Message = Trimmed(explanation, MaxMessageLength),
            CreatedAtUtc = now,
            NotAfterUtc = now + LicenseContract.SwitchNotAfter,
            TenantId = ctx.ActiveTenant!,
        };
        ctx.LicenseSwitchLog.Add(row);
        ctx.SaveChanges();
        FileLog.Write($"[LicenseStore] CreatePendingSwitch: switch={row.SwitchId}");
        return row;
    }

    /// <summary>One switch row by its switch identifier; null when this account has none.</summary>
    public LicenseSwitchLogEntity? FindSwitch(TenantId tenant, string? switchId)
    {
        var id = SwitchKey(switchId);
        return Logged(nameof(FindSwitch), $"tenant={tenant.ToLogString()}, switch={switchId}", () =>
        {
            if (id is null) return null;
            using var ctx = _db.CreateContext(tenant);
            return ctx.LicenseSwitchLog.AsNoTracking().FirstOrDefault(r => r.SwitchId == id);
        }, row => row is null ? "not found" : $"state={row.State}");
    }

    /// <summary>Every switch row still <c>pending</c> or <c>unknown</c>, oldest first.</summary>
    public IReadOnlyList<LicenseSwitchLogEntity> ListOpenSwitches(TenantId tenant)
        => Logged(nameof(ListOpenSwitches), $"tenant={tenant.ToLogString()}", () =>
        {
            using var ctx = _db.CreateContext(tenant);
            return (IReadOnlyList<LicenseSwitchLogEntity>)ctx.LicenseSwitchLog.AsNoTracking()
                .Where(r => r.Kind == LicenseLogKinds.Switch && (r.State == LicenseLogStates.Pending || r.State == LicenseLogStates.Unknown))
                .ToList()
                .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.SwitchId, StringComparer.Ordinal).ToList();
        }, rows => $"count={rows.Count}");

    /// <summary>The log, newest first: every row written at or after <paramref name="sinceUtc"/>, every open switch, and
    /// every no-room event still open.</summary>
    public IReadOnlyList<LicenseSwitchLogEntity> ListLog(TenantId tenant, DateTime sinceUtc)
    {
        var since = Utc(sinceUtc);
        return Logged(nameof(ListLog), $"tenant={tenant.ToLogString()}, since={since:O}", () =>
        {
            using var ctx = _db.CreateContext(tenant);
            return (IReadOnlyList<LicenseSwitchLogEntity>)ctx.LicenseSwitchLog.AsNoTracking()
                .Where(r => r.CreatedAtUtc >= since || r.State == LicenseLogStates.Pending || r.State == LicenseLogStates.Unknown
                            || r.State == LicenseLogStates.Open)
                .ToList()
                .OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.Id).ToList();
        }, rows => $"count={rows.Count}");
    }

    /// <summary>Record one send of an OPEN switch: the count, the time and the Director. False when it is no longer open.</summary>
    public bool RecordSend(TenantId tenant, string switchId, string directorId, DateTime nowUtc)
    {
        var id = SwitchKey(switchId) ?? throw new ArgumentException($"'{switchId}' is not a switch identifier.", nameof(switchId));
        var now = Utc(nowUtc);
        using var ctx = _db.CreateContext(tenant);
        var changed = OpenSwitch(ctx, id).ExecuteUpdate(u => u
            .SetProperty(r => r.SendCount, r => r.SendCount + 1)
            .SetProperty(r => r.LastSentAtUtc, now)
            .SetProperty(r => r.DirectorId, directorId)) == 1;
        FileLog.Write($"[LicenseStore] RecordSend: switch={id}, director={directorId}, changed={changed}");
        return changed;
    }

    /// <summary>
    /// Move an OPEN switch to another open state or to a final state the GATEWAY decided (<c>pending</c> with
    /// <c>director-offline</c>, <c>unknown</c>, <c>failed</c> with <c>director-error</c> / <c>director-offline</c>, or
    /// <c>switched</c> / <c>failed</c> settled from the machine's own report when no answer could be had, contract
    /// section 5.5). A final row is never changed again. True when this call changed it.
    /// </summary>
    public bool TryMark(TenantId tenant, string switchId, string state, string? reasonCode, string? message, DateTime nowUtc)
    {
        var id = SwitchKey(switchId) ?? throw new ArgumentException($"'{switchId}' is not a switch identifier.", nameof(switchId));
        if (state is not (LicenseLogStates.Pending or LicenseLogStates.Unknown or LicenseLogStates.Failed or LicenseLogStates.Switched))
            throw new ArgumentException($"'{state}' is not a state the Gateway decides.", nameof(state));
        var now = Utc(nowUtc);
        DateTime? finished = LicenseLogStates.IsOpen(state) ? null : now;
        var text = message is null ? null : Cap(message, MaxMessageLength);
        using var ctx = _db.CreateContext(tenant);
        var changed = OpenSwitch(ctx, id).ExecuteUpdate(u => u
            .SetProperty(r => r.State, state)
            .SetProperty(r => r.ReasonCode, reasonCode)
            .SetProperty(r => r.Message, text)
            .SetProperty(r => r.FinishedAtUtc, finished)) == 1;
        FileLog.Write($"[LicenseStore] TryMark: switch={id}, state={state}, reason={reasonCode ?? "(none)"}, changed={changed}");
        return changed;
    }

    /// <summary>
    /// Settle an OPEN switch from the Director's answer (contract section 3.5): its outcome, reason code, message,
    /// read-back and timings. An answer that is not for this switch, or an outcome word this Gateway does not know, is
    /// refused rather than stored. True when this call settled it.
    /// </summary>
    public bool TrySettle(TenantId tenant, LicenseSwitchResult result, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        var id = SwitchKey(result.SwitchId) ?? throw new ArgumentException($"'{result.SwitchId}' is not a switch identifier.", nameof(result));
        if (result.Outcome is not (LicenseSwitchOutcomes.Switched or LicenseSwitchOutcomes.AlreadyActive or LicenseSwitchOutcomes.Failed))
            throw new ArgumentException($"'{result.Outcome}' is not a switch outcome.", nameof(result));
        var now = Utc(nowUtc);
        var reasonCode = result.Outcome == LicenseSwitchOutcomes.Failed ? Trimmed(result.ReasonCode, 32) : null;
        var message = Trimmed(result.Message, MaxMessageLength);
        var after = NormalizeEmail(result.ActiveAccountEmailAfter);
        var started = Utc(result.StartedAtUtc);
        var finishedByDirector = Utc(result.FinishedAtUtc);
        using var ctx = _db.CreateContext(tenant);
        var changed = OpenSwitch(ctx, id).ExecuteUpdate(u => u
            .SetProperty(r => r.State, result.Outcome)
            .SetProperty(r => r.ReasonCode, reasonCode)
            .SetProperty(r => r.Message, message)
            .SetProperty(r => r.ActiveAccountEmailAfter, after)
            .SetProperty(r => r.VerifiedByUsageRead, result.VerifiedByUsageRead)
            .SetProperty(r => r.Replayed, result.Replayed)
            .SetProperty(r => r.DirectorStartedAtUtc, started)
            .SetProperty(r => r.DirectorFinishedAtUtc, finishedByDirector)
            .SetProperty(r => r.ExchangeMilliseconds, result.ExchangeMilliseconds)
            .SetProperty(r => r.FinishedAtUtc, now)) == 1;
        FileLog.Write($"[LicenseStore] TrySettle: switch={id}, outcome={result.Outcome}, reason={reasonCode ?? "(none)"}, changed={changed}");
        return changed;
    }

    /// <summary>
    /// Take the machine's active account from a switch answer's read-back (contract section 3.5) - but ONLY when that
    /// answer is newer than everything the machine has reported since. The answer's <c>FinishedAtUtc</c> is the
    /// Director's clock, so it is moved onto the Gateway's clock by the machine's measured skew before it is compared
    /// with the Gateway's receive time of the last report.
    ///
    /// An answer with <c>Replayed = true</c> is a STORED answer to an earlier send, and for it the Director's clock is
    /// not trusted at all: a clock that is ahead by less than the skew that is measured would make an old answer look
    /// newer than a report that came after it. So a replayed answer's read-back is never taken once the machine has
    /// reported after the switch row was written - both of those times are the Gateway's own. The after-switch report
    /// the Director sends after every switch is such a report.
    ///
    /// One conditional update, so a report landing at the same moment cannot be overwritten. True when the account
    /// was taken.
    /// </summary>
    public bool ApplySwitchReadBack(TenantId tenant, Guid machineId, LicenseSwitchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var after = NormalizeEmail(result.ActiveAccountEmailAfter);
        FileLog.Write($"[LicenseStore] ApplySwitchReadBack: tenant={tenant.ToLogString()}, machine={machineId}, switch={result.SwitchId}, replayed={result.Replayed}");
        if (after.Length == 0)
        {
            FileLog.Write("[LicenseStore] ApplySwitchReadBack: the answer read back no active account, nothing taken");
            return false;
        }
        using var ctx = _db.CreateContext(tenant);
        var machine = ctx.LicenseMachines.AsNoTracking().FirstOrDefault(m => m.Id == machineId);
        if (machine is null)
        {
            FileLog.Write($"[LicenseStore] ApplySwitchReadBack: REFUSED, no machine {machineId} in this account");
            return false;
        }
        var finishedOnGatewayClock = Utc(result.FinishedAtUtc) + TimeSpan.FromSeconds(machine.ClockSkewSeconds);
        var candidates = ctx.LicenseMachines.Where(m => m.Id == machineId && m.LastReportAtUtc < finishedOnGatewayClock);
        if (result.Replayed)
        {
            var id = SwitchKey(result.SwitchId);
            var written = ctx.LicenseSwitchLog.AsNoTracking()
                .Where(r => r.SwitchId == id && r.Kind == LicenseLogKinds.Switch)
                .Select(r => (DateTime?)r.CreatedAtUtc)
                .FirstOrDefault();
            if (written is not { } rowWrittenAt)
            {
                FileLog.Write("[LicenseStore] ApplySwitchReadBack: REFUSED, a replayed answer for a switch this account has no row for");
                return false;
            }
            candidates = candidates.Where(m => m.LastReportAtUtc <= rowWrittenAt);
        }
        // The machine row and its holdings say who is active in two places, and the read model reads both; they move
        // together or not at all, so the Licenses page never shows the machine on one account and the holding on another.
        using var transaction = ctx.Database.BeginTransaction();
        var taken = candidates.ExecuteUpdate(u => u.SetProperty(m => m.ActiveAccountEmail, after)) == 1;
        if (taken)
            ctx.LicenseHoldings
                .Where(h => h.MachineId == machineId)
                .ExecuteUpdate(u => u.SetProperty(h => h.IsActive, h => h.AccountEmail == after));
        transaction.Commit();
        FileLog.Write($"[LicenseStore] ApplySwitchReadBack: taken={taken} (finished {finishedOnGatewayClock:O}, last report {machine.LastReportAtUtc:O})");
        return taken;
    }

    private static IQueryable<LicenseSwitchLogEntity> OpenSwitch(GatewayDbContext ctx, string id)
        => ctx.LicenseSwitchLog.Where(r => r.SwitchId == id && r.Kind == LicenseLogKinds.Switch
                                           && (r.State == LicenseLogStates.Pending || r.State == LicenseLogStates.Unknown));

    // ---- Accounts barred as targets (written and read by the decision engine) ----

    /// <summary>Bar an account as a target until <paramref name="untilUtc"/> (the triggering window's reset). One row per
    /// (account, window): barring the same window again moves its time.</summary>
    public void Bar(TenantId tenant, string accountEmail, string window, DateTime untilUtc, DateTime nowUtc)
    {
        if (window is not (LicenseWindows.FiveHour or LicenseWindows.SevenDay))
            throw new ArgumentException($"'{window}' is not a usage window.", nameof(window));
        var email = NormalizeEmail(accountEmail);
        FileLog.Write($"[LicenseStore] Bar: tenant={tenant.ToLogString()}, account={email}, window={window}, until={untilUtc:O}");
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.LicenseBars.FirstOrDefault(b => b.AccountEmail == email && b.Window == window);
        if (row is null)
        {
            row = new LicenseBarEntity { AccountEmail = email, Window = window, TenantId = ctx.ActiveTenant! };
            ctx.LicenseBars.Add(row);
        }
        row.BarredAtUtc = Utc(nowUtc);
        row.UntilUtc = Utc(untilUtc);
        ctx.SaveChanges();
    }

    /// <summary>The bars still in force at <paramref name="nowUtc"/>.</summary>
    public IReadOnlyList<LicenseBarEntity> ListActiveBars(TenantId tenant, DateTime nowUtc)
    {
        var now = Utc(nowUtc);
        return Logged(nameof(ListActiveBars), $"tenant={tenant.ToLogString()}", () =>
        {
            using var ctx = _db.CreateContext(tenant);
            return (IReadOnlyList<LicenseBarEntity>)ctx.LicenseBars.AsNoTracking().Where(b => b.UntilUtc > now).ToList()
                .OrderBy(b => b.AccountEmail, StringComparer.Ordinal).ThenBy(b => b.Window, StringComparer.Ordinal).ToList();
        }, rows => $"count={rows.Count}");
    }

    // ---- No-room events (written by the minute check) ----

    /// <summary>
    /// Record that an exhaustion event began: an account in use is at the threshold and no account has room (one row,
    /// kind <c>no-room</c>, open while its <c>FinishedAtUtc</c> is null; <c>NotAfterUtc</c> holds the event's end). An
    /// account has at most one open event: when one is already open it is returned unchanged.
    /// </summary>
    public LicenseSwitchLogEntity OpenNoRoom(TenantId tenant, string accountEmail, DateTime startedAtUtc, DateTime endsAtUtc)
    {
        var email = NormalizeEmail(accountEmail);
        FileLog.Write($"[LicenseStore] OpenNoRoom: tenant={tenant.ToLogString()}, account={email}, ends={endsAtUtc:O}");
        using var ctx = _db.CreateContext(tenant);
        var open = ctx.LicenseSwitchLog.AsNoTracking()
            .FirstOrDefault(r => r.Kind == LicenseLogKinds.NoRoom && r.FromAccountEmail == email && r.FinishedAtUtc == null);
        if (open is not null)
        {
            FileLog.Write($"[LicenseStore] OpenNoRoom: already open since {open.CreatedAtUtc:O}");
            return open;
        }
        var row = new LicenseSwitchLogEntity
        {
            Kind = LicenseLogKinds.NoRoom,
            FromAccountEmail = email,
            Reason = LicenseSwitchReasons.Threshold,
            State = LicenseLogStates.Open,
            Message = NoRoomMessage(email, "the owner has not been emailed yet"),
            CreatedAtUtc = Utc(startedAtUtc),
            NotAfterUtc = Utc(endsAtUtc),
            TenantId = ctx.ActiveTenant!,
        };
        ctx.LicenseSwitchLog.Add(row);
        ctx.SaveChanges();
        FileLog.Write($"[LicenseStore] OpenNoRoom: row={row.Id}");
        return row;
    }

    /// <summary>The sentence a no-room row shows: what happened, then what became of the owner email.</summary>
    public static string NoRoomMessage(string accountEmail, string ownerEmailClause)
        => $"No account had room for the machines on {NormalizeEmail(accountEmail)}, so nothing was switched; {ownerEmailClause}.";

    /// <summary>Every no-room event still open, oldest first.</summary>
    /// <remarks>Quiet, like <see cref="HasMachines"/>: the minute check reads this for every tenant every minute, and
    /// a check that finds nothing to do leaves no line. A failure is logged by the check that asked.</remarks>
    public IReadOnlyList<LicenseSwitchLogEntity> ListOpenNoRoom(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.LicenseSwitchLog.AsNoTracking()
            .Where(r => r.Kind == LicenseLogKinds.NoRoom && r.FinishedAtUtc == null)
            .ToList()
            .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.FromAccountEmail, StringComparer.Ordinal).ToList();
    }

    /// <summary>Move the end of the account's open no-room event. False when it has none open.</summary>
    public bool ExtendNoRoom(TenantId tenant, string accountEmail, DateTime endsAtUtc)
    {
        var email = NormalizeEmail(accountEmail);
        DateTime? ends = Utc(endsAtUtc);
        using var ctx = _db.CreateContext(tenant);
        var changed = OpenNoRoomRows(ctx, email).ExecuteUpdate(u => u.SetProperty(r => r.NotAfterUtc, ends)) > 0;
        FileLog.Write($"[LicenseStore] ExtendNoRoom: tenant={tenant.ToLogString()}, account={email}, ends={ends:O}, changed={changed}");
        return changed;
    }

    /// <summary>End the account's open no-room event. False when it has none open.</summary>
    public bool CloseNoRoom(TenantId tenant, string accountEmail, DateTime nowUtc)
    {
        var email = NormalizeEmail(accountEmail);
        DateTime? now = Utc(nowUtc);
        using var ctx = _db.CreateContext(tenant);
        var changed = OpenNoRoomRows(ctx, email).ExecuteUpdate(u => u
            .SetProperty(r => r.State, LicenseLogStates.Ended)
            .SetProperty(r => r.FinishedAtUtc, now)) > 0;
        FileLog.Write($"[LicenseStore] CloseNoRoom: tenant={tenant.ToLogString()}, account={email}, changed={changed}");
        return changed;
    }

    /// <summary>
    /// Record what became of the owner email for the account's open no-room event: <paramref name="emailState"/> is
    /// <see cref="LicenseGatewayReasonCodes.OwnerEmailed"/> (with <paramref name="sentAtUtc"/>),
    /// <see cref="LicenseGatewayReasonCodes.OwnerEmailFailed"/> or <see cref="LicenseGatewayReasonCodes.NoOwnerAccount"/>.
    /// An event already recorded as emailed is never changed: one email per event. True when this call changed it.
    /// </summary>
    public bool RecordNoRoomEmail(TenantId tenant, string accountEmail, string emailState, string message, DateTime? sentAtUtc)
    {
        if (emailState is not (LicenseGatewayReasonCodes.OwnerEmailed or LicenseGatewayReasonCodes.OwnerEmailFailed
            or LicenseGatewayReasonCodes.NoOwnerAccount))
            throw new ArgumentException($"'{emailState}' is not an owner email state.", nameof(emailState));
        if ((emailState == LicenseGatewayReasonCodes.OwnerEmailed) != sentAtUtc.HasValue)
            throw new ArgumentException("A sent time is given exactly when the email was sent.", nameof(sentAtUtc));
        var email = NormalizeEmail(accountEmail);
        var sent = UtcOrNull(sentAtUtc);
        var sends = sent is null ? 0 : 1;
        var text = Cap(message, MaxMessageLength);
        using var ctx = _db.CreateContext(tenant);
        var changed = OpenNoRoomRows(ctx, email)
            .Where(r => r.ReasonCode == null || r.ReasonCode != LicenseGatewayReasonCodes.OwnerEmailed)
            .ExecuteUpdate(u => u
                .SetProperty(r => r.ReasonCode, emailState)
                .SetProperty(r => r.Message, text)
                .SetProperty(r => r.LastSentAtUtc, sent)
                .SetProperty(r => r.SendCount, r => r.SendCount + sends)) > 0;
        FileLog.Write($"[LicenseStore] RecordNoRoomEmail: tenant={tenant.ToLogString()}, account={email}, state={emailState}, changed={changed}");
        return changed;
    }

    private static IQueryable<LicenseSwitchLogEntity> OpenNoRoomRows(GatewayDbContext ctx, string email)
        => ctx.LicenseSwitchLog.Where(r => r.Kind == LicenseLogKinds.NoRoom && r.FromAccountEmail == email && r.FinishedAtUtc == null);

    // ---- Reads for the read model ----

    /// <summary>True when any machine of this account has ever reported. Asked once a minute for every account with a
    /// Director connected, so it writes no log line: an account with no machines must leave no trace of the check.</summary>
    public bool HasMachines(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.LicenseMachines.AsNoTracking().Any();
    }

    /// <summary>Everything the read model folds, in one context: accounts, machines and holdings.</summary>
    internal (List<LicenseAccountEntity> Accounts, List<LicenseMachineEntity> Machines, List<LicenseHoldingEntity> Holdings) ReadAll(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return (ctx.LicenseAccounts.AsNoTracking().ToList(),
                ctx.LicenseMachines.AsNoTracking().ToList(),
                ctx.LicenseHoldings.AsNoTracking().ToList());
    }

    /// <summary>One read with its entry, its result and its failure in the log (CLAUDE.md rule 2).</summary>
    private static T Logged<T>(string method, string context, Func<T> read, Func<T, string> describe)
    {
        FileLog.Write($"[LicenseStore] {method}: {context}");
        try
        {
            var result = read();
            FileLog.Write($"[LicenseStore] {method}: {describe(result)}");
            return result;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseStore] {method} FAILED: {ex.Message}");
            throw;
        }
    }

    private static string? SwitchKey(string? switchId)
    {
        var id = switchId?.Trim();
        return id is not null && LicenseContract.SwitchIdShape.IsMatch(id) ? id : null;
    }

    private static string? Trimmed(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : Cap(text, max);
    }

    private static string Cap(string value, int max) => value.Length > max ? value[..max] : value;

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    private static DateTime? UtcOrNull(DateTime? value) => value is { } v ? Utc(v) : null;
}
