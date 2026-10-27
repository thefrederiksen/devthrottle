using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>What an approver record's health means, as finished words: the state, the sentence saying it, and the one
/// thing the owner must do (null when there is nothing to do).</summary>
public sealed record LicenseApproverHealthVerdict(string State, string HealthText, string? WhatToDo);

/// <summary>Whether the Director of an approver's machine can check it now, which decides what a not-checked record's
/// sentence tells the owner to do.</summary>
public enum LicenseApproverReach
{
    /// <summary>No Director reports licenses for that machine and user: none ever has, or the one that did is connected
    /// and has sent no report for <see cref="LicenseStore.SilentAfter"/> - license switching is off there now. Approving,
    /// and the check, need license switching on.</summary>
    NotReporting,

    /// <summary>A Director reported for that machine, and it is not connected now.</summary>
    NotConnected,

    /// <summary>The Director that reports for that machine is connected and reporting: it checks a not-checked record within
    /// <see cref="LicenseApproverContract.DueCheckWithin"/>, with nothing for the owner to do.</summary>
    Connected,
}

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
    private readonly Func<TenantId, string, bool> _isConnected;
    private readonly Func<DateTime> _nowUtc;

    /// <param name="isConnected">Whether a Director of the tenant, by its identifier, is connected now: a record not yet
    /// checked says "it is checked within minutes" only while its machine's Director is.</param>
    public LicenseApproverReadiness(LicenseApproverStore approvers, LicenseReadModel reads, Func<TenantId, string, bool> isConnected,
        Func<DateTime> nowUtc)
    {
        _approvers = approvers ?? throw new ArgumentNullException(nameof(approvers));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
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
        var label = "";
        var reach = LicenseApproverReach.NotReporting;
        if (approver is not null)
        {
            var machines = _reads.Read(tenant, now).Machines;
            label = LicenseSignInFold.Label(machines, approver.MachineName, approver.OsUser);
            reach = ReachOf(approver, machines, directorId => _isConnected(tenant, directorId), now);
        }
        var why = Sentence(approver, account, label, reach, now);
        FileLog.Write($"[LicenseApproverReadiness] WhyNotReady: tenant={tenant.ToLogString()}, account={account}, "
                      + $"state={(approver is null ? "no-approver" : LicenseApproverHealth.StateOf(approver, now))}, ready={why is null}");
        return why;
    }

    /// <summary>The readiness sentence for one account: null when <paramref name="approver"/> can approve at
    /// <paramref name="nowUtc"/>, else what is wrong and the one thing to do, as one text.</summary>
    /// <param name="label">What to call the approver's machine (<see cref="LicenseSignInFold.Label"/>).</param>
    /// <param name="reach">Whether the approver's machine's Director can check it now (<see cref="ReachOf"/>).</param>
    public static string? Sentence(LicenseApprover? approver, string accountEmail, string label, LicenseApproverReach reach, DateTime nowUtc)
    {
        if (approver is null)
            return $"{NoApproverText(accountEmail)} {NoApproverWhatToDo(accountEmail)}";
        var verdict = Verdict(approver, label, reach, nowUtc);
        return verdict.WhatToDo is null ? null : $"{verdict.HealthText} {verdict.WhatToDo}";
    }

    public static string NoApproverText(string accountEmail) => $"No machine is set to approve sign-ins for {accountEmail}.";

    public static string NoApproverWhatToDo(string accountEmail) => $"Set an approver for {accountEmail} on the Licenses page.";

    /// <summary>One approver record's health at <paramref name="nowUtc"/>, as the state, the sentence and the one thing
    /// to do. <see cref="LicenseApproverHealthVerdict.WhatToDo"/> is null exactly when the record can approve.</summary>
    /// <param name="label">What to call the approver's machine (<see cref="LicenseSignInFold.Label"/>).</param>
    /// <param name="reach">Whether the approver's machine's Director can check it now (<see cref="ReachOf"/>). It changes
    /// only what a not-checked record tells the owner to do: never "start the Director" while it is running and
    /// connected.</param>
    public static LicenseApproverHealthVerdict Verdict(LicenseApprover approver, string label, LicenseApproverReach reach, DateTime nowUtc)
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
                NotCheckedWhatToDo(label, reach)),
            _ => throw new InvalidOperationException($"An approver record holds a state that is not a state word: {state}."),
        };
    }

    /// <summary>Whether the Director of <paramref name="approver"/>'s machine can check it now: whether a Director
    /// reports licenses for that machine and user, and whether that Director is connected. A connected Director that has
    /// gone silent for longer than <see cref="LicenseStore.SilentAfter"/> has license switching off and runs no check,
    /// so it is not reporting: "it checks it within minutes" is said only of a Director that will.</summary>
    /// <param name="machines">Every machine of the account that has reported.</param>
    /// <param name="isConnected">Whether a Director of the account, by its identifier, is connected now.</param>
    public static LicenseApproverReach ReachOf(LicenseApprover approver, IReadOnlyList<LicenseMachineView> machines, Func<string, bool> isConnected,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(isConnected);
        var machine = machines.FirstOrDefault(m => approver.NamesMachine(m.MachineName, m.OsUser));
        if (machine is null)
            return LicenseApproverReach.NotReporting;
        if (!isConnected(machine.DirectorId))
            return LicenseApproverReach.NotConnected;
        return nowUtc - machine.LastReportAtUtc > LicenseStore.SilentAfter ? LicenseApproverReach.NotReporting : LicenseApproverReach.Connected;
    }

    // The check comes within minutes of the Director connecting (LicenseApproverAssignment.CheckDue), so the owner is told
    // to start it only when it is not there, and told there is nothing to do when it is.
    private static string NotCheckedWhatToDo(string label, LicenseApproverReach reach)
    {
        var within = $"{LicenseApproverContract.DueCheckWithin.TotalMinutes:0} minutes";
        return reach switch
        {
            LicenseApproverReach.Connected => $"Nothing to do: the Director on {label} is connected and checks it within {within}.",
            LicenseApproverReach.NotConnected => $"Start the Director on {label}; it checks this approver within {within} of connecting.",
            LicenseApproverReach.NotReporting => $"Make sure the Director on {label} is running with license switching on, which approving needs; it checks this approver within {within} of connecting.",
            _ => throw new InvalidOperationException($"An approver reach that is not one: {reach}."),
        };
    }

    /// <summary>
    /// Why the approver checks found no approver, from the checks that failed within
    /// <see cref="LicenseApproverContract.NotCheckedAfter"/> on <paramref name="machineName"/> (or on every machine when it
    /// is null): the latest one's profile, machine, time and the checking Director's sentence. Null when none failed.
    /// Mission "Licenses - Loud When Stranded", step 4: from 19 to 26 October 2026 every check on SOREN_NORTH failed, and no
    /// sentence anywhere said so.
    /// </summary>
    public static string? FailedChecksText(IReadOnlyList<LicenseApproverFailedCheck> failed, DateTime nowUtc, string? machineName = null,
        string? osUser = null)
    {
        ArgumentNullException.ThrowIfNull(failed);
        var recent = failed
            .Where(f => nowUtc - f.CheckedAtUtc <= LicenseApproverContract.NotCheckedAfter)
            .Where(f => machineName is null || f.NamesMachine(machineName, osUser ?? ""))
            .OrderByDescending(f => f.CheckedAtUtc).ToList();
        if (recent.Count == 0)
            return null;
        var latest = recent[0];
        var profile = latest.BrowserProfileName ?? latest.BrowserProfile;
        var what = recent.Count == 1
            ? $"The approver check of browser profile \"{profile}\" on {latest.MachineName} could not run at {Clock(latest.CheckedAtUtc)}"
            : $"{recent.Count} approver checks could not run, the latest of browser profile \"{profile}\" on {latest.MachineName} at {Clock(latest.CheckedAtUtc)}";
        return what + Because(latest.Detail);
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
