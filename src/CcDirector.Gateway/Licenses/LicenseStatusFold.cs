using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE LICENSES PAGE ON ONE SCREEN (the owner's redesign, approved 8 November 2026): one status card that says whether
/// every machine is covered, a tile per license, the Machines table's "Ready for next?" column, and a short list of grey
/// to-dos. Every word is folded here; the Cockpit only draws it (CLAUDE.md rule 7).
///
/// THE CARD HAS ONE RULE, AND IT IS THE ALARM'S. It is red (or yellow) exactly when the one license alarm's conditions
/// (<see cref="LicenseCannotSwitchAlarm.Conditions"/>, handed in by <see cref="LicenseReadinessService.JudgePage"/> from the
/// same picture) are not empty, in that alarm's own tone, and green otherwise. There is no second copy of the rule here: a
/// machine at 80% with a ready next license, a license with no room, an approver that needs a sign-in - none of them turns
/// the card, because none of them raises the alarm.
///
/// TO-DOS ARE NEVER ALARMS. They are grouped so one cause is one line - every approver on one machine is one item, every
/// failed renewal on one machine is one item - and they live only in this answer: nothing here reaches the alarm count, the
/// phone or an email.
/// </summary>
public static class LicenseStatusFold
{
    /// <summary>
    /// Set <see cref="LicenseOverviewResponse.Verdict"/>, <see cref="LicenseOverviewResponse.Todos"/>, each account's role
    /// and tile sentence, and each machine's ready-for-next column. Call last: it reads the readiness, next-license, sign-in
    /// and approver fields the other folds set.
    /// </summary>
    /// <param name="cannotSwitch">The one license alarm's conditions on the same picture.</param>
    /// <param name="runsOut">For a license: when the forecast expects it to reach the switching threshold, or null.</param>
    /// <param name="weeklyPace">For a license: its weekly pace in percent an hour, as the forecast reads it, or null.</param>
    public static void Apply(LicenseOverviewResponse response, IReadOnlyList<LicenseAlarmCondition> cannotSwitch,
        Func<string, DateTime?> runsOut, Func<string, double?> weeklyPace, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(cannotSwitch);
        ArgumentNullException.ThrowIfNull(runsOut);
        ArgumentNullException.ThrowIfNull(weeklyPace);
        var alarmed = cannotSwitch.Where(c => c.MachineId is not null)
            .GroupBy(c => c.MachineId!.Value.ToString("N"))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var machine in response.Machines)
            ApplyMachine(machine, alarmed.GetValueOrDefault(machine.MachineId));

        var covering = response.Machines.Where(IsCovering).ToList();
        foreach (var account in response.Accounts)
        {
            ApplyAccount(account, covering, runsOut, nowUtc);
            account.PaceText = PaceText(weeklyPace(account.AccountEmail));
        }

        response.Todos = Todos(response);
        response.Verdict = response.IsEmpty ? null : Verdict(response, covering, cannotSwitch, runsOut, nowUtc);
        FileLog.Write($"[LicenseStatusFold] Apply: verdict={response.Verdict?.State ?? "none"}, notReady={alarmed.Count}, "
                      + $"todos={response.Todos.Count}, machines={response.Machines.Count}");
    }

    // A machine the status card speaks for: reporting, and on a license.
    private static bool IsCovering(LicenseMachineDto machine)
        => !IsSilent(machine) && machine.ActiveAccountEmail is not null;

    private static bool IsSilent(LicenseMachineDto machine)
        => machine.IsStale || machine.Status is LicenseMachineStatuses.NotReporting or LicenseMachineStatuses.ReportRefused;

    // ---- The Machines table's last column ----

    private static void ApplyMachine(LicenseMachineDto machine, LicenseAlarmCondition? alarm)
    {
        (machine.ReadyForNext, machine.ReadyForNextText, machine.ReadyForNextTone) = alarm is not null
            ? (false, NotReadyWords(machine), AlarmTone(alarm))
            : IsSilent(machine) ? (null, "Not reporting", LicenseTones.Neutral)
            : machine.ActiveAccountEmail is null ? (null, "On no license", LicenseTones.Neutral)
            : machine.Readiness switch
            {
                LicenseReadinessStates.Ready => (true, "Ready", LicenseTones.Good),
                // Not ready, but not running out: the minute check is fixing it, and it is no alarm. Never red.
                LicenseReadinessStates.NotReady => (false, NotReadyWords(machine), LicenseTones.Neutral),
                LicenseReadinessStates.NoRoom => ((bool?)null, "No other license has room", LicenseTones.Neutral),
                _ => ((bool?)null, "", LicenseTones.Neutral),
            };
    }

    private static string NotReadyWords(LicenseMachineDto machine) => machine.RecommendedSignInAction switch
    {
        LicenseNextLicenseActions.AddSpare => "Not signed in",
        LicenseNextLicenseActions.Renew => "Sign-in needs renewing",
        _ => "Not ready",
    };

    private static string AlarmTone(LicenseAlarmCondition alarm)
        => alarm.Tone == LicenseAlarmTones.Critical ? LicenseTones.Bad : LicenseTones.Warning;

    // ---- The license tiles ----

    /// <summary>The tile's pace: "+3.4% an hour" while the weekly use is rising; null when it is not, or not known. Rounded
    /// as the Forecast tab rounds it, so a rise too small to show as 0.1 is not shown at all.</summary>
    internal static string? PaceText(double? percentPerHour)
        => percentPerHour is { } perHour && Math.Round(perHour, 1) > 0
            ? $"+{perHour.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}% an hour"
            : null;

    private static void ApplyAccount(LicenseAccountDto account,
        IReadOnlyList<LicenseMachineDto> covering, Func<string, DateTime?> runsOut, DateTime nowUtc)
    {
        var on = covering.Count(m => m.ActiveAccountEmail == account.AccountEmail);
        if (on > 0)
        {
            account.Role = LicenseRoles.InUse;
            account.RoleText = $"In use - {Count(on, "machine")}";
            account.RoleTone = LicenseTones.Neutral;
            var at = runsOut(account.AccountEmail);
            account.TileText = at is { } when
                ? $"Switches at {Threshold} in about {LicenseDisplayFold.Span(when - nowUtc)}."
                : $"Switches at {Threshold}.";
            account.TileAtUtc = at;
            return;
        }

        var targeting = covering.Where(m => m.ReadinessTargetAccountEmail == account.AccountEmail).ToList();
        if (targeting.Count > 0)
        {
            var ready = targeting.Count(m => m.Readiness == LicenseReadinessStates.Ready);
            var missing = targeting.Where(m => m.Readiness != LicenseReadinessStates.Ready).Select(m => m.Label).ToList();
            account.Role = LicenseRoles.Next;
            account.RoleText = targeting.Count == 1
                ? (ready == 1 ? "Next - ready" : "Next - not ready")
                : ready == targeting.Count ? $"Next - ready on all {targeting.Count}" : $"Next - ready on {ready} of {targeting.Count}";
            account.RoleTone = missing.Count == 0 ? LicenseTones.Good : LicenseTones.Neutral;
            // The license the machines that move to it are on now: it takes over when that one switches.
            var from = targeting.GroupBy(m => m.ActiveAccountEmail!).OrderByDescending(g => g.Count()).First().Key;
            if (missing.Count == 0)
            {
                var at = runsOut(from);
                account.TileText = at is { } when
                    ? $"Takes over in about {LicenseDisplayFold.Span(when - nowUtc)}."
                    : $"Takes over when {from} switches.";
                account.TileAtUtc = at;
            }
            else
            {
                account.TileText = $"Missing on {Join(missing)}.";
                account.TileAtUtc = null;
            }
            return;
        }

        account.Role = LicenseRoles.Resting;
        account.RoleText = "Resting";
        account.RoleTone = LicenseTones.Neutral;
        // A resting license over 80% of its week is resting until it resets: say when it has room again.
        if (account.SevenDay is { Utilization: >= LicenseNextLicenseFold.WarnPercent, ResetsAtUtc: { } reset } && reset > nowUtc)
        {
            account.TileText = $"Not needed now. Free again in {LicenseDisplayFold.Span(reset - nowUtc)}.";
            account.TileAtUtc = reset;
        }
        else
        {
            account.TileText = "Not needed now.";
            account.TileAtUtc = null;
        }
    }

    // ---- The status card ----

    private static LicenseVerdictDto Verdict(LicenseOverviewResponse response, IReadOnlyList<LicenseMachineDto> covering,
        IReadOnlyList<LicenseAlarmCondition> cannotSwitch,
        Func<string, DateTime?> runsOut, DateTime nowUtc)
    {
        if (cannotSwitch.Count > 0)
            return NotReady(response, cannotSwitch);

        var verdict = new LicenseVerdictDto
        {
            State = LicenseVerdictStates.Covered,
            Tone = LicenseTones.Good,
            Headline = covering.Count switch
            {
                0 => "No machine is on a license.",
                1 => $"{covering[0].Label} is covered.",
                _ => $"All {covering.Count} machines are covered.",
            },
        };
        var primary = covering.GroupBy(m => m.ActiveAccountEmail!).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (primary is null)
        {
            verdict.Detail = "Nothing is running out, so nothing needs a next license.";
            return verdict;
        }
        var account = response.Accounts.FirstOrDefault(a => a.AccountEmail == primary.Key);
        var level = account is null ? primary.Key : $"{primary.Key} is at {Highest(account)}";
        var machines = primary.ToList();
        var target = machines.Select(m => m.ReadinessTargetAccountEmail).Where(t => t is not null)
            .GroupBy(t => t!).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var needing = machines.Any(m => m.NextLicenseState == LicenseNextLicenseStates.Ready);
        if (needing && target is not null)
        {
            var ready = machines.Count(m => m.ReadinessTargetAccountEmail == target && m.Readiness == LicenseReadinessStates.Ready);
            var when = runsOut(primary.Key) is { } at ? $" in about {LicenseDisplayFold.Span(at - nowUtc)}" : "";
            var where = ready == machines.Count ? (machines.Count == 1 ? "on it" : "on every machine") : $"on {ready} of {machines.Count} machines";
            verdict.Detail = $"{level} and switches to {target} at {Threshold}{when}. That license is ready {where}.";
        }
        else if (machines.Any(m => m.NextLicenseState == LicenseNextLicenseStates.NoRoom))
        {
            verdict.Detail = $"{level}. No other license has room now; the Forecast tab shows when one will.";
        }
        else
        {
            verdict.Detail = target is null
                ? $"{level}, so no machine needs a next license yet."
                : $"{level}, so no machine needs a next license yet. The next one is {target}.";
        }
        if (account is { CanSwitchNow: true })
            verdict.SwitchNowAccountEmail = account.AccountEmail;
        return verdict;
    }

    private static LicenseVerdictDto NotReady(LicenseOverviewResponse response, IReadOnlyList<LicenseAlarmCondition> cannotSwitch)
    {
        var first = cannotSwitch[0];
        var labels = cannotSwitch.Select(c => c.MachineName).Where(n => n is not null).Select(n => n!).Distinct().ToList();
        var machine = first.MachineId is { } id ? response.Machines.FirstOrDefault(m => m.MachineId == id.ToString("N")) : null;
        var verdict = new LicenseVerdictDto
        {
            State = LicenseVerdictStates.NotReady,
            // The alarm's own tone: red from the switching threshold, yellow below it.
            Tone = cannotSwitch.Any(c => c.Tone == LicenseAlarmTones.Critical) ? LicenseTones.Bad : LicenseTones.Warning,
            Headline = labels.Count <= 1
                ? $"{labels.FirstOrDefault() ?? "A machine"} has no next license ready."
                : $"{labels.Count} machines have no next license ready: {Join(labels)}.",
            Detail = first.Problem,
            ActionText = first.Action,
            Machine = machine is null ? null : new LicenseMachineRefDto
            {
                MachineId = machine.MachineId,
                MachineName = machine.MachineName,
                OsUser = machine.OsUser,
                Label = machine.Label,
            },
        };
        if (machine is { RecommendedAccountEmail: { } account, RecommendedSignInAction: { } action }
            && (action == LicenseNextLicenseActions.AddSpare ? machine.CanAddSpare : CanRenew(machine, account)))
        {
            verdict.Fix = new LicenseVerdictFixDto
            {
                Label = $"Fix: sign {machine.Label} in",
                Action = action,
                AccountEmail = account,
                MachineId = machine.MachineId,
            };
        }
        return verdict;
    }

    private static bool CanRenew(LicenseMachineDto machine, string account)
        => Holdings(machine).Any(h => h.AccountEmail == account && h.CanRenewNow);

    // ---- The to-dos ----

    private static List<LicenseTodoDto> Todos(LicenseOverviewResponse response)
    {
        var todos = new List<LicenseTodoDto>();

        // Approvers that cannot approve, one item per machine they are on.
        var stuck = response.Accounts.Where(a => a.Approver is not null && !a.Approver.CanApproveNow).ToList();
        foreach (var group in stuck.GroupBy(a => a.Approver!.Label))
        {
            var accounts = group.ToList();
            todos.Add(new LicenseTodoDto
            {
                Kind = LicenseTodoKinds.Approver,
                Text = accounts.Count == 1
                    ? $"Sign in to claude.ai once in the approver browser for {accounts[0].AccountEmail} on {group.Key}, so its new sign-ins can be approved."
                    : $"Sign in to claude.ai once in the {accounts.Count} approver browsers on {group.Key}, so new sign-ins can be approved.",
                MattersByUtc = Earliest(accounts.Select(a => a.SignInExpiry.ExpiresAtUtc)),
                Opens = LicenseTodoOpens.Approvers,
                ActionLabel = "Show me how",
            });
        }

        // Sign-ins that could not be renewed, one item per machine.
        foreach (var machine in response.Machines.Where(m => !IsSilent(m)))
        {
            var failed = Holdings(machine).Where(h => h.RenewalFailedAtUtc is not null).ToList();
            if (failed.Count == 0)
                continue;
            todos.Add(new LicenseTodoDto
            {
                Kind = LicenseTodoKinds.RenewalFailed,
                Text = failed.Count == 1
                    ? $"The sign-in for {failed[0].AccountEmail} on {machine.Label} could not be renewed."
                    : $"{failed.Count} sign-ins on {machine.Label} could not be renewed.",
                MattersByUtc = Earliest(failed.Select(h => h.SignInExpiresAtUtc)),
                Opens = LicenseTodoOpens.Machine,
                MachineId = machine.MachineId,
                ActionLabel = "Details",
            });
        }

        // Machines that stopped reporting: worth removing if they are gone.
        foreach (var machine in response.Machines.Where(IsSilent))
        {
            todos.Add(new LicenseTodoDto
            {
                Kind = LicenseTodoKinds.NotReporting,
                Text = $"{machine.Label} is not reporting. {machine.LastReportText}. Remove it here if it is gone.",
                MattersByUtc = null,
                Opens = LicenseTodoOpens.Machine,
                MachineId = machine.MachineId,
                ActionLabel = "Details",
            });
        }

        // Soonest first; the ones with no date last, in the order above.
        return todos.Select((t, i) => (t, i))
            .OrderBy(x => x.t.MattersByUtc ?? DateTime.MaxValue).ThenBy(x => x.i)
            .Select(x => x.t).ToList();
    }

    // ---- Words ----

    private static IEnumerable<LicenseMachineHoldingDto> Holdings(LicenseMachineDto machine)
        => machine.Active is null ? machine.Spares : machine.Spares.Prepend(machine.Active);

    private static DateTime? Earliest(IEnumerable<DateTime?> times)
    {
        var known = times.Where(t => t is not null).Select(t => t!.Value).ToList();
        return known.Count == 0 ? null : known.Min();
    }

    // The fuller of the license's two windows, in words: "92% of its weekly limit".
    private static string Highest(LicenseAccountDto account)
    {
        var week = account.SevenDay;
        var five = account.FiveHour;
        return five.Utilization is { } f && (week.Utilization is not { } w || f > w)
            ? $"{five.UtilizationText} of its 5-hour window"
            : week.Utilization is not null ? $"{week.UtilizationText} of its weekly limit" : "a use it has not reported";
    }

    private static string Threshold
        => LicenseDecisionEngine.ThresholdPercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private static string Count(int number, string unit) => number == 1 ? $"1 {unit}" : $"{number} {unit}s";

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
