using System.Globalization;
using System.Text;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// The license switching decision, as pure code: a picture of one tenant goes in, what to do comes out,
/// and nothing is read or written on the way. The rule is MISSION.md section 5 "Gateway" with the
/// staleness rules of contract section 5:
///
/// For each account in use (some machine has it active): when a FRESH reading shows either window at or
/// above <see cref="ThresholdPercent"/>, every machine on it that can be sent a switch is switched to one
/// target - an account every one of those machines holds a usable spare for (one whose sign-in does not
/// expire at or before the command's <see cref="LicenseContract.SwitchNotAfter"/>, contract 5.7), whose fresh reading shows
/// both windows under the threshold, and which is not barred. Best target is the lowest weekly use, then
/// the lowest 5-hour use, then the account email in ordinal order so the choice never depends on input
/// order. No target means nothing is switched and the owner gets exactly one email per exhaustion event.
///
/// Who decides "which machines": the machines that are on the account and can be sent a switch now. A
/// stale machine (no accepted report for <see cref="LicenseContract.MachineStaleAfter"/>) or one with an
/// open switch is skipped and does not take part in the target choice, because its holdings are not known
/// to be current - letting it veto a target would hold back the machines whose state is known.
///
/// A window whose reset time has passed is never tired: its numbers were read before the reset and say
/// nothing about now. Without this, the tick at a reset would see the last pre-reset reading (fresh for up
/// to 6 minutes) still at 97% or more, open a new exhaustion event and email the owner again every tick.
///
/// Logging: <see cref="Decide"/> and <see cref="DecideManual"/> log entry and exit. The small public
/// predicates (<see cref="IsReadingStale"/>, <see cref="HasRoom"/> and the rest) do not: they are called in
/// loops on every tick, and a line per call would bury the decisions in the log.
/// </summary>
public static class LicenseDecisionEngine
{
    /// <summary>A window at or above this percentage is tired.</summary>
    public const double ThresholdPercent = 97.0;

    /// <summary>The longest a 5-hour window can run - the end of a bar when the provider gave no reset time.</summary>
    public static readonly TimeSpan FiveHourWindowLength = TimeSpan.FromHours(5);

    /// <summary>The longest a weekly window can run - the end of a bar when the provider gave no reset time.</summary>
    public static readonly TimeSpan SevenDayWindowLength = TimeSpan.FromDays(7);

    /// <summary>One tick: decide every threshold switch and every owner email for the tenant.</summary>
    public static LicenseDecisions Decide(LicenseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = snapshot.NowUtc;
        FileLog.Write($"[LicenseDecisionEngine] Decide: accounts={snapshot.Accounts.Count}, machines={snapshot.Machines.Count}, bars={snapshot.Bars.Count}, openExhaustions={snapshot.OpenExhaustions.Count}");

        var readings = IndexReadings(snapshot.Accounts);
        var switches = new List<LicenseSwitchDecision>();
        var newBars = new List<LicenseBar>();
        var emails = new List<LicenseOwnerEmail>();
        var opened = new List<LicenseExhaustion>();
        var closed = new List<string>();
        var skipped = new List<LicenseSkippedMachine>();

        var extended = new List<LicenseExhaustion>();
        var stillOpen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var exhaustion in snapshot.OpenExhaustions)
        {
            var (ended, laterEnd) = JudgeExhaustion(exhaustion, snapshot, readings);
            if (ended)
            {
                closed.Add(exhaustion.AccountEmail);
                continue;
            }
            stillOpen.Add(exhaustion.AccountEmail);
            if (laterEnd is { } end)
                extended.Add(exhaustion with { EndsAtUtc = end });
        }

