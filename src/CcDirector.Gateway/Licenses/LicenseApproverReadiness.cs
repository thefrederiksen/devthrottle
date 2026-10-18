using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>What an approver record's health means, as finished words: the state, the sentence saying it, and the one
/// thing the owner must do (null when there is nothing to do).</summary>
public sealed record LicenseApproverHealthVerdict(string State, string HealthText, string? WhatToDo);

/// <summary>
/// CAN THIS ACCOUNT'S APPROVER APPROVE? (License Switching phase 6, Track C; CLAUDE.md rule 7: the Gateway folds every
/// sentence.) The one place that turns an approver record's health into plain English, naming the account, the
/// machine and the browser profile, and the one thing to do. The approvers route folds its rows with
/// <see cref="Verdict"/>, and Track A's readiness asks <see cref="WhyNotReady"/> - so the page and the readiness say
/// the same sentence.
///
/// An absence is never read as healthy: a record never checked, or whose last check is older than
/// <see cref="LicenseApproverContract.NotCheckedAfter"/>, is not ready (<see cref="LicenseApproverHealth.StateOf"/>).
/// </summary>
public sealed class LicenseApproverReadiness
{
    private readonly LicenseApproverStore _approvers;
    private readonly LicenseReadModel _reads;
    private readonly Func<DateTime> _nowUtc;

    public LicenseApproverReadiness(LicenseApproverStore approvers, LicenseReadModel reads, Func<DateTime> nowUtc)
    {
        _approvers = approvers ?? throw new ArgumentNullException(nameof(approvers));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    /// <summary>
    /// For Track A's readiness: null when the approver of <paramref name="accountEmail"/> in <paramref name="tenant"/>
    /// can approve now, else the finished sentence of why not and the one thing to do - for example "No machine is set
    /// to approve sign-ins for alice@example.com. Set an approver for alice@example.com on the Licenses page."
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="accountEmail"/> is missing or not an account email: a
    /// sentence folded from it would read as "no approver" when the fault is the caller's.</exception>
    public string? WhyNotReady(TenantId tenant, string? accountEmail)
    {
        var account = LicenseStore.NormalizeEmail(accountEmail);
        if (!LicenseApproverStore.IsAccountEmail(account))
            throw new ArgumentException("accountEmail is the email of a Claude account.", nameof(accountEmail));
        var now = _nowUtc();
        var approver = _approvers.Find(tenant, account);
        var label = approver is null ? "" : LicenseSignInFold.Label(_reads.Read(tenant, now).Machines, approver.MachineName, approver.OsUser);
        var why = Sentence(approver, account, label, now);
        FileLog.Write($"[LicenseApproverReadiness] WhyNotReady: tenant={tenant.ToLogString()}, account={account}, "
                      + $"state={(approver is null ? "no-approver" : LicenseApproverHealth.StateOf(approver, now))}, ready={why is null}");
        return why;
    }

    /// <summary>The readiness sentence for one account: null when <paramref name="approver"/> can approve at
    /// <paramref name="nowUtc"/>, else what is wrong and the one thing to do, as one text.</summary>
    /// <param name="label">What to call the approver's machine (<see cref="LicenseSignInFold.Label"/>).</param>
    public static string? Sentence(LicenseApprover? approver, string accountEmail, string label, DateTime nowUtc)
    {
        if (approver is null)
            return $"{NoApproverText(accountEmail)} {NoApproverWhatToDo(accountEmail)}";
        var verdict = Verdict(approver, label, nowUtc);
        return verdict.WhatToDo is null ? null : $"{verdict.HealthText} {verdict.WhatToDo}";
    }

    public static string NoApproverText(string accountEmail) => $"No machine is set to approve sign-ins for {accountEmail}.";

    public static string NoApproverWhatToDo(string accountEmail) => $"Set an approver for {accountEmail} on the Licenses page.";

    /// <summary>One approver record's health at <paramref name="nowUtc"/>, as the state, the sentence and the one thing
    /// to do. <see cref="LicenseApproverHealthVerdict.WhatToDo"/> is null exactly when the record can approve.</summary>
    /// <param name="label">What to call the approver's machine (<see cref="LicenseSignInFold.Label"/>).</param>
    public static LicenseApproverHealthVerdict Verdict(LicenseApprover approver, string label, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(approver);
        var account = approver.AccountEmail;
        var profile = ProfileName(approver);
        var who = $"The approver for {account} (browser profile \"{profile}\" on {label})";
        var state = LicenseApproverHealth.StateOf(approver, nowUtc);
        return state switch
        {
            LicenseApproverStates.CanApprove => new(state, $"{who} can approve sign-ins; it was last checked {Clock(approver.CheckedAtUtc!.Value)}.", null),
            LicenseApproverStates.SignedOut => new(state, $"{who} is not signed in to claude.ai.",
                $"Open that browser on {label} and sign in to claude.ai as {account} once."),
            LicenseApproverStates.NeedsFreshSignIn => new(state, $"{who} needs a fresh claude.ai sign-in on the approval page.",
                $"Open that browser on {label} and sign in to claude.ai once."),
            LicenseApproverStates.WrongAccount => new(state, $"{who} is signed in to claude.ai as {approver.ShownAccount ?? "another account"}, not as {account}.",
                $"Open that browser on {label}, sign out of claude.ai and sign in as {account} once."),
            LicenseApproverStates.CheckFailed => new(state, $"{who} could not be checked{Because(approver.Detail)}",
                $"Make sure the browser profile \"{profile}\" opens on {label}; the check runs again within a day."),
            LicenseApproverStates.NotChecked => new(state, approver.CheckedAtUtc is { } at
                    ? $"{who} was last checked {Clock(at)}, more than {LicenseApproverContract.NotCheckedAfter.TotalHours:0} hours ago."
                    : $"{who} has never been checked.",
                $"Make sure the Director on {label} is running with license switching on; it checks its approvers once a day."),
            _ => throw new InvalidOperationException($"An approver record holds a state that is not a state word: {state}."),
        };
    }

    /// <summary>What to call the approver's browser profile to a person: the display name its last check saw, else its
    /// identifier.</summary>
    public static string ProfileName(LicenseApprover approver) => approver.BrowserProfileName ?? approver.BrowserProfile;

    // The Director's detail is a sentence from its own fixed words, checked when it was stored; it ends with a stop.
    private static string Because(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return ".";
        var text = detail.Trim();
        return $": {text}{(text.EndsWith('.') ? "" : ".")}";
    }

    private static string Clock(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
