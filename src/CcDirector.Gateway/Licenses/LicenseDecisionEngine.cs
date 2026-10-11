using System.Globalization;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// The license switching decision, as pure code: a picture of one tenant goes in, what to do comes out,
/// and nothing is read or written on the way. The rule is MISSION.md section 5 "Gateway" with the
/// staleness rules of contract section 5:
///
/// For each account in use (some machine has it active): when a FRESH reading shows either window at or
/// above <see cref="ThresholdPercent"/>, every machine on it that can be sent a switch is switched to one
/// target - an account every one of those machines holds a usable spare for, whose fresh reading shows
/// both windows under the threshold, and which is not barred. Best target is the lowest weekly use, then
/// the lowest 5-hour use, then the account email in ordinal order so the choice never depends on input
/// order. No target means nothing is switched and the owner gets exactly one email per exhaustion event.
///
/// Who decides "which machines": the machines that are on the account and can be sent a switch now. A
/// stale machine (no accepted report for <see cref="MachineStaleAfter"/>) or one with an open switch is
/// skipped and does not take part in the target choice, because its holdings are not known to be current
/// - letting it veto a target would hold back the machines whose state is known.
/// </summary>
public static class LicenseDecisionEngine
{
    /// <summary>A window at or above this percentage is tired.</summary>
    public const double ThresholdPercent = 97.0;

    /// <summary>A reading taken while the account was active is stale after this (contract section 5.2).</summary>
    public static readonly TimeSpan ActiveReadingStaleAfter = TimeSpan.FromMinutes(6);

    /// <summary>A reading of a spare is stale after this (contract section 5.2).</summary>
    public static readonly TimeSpan SpareReadingStaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>A machine is stale when its last accepted report is older than this (contract section 5.2).</summary>
    public static readonly TimeSpan MachineStaleAfter = TimeSpan.FromMinutes(6);

    /// <summary>The longest a 5-hour window can run - the end of a bar when the provider gave no reset time.</summary>
    public static readonly TimeSpan FiveHourWindowLength = TimeSpan.FromHours(5);

    /// <summary>The longest a weekly window can run - the end of a bar when the provider gave no reset time.</summary>
    public static readonly TimeSpan SevenDayWindowLength = TimeSpan.FromDays(7);

    // The two read errors after which the Director refuses a spare as not usable (contract 3.3 step 9).
    // Spelled as the contract's LicenseReadErrors; the wiring step points these at that class.
    private const string ReadErrorUnauthorized = "unauthorized";
    private const string ReadErrorRefreshFailed = "refresh-failed";

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