        foreach (var source in AccountsInUse(snapshot))
        {
            if (!readings.TryGetValue(source, out var reading) || IsReadingStale(reading, now))
                continue; // stale numbers never trigger (contract 5.2)

            var tired = TiredWindows(reading, now);
            if (tired.Count == 0)
                continue;

            var (eligible, skippedHere) = SplitMachines(snapshot, source);
            skipped.AddRange(skippedHere);
            if (eligible.Count == 0)
                continue; // every machine on it is stale or mid-switch; nothing can be sent this tick

            var target = ChooseTarget(snapshot, readings, source, eligible);
            if (target is null)
            {
                if (!stillOpen.Contains(source))
                {
                    var exhaustion = new LicenseExhaustion(source, now, ExhaustionEnd(tired, now));
                    opened.Add(exhaustion);
                    stillOpen.Add(source);
                    emails.Add(BuildNoRoomEmail(snapshot, readings, source, tired, eligible, skippedHere));
                }
                continue;
            }

            var explanation = ThresholdExplanation(source, tired, target);
            foreach (var machine in eligible)
                switches.Add(new LicenseSwitchDecision(machine.MachineName, machine.OsUser, source, target,
                    LicenseSwitchReasons.Threshold, explanation));

            foreach (var window in tired)
            {
                var until = window.ResetsAtUtc ?? now + WindowLength(window.Name);
                var alreadyBarred = snapshot.Bars.Any(b =>
                    SameAccount(b.AccountEmail, source) && b.Window == window.Name && b.BarredUntilUtc >= until);
                if (!alreadyBarred)
                    newBars.Add(new LicenseBar(source, window.Name, until));
            }
        }

