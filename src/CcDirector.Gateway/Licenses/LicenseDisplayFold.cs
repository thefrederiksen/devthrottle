using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE LICENSES PAGE'S VERDICTS, FOLDED ONCE (License Switching; CLAUDE.md rule 7, the client is dumb). Everything the
/// Licenses page of the Cockpit shows that is a judgement - the status of an account, the level of a window, how old
/// a reading is, the expiry of a sign-in, whether Switch now is offered and why not, what a log row's state means -
/// is decided here and sent as finished words and booleans. The page lays them out; it never compares a percentage
/// with the threshold or a time with now. A new status is one edit here, never a branch in a view.
///
/// It decides nothing the engine decides. Whether a window is at the limit, whether an account has room, whether a
/// reading is stale, which target Switch now would take and why a target is refused all come from
/// <see cref="LicenseDecisionEngine"/> on the same <see cref="LicenseSnapshot"/> the minute check judges, so the page
/// cannot say "has room" about an account the engine would not switch to. Pure: a picture goes in, an answer comes out.
///
/// AN ACCOUNT HAS ONE STATUS, and what the owner must act on outranks what the Gateway handles by itself:
/// <list type="number">
/// <item><c>not-held</c> - no machine holds it; nothing else can be said of it.</item>
/// <item><c>sign-in-needed</c> - a sign-in for it has expired or was refused. Only the owner can fix that, and it is
/// also WHY its numbers go stale, so it comes before stale.</item>
/// <item><c>stale</c> - the numbers cannot be trusted, so neither "at limit" nor "has room" may be said.</item>
/// <item><c>at-limit</c> - a fresh reading shows a window at or over the threshold.</item>
/// <item><c>sign-in-expires-soon</c> - still working, and the owner has days to renew it.</item>
/// <item><c>in-use</c>, then <c>has-room</c>, then <c>use-not-known</c>.</item>
/// </list>
/// </summary>
public static class LicenseDisplayFold
{
    /// <summary>The log kind the minute check writes when no account had room. Spelled here as well as where it is
    /// written, because this fold must read the row whether or not that code is in the same build.</summary>
    public const string NoRoomKind = "no-room";

    public const string NothingReportedText =
        "No machine has reported its Claude accounts yet. They appear here once a Director with license switching turned on has connected.";

    public const string EmptyLogText =
        "Nothing has been logged yet. Every switch, and every sign-in added to or removed from a machine, is listed here.";

    public const string NoSwitchExpectedText = "No switch is expected.";

    // ----- the overview -----

    /// <summary>Every account and every machine, as the Licenses page shows them.</summary>
    public static LicenseOverviewResponse Overview(LicensePicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var now = picture.Snapshot.NowUtc;
        FileLog.Write($"[LicenseDisplayFold] Overview: accounts={picture.Overview.Accounts.Count}, machines={picture.Overview.Machines.Count}");

        var empty = IsEmpty(picture.Overview);
        var response = new LicenseOverviewResponse
        {
            AsOfUtc = Utc(now),
            IsEmpty = empty,
            EmptyText = empty ? NothingReportedText : null,
            Accounts = picture.Overview.Accounts.Select(a => Account(a, picture)).ToList(),
            Machines = picture.Overview.Machines.Select(m => Machine(m, picture.OpenSwitches, now)).ToList(),
        };
        FileLog.Write($"[LicenseDisplayFold] Overview: empty={empty}, canSwitchNow={response.Accounts.Count(a => a.CanSwitchNow)}");
        return response;
    }

    /// <summary>One account: its numbers, its one status, and what Switch now would do with it.</summary>
    public static LicenseAccountDto Account(LicenseAccountView view, LicensePicture picture)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(picture);
        var snapshot = picture.Snapshot;
        var now = snapshot.NowUtc;
        var reading = snapshot.Accounts.First(r => r.AccountEmail == view.AccountEmail);
        var held = picture.Overview.Machines
            .SelectMany(m => m.Holdings.Where(h => h.AccountEmail == view.AccountEmail).Select(h => (Machine: m, Holding: h)))
            .ToList();