        var stillOpen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var exhaustion in snapshot.OpenExhaustions)
        {
            if (ExhaustionHasEnded(exhaustion, snapshot, readings))
                closed.Add(exhaustion.AccountEmail);
            else
                stillOpen.Add(exhaustion.AccountEmail);
        }

        foreach (var source in AccountsInUse(snapshot))
        {
            if (!readings.TryGetValue(source, out var reading) || IsReadingStale(reading, now))
                continue; // stale numbers never trigger (contract 5.2)

            var tired = TiredWindows(reading);
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
                    emails.Add(BuildNoRoomEmail(snapshot, readings, source, tired, eligible));
                }
                continue;
            }

            var explanation = ThresholdExplanation(source, tired, target);
            foreach (var machine in eligible)
                switches.Add(new LicenseSwitchDecision(machine.MachineName, machine.OsUser, source, target,
                    LicenseDecisionReasons.Threshold, explanation));

            foreach (var window in tired)
            {
                var until = window.ResetsAtUtc ?? now + WindowLength(window.Name);
                var alreadyBarred = snapshot.Bars.Any(b =>
                    SameAccount(b.AccountEmail, source) && b.Window == window.Name && b.BarredUntilUtc >= until);
                if (!alreadyBarred)
                    newBars.Add(new LicenseBar(source, window.Name, until));
            }
        }

        FileLog.Write($"[LicenseDecisionEngine] Decide: switches={switches.Count}, newBars={newBars.Count}, emails={emails.Count}, opened={opened.Count}, closed={closed.Count}, skipped={skipped.Count}");
        return new LicenseDecisions(switches, newBars, emails, opened, closed, skipped);
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
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(sourceAccountEmail))
            throw new ArgumentException("The account to switch away from is required", nameof(sourceAccountEmail));
        FileLog.Write($"[LicenseDecisionEngine] DecideManual: targetNamed={targetAccountEmail is not null}");

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
                    $"{missing.MachineName} holds no usable spare for {target}.");
            if (!readings.TryGetValue(target, out var targetReading) || IsReadingStale(targetReading, snapshot.NowUtc))
                return Refuse(skipped, LicenseManualRefusals.TargetStale,
                    $"The latest reading of {target} is too old to show it has room.");
            if (!HasRoom(targetReading))
                return Refuse(skipped, LicenseManualRefusals.TargetFull,
                    $"{target} is at or over {Percent(ThresholdPercent)} on a window, or a window is unknown.");
        }

        var explanation = $"Switch now: from {source} to {target}.";
        var switches = eligible
            .Select(m => new LicenseSwitchDecision(m.MachineName, m.OsUser, source, target, LicenseDecisionReasons.Manual, explanation))
            .ToList();
        FileLog.Write($"[LicenseDecisionEngine] DecideManual: switches={switches.Count}, skipped={skipped.Count}");
        return new LicenseManualSwitchVerdict(switches, skipped, null, null);
    }

    /// <summary>
    /// The best target for the machines on <paramref name="sourceAccountEmail"/>, or null when none
    /// qualifies. Used by the tick, by Switch now, and by the forecast, so all three agree.
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
        var limit = reading.ReadWhileActive ? ActiveReadingStaleAfter : SpareReadingStaleAfter;
        return nowUtc - readAt > limit;
    }

    /// <summary>True when the machine's last accepted report is older than <see cref="MachineStaleAfter"/>.</summary>
    public static bool IsMachineStale(LicenseMachineState machine, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return nowUtc - machine.LastReportReceivedAtUtc > MachineStaleAfter;
    }

    /// <summary>True when both windows are known and under the threshold.</summary>
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
            .Where(m => !string.IsNullOrWhiteSpace(m.ActiveAccountEmail))
            .Select(m => Normalize(m.ActiveAccountEmail ?? ""))
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

    // ----- the rule's parts -----

    private sealed record TiredWindow(string Name, double Utilization, DateTime? ResetsAtUtc);

    private static string? ChooseTarget(LicenseSnapshot snapshot, IReadOnlyDictionary<string, LicenseAccountReading> readings,
        string source, IReadOnlyList<LicenseMachineState> machines)
    {
        if (machines.Count == 0)
            return null;
        return readings.Values
            .Where(r => WhyNotTarget(snapshot, r, source, machines) is null)
            .OrderBy(r => r.SevenDay?.Utilization ?? double.MaxValue)
            .ThenBy(r => r.FiveHour?.Utilization ?? double.MaxValue)
            .ThenBy(r => r.AccountEmail, StringComparer.Ordinal)
            .Select(r => r.AccountEmail)
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
            return TiredWindows(candidate) is { Count: > 0 } tired
                ? $"it is at {DescribeWindows(tired)}"
                : "a window's use is unknown";
        var bar = snapshot.Bars
            .Where(b => SameAccount(b.AccountEmail, candidate.AccountEmail) && b.BarredUntilUtc > now)
            .OrderByDescending(b => b.BarredUntilUtc)
            .FirstOrDefault();
        if (bar is not null)
            return $"it triggered a switch and is barred until its {WindowLabel(bar.Window)} resets at {Time(bar.BarredUntilUtc)}";
        var missing = machines.FirstOrDefault(m => !HoldsUsableSpare(m, candidate.AccountEmail));
        if (missing is not null)
            return $"{missing.MachineName} holds no usable spare for it";
        return null;
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
            && s.ReadError is not (ReadErrorUnauthorized or ReadErrorRefreshFailed));

    private static List<TiredWindow> TiredWindows(LicenseAccountReading reading)
    {
        var tired = new List<TiredWindow>();
        if (reading.FiveHour is { } five && five.Utilization >= ThresholdPercent)
            tired.Add(new TiredWindow(LicenseWindowNames.FiveHour, five.Utilization, five.ResetsAtUtc));
        if (reading.SevenDay is { } week && week.Utilization >= ThresholdPercent)
            tired.Add(new TiredWindow(LicenseWindowNames.SevenDay, week.Utilization, week.ResetsAtUtc));
        return tired;
    }

    private static bool ExhaustionHasEnded(LicenseExhaustion exhaustion, LicenseSnapshot snapshot,
        IReadOnlyDictionary<string, LicenseAccountReading> readings)
    {
        if (snapshot.NowUtc >= exhaustion.EndsAtUtc)
            return true;
        if (MachinesOn(snapshot, exhaustion.AccountEmail).Count == 0)
            return true; // nothing is on it any more: a switch succeeded or a machine was moved by hand
        // A fresh reading under the threshold on both windows means the window reset early.
        return readings.TryGetValue(Normalize(exhaustion.AccountEmail), out var reading)
            && !IsReadingStale(reading, snapshot.NowUtc)
            && TiredWindows(reading).Count == 0;
    }

    private static DateTime ExhaustionEnd(IReadOnlyList<TiredWindow> tired, DateTime now) =>
        tired.Max(w => w.ResetsAtUtc ?? now + WindowLength(w.Name));

    private static TimeSpan WindowLength(string window) =>
        window == LicenseWindowNames.FiveHour ? FiveHourWindowLength : SevenDayWindowLength;

    // ----- words -----

    private static string ThresholdExplanation(string source, IReadOnlyList<TiredWindow> tired, string target) =>
        $"{source} reached {DescribeWindows(tired)}; switched to {target}, the account with the lowest weekly use that every machine on it holds.";

    private static LicenseOwnerEmail BuildNoRoomEmail(LicenseSnapshot snapshot,
        IReadOnlyDictionary<string, LicenseAccountReading> readings, string source,
        IReadOnlyList<TiredWindow> tired, IReadOnlyList<LicenseMachineState> machines)
    {
        var subject = $"DevThrottle: no Claude account has room - {source} is at {DescribeWindows(tired)}";
        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"{source} has reached {DescribeWindows(tired)}, and no other account can take its machines, so nothing was switched.\r\n\r\n");
        body.Append("Machines on it: ").Append(string.Join(", ", machines.Select(m => m.MachineName))).Append(".\r\n\r\n");
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
    {
        FileLog.Write($"[LicenseDecisionEngine] DecideManual: refused {code}");
        return new LicenseManualSwitchVerdict(Array.Empty<LicenseSwitchDecision>(), skipped, code, message);
    }

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