        FileLog.Write($"[LicenseDecisionEngine] Decide: switches={switches.Count}, newBars={newBars.Count}, emails={emails.Count}, opened={opened.Count}, closed={closed.Count}, extended={extended.Count}, skipped={skipped.Count}");
        return new LicenseDecisions(switches, newBars, emails, opened, closed, extended, skipped);
    }

    /// <summary>
    /// Switch now: move every machine on <paramref name="sourceAccountEmail"/> to the best target, or to
    /// <paramref name="targetAccountEmail"/> when one is named. It takes an ACCOUNT, not a machine, because
    /// usage is counted per account (MISSION.md decision a): moving one machine off a tired account leaves the
    /// others on it. A named target is refused when it is not held as a usable spare by every machine to be
    /// switched, when its reading is stale, or when either window is at or over the threshold or unknown. A
    /// named target that is barred is allowed - the bar exists to stop the AUTOMATIC choice from bouncing
    /// back, and a person naming it has decided otherwise. With no target named, the choice is exactly the
    /// automatic one, bars included. Switch now records no bar: nothing triggered.
    /// </summary>
    public static LicenseManualSwitchVerdict DecideManual(LicenseSnapshot snapshot, string sourceAccountEmail, string? targetAccountEmail)
    {
        FileLog.Write($"[LicenseDecisionEngine] DecideManual: targetNamed={targetAccountEmail is not null}");
        var verdict = PreviewManual(snapshot, sourceAccountEmail, targetAccountEmail);
        FileLog.Write(verdict.Accepted
            ? $"[LicenseDecisionEngine] DecideManual: switches={verdict.Switches.Count}, skipped={verdict.Skipped.Count}"
            : $"[LicenseDecisionEngine] DecideManual: refused {verdict.RefusalCode}");
        return verdict;
    }

    /// <summary>
    /// The answer <see cref="DecideManual"/> would give, for a display that asks about every account and every
    /// target on each read of the Licenses page. It is the same code and so the same answer; it writes no log line,
    /// because a line per question would bury the lines of the switches that are really decided. Anything that is
    /// about to SEND a switch calls <see cref="DecideManual"/>.
    /// </summary>
    public static LicenseManualSwitchVerdict PreviewManual(LicenseSnapshot snapshot, string sourceAccountEmail, string? targetAccountEmail)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(sourceAccountEmail))
            throw new ArgumentException("The account to switch away from is required", nameof(sourceAccountEmail));

        var source = sourceAccountEmail.Trim().ToLowerInvariant();
        var target = string.IsNullOrWhiteSpace(targetAccountEmail) ? null : targetAccountEmail.Trim().ToLowerInvariant();
        var readings = IndexReadings(snapshot.Accounts);
        var (eligible, skipped) = SplitMachines(snapshot, source);

        if (eligible.Count == 0)
            return Refuse(skipped, LicenseManualRefusals.NoMachineToSwitch,
                $"No machine on {source} can be switched now: {(skipped.Count == 0 ? "none is on it" : "every machine on it is stale or already mid-switch")}.");

        if (target is null)
        {
            target = ChooseTarget(snapshot, readings, source, eligible);
            if (target is null)
                return Refuse(skipped, LicenseManualRefusals.NoTarget,
                    $"No other account has room for the machines on {source}.");
        }
        else
        {
            if (SameAccount(target, source))
                return Refuse(skipped, LicenseManualRefusals.TargetIsSource, $"The machines are already on {target}.");
            var missing = eligible.FirstOrDefault(m => !HoldsUsableSpare(m, target));
            if (missing is not null)
                return Refuse(skipped, LicenseManualRefusals.TargetNotHeld,
                    $"{Label(snapshot, missing)} holds no usable spare for {target}.");
            var expiring = eligible.FirstOrDefault(m => SpareSignInExpiresTooSoon(m, target, snapshot.NowUtc));
            if (expiring is not null)
                return Refuse(skipped, LicenseManualRefusals.TargetSignInExpiring,
                    $"{Label(snapshot, expiring)}'s sign-in for {target} expires before a switch could finish.");
            if (!readings.TryGetValue(target, out var targetReading) || IsReadingStale(targetReading, snapshot.NowUtc))
                return Refuse(skipped, LicenseManualRefusals.TargetStale,
                    $"The latest reading of {target} is too old to show it has room.");
            if (!HasRoom(targetReading))
                return Refuse(skipped, LicenseManualRefusals.TargetFull,
                    $"{target} has no room: {NoRoomReason(targetReading, snapshot.NowUtc)}.");
        }

        var explanation = $"Switch now: from {source} to {target}.";
        var switches = eligible
            .Select(m => new LicenseSwitchDecision(m.MachineName, m.OsUser, source, target, LicenseSwitchReasons.Manual, explanation))
            .ToList();
        return new LicenseManualSwitchVerdict(switches, skipped, null, null);
    }

    /// <summary>
    /// The owner email for <paramref name="accountEmail"/>'s no-room event as things stand NOW, or null when it
    /// cannot truthfully be written now: the account's reading is stale or shows no tired window, no machine on
    /// it can be sent a switch, or a target exists. <see cref="Decide"/> writes the email once, on the tick that
    /// opens the event; this is for the minute check to write it again when that send failed, from the facts
    /// of the later tick rather than a stored copy of the first one.
    /// </summary>
    public static LicenseOwnerEmail? NoRoomEmail(LicenseSnapshot snapshot, string accountEmail)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(accountEmail))
            throw new ArgumentException("The account the event is about is required", nameof(accountEmail));
        var now = snapshot.NowUtc;
        var source = Normalize(accountEmail);
        var readings = IndexReadings(snapshot.Accounts);
        if (!readings.TryGetValue(source, out var reading) || IsReadingStale(reading, now))
            return null;
        var tired = TiredWindows(reading, now);
        if (tired.Count == 0)
            return null;
        var (eligible, skipped) = SplitMachines(snapshot, source);
        if (eligible.Count == 0 || ChooseTarget(snapshot, readings, source, eligible) is not null)
            return null;
        return BuildNoRoomEmail(snapshot, readings, source, tired, eligible, skipped);
    }

    /// <summary>
    /// The best target for <paramref name="machines"/> - pass <see cref="SwitchableMachines"/> - or null when
    /// none qualifies. Used by the tick, by Switch now, and by the forecast, so all three agree.
    /// </summary>
    public static string? ChooseTarget(LicenseSnapshot snapshot, string sourceAccountEmail, IReadOnlyList<LicenseMachineState> machines)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(machines);
        return ChooseTarget(snapshot, IndexReadings(snapshot.Accounts), sourceAccountEmail, machines);
    }

    /// <summary>
    /// True when the reading cannot be trusted at <paramref name="nowUtc"/>: never read, or older than the
    /// limit for where it was read (active 6 minutes, spare 30).
    /// </summary>
    public static bool IsReadingStale(LicenseAccountReading reading, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.ReadAtUtc is not { } readAt)
            return true;
        var limit = reading.ReadWhileActive ? LicenseContract.ActiveHoldingStaleAfter : LicenseContract.SpareHoldingStaleAfter;
        return nowUtc - readAt > limit;
    }

    /// <summary>True when the machine's last accepted report is older than <see cref="LicenseContract.MachineStaleAfter"/>.</summary>
    public static bool IsMachineStale(LicenseMachineState machine, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return nowUtc - machine.LastReportReceivedAtUtc > LicenseContract.MachineStaleAfter;
    }

    /// <summary>
    /// True when both windows are known and under the threshold. A reading taken before a reset still
    /// answers this truly when it is under the threshold: use only falls at a reset, so it is still under.
    /// </summary>
    public static bool HasRoom(LicenseAccountReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.FiveHour is { } five && five.Utilization < ThresholdPercent
            && reading.SevenDay is { } week && week.Utilization < ThresholdPercent;
    }

    /// <summary>The account emails some machine in the snapshot has active, in ordinal order.</summary>
    public static IReadOnlyList<string> AccountsInUse(LicenseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Machines
            .Select(m => m.ActiveAccountEmail)
            .OfType<string>()
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The machines whose active account is <paramref name="accountEmail"/>.</summary>
    public static IReadOnlyList<LicenseMachineState> MachinesOn(LicenseSnapshot snapshot, string accountEmail)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Machines
            .Where(m => m.ActiveAccountEmail is { } active && SameAccount(active, accountEmail))
            .ToList();
    }

    /// <summary>
    /// The machines on <paramref name="accountEmail"/> that a switch can be sent to now: not stale, no open
    /// switch, none reported in progress. The same split the tick and Switch now use.
    /// </summary>
    public static IReadOnlyList<LicenseMachineState> SwitchableMachines(LicenseSnapshot snapshot, string accountEmail)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return SplitMachines(snapshot, Normalize(accountEmail)).Eligible;
    }

    /// <summary>
    /// True when the window is at or over the threshold and its reset time, if known, is still ahead of
    /// <paramref name="nowUtc"/>. Shared with the forecast so both read a pre-reset reading the same way.
    /// </summary>
    public static bool IsTired(LicenseWindowReading window, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Utilization >= ThresholdPercent && (window.ResetsAtUtc is not { } reset || reset > nowUtc);
    }

    // ----- the rule's parts -----

    private sealed record TiredWindow(string Name, double Utilization, DateTime? ResetsAtUtc);

    private static string? ChooseTarget(LicenseSnapshot snapshot, IReadOnlyDictionary<string, LicenseAccountReading> readings,
        string source, IReadOnlyList<LicenseMachineState> machines)
    {
        if (machines.Count == 0)
            return null;
        // A qualifying reading has both windows (HasRoom checked them), so a missing one is a defect here.
        return readings.Values
            .Where(r => WhyNotTarget(snapshot, r, source, machines) is null)
            .Select(r => r is { SevenDay: { } week, FiveHour: { } five }
                ? (r.AccountEmail, Week: week.Utilization, Five: five.Utilization)
                : throw new InvalidOperationException($"Target candidate {r.AccountEmail} passed the room check without both windows."))
            .OrderBy(c => c.Week)
            .ThenBy(c => c.Five)
            .ThenBy(c => c.AccountEmail, StringComparer.Ordinal)
            .Select(c => c.AccountEmail)
            .FirstOrDefault();
    }

    // Null when the account qualifies as a target; otherwise one plain sentence saying why not, which is
    // also what the owner's no-room email lists for each account.
    private static string? WhyNotTarget(LicenseSnapshot snapshot, LicenseAccountReading candidate, string source,
        IReadOnlyList<LicenseMachineState> machines)
    {
        var now = snapshot.NowUtc;
        if (SameAccount(candidate.AccountEmail, source))
            return "it is the account in use";
        if (IsReadingStale(candidate, now))
            return "its latest reading is too old to show it has room";
        if (!HasRoom(candidate))
            return NoRoomReason(candidate, now);
        var bar = snapshot.Bars
            .Where(b => SameAccount(b.AccountEmail, candidate.AccountEmail) && b.BarredUntilUtc > now)
            .OrderByDescending(b => b.BarredUntilUtc)
            .FirstOrDefault();
        if (bar is not null)
            return $"it triggered a switch and is barred until its {WindowLabel(bar.Window)} resets at {Time(bar.BarredUntilUtc)}";
        var missing = machines.FirstOrDefault(m => !HoldsUsableSpare(m, candidate.AccountEmail));
        if (missing is not null)
            return $"{Label(snapshot, missing)} holds no usable spare for it";
        var expiring = machines.FirstOrDefault(m => SpareSignInExpiresTooSoon(m, candidate.AccountEmail, now));
        if (expiring is not null)
            return $"{Label(snapshot, expiring)}'s sign-in for it expires before a switch could finish";
        return null;
    }

    // Why a reading without room has none, in the words the no-room email and Switch now both use.
    private static string NoRoomReason(LicenseAccountReading reading, DateTime now)
    {
        if (TiredWindows(reading, now) is { Count: > 0 } tired)
            return $"it is at {DescribeWindows(tired)}";
        if (reading.FiveHour is null || reading.SevenDay is null)
            return "a window's use is unknown";
        return "its latest reading was taken before a window reset, so its use now is not known yet";
    }

    private static (List<LicenseMachineState> Eligible, List<LicenseSkippedMachine> Skipped) SplitMachines(
        LicenseSnapshot snapshot, string source)
    {
        var eligible = new List<LicenseMachineState>();
        var skipped = new List<LicenseSkippedMachine>();
        foreach (var machine in MachinesOn(snapshot, source))
        {
            string? why = null;
            if (IsMachineStale(machine, snapshot.NowUtc))
                why = "its last report is too old";
            else if (machine.HasOpenSwitch)
                why = "a switch sent to it has not finished";
            else if (machine.SwitchInProgressId is not null)
                why = "it reports a switch in progress";

            if (why is null)
                eligible.Add(machine);
            else
                skipped.Add(new LicenseSkippedMachine(machine.MachineName, machine.OsUser, source, why));
        }
        return (eligible, skipped);
    }

    private static bool HoldsUsableSpare(LicenseMachineState machine, string accountEmail) =>
        machine.Spares.Any(s => SameAccount(s.AccountEmail, accountEmail)
            && s.ReadError is not (LicenseReadErrors.Unauthorized or LicenseReadErrors.RefreshFailed));

    // An unknown expiry is not "at or before" anything, so it does not block (contract 5.7); the forecast
    // shows it as not known.
    private static bool SpareSignInExpiresTooSoon(LicenseMachineState machine, string accountEmail, DateTime now) =>
        machine.Spares.Any(s => SameAccount(s.AccountEmail, accountEmail)
            && s.SignInExpiresAtUtc is { } expires && expires <= now + LicenseContract.SwitchNotAfter);

    private static List<TiredWindow> TiredWindows(LicenseAccountReading reading, DateTime now)
    {
        var tired = new List<TiredWindow>();
        if (reading.FiveHour is { } five && IsTired(five, now))
            tired.Add(new TiredWindow(LicenseWindowNames.FiveHour, five.Utilization, five.ResetsAtUtc));
        if (reading.SevenDay is { } week && IsTired(week, now))
            tired.Add(new TiredWindow(LicenseWindowNames.SevenDay, week.Utilization, week.ResetsAtUtc));
        return tired;
    }

    // One email per CONTINUOUS outage (the Tech Lead's ruling on the second review): whether the event has
    // ended and, while it has not, a later end to move it to.
    private static (bool Ended, DateTime? LaterEnd) JudgeExhaustion(LicenseExhaustion exhaustion, LicenseSnapshot snapshot,
        IReadOnlyDictionary<string, LicenseAccountReading> readings)
    {
        var now = snapshot.NowUtc;
        if (MachinesOn(snapshot, exhaustion.AccountEmail).Count == 0)
            return (true, null); // nothing is on it any more: a switch succeeded or a machine was moved by hand

        var fresh = readings.TryGetValue(Normalize(exhaustion.AccountEmail), out var reading)
            && !IsReadingStale(reading, now);
        if (fresh && reading is not null && TiredWindows(reading, now) is { Count: > 0 } tired)
        {
            // Still out of room: the event never closes by time while a fresh reading shows a tired window. When
            // the outage now lasts longer than the event said - the week tired during it, or the provider moved
            // a reset later - the end moves with it, so that reset cannot open a second event and a second email.
            var end = ExhaustionEnd(tired, now);
            return (false, end > exhaustion.EndsAtUtc ? end : null);
        }
        if (now >= exhaustion.EndsAtUtc)
            return (true, null);
        // A fresh reading with no tired window means the window reset early. No margin below 97% is needed
        // against a reading that wobbles across it: the provider's use only falls at a reset, so a fall under
        // 97% IS the end of the event. A stale reading says nothing, and the event stays open.
        return (fresh, null);
    }

    private static DateTime ExhaustionEnd(IReadOnlyList<TiredWindow> tired, DateTime now) =>
        tired.Max(w => w.ResetsAtUtc ?? now + WindowLength(w.Name));

    private static TimeSpan WindowLength(string window) =>
        window == LicenseWindowNames.FiveHour ? FiveHourWindowLength : SevenDayWindowLength;

    // ----- words -----

    // What a sentence calls a machine: its name - and, when another machine of the picture has the same name, its
    // operating system user in brackets, because two users of one computer are two machines here and the name alone
    // would not say which is meant. The Licenses page labels its machines by the same rule.
    private static string Label(LicenseSnapshot snapshot, LicenseMachineState machine)
        => Label(snapshot, machine.MachineName, machine.OsUser);

    private static string Label(LicenseSnapshot snapshot, string machineName, string osUser)
        => snapshot.Machines.Count(m => string.Equals(m.MachineName, machineName, StringComparison.OrdinalIgnoreCase)) > 1
            ? $"{machineName} ({osUser})"
            : machineName;

    private static string ThresholdExplanation(string source, IReadOnlyList<TiredWindow> tired, string target) =>
        $"{source} reached {DescribeWindows(tired)}; switched to {target}, the account with the lowest weekly use that every machine on it holds.";

    private static LicenseOwnerEmail BuildNoRoomEmail(LicenseSnapshot snapshot,
        IReadOnlyDictionary<string, LicenseAccountReading> readings, string source,
        IReadOnlyList<TiredWindow> tired, IReadOnlyList<LicenseMachineState> machines,
        IReadOnlyList<LicenseSkippedMachine> skipped)
    {
        var subject = $"DevThrottle: no Claude account has room - {source} is at {DescribeWindows(tired)}";
        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"{source} has reached {DescribeWindows(tired)}, and no other account can take its machines, so nothing was switched.\r\n\r\n");
        body.Append("Machines on it: ").Append(string.Join(", ", machines.Select(m => Label(snapshot, m))));
        foreach (var machine in skipped)
            body.Append(CultureInfo.InvariantCulture, $", {Label(snapshot, machine.MachineName, machine.OsUser)} (not sent a switch: {machine.Why})");
        body.Append(".\r\n\r\n");
        foreach (var window in tired)
            body.Append(CultureInfo.InvariantCulture, $"Its {WindowLabel(window.Name)} resets {(window.ResetsAtUtc is { } r ? "at " + Time(r) : "at a time the provider did not give")}.\r\n");
        var others = readings.Values
            .Where(r => !SameAccount(r.AccountEmail, source))
            .OrderBy(r => r.AccountEmail, StringComparer.Ordinal)
            .ToList();
        body.Append("\r\n");
        if (others.Count == 0)
            body.Append("No other account is known.\r\n");
        foreach (var other in others)
            body.Append(CultureInfo.InvariantCulture, $"{other.AccountEmail} was not chosen: {WhyNotTarget(snapshot, other, source, machines)}.\r\n");
        body.Append("\r\nThis is the only email for this event. Sessions on these machines will stop when the limit is reached.");
        return new LicenseOwnerEmail(source, subject, body.ToString());
    }

    private static string DescribeWindows(IReadOnlyList<TiredWindow> tired) =>
        string.Join(" and ", tired.Select(w => $"{Percent(w.Utilization)} of its {WindowLabel(w.Name)}"));

    private static string WindowLabel(string window) =>
        window == LicenseWindowNames.FiveHour ? "5-hour window" : "weekly limit";

    private static string Percent(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    // ----- small helpers -----

    private static LicenseManualSwitchVerdict Refuse(List<LicenseSkippedMachine> skipped, string code, string message)
        => new(Array.Empty<LicenseSwitchDecision>(), skipped, code, message);

    private static Dictionary<string, LicenseAccountReading> IndexReadings(IReadOnlyList<LicenseAccountReading> accounts)
    {
        var index = new Dictionary<string, LicenseAccountReading>(StringComparer.Ordinal);
        foreach (var reading in accounts)
        {
            var key = Normalize(reading.AccountEmail);
            if (!index.TryAdd(key, reading with { AccountEmail = key }))
                throw new ArgumentException($"The snapshot carries two readings for one account ({key}); the store must pass the freshest only.", nameof(accounts));
        }
        return index;
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    private static bool SameAccount(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
}