        var dto = new LicenseAccountDto
        {
            AccountEmail = view.AccountEmail,
            Plan = view.Plan,
            PlanText = PlanText(view.Plan),
            RateLimitTier = view.RateLimitTier,
            IsInUse = view.ActiveOnMachines.Count > 0,
            FiveHour = Window(reading.FiveHour, now),
            SevenDay = Window(reading.SevenDay, now),
            IsStale = view.IsStale,
            ReadAtUtc = view.Freshest is { } read ? Utc(read.JudgedReadAtUtc) : null,
            ReadAgeText = view.Freshest is { } freshest
                ? $"Read {Ago(now - freshest.JudgedReadAtUtc)} on {freshest.MachineName}"
                : "Never read",
            ReadError = view.Freshest?.ReadError,
            ReadErrorText = ReadErrorText(view.Freshest?.ReadError, view.Freshest?.ReadErrorDetail),
            ExtraUsage = ExtraUsage(view.Freshest?.ExtraUsageEnabled, view.Freshest?.ExtraUsageUtilization),
            ActiveOnMachines = view.ActiveOnMachines.ToList(),
            SpareOnMachines = view.SpareOnMachines.ToList(),
        };
        ApplyStatus(dto, view, reading, held, now);
        ApplySwitchNow(dto, snapshot);
        return dto;
    }

    private static void ApplyStatus(LicenseAccountDto dto, LicenseAccountView view, LicenseAccountReading reading,
        IReadOnlyList<(LicenseMachineView Machine, LicenseHoldingView Holding)> held, DateTime now)
    {
        var expired = held.Where(h => h.Holding.ExpiryVerdict == LicenseSignInExpiry.Expired).ToList();
        var refused = held.Where(h => h.Holding.ReadError is LicenseReadErrors.Unauthorized or LicenseReadErrors.RefreshFailed).ToList();
        var expiringSoon = held.Where(h => h.Holding.ExpiryVerdict == LicenseSignInExpiry.ExpiresSoon).ToList();
        var tired = TiredWindows(reading, now);

        if (held.Count == 0)
        {
            Status(dto, LicenseAccountStatuses.NotHeld, "Not on any machine", LicenseTones.Neutral,
                "No machine holds a sign-in for this account any more.");
        }
        else if (expired.Count > 0 || refused.Count > 0)
        {
            var machines = Names(expired.Concat(refused).Select(h => h.Machine.MachineName).Distinct(StringComparer.Ordinal).ToList());
            var what = refused.Count == 0 ? "has expired" : expired.Count == 0 ? "was refused by the provider" : "has expired or was refused by the provider";
            Status(dto, LicenseAccountStatuses.SignInNeeded, "Sign-in needed", LicenseTones.Bad,
                $"The sign-in on {machines} {what}. This account needs a new sign-in there.");
        }
        else if (view.IsStale)
        {
            Status(dto, LicenseAccountStatuses.Stale, "Stale", LicenseTones.Warning,
                view.Freshest is { } freshest
                    ? $"The latest reading is {Span(now - freshest.JudgedReadAtUtc)} old, so its use now is not known."
                    : "No usage reading has arrived for this account yet.");
        }
        else if (tired.Count > 0)
        {
            Status(dto, LicenseAccountStatuses.AtLimit, "At limit", LicenseTones.Bad,
                "At " + string.Join(" and ", tired.Select(w => $"{Percent(w.Reading.Utilization)} of its {WindowLabel(w.Name)}, which {ResetsPhrase(w.Reading.ResetsAtUtc, now)}")) + ".");
        }
        else if (expiringSoon.Count > 0)
        {
            var soonest = expiringSoon.OrderBy(h => h.Holding.SignInExpiresAtUtc).First();
            Status(dto, LicenseAccountStatuses.SignInExpiresSoon, "Sign-in expires soon", LicenseTones.Warning,
                $"The sign-in on {soonest.Machine.MachineName} expires in {Span(soonest.Holding.SignInExpiresAtUtc!.Value - now)}.");
        }
        else if (dto.IsInUse)
        {
            Status(dto, LicenseAccountStatuses.InUse, "In use", LicenseTones.Good, $"In use on {Names(view.ActiveOnMachines)}.");
        }
        else if (LicenseDecisionEngine.HasRoom(reading))
        {
            Status(dto, LicenseAccountStatuses.HasRoom, "Has room", LicenseTones.Good,
                $"Not in use. Held as a spare on {Names(view.SpareOnMachines)}.");
        }
        else
        {
            Status(dto, LicenseAccountStatuses.UseNotKnown, "Use not known", LicenseTones.Warning,
                reading.FiveHour is null || reading.SevenDay is null
                    ? "Its use now is not known: the latest reading carried no number for a window."
                    : "Its use now is not known: a window has reset since the latest reading.");
        }
    }

    private static void Status(LicenseAccountDto dto, string status, string text, string tone, string detail)
    {
        dto.Status = status;
        dto.StatusText = text;
        dto.StatusTone = tone;
        dto.StatusDetail = detail;
    }

    // Switch now is the engine's own answer (DecideManual), asked once with no target and once per other account, so
    // the page offers exactly what the route would accept.
    private static void ApplySwitchNow(LicenseAccountDto dto, LicenseSnapshot snapshot)
    {
        var automatic = LicenseDecisionEngine.DecideManual(snapshot, dto.AccountEmail, null);
        if (automatic.RefusalCode == LicenseManualRefusals.NoMachineToSwitch)
        {
            dto.CanSwitchNow = false;
            dto.SwitchNowText = automatic.RefusalMessage!;
            return;
        }

        var chosen = automatic.Accepted ? automatic.Switches[0].TargetAccountEmail : null;
        foreach (var other in snapshot.Accounts.Select(a => a.AccountEmail).Where(e => e != dto.AccountEmail).OrderBy(e => e, StringComparer.Ordinal))
        {
            var named = LicenseDecisionEngine.DecideManual(snapshot, dto.AccountEmail, other);
            dto.SwitchTargets.Add(new LicenseSwitchTargetDto
            {
                AccountEmail = other,
                CanChoose = named.Accepted,
                UnavailableText = named.RefusalMessage,
                IsAutomaticChoice = other == chosen,
            });
        }

        dto.CanSwitchNow = dto.SwitchTargets.Any(t => t.CanChoose);
        dto.SwitchNowTargetAccountEmail = chosen;
        var notSwitched = string.Concat(automatic.Skipped.Select(s => $" {s.MachineName} is not switched: {s.Why}."));
        if (automatic.Accepted)
            dto.SwitchNowText = $"Moves {Names(automatic.Switches.Select(s => s.MachineName).ToList())} to {chosen}." + notSwitched;
        else if (dto.CanSwitchNow)
            dto.SwitchNowText = $"{automatic.RefusalMessage} A target can still be chosen by hand." + notSwitched;
        else
            dto.SwitchNowText = automatic.RefusalMessage + notSwitched;
    }

    /// <summary>One usage window. Its level is the engine's own reading of it.</summary>
    public static LicenseWindowDto Window(LicenseWindowReading? window, DateTime nowUtc)
    {
        if (window is null)
            return new LicenseWindowDto
            {
                UtilizationText = "Not known",
                Level = LicenseWindowLevels.NotKnown,
                ResetsText = "Reset time not known",
            };

        var hasReset = window.ResetsAtUtc is { } reset && reset <= nowUtc;
        return new LicenseWindowDto
        {
            Utilization = window.Utilization,
            UtilizationText = Percent(window.Utilization),
            Level = hasReset ? LicenseWindowLevels.ResetSince
                : LicenseDecisionEngine.IsTired(window, nowUtc) ? LicenseWindowLevels.AtLimit
                : LicenseWindowLevels.Normal,
            ResetsAtUtc = window.ResetsAtUtc is { } at ? Utc(at) : null,
            ResetsText = window.ResetsAtUtc is null ? "Reset time not known"
                : hasReset ? "Has reset since this reading"
                : $"Resets in {Span(window.ResetsAtUtc.Value - nowUtc)}",
        };
    }

    /// <summary>One machine: how it is doing, its active sign-in, its spares and any switch it is in the middle of.</summary>
    public static LicenseMachineDto Machine(LicenseMachineView machine, IReadOnlyList<LicenseSwitchLogEntity> openSwitches, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(openSwitches);
        var inProgress = SwitchInProgress(machine, openSwitches);
        var (status, text, tone) = machine.IsStale ? (LicenseMachineStatuses.NotReporting, "Not reporting", LicenseTones.Warning)
            : inProgress is not null ? (LicenseMachineStatuses.Switching, "Switching", LicenseTones.Neutral)
            : machine.ActiveAccountEmail is null ? (LicenseMachineStatuses.SignedOut, "Signed out", LicenseTones.Warning)
            : (LicenseMachineStatuses.Reporting, "Reporting", LicenseTones.Good);
        var holdings = machine.Holdings.Select(h => Holding(h, nowUtc)).ToList();
        return new LicenseMachineDto
        {
            MachineId = machine.MachineId.ToString("N"),
            MachineName = machine.MachineName,
            OsUser = machine.OsUser,
            ActiveAccountEmail = machine.ActiveAccountEmail,
            Status = status,
            StatusText = text,
            StatusTone = tone,
            IsStale = machine.IsStale,
            LastReportAtUtc = Utc(machine.LastReportAtUtc),
            LastReportText = $"Last report {Ago(nowUtc - machine.LastReportAtUtc)}",
            Active = holdings.FirstOrDefault(h => h.IsActive),
            Spares = holdings.Where(h => !h.IsActive).ToList(),
            SwitchInProgress = inProgress,
        };
    }

    private static LicenseSwitchInProgressDto? SwitchInProgress(LicenseMachineView machine, IReadOnlyList<LicenseSwitchLogEntity> openSwitches)
    {
        var row = openSwitches
            .Where(r => r.MachineId == machine.MachineId && r.Kind == LicenseLogKinds.Switch && LicenseLogStates.IsOpen(r.State))
            .OrderBy(r => r.CreatedAtUtc)
            .FirstOrDefault();
        if (row is not null)
            return new LicenseSwitchInProgressDto
            {
                SwitchId = row.SwitchId ?? "",
                FromAccountEmail = row.FromAccountEmail,
                ToAccountEmail = row.ToAccountEmail,
                Text = row.State == LicenseLogStates.Unknown
                    ? $"Switching from {row.FromAccountEmail} to {row.ToAccountEmail}: the command was sent and no answer has come back yet."
                    : row.ReasonCode == LicenseGatewayReasonCodes.DirectorOffline
                        ? $"Waiting to switch from {row.FromAccountEmail} to {row.ToAccountEmail}: the machine's Director is not connected."
                        : $"Switching from {row.FromAccountEmail} to {row.ToAccountEmail}: the command is being sent.",
            };
        if (machine.SwitchInProgressId is { } reported)
            return new LicenseSwitchInProgressDto { SwitchId = reported, Text = "The machine reports a switch in progress." };
        return null;
    }

    /// <summary>One sign-in a machine holds, with its expiry verdict as the read model gave it and the words for it.</summary>
    public static LicenseMachineHoldingDto Holding(LicenseHoldingView holding, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(holding);
        var (expiryText, expiryTone) = holding.ExpiryVerdict switch
        {
            LicenseSignInExpiry.Expired => ($"Sign-in expired {Ago(nowUtc - holding.SignInExpiresAtUtc!.Value)}", LicenseTones.Bad),
            LicenseSignInExpiry.ExpiresSoon => ($"Sign-in expires in {Span(holding.SignInExpiresAtUtc!.Value - nowUtc)}", LicenseTones.Warning),
            LicenseSignInExpiry.Fine => ($"Sign-in expires in {Span(holding.SignInExpiresAtUtc!.Value - nowUtc)}", LicenseTones.Good),
            _ => ("Sign-in expiry not known", LicenseTones.Neutral),
        };
        return new LicenseMachineHoldingDto
        {
            AccountEmail = holding.AccountEmail,
            IsActive = holding.IsActive,
            SignedInAtUtc = holding.SignedInAtUtc is { } signedIn ? Utc(signedIn) : null,
            SignedInText = holding.SignedInAtUtc is { } at ? $"Signed in {Ago(nowUtc - at)}" : "Sign-in date not known",
            SignInExpiresAtUtc = holding.SignInExpiresAtUtc is { } expires ? Utc(expires) : null,
            ExpiryVerdict = holding.ExpiryVerdict,
            ExpiryText = expiryText,
            ExpiryTone = expiryTone,
            IsReadingStale = holding.IsReadingStale,
            ReadError = holding.ReadError,
            ReadErrorText = ReadErrorText(holding.ReadError, null),
        };
    }

    // ----- the forecast -----

    /// <summary>The forecast and the schedule, in words. <paramref name="forecast"/> is
    /// <see cref="LicenseForecaster.Forecast"/> on the picture's own snapshot.</summary>
    public static LicenseForecastResponse Forecast(LicensePicture picture, LicenseForecast forecast)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(forecast);
        var snapshot = picture.Snapshot;
        var now = snapshot.NowUtc;
        FileLog.Write($"[LicenseDisplayFold] Forecast: accounts={forecast.Accounts.Count}, nextSwitch={(forecast.NextSwitch is null ? "none" : "one")}");

        var inUse = LicenseDecisionEngine.AccountsInUse(snapshot);
        // What Switch now with no target would do for each account in use: the same choice the minute check makes.
        var moves = inUse.ToDictionary(e => e, e => LicenseDecisionEngine.DecideManual(snapshot, e, null), StringComparer.Ordinal);
        var accounts = forecast.Accounts
            .Select(a => AccountForecast(a, moves.GetValueOrDefault(a.AccountEmail), now))
            .ToList();
        var expectedByAccount = accounts.ToDictionary(a => a.AccountEmail, a => a.ExpectedAtLimitUtc, StringComparer.Ordinal);

        var empty = IsEmpty(picture.Overview);
        var response = new LicenseForecastResponse
        {
            AsOfUtc = Utc(now),
            IsEmpty = empty,
            EmptyText = empty ? NothingReportedText : null,
            NextSwitch = forecast.NextSwitch is { } next
                ? new LicenseNextSwitchDto
                {
                    FromAccountEmail = next.FromAccountEmail,
                    ExpectedAtUtc = Utc(next.ExpectedAtUtc),
                    Window = next.Window,
                    WindowText = WindowLabel(next.Window),
                    TargetAccountEmail = next.TargetAccountEmail,
                    Machines = LicenseDecisionEngine.SwitchableMachines(snapshot, next.FromAccountEmail).Select(m => m.MachineName).ToList(),
                }
                : null,
            NextSwitchText = forecast.NextSwitch is { } expected
                ? NextSwitchText(expected, moves.GetValueOrDefault(expected.FromAccountEmail), now)
                : NoSwitchExpectedText,
            Accounts = accounts,
            Machines = picture.Overview.Machines.Select(m => MachineForecast(m, moves, expectedByAccount, now)).ToList(),
        };
        FileLog.Write($"[LicenseDisplayFold] Forecast: empty={empty}, machines={response.Machines.Count}");
        return response;
    }

    // With no target the sentence gives the engine's own reason - no account has room, or no machine on the account
    // can be sent a switch - rather than assuming which it is.
    private static string NextSwitchText(LicenseNextSwitch next, LicenseManualSwitchVerdict? move, DateTime now)
    {
        var when = next.ExpectedAtUtc <= now
            ? $"{next.FromAccountEmail} has reached {Threshold} of its {WindowLabel(next.Window)}"
            : $"{next.FromAccountEmail} is expected to reach {Threshold} of its {WindowLabel(next.Window)} in about {Span(next.ExpectedAtUtc - now)}";
        return next.TargetAccountEmail is { } target
            ? $"{when}; its machines then move to {target}, the account the Gateway would choose today."
            : $"{when}. {move?.RefusalMessage ?? $"No other account has room for the machines on {next.FromAccountEmail}."} Nothing would be switched.";
    }

    private static LicenseAccountForecastDto AccountForecast(LicenseAccountForecast account, LicenseManualSwitchVerdict? move, DateTime now)
    {
        var dto = new LicenseAccountForecastDto
        {
            AccountEmail = account.AccountEmail,
            IsInUse = move is not null,
            IsStale = account.IsStale,
            FiveHour = WindowForecast(account.FiveHour, account.IsStale, now),
            SevenDay = WindowForecast(account.SevenDay, account.IsStale, now),
            ExpectedAtLimitUtc = account.ExpectedTiredAtUtc is { } at ? Utc(at) : null,
        };
        dto.ExpectedText = account switch
        {
            { IsStale: true } => "No forecast: the latest reading is too old.",
            { ExpectedTiredAtUtc: { } expected, ExpectedTiredWindow: { } window } when expected <= now
                => $"Has reached {Threshold} of its {WindowLabel(window)}.",
            { ExpectedTiredAtUtc: { } expected, ExpectedTiredWindow: { } window }
                => $"Expected to reach {Threshold} of its {WindowLabel(window)} in about {Span(expected - now)}.",
            _ => "Not expected to reach its limit.",
        };

        if (move is null)
        {
            dto.NextSwitchText = "Not in use, so there is nothing to switch.";
            return dto;
        }
        dto.NextSwitchTargetAccountEmail = move.Accepted ? move.Switches[0].TargetAccountEmail : null;
        var when = account.ExpectedTiredAtUtc is not { } tiredAt ? null
            : tiredAt <= now ? "now"
            : $"in about {Span(tiredAt - now)}";
        dto.NextSwitchText = (move, when) switch
        {
            ({ Accepted: true }, null) => $"Next: {dto.NextSwitchTargetAccountEmail}, when this account reaches its limit. No switch is expected yet.",
            ({ Accepted: true }, _) => $"Next: {dto.NextSwitchTargetAccountEmail}, {when}.",
            (_, null) => $"{move.RefusalMessage} No switch is expected yet.",
            (_, "now") => $"{move.RefusalMessage} It is at its limit now.",
            _ => $"{move.RefusalMessage} It reaches its limit {when}.",
        };
        return dto;
    }

    private static LicenseWindowForecastDto WindowForecast(LicenseWindowForecast window, bool accountIsStale, DateTime now)
    {
        var dto = new LicenseWindowForecastDto
        {
            Utilization = window.Utilization,
            ResetsAtUtc = window.ResetsAtUtc is { } resets ? Utc(resets) : null,
            PercentPerHour = window.PercentPerHour,
            RateText = window.PercentPerHour switch
            {
                null => "No rate yet",
                <= 0 => "Not rising",
                { } rate => $"{rate.ToString("0.#", CultureInfo.InvariantCulture)}% an hour",
            },
            ExpectedAtLimitUtc = window.ExpectedAtThresholdUtc is { } at ? Utc(at) : null,
        };
        dto.ExpectedText = window switch
        {
            { Utilization: null } => "Not known.",
            _ when accountIsStale => "No forecast: the latest reading is too old.",
            { ResetsAtUtc: { } reset } when reset <= now => "No forecast: the window has reset since the latest reading.",
            { ExpectedAtThresholdUtc: { } expected } when expected <= now => $"Has reached {Threshold}.",
            { ExpectedAtThresholdUtc: { } expected } => $"Expected to reach {Threshold} in about {Span(expected - now)}.",
            { PercentPerHour: null } => "No forecast yet: there are not enough readings to show a rate.",
            { PercentPerHour: <= 0 } => $"Not rising, so not expected to reach {Threshold}.",
            _ => $"Not expected to reach {Threshold} before it resets.",
        };
        return dto;
    }

    private static LicenseMachineForecastDto MachineForecast(LicenseMachineView machine,
        IReadOnlyDictionary<string, LicenseManualSwitchVerdict> moves, IReadOnlyDictionary<string, DateTime?> expectedByAccount, DateTime now)
    {
        var dto = new LicenseMachineForecastDto
        {
            MachineName = machine.MachineName,
            OsUser = machine.OsUser,
            ActiveAccountEmail = machine.ActiveAccountEmail,
        };
        if (machine.ActiveAccountEmail is not { } active || !moves.TryGetValue(active, out var move))
        {
            dto.Text = "Signed in to no account, so there is nothing to switch.";
            return dto;
        }
        var skipped = move.Skipped.FirstOrDefault(s => SameMachine(s.MachineName, s.OsUser, machine));
        if (skipped is not null)
        {
            dto.Text = $"Would not be switched now: {skipped.Why}.";
            return dto;
        }
        var mine = move.Switches.FirstOrDefault(s => SameMachine(s.MachineName, s.OsUser, machine));
        if (mine is null)
        {
            dto.Text = move.RefusalMessage ?? "No next account is known.";
            return dto;
        }
        dto.NextAccountEmail = mine.TargetAccountEmail;
        dto.ExpectedSwitchAtUtc = expectedByAccount.GetValueOrDefault(active);
        dto.Text = dto.ExpectedSwitchAtUtc switch
        {
            null => $"Next: {mine.TargetAccountEmail}, when {active} reaches its limit. No switch is expected yet.",
            { } at when at <= now => $"Next: {mine.TargetAccountEmail}, now.",
            { } at => $"Next: {mine.TargetAccountEmail}, in about {Span(at - now)}.",
        };
        return dto;
    }

    private static bool SameMachine(string machineName, string osUser, LicenseMachineView machine)
        => string.Equals(machineName, machine.MachineName, StringComparison.OrdinalIgnoreCase)
           && string.Equals(osUser, machine.OsUser, StringComparison.OrdinalIgnoreCase);

    // ----- the log -----

    /// <summary>One row of the switch log in words. A row of a kind, a state or a reason this build does not know is
    /// still a row: its kind is "Note", its state and reason are the stored words, and its summary is its message.</summary>
    public static LicenseLogEntryDto LogEntry(LicenseSwitchLogEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var (stateText, stateTone) = row.State switch
        {
            LicenseLogStates.Pending when row.ReasonCode == LicenseGatewayReasonCodes.DirectorOffline => ("Waiting for the machine", LicenseTones.Warning),
            LicenseLogStates.Pending => ("Waiting to be sent", LicenseTones.Neutral),
            LicenseLogStates.Unknown => ("Sent, no answer yet", LicenseTones.Warning),
            LicenseLogStates.Switched => ("Switched", LicenseTones.Good),
            LicenseLogStates.AlreadyActive => ("Already on the target", LicenseTones.Good),
            LicenseLogStates.Failed => ("Failed", LicenseTones.Bad),
            LicenseLogStates.Recorded => ("Recorded", LicenseTones.Neutral),
            _ => (string.IsNullOrWhiteSpace(row.State) ? "Recorded" : row.State, LicenseTones.Neutral),
        };
        var kindText = row.Kind switch
        {
            LicenseLogKinds.Switch => "Switch",
            LicenseLogKinds.SpareAdded => "Spare added",
            LicenseLogKinds.SpareRemoved => "Spare removed",
            NoRoomKind => "No account had room",
            _ => "Note",
        };
        var message = string.IsNullOrWhiteSpace(row.Message) ? null : row.Message;
        return new LicenseLogEntryDto
        {
            EntryId = row.Id.ToString("N"),
            Kind = row.Kind,
            KindText = kindText,
            AtUtc = Utc(row.CreatedAtUtc),
            MachineName = row.MachineName,
            OsUser = row.OsUser,
            FromAccountEmail = row.FromAccountEmail,
            ToAccountEmail = row.ToAccountEmail,
            Why = row.Reason,
            WhyText = row.Reason switch
            {
                LicenseSwitchReasons.Threshold => "Threshold reached",
                LicenseSwitchReasons.Manual => "By hand",
                null => "",
                var other => other,
            },
            State = row.State,
            StateText = stateText,
            StateTone = stateTone,
            IsOpen = row.Kind == NoRoomKind ? row.FinishedAtUtc is null : LicenseLogStates.IsOpen(row.State),
            ReasonCode = row.ReasonCode,
            ReasonText = ReasonText(row.ReasonCode),
            Message = message,
            Summary = row.Kind == LicenseLogKinds.Switch
                ? row.State switch
                {
                    LicenseLogStates.Switched => $"{row.MachineName} switched from {row.FromAccountEmail} to {row.ToAccountEmail}.",
                    LicenseLogStates.AlreadyActive => $"{row.MachineName} was already on {row.ToAccountEmail}.",
                    LicenseLogStates.Failed => $"{row.MachineName} was not switched from {row.FromAccountEmail} to {row.ToAccountEmail}.",
                    _ => $"{row.MachineName} is switching from {row.FromAccountEmail} to {row.ToAccountEmail}.",
                }
                : message ?? kindText,
            SendCount = row.SendCount,
            LastSentAtUtc = UtcOrNull(row.LastSentAtUtc),
            StartedOnMachineAtUtc = UtcOrNull(row.DirectorStartedAtUtc),
            FinishedOnMachineAtUtc = UtcOrNull(row.DirectorFinishedAtUtc),
            ExchangeMilliseconds = row.ExchangeMilliseconds,
            FinishedAtUtc = UtcOrNull(row.FinishedAtUtc),
            TimingText = TimingText(row),
            VerifiedByUsageRead = row.VerifiedByUsageRead,
        };
    }

    private static string? TimingText(LicenseSwitchLogEntity row)
    {
        if (row.DirectorStartedAtUtc is not { } started || row.DirectorFinishedAtUtc is not { } finished || finished < started)
            return null;
        var took = finished - started;
        var text = took < TimeSpan.FromSeconds(1)
            ? $"Took {(int)took.TotalMilliseconds} milliseconds on the machine"
            : $"Took {took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} seconds on the machine";
        return row.ExchangeMilliseconds is > 0 ? $"{text} (the exchange itself {row.ExchangeMilliseconds} milliseconds)" : text;
    }

    /// <summary>The sentence for a switch row's reason word; the word itself when this build does not know it.</summary>
    public static string? ReasonText(string? reasonCode) => reasonCode switch
    {
        null or "" => null,
        LicenseSwitchReasonCodes.NotKeyHolder => "The Director that answered does not hold the machine's sign-ins.",
        LicenseSwitchReasonCodes.SwitchInProgress => "Another switch was already running on the machine.",
        LicenseSwitchReasonCodes.Expired => "The command reached the machine too late to start.",
        LicenseSwitchReasonCodes.ActiveAccountChanged => "The machine was no longer on the account the Gateway expected.",
        LicenseSwitchReasonCodes.SpareNotHeld => "The machine holds no spare for the target.",
        LicenseSwitchReasonCodes.SpareNotUsable => "The machine's spare for the target cannot be used; it needs a new sign-in.",
        LicenseSwitchReasonCodes.ExchangeFailedRestored => "The exchange failed and was undone; the machine is on the account it was on.",
        LicenseSwitchReasonCodes.ExchangeFailedUnknown => "The exchange failed and could not be undone; check the machine.",
        LicenseSwitchReasonCodes.VerifyFailed => "After the exchange the machine was not on the target.",
        LicenseGatewayReasonCodes.DirectorOffline => "The machine's Director was not connected.",
        LicenseGatewayReasonCodes.DirectorError => "The machine's Director answered with an error.",
        _ => reasonCode,
    };

    // ----- words -----

    /// <summary>The sentence for a failed read; the Director's own sentence, or the word, when this build does not know it.</summary>
    public static string? ReadErrorText(string? readError, string? detail) => readError switch
    {
        null or "" => null,
        LicenseReadErrors.Unauthorized => "The provider refused this sign-in. It needs a new sign-in.",
        LicenseReadErrors.RefreshFailed => "The stored sign-in could not be renewed. It needs a new sign-in.",
        LicenseReadErrors.RateLimited => "The provider is limiting how often usage can be read. The numbers shown are the last ones read.",
        LicenseReadErrors.Network => "The machine could not reach the provider. The numbers shown are the last ones read.",
        LicenseReadErrors.UnexpectedAnswer => "The provider's answer could not be understood. The numbers shown are the last ones read.",
        LicenseReadErrors.StoreUnreadable => "The machine could not read its stored sign-in.",
        _ => string.IsNullOrWhiteSpace(detail) ? $"The last read failed ({readError})." : detail,
    };

    private static LicenseExtraUsageDto ExtraUsage(bool? enabled, double? utilization) => enabled switch
    {
        true => new LicenseExtraUsageDto
        {
            State = LicenseExtraUsageStates.On,
            Text = utilization is { } used ? $"Paid extra usage is on ({Percent(used)} used)" : "Paid extra usage is on",
            Utilization = utilization,
        },
        false => new LicenseExtraUsageDto { State = LicenseExtraUsageStates.Off, Text = "Paid extra usage is off", Utilization = utilization },
        null => new LicenseExtraUsageDto { State = LicenseExtraUsageStates.NotKnown, Text = "Not known", Utilization = utilization },
    };

    private static string PlanText(string? plan)
    {
        var word = plan?.Trim();
        return string.IsNullOrEmpty(word) ? "Not known" : char.ToUpperInvariant(word[0]) + word[1..];
    }

    /// <summary>
    /// A length of time in words, rounded down: "less than a minute", "1 minute", "59 minutes", "1 hour",
    /// "1 hour 48 minutes", "1 day", "2 days 3 hours". A negative length reads as less than a minute.
    /// </summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
            return "less than a minute";
        if (span < TimeSpan.FromHours(1))
            return Count(span.Minutes, "minute");
        if (span < TimeSpan.FromDays(1))
            return span.Minutes == 0 ? Count(span.Hours, "hour") : $"{Count(span.Hours, "hour")} {Count(span.Minutes, "minute")}";
        return span.Hours == 0 ? Count(span.Days, "day") : $"{Count(span.Days, "day")} {Count(span.Hours, "hour")}";
    }

    private static string Ago(TimeSpan span) => Span(span) + " ago";

    private static string Count(int number, string unit) => number == 1 ? $"1 {unit}" : $"{number} {unit}s";

    private static string ResetsPhrase(DateTime? resetsAtUtc, DateTime now)
        => resetsAtUtc is { } reset ? $"resets in {Span(reset - now)}" : "resets at a time the provider did not give";

    private static List<(string Name, LicenseWindowReading Reading)> TiredWindows(LicenseAccountReading reading, DateTime now)
    {
        var tired = new List<(string, LicenseWindowReading)>();
        if (reading.FiveHour is { } five && LicenseDecisionEngine.IsTired(five, now))
            tired.Add((LicenseWindowNames.FiveHour, five));
        if (reading.SevenDay is { } week && LicenseDecisionEngine.IsTired(week, now))
            tired.Add((LicenseWindowNames.SevenDay, week));
        return tired;
    }

    private static string WindowLabel(string window) => window == LicenseWindowNames.FiveHour ? "5-hour window" : "weekly limit";

    private static string Threshold => Percent(LicenseDecisionEngine.ThresholdPercent);

    // Rounded DOWN to one decimal, so a window just under the threshold never reads as being at it: 96.99 is "96.9%".
    private static string Percent(double value) => (Math.Floor(value * 10) / 10).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "no machine",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private static bool IsEmpty(LicenseOverview overview) => overview.Accounts.Count == 0 && overview.Machines.Count == 0;

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    private static DateTime? UtcOrNull(DateTime? value) => value is { } v ? Utc(v) : null;
}
