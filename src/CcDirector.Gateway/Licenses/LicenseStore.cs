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

    public static bool IsOpen(string state) => state is Pending or Unknown;
}

/// <summary>The reason codes the GATEWAY writes on a switch row (contract section 3.5), beside the Director's own
/// <see cref="LicenseSwitchReasonCodes"/>.</summary>
public static class LicenseGatewayReasonCodes
{
    public const string DirectorOffline = "director-offline";
    public const string DirectorError = "director-error";
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
    /// <c>NotAfterUtc</c> = now + <see cref="LicenseContract.SwitchNotAfter"/>. Null when the machine is unknown to this
    /// account.
    /// </summary>
    public LicenseSwitchLogEntity? CreatePendingSwitch(TenantId tenant, Guid machineId, string fromEmail, string toEmail,
        string reason, DateTime nowUtc)
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

    /// <summary>The log, newest first: every row written at or after <paramref name="sinceUtc"/>, and every open switch.</summary>
    public IReadOnlyList<LicenseSwitchLogEntity> ListLog(TenantId tenant, DateTime sinceUtc)
    {
        var since = Utc(sinceUtc);
        return Logged(nameof(ListLog), $"tenant={tenant.ToLogString()}, since={since:O}", () =>
        {
            using var ctx = _db.CreateContext(tenant);
            return (IReadOnlyList<LicenseSwitchLogEntity>)ctx.LicenseSwitchLog.AsNoTracking()
                .Where(r => r.CreatedAtUtc >= since || r.State == LicenseLogStates.Pending || r.State == LicenseLogStates.Unknown)
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
    /// <c>director-offline</c>, <c>unknown</c>, or <c>failed</c> with <c>director-error</c> / <c>director-offline</c>).
    /// A final row is never changed again. True when this call changed it.
    /// </summary>
    public bool TryMark(TenantId tenant, string switchId, string state, string? reasonCode, string? message, DateTime nowUtc)
    {
        var id = SwitchKey(switchId) ?? throw new ArgumentException($"'{switchId}' is not a switch identifier.", nameof(switchId));
        if (state is not (LicenseLogStates.Pending or LicenseLogStates.Unknown or LicenseLogStates.Failed))
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

    // ---- Reads for the read model ----

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
