using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE APPROVAL ROWS OF THE LICENSES LOG (License Switching phase 6, Track C). One row, final when written, for every
/// approval the relay passed between machines: which machine asked, for which account, which machine was set to
/// approve it, and whether it was approved or refused with the reason word.
///
/// IT EXISTS FOR THE RENEWAL NOBODY STARTED. The daily renewal on a Director asks the relay when the account has no
/// browser profile on that machine, and before this row nothing of it reached the Licenses log: the Gateway saw only
/// the holding's new sign-in time in the next report, and a renewal approved by another machine left no trace of
/// which machine approved it. A sign-in started from the Cockpit has its own row as well (<see cref="LicenseSignInLog"/>);
/// this one says who approved it.
///
/// A row uses the log's columns with these meanings: <c>SwitchId</c> holds the sign-in's identifier; the machine
/// columns are the machine that ASKED; <c>ToAccountEmail</c> the account (<c>FromAccountEmail</c> is empty);
/// <c>State</c> is <see cref="LicenseApprovalOutcomes.Approved"/> or <see cref="LicenseApprovalOutcomes.Refused"/>;
/// <c>ReasonCode</c> the refusal's word; <c>Message</c> the Gateway's own sentence, naming the approving machine.
///
/// NEVER WHAT COMPLETES A SIGN-IN, AND NEVER A DIRECTOR'S WORDS. A row carries an email, machine names, a reason word
/// and a sentence written here from them - not the address the sign-in was approved at, not the sealed approval, and
/// not the approving Director's message, which could quote either.
/// </summary>
public sealed class LicenseApprovalLog
{
    /// <summary>The kind of an approval row.</summary>
    public const string Kind = LicenseLogKinds.Approval;

    private readonly GatewayDatabase _db;

    public LicenseApprovalLog(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Write the row for one relayed approval.</summary>
    /// <param name="asking">The machine whose Director asked.</param>
    /// <param name="approverMachine">The machine set to approve the account; null when none is set.</param>
    /// <param name="outcome"><see cref="LicenseApprovalOutcomes.Approved"/> or <see cref="LicenseApprovalOutcomes.Refused"/>.</param>
    /// <param name="reasonCode">The refusal's word; ignored for an approval.</param>
    public LicenseSwitchLogEntity Record(TenantId tenant, LicenseMachineView asking, string signInId, string accountEmail,
        string? approverMachine, string outcome, string? reasonCode, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(asking);
        if (outcome is not (LicenseApprovalOutcomes.Approved or LicenseApprovalOutcomes.Refused))
            throw new ArgumentException($"'{outcome}' is not how an approval ends.", nameof(outcome));
        var email = LicenseStore.NormalizeEmail(accountEmail);
        var refused = outcome == LicenseApprovalOutcomes.Refused;
        var now = Utc(nowUtc);
        using var ctx = _db.CreateContext(tenant);
        var row = new LicenseSwitchLogEntity
        {
            Kind = Kind,
            SwitchId = signInId,
            MachineId = asking.MachineId,
            MachineName = asking.MachineName,
            OsUser = asking.OsUser,
            DirectorId = asking.DirectorId,
            FromAccountEmail = "",
            ToAccountEmail = email,
            State = outcome,
            ReasonCode = refused ? reasonCode : null,
            Message = Cap(Sentence(asking.MachineName, email, approverMachine, outcome, refused ? reasonCode : null)),
            CreatedAtUtc = now,
            FinishedAtUtc = now,
            TenantId = ctx.ActiveTenant!,
        };
        ctx.LicenseSwitchLog.Add(row);
        ctx.SaveChanges();
        FileLog.Write($"[LicenseApprovalLog] Record: tenant={tenant.ToLogString()}, signIn={signInId}, machine={asking.MachineId}, account={email}, approver={approverMachine ?? "(none)"}, outcome={outcome}, reason={row.ReasonCode ?? "-"}");
        return row;
    }

    /// <summary>The row's sentence, written from its own fields.</summary>
    internal static string Sentence(string askingMachine, string account, string? approverMachine, string outcome, string? reasonCode)
    {
        var self = approverMachine is not null && string.Equals(approverMachine, askingMachine, StringComparison.OrdinalIgnoreCase);
        if (outcome == LicenseApprovalOutcomes.Approved)
            return self
                ? $"{askingMachine} approved its own sign-in for {account}."
                : $"{approverMachine} approved the sign-in for {account} on {askingMachine}.";
        var who = approverMachine is null ? "" : self ? " by itself" : $" by {approverMachine}";
        var why = LicenseDisplayFold.RenewalWhy(reasonCode);
        return why is null
            ? $"The sign-in for {account} on {askingMachine} was not approved{who}."
            : $"The sign-in for {account} on {askingMachine} was not approved{who}: {why}.";
    }

    /// <summary>One approval row of the log in words. <paramref name="label"/> is what the row's machine is called.</summary>
    public static LicenseLogEntryDto LogEntry(LicenseSwitchLogEntity row, string label)
    {
        ArgumentNullException.ThrowIfNull(row);
        var message = string.IsNullOrWhiteSpace(row.Message) ? null : row.Message;
        var (stateText, stateTone) = row.State switch
        {
            LicenseApprovalOutcomes.Approved => ("Approved", LicenseTones.Good),
            LicenseApprovalOutcomes.Refused => ("Refused", LicenseTones.Bad),
            // A state this build does not know: what happened is not known here, so nothing is claimed.
            _ => (string.IsNullOrWhiteSpace(row.State) ? "Recorded" : row.State, LicenseTones.Neutral),
        };
        return new LicenseLogEntryDto
        {
            EntryId = row.Id.ToString("N"),
            Kind = row.Kind,
            KindText = "Approval",
            AtUtc = Utc(row.CreatedAtUtc),
            MachineId = row.MachineId == Guid.Empty ? null : row.MachineId.ToString("N"),
            MachineName = row.MachineName,
            OsUser = row.OsUser,
            Label = label,
            FromAccountEmail = row.FromAccountEmail,
            ToAccountEmail = row.ToAccountEmail,
            Why = null,
            WhyText = "",
            State = row.State,
            StateText = stateText,
            StateTone = stateTone,
            IsOpen = false,
            ReasonCode = row.ReasonCode,
            ReasonText = LicenseSignInFold.ReasonText(row.ReasonCode),
            Message = message,
            Summary = message ?? $"{label}: an approval for {row.ToAccountEmail}.",
            SendCount = row.SendCount,
            LastSentAtUtc = null,
            StartedOnMachineAtUtc = null,
            FinishedOnMachineAtUtc = null,
            ExchangeMilliseconds = null,
            FinishedAtUtc = row.FinishedAtUtc is { } finished ? Utc(finished) : null,
            TimingText = null,
            VerifiedByUsageRead = null,
            VerifiedText = null,
        };
    }

    private static string Cap(string sentence)
        => sentence.Length > LicenseStore.MaxMessageLength ? sentence[..LicenseStore.MaxMessageLength] : sentence;

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
