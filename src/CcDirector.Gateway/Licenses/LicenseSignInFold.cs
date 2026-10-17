using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE SIGN-IN VERDICTS OF THE LICENSES PAGE, FOLDED ONCE (License Switching, contract section 8; CLAUDE.md rule 7,
/// the client is dumb). Who approves an account's sign-ins and whether that machine can approve now; whether "Add a
/// spare" and "Renew now" are offered for a machine and the sentence saying why not; and what a sign-in row of the log
/// means - all decided here and sent as finished words and booleans, beside what <see cref="LicenseDisplayFold"/> folds.
///
/// THE PAGE AND THE ROUTE SAY THE SAME SENTENCE. Every sentence a route refuses a sign-in with is built here, and the
/// overview's "why not" is the same function called on the same facts, so the page never offers what the route
/// refuses with a different reason.
///
/// A MACHINE IS CALLED WHAT THE OVERVIEW CALLS IT: its name, with the operating system user in brackets when another
/// machine has the same name. Pure: facts go in, an answer comes out.
/// </summary>
public static class LicenseSignInFold
{
    public const string NoApproversText =
        "No machine is set to approve sign-ins for any account. A machine that has its own browser profile for an account approves its sign-ins there.";

    public const string NoApproverForAccountText =
        "No machine is set to approve sign-ins for this account. A machine that has its own browser profile for it approves there.";

    // ----- the sentences a start is refused with, and the page's "why not" -----

    public static string DirectorOfflineText(string label)
        => $"The Director on {label} is not connected, so a sign-in cannot be started there.";

    /// <summary>Why the browser profiles of a machine cannot be listed while an approver is chosen.</summary>
    public static string BrowserProfilesOfflineText(string label)
        => $"{label}'s Director is not connected, so its browser profiles cannot be listed.";

    public static string AlreadyRunningText(string label, string accountEmail)
        => $"A sign-in for {accountEmail} on {label} is already running.";

    public static string AlreadyHeldText(string label, string accountEmail)
        => $"{label} already holds a sign-in for {accountEmail}. Use Renew now to renew it.";

    public static string NotHeldText(string label, string accountEmail)
        => $"{label} holds no sign-in for {accountEmail}. Use Add a spare to sign it in there.";

    /// <summary>The sentence a sign-in row carries from the moment it is written until its Director answers.
    /// An automatic one says so, and why: no person asked for it.</summary>
    public static string AskedText(string label, string accountEmail, string reason) => reason switch
    {
        LicenseSignInReasons.AutomaticSpare =>
            $"Started automatically: {label} holds no sign-in for {accountEmail}, the account it would switch to next, so the Director on {label} was asked to sign in to it as a new spare.",
        LicenseSignInReasons.AutomaticRenewal =>
            $"Started automatically: the sign-in {label} holds for {accountEmail}, the account it would switch to next, cannot be switched to, so the Director on {label} was asked to renew it.",
        LicenseSignInReasons.Renewal => $"Asked the Director on {label} to renew the sign-in for {accountEmail}.",
        _ => $"Asked the Director on {label} to sign in to {accountEmail} as a new spare.",
    };

    /// <summary>The sentence of a row that was settled from the machine's report because no answer came back.</summary>
    public static string SettledFromReportText(string label, string accountEmail, string? reason, bool made)
    {
        const string how = "Settled from the machine's report: no answer to this sign-in came back";
        if (LicenseSignInReasons.IsRenewal(reason))
            return made
                ? $"{how}, and the report shows the sign-in for {accountEmail} on {label} was renewed."
                : $"{how}, and the report does not show a newer sign-in for {accountEmail} on {label}.";
        return made
            ? $"{how}, and the report shows {label} holds a sign-in for {accountEmail}."
            : $"{how}, and the report does not show a sign-in for {accountEmail} on {label}.";
    }

    /// <summary>The sentence of a row that was ended because its machine has sent no report for a day.</summary>
    public static string MachineNotSeenText(string label, string accountEmail, string? reason)
        => LicenseSignInReasons.IsRenewal(reason)
            ? $"Ended without an answer: {label} has not been seen for a day, so it is not known whether the sign-in for {accountEmail} was renewed. Renew now can be asked for again when the machine is back."
            : $"Ended without an answer: {label} has not been seen for a day, so it is not known whether it signed in to {accountEmail}. Add a spare can be asked for again when the machine is back.";

    /// <summary>What a machine is called among <paramref name="machines"/>: its name, with the user in brackets when
    /// another machine has the same name.</summary>
    public static string Label(IReadOnlyList<LicenseMachineView> machines, string machineName, string osUser)
    {
        ArgumentNullException.ThrowIfNull(machines);
        var sameName = machines.Where(m => string.Equals(m.MachineName, machineName, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.OsUser.ToLowerInvariant())
            .Append(osUser.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Count();
        return sameName > 1 ? $"{machineName} ({osUser})" : machineName;
    }

    // ----- who approves -----

    /// <summary>One approver as the page shows it: the machine, the browser profile, and whether a sign-in can be
    /// approved there now.</summary>
    /// <param name="machines">Every machine of the account that has reported.</param>
    /// <param name="isConnected">Whether a Director of this account, by its identifier, is connected now.</param>
    /// <param name="nowUtc">The moment the record's health is judged at (the not-checked rule).</param>
    public static LicenseApproverDto Approver(LicenseApprover approver, IReadOnlyList<LicenseMachineView> machines, Func<string, bool> isConnected,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(isConnected);
        var machine = machines.FirstOrDefault(m => string.Equals(m.MachineName, approver.MachineName, StringComparison.OrdinalIgnoreCase)
                                                   && string.Equals(m.OsUser, approver.OsUser, StringComparison.OrdinalIgnoreCase));
        var label = Label(machines, machine?.MachineName ?? approver.MachineName, machine?.OsUser ?? approver.OsUser);
        var who = $"Sign-ins for {approver.AccountEmail} are approved on {label}, in the browser profile {LicenseApproverReadiness.ProfileName(approver)}.";
        var health = LicenseApproverReadiness.Verdict(approver, label, nowUtc);
        // A machine that cannot be reached is said first: nothing about the profile matters until its Director is there.
        // Then the profile's own health, so "Ready" is never shown beside a profile that cannot approve.
        var (status, statusText, tone, text) = machine is null
            ? (LicenseApproverStatuses.NeverReported, "Not reporting yet", LicenseTones.Warning,
                $"{who} No Director on {label} has reported to this account, so no sign-in can be approved there yet.")
            : !isConnected(machine.DirectorId)
                ? (LicenseApproverStatuses.NotConnected, "Not connected", LicenseTones.Warning,
                    $"{who} The Director on {label} is not connected, so no sign-in can be approved there now.")
                : health.WhatToDo is null
                    ? (LicenseApproverStatuses.Ready, "Ready", LicenseTones.Good, who)
                    : (LicenseApproverStatuses.CannotApprove, health.State == LicenseApproverStates.NotChecked ? "Not checked" : "Cannot approve",
                        LicenseTones.Warning, $"{health.HealthText} {health.WhatToDo}");
        return new LicenseApproverDto
        {
            AccountEmail = approver.AccountEmail,
            MachineName = machine?.MachineName ?? approver.MachineName,
            OsUser = machine?.OsUser ?? approver.OsUser,
            BrowserProfile = approver.BrowserProfile,
            MachineId = machine?.MachineId.ToString("N"),
            Label = label,
            CanApproveNow = status == LicenseApproverStatuses.Ready,
            Status = status,
            StatusText = statusText,
            StatusTone = tone,
            Text = text,
            BrowserProfileName = approver.BrowserProfileName,
            Source = approver.Source,
            State = health.State,
            CheckedAtUtc = approver.CheckedAtUtc,
            HealthText = health.HealthText,
            WhatToDo = health.WhatToDo,
        };
    }

    /// <summary>Every approver of the account, as the approvers route answers.</summary>
    public static LicenseApproversResponse Approvers(IReadOnlyList<LicenseApprover> approvers, IReadOnlyList<LicenseMachineView> machines,
        Func<string, bool> isConnected, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(approvers);
        FileLog.Write($"[LicenseSignInFold] Approvers: approvers={approvers.Count}, machines={machines.Count}");
        return new LicenseApproversResponse
        {
            AsOfUtc = Utc(nowUtc),
            IsEmpty = approvers.Count == 0,
            EmptyText = approvers.Count == 0 ? NoApproversText : null,
            Approvers = approvers.OrderBy(a => a.AccountEmail, StringComparer.Ordinal).Select(a => Approver(a, machines, isConnected, nowUtc)).ToList(),
        };
    }

    // ----- the overview -----

    /// <summary>
    /// Fill in the sign-in verdicts of an overview <see cref="LicenseDisplayFold.Overview"/> has folded: on each
    /// account who approves its sign-ins; on each machine whether "Add a spare" is offered and the sign-ins it has not
    /// finished; on each sign-in a machine holds whether "Renew now" is offered.
    /// </summary>
    /// <param name="machines">The machines the overview was folded from.</param>
    /// <param name="openSignIns">The account's sign-in rows that have not ended (<see cref="LicenseSignInLog.ListOpen"/>).</param>
    public static void Apply(LicenseOverviewResponse overview, IReadOnlyList<LicenseMachineView> machines, IReadOnlyList<LicenseApprover> approvers,
        IReadOnlyList<LicenseSwitchLogEntity> openSignIns, Func<string, bool> isConnected, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(approvers);
        ArgumentNullException.ThrowIfNull(openSignIns);
        ArgumentNullException.ThrowIfNull(isConnected);
        FileLog.Write($"[LicenseSignInFold] Apply: accounts={overview.Accounts.Count}, machines={overview.Machines.Count}, approvers={approvers.Count}, openSignIns={openSignIns.Count}");

        foreach (var account in overview.Accounts)
        {
            var approver = approvers.FirstOrDefault(a => a.AccountEmail == account.AccountEmail);
            account.Approver = approver is null ? null : Approver(approver, machines, isConnected, nowUtc);
            account.ApproverText = account.Approver?.Text ?? NoApproverForAccountText;
            account.ApproverTone = account.Approver?.StatusTone ?? LicenseTones.Neutral;
        }

        foreach (var dto in overview.Machines)
        {
            var machine = machines.FirstOrDefault(m => m.MachineId.ToString("N") == dto.MachineId);
            if (machine is null)
            {
                // A machine known only from its refused reports (LicenseDisplayFold.RefusedMachine): it holds nothing
                // the Gateway has accepted, so nothing can be started on it.
                if (dto.Status != LicenseMachineStatuses.ReportRefused)
                    throw new InvalidOperationException($"The overview lists machine {dto.MachineId}, which is neither a machine of the picture nor a refused one.");
                dto.CanAddSpare = false;
                dto.AddSpareText = $"A spare can be added to {dto.Label} once a report from it is accepted.";
                continue;
            }
            var connected = isConnected(machine.DirectorId);
            var running = openSignIns.Where(r => r.MachineId == machine.MachineId).OrderBy(r => r.CreatedAtUtc).ToList();
            dto.CanAddSpare = connected;
            dto.AddSpareText = connected
                ? $"Signs {dto.Label} in to another account, to be held there as a spare."
                : DirectorOfflineText(dto.Label);
            dto.DirectorId = machine.DirectorId;
            dto.CanListBrowserProfiles = connected;
            dto.BrowserProfilesText = connected
                ? $"The browser profiles of {dto.Label}'s Director."
                : BrowserProfilesOfflineText(dto.Label);
            dto.SignInsInProgress = running.Select(r => new LicenseSignInInProgressDto
            {
                SignInId = r.SwitchId ?? "",
                AccountEmail = r.ToAccountEmail,
                Text = InProgressText(r, dto.Label),
            }).ToList();

            foreach (var holding in new[] { dto.Active }.Concat(dto.Spares).OfType<LicenseMachineHoldingDto>())
            {
                var alreadyRunning = running.Any(r => r.ToAccountEmail == holding.AccountEmail);
                holding.CanRenewNow = connected && !alreadyRunning;
                holding.RenewNowText = !connected ? DirectorOfflineText(dto.Label)
                    : alreadyRunning ? AlreadyRunningText(dto.Label, holding.AccountEmail)
                    : $"Signs {dto.Label} in to {holding.AccountEmail} again, replacing the sign-in it holds.";
            }
        }
    }

    private static string InProgressText(LicenseSwitchLogEntity row, string label) => row.State == LicenseLogStates.Unknown
        ? $"A sign-in for {row.ToAccountEmail} was sent to {label} and no answer has come back yet."
        : LicenseSignInReasons.IsRenewal(row.Reason)
            ? $"{label} is renewing its sign-in for {row.ToAccountEmail}."
            : $"{label} is signing in to {row.ToAccountEmail}.";

    // ----- the log -----

    /// <summary>One sign-in row of the log in words. <paramref name="label"/> is what the row's machine is called.</summary>
    public static LicenseLogEntryDto LogEntry(LicenseSwitchLogEntity row, string label)
    {
        ArgumentNullException.ThrowIfNull(row);
        var renewal = LicenseSignInReasons.IsRenewal(row.Reason);
        var account = row.ToAccountEmail;
        var message = string.IsNullOrWhiteSpace(row.Message) ? null : row.Message;
        var (stateText, stateTone, summary) = row.State switch
        {
            LicenseLogStates.Pending => ("Signing in", LicenseTones.Neutral,
                renewal ? $"{label} is renewing its sign-in for {account}." : $"{label} is signing in to {account}."),
            LicenseLogStates.Unknown => ("Sent, no answer yet", LicenseTones.Warning,
                $"A sign-in for {account} was sent to {label} and no answer has come back yet."),
            LicenseSignInOutcomes.Added => ("Signed in", LicenseTones.Good, $"{label} was signed in to {account} and holds it as a spare."),
            LicenseSignInOutcomes.Renewed => ("Renewed", LicenseTones.Good, $"The sign-in for {account} on {label} was renewed."),
            LicenseSignInOutcomes.Failed => ("Failed", LicenseTones.Bad,
                renewal ? $"The sign-in for {account} on {label} was not renewed." : $"{label} was not signed in to {account}."),
            // A state this build does not know: what happened is not known here, so nothing is claimed.
            _ => (string.IsNullOrWhiteSpace(row.State) ? "Recorded" : row.State, LicenseTones.Neutral,
                message ?? $"{label}: a sign-in for {account}, in a state this Gateway does not know ({row.State})."),
        };
        return new LicenseLogEntryDto
        {
            EntryId = row.Id.ToString("N"),
            Kind = row.Kind,
            KindText = "Sign-in",
            AtUtc = Utc(row.CreatedAtUtc),
            MachineId = row.MachineId == Guid.Empty ? null : row.MachineId.ToString("N"),
            MachineName = row.MachineName,
            OsUser = row.OsUser,
            Label = label,
            FromAccountEmail = row.FromAccountEmail,
            ToAccountEmail = account,
            Why = row.Reason,
            WhyText = row.Reason switch
            {
                LicenseSignInReasons.Manual => "Add a spare",
                LicenseSignInReasons.Renewal => "Renew now",
                LicenseSignInReasons.AutomaticSpare => "Automatic: a new spare",
                LicenseSignInReasons.AutomaticRenewal => "Automatic: a renewal",
                null => "",
                var other => other,
            },
            State = row.State,
            StateText = stateText,
            StateTone = stateTone,
            IsOpen = LicenseLogStates.IsOpen(row.State),
            ReasonCode = row.ReasonCode,
            ReasonText = ReasonText(row.ReasonCode),
            Message = message,
            Summary = summary,
            SendCount = row.SendCount,
            LastSentAtUtc = UtcOrNull(row.LastSentAtUtc),
            // The row keeps another time in that column (LicenseSignInLog.SignedInBefore): it is not when anything started.
            StartedOnMachineAtUtc = null,
            FinishedOnMachineAtUtc = UtcOrNull(row.DirectorFinishedAtUtc),
            ExchangeMilliseconds = null,
            FinishedAtUtc = UtcOrNull(row.FinishedAtUtc),
            TimingText = null,
            VerifiedByUsageRead = null,
            VerifiedText = null,
        };
    }

    /// <summary>The sentence for a sign-in row's reason word; the word itself when this build does not know it. A
    /// sign-in has its own sentences because one word, "expired", means something else on a switch.</summary>
    public static string? ReasonText(string? reasonCode) => reasonCode switch
    {
        null or "" => null,
        LicenseRenewalErrors.LoginFailed => "The sign-in did not start on the machine, or ended without a sign-in.",
        LicenseRenewalErrors.ApprovalFailed => "The approval in the browser did not complete.",
        LicenseRenewalErrors.WrongAccount => "The approving browser profile is signed in to claude.ai as another account.",
        LicenseRenewalErrors.BrowserNotSignedIn => "The approving browser profile is not signed in to claude.ai.",
        LicenseRenewalErrors.NeedsFreshSignIn => "The approving browser profile needs a fresh claude.ai sign-in: the approval page asked for one.",
        LicenseRenewalErrors.NoApprover => "No browser profile is set to approve this account, on the machine or on another.",
        LicenseRenewalErrors.ApproverOffline => "The machine set to approve this account had no Director connected.",
        LicenseRenewalErrors.Expired => "The sign-in ran out of time: it reached the machine too late, or no approval came back before its deadline.",
        LicenseRenewalErrors.StoreRefused => "The machine's sign-in store would not take the sign-in.",
        LicenseSwitchReasonCodes.NotKeyHolder => "The Director that answered does not hold the machine's sign-ins.",
        LicenseGatewayReasonCodes.DirectorOffline => "The machine's Director was not connected.",
        LicenseGatewayReasonCodes.DirectorError => "The machine's Director answered with an error.",
        _ => reasonCode,
    };

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    private static DateTime? UtcOrNull(DateTime? value) => value is { } v ? Utc(v) : null;
}
