using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// STANDING APPROVERS (License Switching phase 6, Track C). A Director checks the browser profiles of its machine daily
/// by reading a real approval page, and reports what it found over its own hub connection. This class decides what the
/// Gateway keeps of each report: a profile proven able to approve an account becomes that account's approver record,
/// marked registered; a check of a profile a record already names updates that record's health.
///
/// WHO IS ASKING COMES FROM THE GATEWAY, NEVER FROM THE REPORT. The tenant and the Director are the hub connection's
/// binding; the machine and operating system user are the Licenses read model's record of which machine that Director
/// last reported for, as <see cref="LicenseApprovalRelay"/> looks them up. A Director that is not the one reporting
/// licenses for any machine of the tenant registers nothing and updates nothing. That is also why there is no separate
/// tenant guard: a Director reports licenses only with license switching switched on for it.
///
/// THE OWNER KEEPS THE LAST WORD. A registration is refused when: the machine does not offer its profiles as approvers;
/// the account is not one the tenant's machines hold; the owner set the account's approver; the owner removed this very
/// registration; another registered approver for the account can approve and was checked within
/// <see cref="LicenseApproverContract.NotCheckedAfter"/>; or the tenant already keeps the most approvers it may. A row
/// that does not prove the profile can approve only updates the health of a record naming exactly the caller's machine,
/// user and that profile, whoever made the record.
///
/// A REPORT IS DECIDED UNDER THE STORE'S LOCK, so an owner's change can never land between a decision and its write. A
/// report that is not one - missing, or more than <see cref="LicenseApproverContract.MaxCheckRows"/> rows - is refused
/// whole by throwing; a row that is not taken is refused whole and nothing of it is stored. Nothing the Director sent
/// is written to the log unchecked: a line names the tenant, the Director, the machine (the Gateway's own record), the
/// account once it has been checked to be an email, and the outcome.
/// </summary>
public sealed class LicenseApproverRegistration
{
    /// <summary>How far ahead of the Gateway's clock a check's time may be: a Director clock a little fast is fine, a
    /// check dated days ahead would keep a record healthy for days without a check.</summary>
    public static readonly TimeSpan CheckedAtFutureTolerance = TimeSpan.FromMinutes(5);

    private readonly LicenseApproverStore _approvers;
    private readonly LicenseReadModel _reads;
    private readonly Func<DateTime> _nowUtc;

