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
///
/// A STALE ACCOUNT'S WINDOWS ARE STALE TOO. Each window carries its own level, and for an account whose reading is
/// too old that level is <c>stale</c>, never <c>at-limit</c> or <c>normal</c> - so the level alone is enough to
/// colour a bar, and no page has to weigh a window's level against its account's status.
///
/// A MACHINE IS CALLED THE SAME THING EVERYWHERE: its name, with the operating system user in brackets when
/// another machine has the same name. Every list that names a machine carries its identifier beside that.
///
/// LOGGING. <see cref="Overview"/> and <see cref="Forecast"/> log entry and exit. The per-row methods
/// (<see cref="Account"/>, <see cref="Window"/>, <see cref="Machine"/>, <see cref="Holding"/>,
/// <see cref="LogEntry"/>, <see cref="ReasonText"/>, <see cref="ReadErrorText"/>, <see cref="Span"/>) write no line:
/// each is called once for every row of every read of the page, and a line per row would bury the lines that say a
/// switch was decided. For the same reason the fold asks the engine through
/// <see cref="LicenseDecisionEngine.PreviewManual"/>, which answers as Switch now would and logs nothing.
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

    /// <summary>What a <c>no-room</c> row is called: an account in use is at its limit and nothing could be switched.</summary>
    public const string NothingSwitchedKindText = "Nothing was switched";

    /// <summary>What an account's renewal date says while none is set. The date is one a person sets.</summary>
    public const string RenewalDateNotSetText = "Renewal date not set";

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
            Machines = picture.Overview.Machines.Select(m => Machine(m, picture)).ToList(),
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
        var names = new MachineNames(picture.Overview.Machines);
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
            FiveHour = Window(reading.FiveHour, now, view.IsStale),
            SevenDay = Window(reading.SevenDay, now, view.IsStale),
            IsStale = view.IsStale,
            ReadAtUtc = view.Freshest is { } read ? Utc(read.JudgedReadAtUtc) : null,
            ReadAgeText = view.Freshest is { } freshest
                ? $"Read {Ago(now - freshest.JudgedReadAtUtc)} on {names.Label(freshest.MachineName, freshest.OsUser)}"
                : "Never read on any machine",
            ReadError = view.Freshest?.ReadError,
            ReadErrorText = ReadErrorText(view.Freshest?.ReadError, view.Freshest?.ReadErrorDetail, accountWasEverRead: true),
            ExtraUsage = ExtraUsage(view.Freshest?.ExtraUsageEnabled, view.Freshest?.ExtraUsageUtilization),
            RenewsOn = view.RenewsOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            RenewsText = RenewsText(view.RenewsOn, now),
            BarredUntilUtc = Bar(snapshot, view.AccountEmail) is { } barred ? Utc(barred.BarredUntilUtc) : null,
            BarredText = Bar(snapshot, view.AccountEmail) is { } bar ? BarredText(bar, now) : null,
            SignInExpiry = SignInExpiry(held, names, now),
            ActiveOnMachines = held.Where(h => h.Holding.IsActive).Select(h => names.Ref(h.Machine)).ToList(),
            SpareOnMachines = held.Where(h => !h.Holding.IsActive).Select(h => names.Ref(h.Machine)).ToList(),
        };
        ApplyStatus(dto, view, reading, held, names, now);
        // Asked of the picture Switch now itself decides on, so the page offers exactly what the route accepts: a
        // machine whose switch is only waiting for its Director to connect can still be switched now.
        ApplySwitchNow(dto, LicenseSwitchService.SnapshotSwitchNowDecidesOn(picture), names);
        return dto;
    }

    // The sign-in of this account that expires first, on whichever machine holds it. A holding that gave no expiry
    // cannot be the soonest; when none gave one, the expiry is not known - never read as fine.
    private static LicenseAccountSignInExpiryDto SignInExpiry(
        IReadOnlyList<(LicenseMachineView Machine, LicenseHoldingView Holding)> held, MachineNames names, DateTime now)
    {
        if (held.Count == 0)
            return new LicenseAccountSignInExpiryDto { Verdict = LicenseSignInExpiry.NotKnown, Text = "Not signed in on any machine", Tone = LicenseTones.Neutral };
        var known = held.Where(h => h.Holding.SignInExpiresAtUtc is not null).OrderBy(h => h.Holding.SignInExpiresAtUtc).ToList();
        if (known.Count == 0)
            return new LicenseAccountSignInExpiryDto { Verdict = LicenseSignInExpiry.NotKnown, Text = "Sign-in expiry not known", Tone = LicenseTones.Neutral };

        var (machine, holding) = known[0];
        var expires = holding.SignInExpiresAtUtc!.Value;
        var label = names.Label(machine);
        var (text, tone) = holding.ExpiryVerdict switch
        {
            LicenseSignInExpiry.Expired => ($"Sign-in on {label} expired {Ago(now - expires)}", LicenseTones.Bad),
            LicenseSignInExpiry.ExpiresSoon => ($"Sign-in on {label} expires in {Span(expires - now)}", LicenseTones.Warning),
            _ => ($"Sign-in on {label} expires in {Span(expires - now)}", LicenseTones.Good),
        };
        return new LicenseAccountSignInExpiryDto
        {
            ExpiresAtUtc = Utc(expires),
            Machine = names.Ref(machine),
            Verdict = holding.ExpiryVerdict,
            Text = text,
            Tone = tone,
        };
    }

    private static void ApplyStatus(LicenseAccountDto dto, LicenseAccountView view, LicenseAccountReading reading,
        IReadOnlyList<(LicenseMachineView Machine, LicenseHoldingView Holding)> held, MachineNames names, DateTime now)
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
            var machines = Names(expired.Concat(refused).Select(h => names.Label(h.Machine)).Distinct(StringComparer.Ordinal).ToList());
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
                $"The sign-in on {names.Label(soonest.Machine)} expires in {Span(soonest.Holding.SignInExpiresAtUtc!.Value - now)}.");
        }
        else if (dto.IsInUse)
        {
            Status(dto, LicenseAccountStatuses.InUse, "In use", LicenseTones.Good, $"In use on {Names(dto.ActiveOnMachines.Select(m => m.Label).ToList())}.");
        }
        else if (LicenseDecisionEngine.HasRoom(reading))
        {
            Status(dto, LicenseAccountStatuses.HasRoom, "Has room", LicenseTones.Good,
                $"Not in use. Held as a spare on {Names(dto.SpareOnMachines.Select(m => m.Label).ToList())}.");
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

    // Switch now is the engine's own answer (the one DecideManual gives), asked once with no target and once per other
    // account, so the page offers exactly what the route would accept.
    private static void ApplySwitchNow(LicenseAccountDto dto, LicenseSnapshot snapshot, MachineNames names)
    {
        var automatic = LicenseDecisionEngine.PreviewManual(snapshot, dto.AccountEmail, null);
        // Each machine on the account that cannot be sent a switch, with its own reason - the same words the route
        // answers a refused Switch now with (SwitchNowRefused).
        var notSwitched = string.Concat(automatic.Skipped.Select(s => $" {names.Label(s.MachineName, s.OsUser)} is not switched: {s.Why}."));
        if (automatic.RefusalCode == LicenseManualRefusals.NoMachineToSwitch)
        {
            dto.CanSwitchNow = false;
            dto.SwitchNowText = automatic.RefusalMessage + notSwitched;
            return;
        }

        var chosen = automatic.Accepted ? automatic.Switches[0].TargetAccountEmail : null;
        foreach (var other in snapshot.Accounts.Select(a => a.AccountEmail).Where(e => e != dto.AccountEmail).OrderBy(e => e, StringComparer.Ordinal))
        {
            var named = LicenseDecisionEngine.PreviewManual(snapshot, dto.AccountEmail, other);
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
        if (automatic.Accepted)
            dto.SwitchNowText = $"Moves {Names(automatic.Switches.Select(s => names.Label(s.MachineName, s.OsUser)).ToList())} to {chosen}." + notSwitched;
        else if (dto.CanSwitchNow)
            dto.SwitchNowText = $"{automatic.RefusalMessage} A target can still be chosen by hand." + notSwitched;
        else
            dto.SwitchNowText = automatic.RefusalMessage + notSwitched;
    }

    // The bar that keeps this account out of the AUTOMATIC choice now, or null: the one that lasts longest when it
    // has more than one. A bar whose time has come is no bar.
    private static LicenseBar? Bar(LicenseSnapshot snapshot, string accountEmail)
        => snapshot.Bars
            .Where(b => b.AccountEmail == accountEmail && b.BarredUntilUtc > snapshot.NowUtc)
            .OrderByDescending(b => b.BarredUntilUtc)
            .FirstOrDefault();

    // A bar stops the automatic choice only (the ruling on the decision engine's review): a person can still choose
    // the account by hand, and the sentence says so.
    private static string BarredText(LicenseBar bar, DateTime now)
        => $"Left out of the automatic choice for {Span(bar.BarredUntilUtc - now)}, until its {WindowLabel(bar.Window)} resets: "
           + "it triggered a switch. It can still be chosen by hand.";

    // ----- Switch now, answered -----

    /// <summary>
    /// The answer of an ACCEPTED Switch now, in words. <paramref name="rows"/> are the switch log rows of the switches
    /// it sent, as they stood when it returned; <paramref name="machines"/> is every machine of the account, so a
    /// machine is called here what the overview and the log call it.
    /// </summary>
    public static LicenseSwitchNowResponse SwitchNowAccepted(LicenseSwitchNowResult result,
        IReadOnlyList<LicenseSwitchLogEntity> rows, IReadOnlyList<(string MachineName, string OsUser)> machines)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(machines);
        if (!result.Accepted)
            throw new InvalidOperationException("A refused Switch now has no accepted answer.");
        FileLog.Write($"[LicenseDisplayFold] SwitchNowAccepted: switches={rows.Count}, skipped={result.Skipped.Count}");

        var entries = LogEntries(rows, machines);
        var labels = Labels(machines.Concat(rows.Select(r => (r.MachineName, r.OsUser))));
        var response = new LicenseSwitchNowResponse
        {
            TargetAccountEmail = result.TargetAccountEmail,
            Switches = entries.Select((e, i) => new LicenseSwitchNowMachineDto
            {
                SwitchId = rows[i].SwitchId ?? throw new InvalidOperationException($"Switch log row {rows[i].Id} has no switch identifier."),
                EntryId = e.EntryId,
                MachineName = e.MachineName,
                OsUser = e.OsUser,
                Label = e.Label,
                State = e.State,
                StateText = e.StateText,
                StateTone = e.StateTone,
                Summary = e.Summary,
            }).ToList(),
            Skipped = result.Skipped.Select(s => new LicenseSwitchNowSkippedDto
            {
                MachineName = s.MachineName,
                OsUser = s.OsUser,
                Label = labels(s.MachineName, s.OsUser),
                Why = $"{labels(s.MachineName, s.OsUser)} is not switched: {s.Why}.",
            }).ToList(),
        };
        response.Text = SwitchNowText(response);
        return response;
    }

    // The whole outcome in one sentence: how many machines are on the target, how many are still on their way, how
    // many failed - each said only when it is not none - and then the machines that were left out.
    private static string SwitchNowText(LicenseSwitchNowResponse response)
    {
        var target = response.TargetAccountEmail;
        var done = response.Switches.Where(s => s.State is LicenseLogStates.Switched or LicenseLogStates.AlreadyActive).Select(s => s.Label).ToList();
        var failed = response.Switches.Where(s => s.State == LicenseLogStates.Failed).Select(s => s.Label).ToList();
        var waiting = response.Switches.Where(s => s.State is not (LicenseLogStates.Switched or LicenseLogStates.AlreadyActive or LicenseLogStates.Failed)).Select(s => s.Label).ToList();
        var parts = new List<string>();
        if (done.Count > 0)
            parts.Add($"{Names(done)} {(done.Count == 1 ? "is" : "are")} now on {target}.");
        if (waiting.Count > 0)
            parts.Add($"The switch of {Names(waiting)} to {target} has not finished; the switch log shows how it ends.");
        if (failed.Count > 0)
            parts.Add($"The switch of {Names(failed)} to {target} failed; the switch log says why.");
        parts.AddRange(response.Skipped.Select(s => s.Why));
        return string.Join(" ", parts);
    }

    /// <summary>The sentence of a REFUSED Switch now: the engine's reason, and then each machine on the account that
    /// could not be sent a switch with its own reason - what <c>switchNowText</c> says of the same account.</summary>
    public static string SwitchNowRefused(LicenseSwitchNowResult result, IReadOnlyList<(string MachineName, string OsUser)> machines)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(machines);
        if (result.Accepted || string.IsNullOrWhiteSpace(result.RefusalMessage))
            throw new InvalidOperationException("A Switch now that was not refused with a reason has no refusal sentence.");
        var labels = Labels(machines);
        return result.RefusalMessage + string.Concat(result.Skipped.Select(s => $" {labels(s.MachineName, s.OsUser)} is not switched: {s.Why}."));
    }

    // What to call a machine among these machines: its name, with its user in brackets when the name is not unique.
    private static Func<string, string, string> Labels(IEnumerable<(string MachineName, string OsUser)> machines)
    {
        var repeated = machines
            .Select(m => (Name: m.MachineName.ToLowerInvariant(), User: m.OsUser.ToLowerInvariant()))
            .Distinct()
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        return (name, user) => repeated.Contains(name.ToLowerInvariant()) ? $"{name} ({user})" : name;
    }

    /// <summary>One usage window. Its level is the engine's own reading of it - unless
    /// <paramref name="readingIsStale"/>, when the number is too old to say anything and the level is "stale".</summary>
    public static LicenseWindowDto Window(LicenseWindowReading? window, DateTime nowUtc, bool readingIsStale)
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
            Level = readingIsStale ? LicenseWindowLevels.Stale
                : hasReset ? LicenseWindowLevels.ResetSince
                : LicenseDecisionEngine.IsTired(window, nowUtc) ? LicenseWindowLevels.AtLimit
                : LicenseWindowLevels.Normal,
            ResetsAtUtc = window.ResetsAtUtc is { } at ? Utc(at) : null,
            ResetsText = window.ResetsAtUtc is null ? "Reset time not known"
                : hasReset ? "Has reset since this reading"
                : $"Resets in {Span(window.ResetsAtUtc.Value - nowUtc)}",
        };
    }

    /// <summary>One machine: how it is doing, its active sign-in, its spares and any switch it is in the middle of.</summary>
    public static LicenseMachineDto Machine(LicenseMachineView machine, LicensePicture picture)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(picture);
        var openSwitches = picture.OpenSwitches;
        var nowUtc = picture.Snapshot.NowUtc;
        var inProgress = SwitchInProgress(machine, openSwitches);
        var (status, text, tone) = machine.IsStale ? (LicenseMachineStatuses.NotReporting, "Not reporting", LicenseTones.Warning)
            : inProgress is not null ? (LicenseMachineStatuses.Switching, "Switching", LicenseTones.Neutral)
            // Before "signed out": a machine that cannot run a switch cannot be moved onto any account either.
            : machine.CannotSwitchReason is { } reason
                ? (LicenseMachineStatuses.CannotSwitch, $"Cannot switch: {LicenseStore.CannotSwitchSentence(reason)}", LicenseTones.Warning)
            : machine.ActiveAccountEmail is null ? (LicenseMachineStatuses.SignedOut, "Signed out", LicenseTones.Warning)
            : (LicenseMachineStatuses.Reporting, "Reporting", LicenseTones.Good);
        // A read error of a sign-in whose account no machine has ever read must not speak of "the numbers shown":
        // the card shows none.
        var everRead = picture.Overview.Accounts.Where(a => a.Freshest is not null).Select(a => a.AccountEmail).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var holdings = machine.Holdings.Select(h => Holding(h, nowUtc, everRead.Contains(h.AccountEmail))).ToList();
        var hold = HoldInForce(picture.Snapshot, machine);
        return new LicenseMachineDto
        {
            MachineId = machine.MachineId.ToString("N"),
            MachineName = machine.MachineName,
            OsUser = machine.OsUser,
            Label = new MachineNames(picture.Overview.Machines).Label(machine),
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
            HeldUntilUtc = hold is null ? null : Utc(hold.HeldUntilUtc),
            HeldText = hold is null ? null : HeldText(hold, nowUtc),
        };
    }

    // The hold that keeps this machine out of the AUTOMATIC switch now, or null. Whether a hold is in force is the
    // engine's own answer, so the page never says "held" of a machine the minute check would switch.
    private static LicenseHold? HoldInForce(LicenseSnapshot snapshot, LicenseMachineView machine)
    {
        var state = snapshot.Machines.FirstOrDefault(m => SameMachine(m.MachineName, m.OsUser, machine));
        return state is not null && LicenseDecisionEngine.IsHeld(state, snapshot.NowUtc) ? state.Hold : null;
    }

    // A hold stops the automatic switch only: a person can still use Switch now, and the sentence says so.
    private static string HeldText(LicenseHold hold, DateTime now)
        => $"Held out of the automatic switch for {Span(hold.HeldUntilUtc - now)}, until {Clock(hold.HeldUntilUtc)}: "
           + $"its switch from {hold.FromAccountEmail} to {hold.ToAccountEmail} has failed more than once in a row{LicenseDecisionEngine.HoldReason(hold)}. "
           + "Switch now still sends it at once.";

    private static string Clock(DateTime utc) => Utc(utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

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

    /// <summary>One sign-in a machine holds, with its expiry verdict as the read model gave it and the words for it.
    /// <paramref name="accountWasEverRead"/> is whether any machine has read this account's usage, so a read error
    /// never points at numbers the page does not have.</summary>
    public static LicenseMachineHoldingDto Holding(LicenseHoldingView holding, DateTime nowUtc, bool accountWasEverRead)
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
            RoleText = holding.IsActive ? "Active" : "Spare",
            SignedInAtUtc = holding.SignedInAtUtc is { } signedIn ? Utc(signedIn) : null,
            SignedInText = holding.SignedInAtUtc is { } at ? $"Signed in {Ago(nowUtc - at)}" : "Sign-in date not known",
            SignInExpiresAtUtc = holding.SignInExpiresAtUtc is { } expires ? Utc(expires) : null,
            ExpiryVerdict = holding.ExpiryVerdict,
            ExpiryText = expiryText,
            ExpiryTone = expiryTone,
            // A failed renewal of this SIGN-IN (nothing to do with the subscription's renewal date): all three null
            // when the last renewal worked or is not known.
            RenewalFailedAtUtc = holding.RenewalFailedAtUtc is { } failedAt ? Utc(failedAt) : null,
            RenewalFailedText = holding.RenewalFailedAtUtc is null ? null : RenewalFailedText(holding, nowUtc),
            RenewalFailedTone = holding.RenewalFailedAtUtc is null ? null
                // The sign-in is dead or about to be, and the machine could not renew it: the owner must act.
                : holding.ExpiryVerdict is LicenseSignInExpiry.Expired or LicenseSignInExpiry.ExpiresSoon ? LicenseTones.Bad
                : LicenseTones.Warning,
            IsReadingStale = holding.IsReadingStale,
            ReadError = holding.ReadError,
            ReadErrorText = ReadErrorText(holding.ReadError, null, accountWasEverRead),
        };
    }

    // ----- the forecast -----

    /// <summary>
    /// The forecast and the schedule, in words, with the rates from <paramref name="histories"/>.
    /// Folded from the picture Switch now decides on (<see cref="LicenseSwitchService.SnapshotSwitchNowDecidesOn"/>),
    /// the same one the overview's Switch now offer is asked of: a switch the Gateway knows it never sent (pending,
    /// the Director not connected) neither holds its machine back here nor is called a switch "sent to it", so the
    /// page can never offer Switch now on a card while the schedule says nothing would be switched.
    /// </summary>
    public static LicenseForecastResponse Forecast(LicensePicture picture, IReadOnlyList<LicenseAccountHistory> histories)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(histories);
        var snapshot = LicenseSwitchService.SnapshotSwitchNowDecidesOn(picture);
        var forecast = LicenseForecaster.Forecast(snapshot, histories);
        var now = snapshot.NowUtc;
        FileLog.Write($"[LicenseDisplayFold] Forecast: accounts={forecast.Accounts.Count}, nextSwitch={(forecast.NextSwitch is null ? "none" : "one")}");

        var names = new MachineNames(picture.Overview.Machines);
        var inUse = LicenseDecisionEngine.AccountsInUse(snapshot);
        // What Switch now with no target would do for each account in use: the same choice the minute check makes.
        var moves = inUse.ToDictionary(e => e, e => LicenseDecisionEngine.PreviewManual(snapshot, e, null), StringComparer.Ordinal);
        var readings = snapshot.Accounts.ToDictionary(r => r.AccountEmail, StringComparer.Ordinal);
        var accounts = forecast.Accounts
            .Select(a => AccountForecast(a, readings[a.AccountEmail], moves.GetValueOrDefault(a.AccountEmail), EveryMachineHeldText(snapshot, a.AccountEmail), now))
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
                    Machines = LicenseDecisionEngine.SwitchableMachines(snapshot, next.FromAccountEmail).Select(m => names.Ref(m.MachineName, m.OsUser)).ToList(),
                }
                : null,
            NextSwitchText = forecast.NextSwitch is { } expected
                ? NextSwitchText(expected, moves[expected.FromAccountEmail], now) + (EveryMachineHeldText(snapshot, expected.FromAccountEmail) is { } held ? " " + held : "")
                : NoSwitchExpectedText,
            Accounts = accounts,
            Machines = picture.Overview.Machines.Select(m => MachineForecast(m, names, moves, expectedByAccount, HoldInForce(snapshot, m), now)).ToList(),
        };
        FileLog.Write($"[LicenseDisplayFold] Forecast: empty={empty}, machines={response.Machines.Count}");
        return response;
    }

    // With no target the sentence gives the engine's own reason - no account has room, or no machine on the account
    // can be sent a switch - rather than assuming which it is. The forecaster and the engine choose a target by the
    // same code, so "no target" with no refusal is a defect, and it is not papered over with a guessed reason.
    private static string NextSwitchText(LicenseNextSwitch next, LicenseManualSwitchVerdict move, DateTime now)
    {
        var when = next.ExpectedAtUtc <= now
            ? $"{next.FromAccountEmail} has reached {Threshold} of its {WindowLabel(next.Window)}"
            : $"{next.FromAccountEmail} is expected to reach {Threshold} of its {WindowLabel(next.Window)} in about {Span(next.ExpectedAtUtc - now)}";
        return next.TargetAccountEmail is { } target
            ? $"{when}; its machines then move to {target}, the account the Gateway would choose today."
            : $"{when}. {move.RefusalMessage ?? throw new InvalidOperationException("The forecast gives no target for the next switch, and Switch now would not refuse one.")} Nothing would be switched.";
    }

    // When every machine on the account that could be sent a switch is held because its switch keeps failing: the
    // sentence saying so, and when the automatic switch is next tried. Null otherwise.
    private static string? EveryMachineHeldText(LicenseSnapshot snapshot, string accountEmail)
    {
        var switchable = LicenseDecisionEngine.SwitchableMachines(snapshot, accountEmail);
        if (switchable.Count == 0 || !switchable.All(m => LicenseDecisionEngine.IsHeld(m, snapshot.NowUtc)))
            return null;
        var next = switchable.Min(m => m.Hold!.HeldUntilUtc);
        return $"The automatic switch keeps failing and is held: it is tried again in {Span(next - snapshot.NowUtc)}, at {Clock(next)}. Switch now still sends it at once.";
    }

    private static LicenseAccountForecastDto AccountForecast(LicenseAccountForecast account, LicenseAccountReading reading,
        LicenseManualSwitchVerdict? move, string? everyMachineHeld, DateTime now)
    {
        var dto = new LicenseAccountForecastDto
        {
            AccountEmail = account.AccountEmail,
            IsInUse = move is not null,
            IsStale = account.IsStale,
            FiveHour = WindowForecast(account.FiveHour, reading.FiveHour, account.IsStale, now),
            SevenDay = WindowForecast(account.SevenDay, reading.SevenDay, account.IsStale, now),
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
        if (everyMachineHeld is not null)
            dto.NextSwitchText += " " + everyMachineHeld;
        return dto;
    }

    // The number, its level and its reset are the overview's own words for the same window (Window), so the schedule
    // never has to compare a percentage with the threshold or a reset time with now, or look the account up elsewhere.
    private static LicenseWindowForecastDto WindowForecast(LicenseWindowForecast window, LicenseWindowReading? reading, bool accountIsStale, DateTime now)
    {
        var words = Window(reading, now, accountIsStale);
        var dto = new LicenseWindowForecastDto
        {
            Utilization = words.Utilization,
            UtilizationText = words.UtilizationText,
            Level = words.Level,
            ResetsAtUtc = words.ResetsAtUtc,
            ResetsText = words.ResetsText,
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

    private static LicenseMachineForecastDto MachineForecast(LicenseMachineView machine, MachineNames names,
        IReadOnlyDictionary<string, LicenseManualSwitchVerdict> moves, IReadOnlyDictionary<string, DateTime?> expectedByAccount,
        LicenseHold? hold, DateTime now)
    {
        var dto = new LicenseMachineForecastDto
        {
            MachineId = machine.MachineId.ToString("N"),
            MachineName = machine.MachineName,
            OsUser = machine.OsUser,
            Label = names.Label(machine),
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
            // Every machine on an account the engine was asked about is switched, left out with a reason, or covered
            // by the refusal. Anything else is a defect, and it is not shown to the owner as a guessed sentence.
            dto.Text = move.RefusalMessage ?? throw new InvalidOperationException(
                "Switch now would move the machines of an account and names neither a switch nor a reason for one of them.");
            return dto;
        }
        dto.NextAccountEmail = mine.TargetAccountEmail;
        dto.ExpectedSwitchAtUtc = expectedByAccount.GetValueOrDefault(active);
        if (hold is not null)
        {
            // Held: the automatic switch is not tried before the hold has passed, whatever the numbers say.
            if (dto.ExpectedSwitchAtUtc is { } expected && expected < hold.HeldUntilUtc)
                dto.ExpectedSwitchAtUtc = Utc(hold.HeldUntilUtc);
            // The machine's own heldText says why and for how long, and the page draws it beside this; said twice in
            // one row it would be read once. So this says only what is next, and no earlier than when.
            dto.Text = $"Next: {mine.TargetAccountEmail}, not before {Clock(hold.HeldUntilUtc)}: the automatic switch is held for this machine.";
            return dto;
        }
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
    public static LicenseLogEntryDto LogEntry(LicenseSwitchLogEntity row) => LogEntry(row, NoRepeatedNames);

    private static readonly IReadOnlySet<string> NoRepeatedNames = new HashSet<string>();

    /// <summary>
    /// One page of the log in words, each row's machine called what the overview calls it: its name, with the
    /// operating system user in brackets when another machine has the same name. <paramref name="machines"/> is every
    /// machine of the account; the machines of the rows themselves count too, so two users of a machine that is no
    /// longer listed are still told apart.
    /// </summary>
    public static List<LicenseLogEntryDto> LogEntries(IReadOnlyList<LicenseSwitchLogEntity> rows,
        IReadOnlyList<(string MachineName, string OsUser)> machines)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(machines);
        var repeated = machines
            .Concat(rows.Where(r => r.MachineName.Length > 0).Select(r => (r.MachineName, r.OsUser)))
            .Select(m => (Name: m.Item1.ToLowerInvariant(), User: m.Item2.ToLowerInvariant()))
            .Distinct()
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        return rows.Select(r => LogEntry(r, repeated)).ToList();
    }

    private static LicenseLogEntryDto LogEntry(LicenseSwitchLogEntity row, IReadOnlySet<string> repeatedNames)
    {
        ArgumentNullException.ThrowIfNull(row);
        var label = repeatedNames.Contains(row.MachineName.ToLowerInvariant()) ? $"{row.MachineName} ({row.OsUser})" : row.MachineName;
        // A sign-in started from the Cockpit is a row of its own kind, with its own words (contract section 8.4).
        if (row.Kind == LicenseSignInLog.Kind)
            return LicenseSignInFold.LogEntry(row, label);
        var (stateText, stateTone) = row.State switch
        {
            LicenseLogStates.Pending when row.ReasonCode == LicenseGatewayReasonCodes.DirectorOffline => ("Waiting for the machine", LicenseTones.Warning),
            LicenseLogStates.Pending => ("Waiting to be sent", LicenseTones.Neutral),
            LicenseLogStates.Unknown => ("Sent, no answer yet", LicenseTones.Warning),
            LicenseLogStates.Switched => ("Switched", LicenseTones.Good),
            LicenseLogStates.AlreadyActive => ("Already on the target", LicenseTones.Good),
            LicenseLogStates.Failed => ("Failed", LicenseTones.Bad),
            LicenseLogStates.Recorded => ("Recorded", LicenseTones.Neutral),
            // A failed-renewal warning: open while the renewal keeps failing, ended when one has worked.
            LicenseLogStates.Open when row.Kind == LicenseRenewalFailureLog.Kind => ("Failing", LicenseTones.Bad),
            LicenseLogStates.Ended when row.Kind == LicenseRenewalFailureLog.Kind => ("Over", LicenseTones.Neutral),
            _ => (string.IsNullOrWhiteSpace(row.State) ? "Recorded" : row.State, LicenseTones.Neutral),
        };
        var kindText = row.Kind switch
        {
            LicenseLogKinds.Switch => "Switch",
            LicenseLogKinds.SpareAdded => "Spare added",
            LicenseLogKinds.SpareRemoved => "Spare removed",
            // One kind for both reasons nothing could be switched: no account had room, or the switch keeps failing
            // on every machine. The row's message says which.
            NoRoomKind => NothingSwitchedKindText,
            LicenseRenewalFailureLog.Kind => "Renewal failing",
            LicenseLogKinds.AlarmOpened => "Alarm opened",
            LicenseLogKinds.AlarmClosed => "Alarm closed",
            LicenseLogKinds.CannotSwitch => "Cannot switch",
            LicenseLogKinds.CanSwitchAgain => "Can switch again",
            _ => "Note",
        };
        var message = string.IsNullOrWhiteSpace(row.Message) ? null : row.Message;
        return new LicenseLogEntryDto
        {
            EntryId = row.Id.ToString("N"),
            Kind = row.Kind,
            KindText = kindText,
            AtUtc = Utc(row.CreatedAtUtc),
            MachineId = row.MachineId == Guid.Empty ? null : row.MachineId.ToString("N"),
            MachineName = row.MachineName,
            OsUser = row.OsUser,
            Label = label,
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
            IsOpen = row.Kind is NoRoomKind or LicenseRenewalFailureLog.Kind ? row.FinishedAtUtc is null : LicenseLogStates.IsOpen(row.State),
            ReasonCode = row.ReasonCode,
            ReasonText = ReasonText(row.ReasonCode),
            Message = message,
            Summary = row.Kind == LicenseLogKinds.Switch
                ? row.State switch
                {
                    LicenseLogStates.Switched => $"{label} switched from {row.FromAccountEmail} to {row.ToAccountEmail}.",
                    LicenseLogStates.AlreadyActive => $"{label} was already on {row.ToAccountEmail}.",
                    LicenseLogStates.Failed => $"{label} was not switched from {row.FromAccountEmail} to {row.ToAccountEmail}."
                        + (row.HeldUntilUtc is { } heldUntil
                            // A moment, never a distance: the stored row is read long after the hold has passed.
                            ? $" It was not the first time in a row that this switch failed this way, so the automatic switch left {label} out until {Clock(heldUntil)}."
                            : ""),
                    LicenseLogStates.Pending or LicenseLogStates.Unknown => $"{label} is switching from {row.FromAccountEmail} to {row.ToAccountEmail}.",
                    // A state this build does not know: what happened is not known here, so nothing is claimed.
                    _ => message ?? (string.IsNullOrWhiteSpace(row.State)
                        ? $"{label}: a switch from {row.FromAccountEmail} to {row.ToAccountEmail}, with no state recorded."
                        : $"{label}: a switch from {row.FromAccountEmail} to {row.ToAccountEmail}, in a state this Gateway does not know ({row.State})."),
                }
                : message ?? kindText,
            SendCount = row.SendCount,
            LastSentAtUtc = UtcOrNull(row.LastSentAtUtc),
            // On a failed-renewal row that column is when the sign-in was made, not when anything started.
            StartedOnMachineAtUtc = row.Kind == LicenseRenewalFailureLog.Kind ? null : UtcOrNull(row.DirectorStartedAtUtc),
            FinishedOnMachineAtUtc = UtcOrNull(row.DirectorFinishedAtUtc),
            ExchangeMilliseconds = row.ExchangeMilliseconds,
            FinishedAtUtc = UtcOrNull(row.FinishedAtUtc),
            HeldUntilUtc = row.Kind == LicenseLogKinds.Switch ? UtcOrNull(row.HeldUntilUtc) : null,
            TimingText = TimingText(row),
            VerifiedByUsageRead = row.VerifiedByUsageRead,
            VerifiedText = row.Kind != LicenseLogKinds.Switch || row.State is not (LicenseLogStates.Switched or LicenseLogStates.AlreadyActive) ? null
                : row.VerifiedByUsageRead switch
                {
                    true => "The new sign-in answered a usage read.",
                    false => "The new sign-in did not answer a usage read.",
                    null => null,
                },
        };
    }

    private static string? TimingText(LicenseSwitchLogEntity row)
    {
        // A refusal is answered at once: it began and ended at the same moment, and there is no length of time to give.
        if (row.DirectorStartedAtUtc is not { } started || row.DirectorFinishedAtUtc is not { } finished || finished <= started)
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
        LicenseGatewayReasonCodes.OwnerEmailed => "The owner was emailed.",
        LicenseGatewayReasonCodes.OwnerEmailFailed => "The owner has not been emailed yet; it is tried again.",
        LicenseGatewayReasonCodes.NoOwnerAccount => "This Gateway has no DevThrottle account to email.",
        LicenseGatewayReasonCodes.DirectorError => "The machine's Director answered with an error.",
        LicenseCannotSwitchReasons.ToolMissing => "The DevThrottle tool is missing from the machine's Python tool environment.",
        _ => reasonCode,
    };

    // ----- words -----

    /// <summary>The sentence for a failed read; the Director's own sentence, or the word, when this build does not know it.</summary>
    public static string? ReadErrorText(string? readError, string? detail, bool accountWasEverRead)
    {
        // Said only when there are numbers on the card to point at; an account never read has none.
        var numbers = accountWasEverRead ? "The numbers shown are the last ones read." : "No usage has been read for this account yet.";
        return readError switch
        {
            null or "" => null,
            LicenseReadErrors.Unauthorized => "The provider refused this sign-in. It needs a new sign-in.",
            LicenseReadErrors.RefreshFailed => "The stored sign-in could not be renewed. It needs a new sign-in.",
            LicenseReadErrors.RateLimited => $"The provider is limiting how often usage can be read. {numbers}",
            LicenseReadErrors.Network => $"The machine could not reach the provider. {numbers}",
            LicenseReadErrors.UnexpectedAnswer => $"The provider's answer could not be understood. {numbers}",
            LicenseReadErrors.StoreUnreadable => "The machine could not read its stored sign-in.",
            _ => string.IsNullOrWhiteSpace(detail) ? $"The last read failed ({readError})." : detail,
        };
    }

    /// <summary>
    /// The sentence for an account's renewal date - the day its SUBSCRIPTION is billed again, as a person set it. The
    /// date has no time and no zone, and it is compared with the Gateway's date in Coordinated Universal Time, so
    /// around midnight "today" can be a day off for the viewer; the date itself is always given, today's included.
    /// </summary>
    public static string RenewsText(DateOnly? renewsOn, DateTime nowUtc)
    {
        if (renewsOn is not { } date)
            return RenewalDateNotSetText;
        var today = DateOnly.FromDateTime(Utc(nowUtc));
        var day = date.ToString(date.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
        return date == today ? $"Renews today, {day}"
            : date < today ? $"Renewal date {day} has passed"
            : $"Renews {day}";
    }

    // When the failed renewal was, and why in the Director's contract words. The time is the machine's own clock, so
    // the distance is only said when it is not in the future by the Gateway's.
    private static string RenewalFailedText(LicenseHoldingView holding, DateTime nowUtc)
    {
        var at = Utc(holding.RenewalFailedAtUtc!.Value);
        var when = at <= nowUtc ? $"Renewing this sign-in failed {Ago(nowUtc - at)}" : "Renewing this sign-in failed";
        var why = RenewalWhy(holding.RenewalError);
        if (why is not null)
            return $"{when}: {why}.";
        // No reason this Gateway knows: the Director's own sentence when it wrote one, and otherwise only the fact.
        return string.IsNullOrWhiteSpace(holding.RenewalErrorDetail) ? $"{when}." : $"{when}. {holding.RenewalErrorDetail.Trim()}";
    }

    /// <summary>Why a renewal failed, in words, for a reason word of the contract; null for any other word or none.
    /// The Licenses page and the owner email say the same of one reason.</summary>
    internal static string? RenewalWhy(string? renewalError) => renewalError switch
    {
        LicenseRenewalErrors.LoginFailed => "the sign-in did not start, or ended without a sign-in",
        LicenseRenewalErrors.ApprovalFailed => "the approval in the browser did not complete",
        LicenseRenewalErrors.WrongAccount => "the approving browser is signed in as another account",
        LicenseRenewalErrors.BrowserNotSignedIn => "the approving browser is not signed in",
        LicenseRenewalErrors.NoApprover => "no browser is set to approve this account",
        LicenseRenewalErrors.ApproverOffline => "the machine that approves this account was not connected",
        LicenseRenewalErrors.Expired => "no approval came back in time",
        LicenseRenewalErrors.StoreRefused => "the machine's key store would not take the new sign-in",
        _ => null,
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
        null => new LicenseExtraUsageDto { State = LicenseExtraUsageStates.NotKnown, Text = "Paid extra usage not known", Utilization = utilization },
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

    /// <summary>What each machine is called. A machine is a name and an operating system user; the user is said only
    /// when the name alone would not tell two machines apart.</summary>
    private sealed class MachineNames
    {
        private readonly IReadOnlyList<LicenseMachineView> _machines;
        private readonly HashSet<string> _repeated;

        public MachineNames(IReadOnlyList<LicenseMachineView> machines)
        {
            _machines = machines;
            _repeated = machines.GroupBy(m => m.MachineName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public string Label(LicenseMachineView machine) => Label(machine.MachineName, machine.OsUser);

        public string Label(string machineName, string osUser)
            => _repeated.Contains(machineName) ? $"{machineName} ({osUser})" : machineName;

        public LicenseMachineRefDto Ref(LicenseMachineView machine) => new()
        {
            MachineId = machine.MachineId.ToString("N"),
            MachineName = machine.MachineName,
            OsUser = machine.OsUser,
            Label = Label(machine),
        };

        /// <summary>The machine the engine names by its name and user. The engine's picture is built from these same
        /// machines, so one it names that is not among them is a defect.</summary>
        public LicenseMachineRefDto Ref(string machineName, string osUser)
            => Ref(_machines.FirstOrDefault(m => SameMachine(machineName, osUser, m))
                   ?? throw new InvalidOperationException("The engine names a machine that is not in the picture it was given."));
    }

    private static bool IsEmpty(LicenseOverview overview) => overview.Accounts.Count == 0 && overview.Machines.Count == 0;

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    private static DateTime? UtcOrNull(DateTime? value) => value is { } v ? Utc(v) : null;
}