    public LicenseApproverRegistration(LicenseApproverStore approvers, LicenseReadModel reads, Func<DateTime> nowUtc)
    {
        _approvers = approvers ?? throw new ArgumentNullException(nameof(approvers));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    /// <summary>The approver records that name the calling Director's machine and operating system user, which that
    /// Director checks daily. Empty when the Director reports licenses for no machine of the tenant.</summary>
    /// <param name="tenant">The calling connection's bound tenant.</param>
    /// <param name="directorId">The calling connection's bound Director.</param>
    public LicenseApproverAssignments Assignments(TenantId tenant, string directorId)
    {
        FileLog.Write($"[LicenseApproverRegistration] Assignments: tenant={tenant.ToLogString()}, director={directorId}");
        var machine = ReportingMachine(_reads.Read(tenant, _nowUtc()), directorId);
        if (machine is null)
        {
            FileLog.Write($"[LicenseApproverRegistration] Assignments: director={directorId} reports licenses for no machine of this account, none");
            return new LicenseApproverAssignments();
        }
        var rows = _approvers.List(tenant)
            .Where(a => a.NamesMachine(machine.MachineName, machine.OsUser))
            .Select(a => new LicenseApproverAssignment { AccountEmail = a.AccountEmail, BrowserProfile = a.BrowserProfile, Source = a.Source })
            .ToList();
        FileLog.Write($"[LicenseApproverRegistration] Assignments: director={directorId}, machine={machine.MachineName}, count={rows.Count}");
        return new LicenseApproverAssignments { Rows = rows };
    }

    /// <summary>
    /// Take one check report from a bound Director and answer what was done with each row, in the report's order.
    /// </summary>
    /// <param name="tenant">The calling connection's bound tenant - never a value from the report.</param>
    /// <param name="directorId">The calling connection's bound Director.</param>
    /// <exception cref="ArgumentException">The report is missing, has no list of rows, or has more than
    /// <see cref="LicenseApproverContract.MaxCheckRows"/> rows. Nothing of it is kept.</exception>
    public LicenseApproverCheckAnswer Report(TenantId tenant, string directorId, LicenseApproverCheckReport? report)
    {
        if (report?.Rows is not { } rows)
            throw NotTaken(tenant, directorId, "no report, or no rows", "The report must carry a list of rows.");
        if (rows.Count > LicenseApproverContract.MaxCheckRows)
            throw NotTaken(tenant, directorId, $"{rows.Count} rows",
                $"A report carries at most {LicenseApproverContract.MaxCheckRows} rows.");

        var now = _nowUtc();
        FileLog.Write($"[LicenseApproverRegistration] Report: tenant={tenant.ToLogString()}, director={directorId}, rows={rows.Count}, offers={report.OffersApprovers}");
        var overview = _reads.Read(tenant, now);
        var machine = ReportingMachine(overview, directorId);
        if (machine is null)
        {
            FileLog.Write($"[LicenseApproverRegistration] Report: REFUSED every row, director={directorId} reports licenses for no machine of this account");
            return new LicenseApproverCheckAnswer
            {
                Rows = rows.Select(_ => Answer(null, null, LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.NotTheReportingDirector)).ToList(),
            };
        }

        var held = HeldAccounts(overview);
        var answers = _approvers.Change(tenant, records =>
        {
            var approvers = records.Approvers.ToList();
            var changed = false;
            var decided = new List<LicenseApproverCheckRowAnswer>(rows.Count);
            foreach (var given in rows)
            {
                var answer = Decide(approvers, records.Removals, held, machine, report.OffersApprovers, given, now, out var stored);
                changed |= stored;
                decided.Add(answer);
            }
            return (changed ? approvers : null, decided);
        }, now);

        foreach (var answer in answers)
            FileLog.Write($"[LicenseApproverRegistration] Report: director={directorId}, machine={machine.MachineName}, account={answer.AccountEmail ?? "-"}, "
                          + $"outcome={answer.Outcome}, reason={answer.Reason ?? "-"}");
        return new LicenseApproverCheckAnswer { Rows = answers };
    }

    /// <summary>One row, against the records as earlier rows of the same report left them.</summary>
    /// <param name="stored">True when the row changed <paramref name="approvers"/>.</param>
    private static LicenseApproverCheckRowAnswer Decide(List<LicenseApprover> approvers, IReadOnlyList<LicenseApproverRemoval> removals,
        IReadOnlySet<string> held, LicenseMachineView machine, bool offers, LicenseApproverCheckRow? given, DateTime now, out bool stored)
    {
        stored = false;
        if (Checked(given, now) is not { } row)
            return Answer(null, null, LicenseApproverCheckOutcomes.Refused, LicenseApproverRefusals.InvalidRow);
        if (row.AccountEmail is not { } account)
            return Answer(row.BrowserProfile, null, LicenseApproverCheckOutcomes.NothingToStore, null);

        LicenseApproverCheckRowAnswer Refused(string reason) => Answer(row.BrowserProfile, account, LicenseApproverCheckOutcomes.Refused, reason);

        var at = approvers.FindIndex(a => a.AccountEmail == account);
        var existing = at >= 0 ? approvers[at] : null;

        // The record names exactly this machine, user and profile: whoever made it, this is its check.
        if (existing is not null && existing.Names(machine.MachineName, machine.OsUser, row.BrowserProfile))
        {
            approvers[at] = existing with { State = row.State, CheckedAtUtc = row.CheckedAtUtc, ShownAccount = row.ShownAccount, Detail = row.Detail };
            stored = true;
            return Answer(row.BrowserProfile, account, LicenseApproverCheckOutcomes.HealthUpdated, null);
        }

        // Anything short of proof that the profile can approve registers nothing, and a record naming another approver
        // is not this row's to update.
        if (row.State != LicenseApproverStates.CanApprove)
            return existing is null
                ? Answer(row.BrowserProfile, account, LicenseApproverCheckOutcomes.NothingToStore, null)
                : Refused(LicenseApproverRefusals.RecordNamesAnotherApprover);

        if (!offers)
            return Refused(LicenseApproverRefusals.NotOffered);
        if (!held.Contains(account))
            return Refused(LicenseApproverRefusals.AccountNotHeld);
        if (existing?.Source == LicenseApproverSources.Owner)
            return Refused(LicenseApproverRefusals.OwnerSet);
        if (removals.Any(r => r.Matches(account, machine.MachineName, machine.OsUser, row.BrowserProfile)))
            return Refused(LicenseApproverRefusals.OwnerRemoved);
        if (existing is not null && LicenseApproverHealth.CanApprove(existing, now))
            return Refused(LicenseApproverRefusals.HealthyIncumbent);
        if (existing is null && approvers.Count >= LicenseApproverStore.MaxApprovers)
            return Refused(LicenseApproverRefusals.TooManyApprovers);

        var registered = new LicenseApprover(account, machine.MachineName, machine.OsUser, row.BrowserProfile, LicenseApproverSources.Registered,
            row.State, row.CheckedAtUtc, row.ShownAccount, row.Detail);
        if (existing is null) approvers.Add(registered);
        else approvers[at] = registered;
        stored = true;
        return Answer(row.BrowserProfile, account, LicenseApproverCheckOutcomes.Registered, null);
    }

    /// <summary>A row as it is kept, every field checked; null when any field is missing, too long or of the wrong
    /// shape. The lengths are looked at before any text is copied: the hub takes a message of many megabytes.</summary>
    internal static LicenseApproverCheckRow? Checked(LicenseApproverCheckRow? row, DateTime now)
    {
        if (row is null)
            return null;
        if (row.BrowserProfile is not { Length: <= LicenseApproverStore.MaxNameLength } givenProfile)
            return null;
        var profile = givenProfile.Trim();
        if (!LicenseApproverStore.BrowserProfileShape.IsMatch(profile))
            return null;
        if (!Email(row.AccountEmail, out var account) || !Email(row.ShownAccount, out var shown))
            return null;
        if (row.State is null || !LicenseApproverStates.Reportable.Contains(row.State))
            return null;
        var checkedAt = Utc(row.CheckedAtUtc);
        // A check from before the not-checked window is not today's check, and one dated far ahead would read as
        // healthy long after it stopped being true.
        if (checkedAt > now + CheckedAtFutureTolerance || now - checkedAt > LicenseApproverContract.NotCheckedAfter)
            return null;
        string? detail = null;
        if (row.Detail is not null)
        {
            if (row.Detail.Length > LicenseApproverContract.MaxDetailLength)
                return null;
            detail = row.Detail.Trim();
            if (detail.Any(LicenseApproverStore.NotReadable))
                return null;
            if (detail.Length == 0)
                detail = null;
        }
        // A profile proven able to approve an account names that account, and the page cannot have shown anyone else.
        if (row.State == LicenseApproverStates.CanApprove && (account is null || (shown is not null && shown != account)))
            return null;
        return new LicenseApproverCheckRow
        {
            BrowserProfile = profile, AccountEmail = account, ShownAccount = shown, State = row.State, CheckedAtUtc = checkedAt, Detail = detail,
        };
    }

    // Null is allowed and stays null; anything else must be an account email.
    private static bool Email(string? given, out string? email)
    {
        email = null;
        if (given is null)
            return true;
        if (given.Length > LicenseApprovalRelay.MaxAccountEmailLength + 64)
            return false;
        var normal = LicenseStore.NormalizeEmail(given);
        if (!LicenseApproverStore.IsAccountEmail(normal))
            return false;
        email = normal;
        return true;
    }

    /// <summary>The machine the Director last reported licenses for in this tenant, or null when it reports for none.
    /// Should a Director's identifier still stand on an older machine row (a renamed machine), the row it reported to
    /// most recently is the one.</summary>
    private static LicenseMachineView? ReportingMachine(LicenseOverview overview, string directorId)
        => overview.Machines
            .Where(m => string.Equals(m.DirectorId, directorId, StringComparison.Ordinal))
            .OrderByDescending(m => m.LastReportAtUtc)
            .FirstOrDefault();

    /// <summary>Every account some machine of the tenant holds, active or spare.</summary>
    private static IReadOnlySet<string> HeldAccounts(LicenseOverview overview)
        => overview.Machines.SelectMany(m => m.Holdings).Select(h => h.AccountEmail).ToHashSet(StringComparer.Ordinal);

    private static LicenseApproverCheckRowAnswer Answer(string? profile, string? account, string outcome, string? reason)
        => new() { BrowserProfile = profile ?? "", AccountEmail = account, Outcome = outcome, Reason = reason };

    private static ArgumentException NotTaken(TenantId tenant, string directorId, string why, string sentence)
    {
        FileLog.Write($"[LicenseApproverRegistration] Report: REFUSED, {why}: tenant={tenant.ToLogString()}, director={directorId}");
        return new ArgumentException(sentence, "report");
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
