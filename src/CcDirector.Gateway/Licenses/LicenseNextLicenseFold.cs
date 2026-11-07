using System.Globalization;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>What a machine's next license comes to (<see cref="LicenseNextLicense.State"/>).</summary>
public static class LicenseNextLicenseStates
{
    /// <summary>Both windows of the license it is on are under <see cref="LicenseNextLicenseFold.WarnPercent"/>: nothing is needed yet.</summary>
    public const string NotNeeded = "not-needed";

    /// <summary>At <see cref="LicenseNextLicenseFold.WarnPercent"/> or more, and the ready licenses it holds last until the
    /// license it is on resets - or every license with room is already ready on it.</summary>
    public const string Ready = "ready";

    /// <summary>At <see cref="LicenseNextLicenseFold.WarnPercent"/> or more, and it does not hold enough READY next licenses:
    /// the machine will be stuck. The one state that is a problem.</summary>
    public const string Needed = "needed";

    /// <summary>No other license has room: status on the Licenses page only, never an alarm.</summary>
    public const string NoRoom = "no-room";

    /// <summary>It cannot be judged now: the reading of the license it is on is too old, or the machine cannot be sent a
    /// switch now (not reporting, mid-switch, refused, held because its switch keeps failing). Those have their own words.</summary>
    public const string NotJudged = "not-judged";
}

/// <summary>The sign-in that gives a machine its recommended next license (<see cref="LicenseNextLicense.SignInAction"/>).</summary>
public static class LicenseNextLicenseActions
{
    /// <summary>The machine holds no sign-in for it: Add a spare.</summary>
    public const string AddSpare = "add-spare";

    /// <summary>The machine holds a sign-in for it that is refused or ends too soon: Renew now.</summary>
    public const string Renew = "renew";
}

/// <summary>One machine's next license, judged (<see cref="LicenseNextLicenseFold.Judge"/>).</summary>
/// <param name="MachineId">The machine's identifier on the Licenses page.</param>
/// <param name="Label">What the machine is called (<see cref="LicenseSignInFold.Label"/>).</param>
/// <param name="ActiveAccountEmail">The license it is on, lower-cased.</param>
/// <param name="State">One of <see cref="LicenseNextLicenseStates"/>.</param>
/// <param name="Tired">The windows of the license it is on at or above <see cref="LicenseNextLicenseFold.WarnPercent"/>, in
/// words ("85% of its weekly limit"); null when none is.</param>
/// <param name="AtThreshold">A window of the license it is on is at or above the switching threshold: rule 4, red.</param>
/// <param name="NeededUntilUtc">When the license it is on resets - the latest reset of its windows at or above
/// <see cref="LicenseNextLicenseFold.WarnPercent"/>; null when that is not known or nothing is needed.</param>
/// <param name="ReadyAccounts">The licenses with room it is READY for now (signed in, fresh, not refused), best first.</param>
/// <param name="RecommendedAccountEmail">When <see cref="State"/> is <see cref="LicenseNextLicenseStates.Needed"/>: the license
/// to sign it in to (<see cref="LicenseNextLicenseFold.Recommend"/>); else null.</param>
/// <param name="SignInAction">With <paramref name="RecommendedAccountEmail"/>: one of <see cref="LicenseNextLicenseActions"/>.</param>
/// <param name="Why">When needed: why, as a clause with no subject and no full stop ("holds no sign-in for account-b@example.com",
/// "account-c@example.com would fill before account-a@example.com resets at 2026-10-12 03:00 UTC"); else null.</param>
/// <param name="Text">The finished sentence the Licenses page shows.</param>
/// <param name="Tone">One of <see cref="LicenseTones"/>.</param>
public sealed record LicenseNextLicense(
    Guid MachineId,
    string MachineName,
    string OsUser,
    string Label,
    string ActiveAccountEmail,
    string State,
    string? Tired,
    bool AtThreshold,
    DateTime? NeededUntilUtc,
    IReadOnlyList<string> ReadyAccounts,
    string? RecommendedAccountEmail,
    string? SignInAction,
    string? Why,
    string Text,
    string Tone)
{
    /// <summary>The machine will be stuck: rule 3 (yellow), or rule 4 (red) at the threshold.</summary>
    public bool IsNeeded => State == LicenseNextLicenseStates.Needed;
}

/// <summary>
/// A MACHINE NEEDS A READY NEXT LICENSE ONLY WHEN IT IS RUNNING OUT (mission "Licenses - Always Ready, Loud When Not", the
/// owner's ruling of 7 November 2026: "We don't need all licenses logged in at all times. When we run out, we just need a
/// new one that becomes available when the one we have will expire soon."). The coverage grid, the cannot-switch warning,
/// the daily fire drill and the Licenses answer all read this one verdict, per machine:
/// <list type="number">
/// <item>Under <see cref="WarnPercent"/> in both windows of the license it is on: no problem.</item>
/// <item><see cref="WarnPercent"/> or more, and a READY next license held on this machine: no problem. It still switches at
/// the threshold as before.</item>
/// <item><see cref="WarnPercent"/> or more, and no READY next license: needed - the cannot-switch warning, yellow, naming the
/// recommended license.</item>
/// <item>At the threshold or more and still none: needed, red (<see cref="LicenseNextLicense.AtThreshold"/>; the stranded
/// alarm owns it).</item>
/// <item>No other license has room: status only (<see cref="LicenseNextLicenseStates.NoRoom"/>).</item>
/// </list>
///
/// READY: the machine is ready for the license (<see cref="LicenseDecisionEngine.Readiness"/>, the one definition: signed in,
/// not refused, not ending before a switch could finish) AND, at today's pace, its room lasts until the license the machine
/// is on resets. Room is the weekly room left (threshold less its weekly use); today's pace is the weekly pace of the
/// license the machine is on now, which is the work the next license would carry. The next license carries that work from
/// the moment the license in use reaches the threshold - at today's pace - until that license resets, so that is the time
/// it must last. When one ready license would fill before the reset, the next ready one is counted after it, best first;
/// until the ready licenses together last, the machine needs one more. With no pace known yet, or no reset time known, the
/// ready licenses are taken as lasting: there is nothing to say they will not, and a warning nobody can explain is not
/// raised. Every license with room already ready on the machine is no problem either: there is nothing left to sign in. At
/// the threshold a machine with a ready license is switched to it on this minute, so it is ready: whether its NEW license
/// lasts is judged on that license once it is on it.
///
/// RECOMMENDED NEXT (<see cref="Recommend"/>): the license with the most room left this week; when two are within
/// <see cref="CloseWithinPercent"/> of each other, the one that resets sooner. Pure: facts go in, an answer comes out.
/// </summary>
public static class LicenseNextLicenseFold
{
    /// <summary>The use of the license a machine is on, in either window, at or above which it needs a ready next license.</summary>
    public const double WarnPercent = LicenseReadinessFold.AlarmPercent;

    /// <summary>Two licenses whose weekly use is within this many percentage points of each other are close: the one that
    /// resets sooner is recommended.</summary>
    public const double CloseWithinPercent = 5.0;

    /// <summary>
    /// The weekly pace of each license from <paramref name="forecast"/>: its recent pace when the forecast has one, else its
    /// average this week. Null for a license whose forecast is stale or shows no pace.
    /// </summary>
    public static Func<string, double?> WeeklyPace(LicenseForecast forecast)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        var pace = forecast.Accounts
            .Where(a => !a.IsStale)
            .ToDictionary(a => LicenseStore.NormalizeEmail(a.AccountEmail),
                a => a.SevenDay.RecentPercentPerHour ?? a.SevenDay.AveragePercentPerHour ?? a.SevenDay.PercentPerHour,
                StringComparer.Ordinal);
        return account => pace.TryGetValue(LicenseStore.NormalizeEmail(account), out var perHour) ? perHour : null;
    }

    /// <summary>Every machine of <paramref name="picture"/> that is on a license, judged. In the picture's order.</summary>
    /// <param name="weeklyPacePerHour">For a license: its weekly pace in percent per hour (<see cref="WeeklyPace"/>), or null
    /// when it is not known.</param>
    public static IReadOnlyList<LicenseNextLicense> Judge(LicensePicture picture, Func<string, double?> weeklyPacePerHour)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(weeklyPacePerHour);
        var snapshot = picture.Snapshot;
        var now = snapshot.NowUtc;
        var machines = picture.Overview.Machines;
        var states = snapshot.Machines.ToDictionary(m => Key(m.MachineName, m.OsUser), StringComparer.Ordinal);
        var readings = snapshot.Accounts.ToDictionary(r => LicenseStore.NormalizeEmail(r.AccountEmail), StringComparer.Ordinal);
        var judged = new List<LicenseNextLicense>();
        foreach (var machine in machines)
        {
            if (machine.ActiveAccountEmail is not { } activeRaw)
                continue;
            var active = LicenseStore.NormalizeEmail(activeRaw);
            var state = states[Key(machine.MachineName, machine.OsUser)];
            var label = LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser);
            LicenseNextLicense Verdict(string verdictState, string text, string tone, string? tired = null, bool atThreshold = false,
                DateTime? until = null, IReadOnlyList<string>? ready = null, string? recommended = null, string? action = null, string? why = null)
                => new(machine.MachineId, machine.MachineName, machine.OsUser, label, active, verdictState, tired, atThreshold, until,
                    ready ?? Array.Empty<string>(), recommended, action, why, text, tone);

            if (!readings.TryGetValue(active, out var reading) || LicenseDecisionEngine.IsReadingStale(reading, now))
            {
                judged.Add(Verdict(LicenseNextLicenseStates.NotJudged,
                    $"Not judged: the reading of {active} is too old to say whether a next license is needed.", LicenseTones.Neutral));
                continue;
            }
            var atLevel = WindowsAt(reading, now, WarnPercent);
            if (atLevel.Count == 0)
            {
                judged.Add(Verdict(LicenseNextLicenseStates.NotNeeded,
                    $"{active} is under {Percent(WarnPercent)} of both limits, so no next license is needed yet.", LicenseTones.Good));
                continue;
            }
            var tired = string.Join(" and ", atLevel.Select(w => $"{LicenseDecisionEngine.Percent(w.Window.Utilization)} of its {w.Label}"));
            var atThreshold = atLevel.Any(w => LicenseDecisionEngine.IsTired(w.Window, now));
            var ranked = LicenseDecisionEngine.TargetsWithRoom(snapshot, active);
            if (ranked.Count == 0)
            {
                judged.Add(Verdict(LicenseNextLicenseStates.NoRoom,
                    $"{active} is at {tired}, and no other license has room to switch to.", LicenseTones.Neutral, tired, atThreshold));
                continue;
            }
            var switchable = LicenseDecisionEngine.SwitchableMachines(snapshot, active)
                .Any(m => Key(m.MachineName, m.OsUser) == Key(machine.MachineName, machine.OsUser));
            if (!switchable || LicenseDecisionEngine.IsHeld(state, now))
            {
                judged.Add(Verdict(LicenseNextLicenseStates.NotJudged,
                    $"Not judged: {label} cannot be sent a switch now, so whether it has a next license is said by its status.",
                    LicenseTones.Neutral, tired, atThreshold));
                continue;
            }
            var until = atLevel.All(w => w.Window.ResetsAtUtc is not null) ? atLevel.Max(w => w.Window.ResetsAtUtc) : null;
            var ready = ranked.Where(t => LicenseDecisionEngine.Readiness(state, t, now).IsReady).ToList();
            var notReady = ranked.Where(t => !ready.Contains(t)).ToList();
            var lasts = Lasts(ready, readings, weeklyPacePerHour(active), reading.SevenDay?.Utilization, until, now);
            if (ready.Count > 0 && (notReady.Count == 0 || atThreshold || lasts))
            {
                var text = lasts
                    ? $"Ready: {Join(ready)} {(ready.Count == 1 ? "has" : "have")} room to last until {active} resets."
                    : notReady.Count == 0
                        ? $"Ready: signed in to every license with room ({Join(ready)}); at today's pace they may fill before {active} resets at {Clock(until!.Value)}."
                        : $"Ready: it switches to {ready[0]} now; whether that lasts is judged once it is on it.";
                judged.Add(Verdict(LicenseNextLicenseStates.Ready, text, LicenseTones.Good, tired, atThreshold, until, ready));
                continue;
            }
            var recommended = Recommend(snapshot, notReady)
                ?? throw new InvalidOperationException("A machine that needs a next license has no license to recommend.");
            var readiness = LicenseDecisionEngine.Readiness(state, recommended, now);
            var action = readiness.NotReadyReason == LicenseNotReadyReasons.NoSignIn ? LicenseNextLicenseActions.AddSpare : LicenseNextLicenseActions.Renew;
            var why = ready.Count == 0
                ? LicenseDecisionEngine.NotReadyWhy(readiness) ?? throw new InvalidOperationException("A license that is not ready has no reason.")
                : $"{Join(ready)} would fill before {active} resets at {Clock(until!.Value)}";
            var verb = action == LicenseNextLicenseActions.AddSpare ? "Sign it in to" : "Renew its sign-in for";
            var needed = ready.Count == 0
                ? $"Needs a next license: {active} is at {tired}, and it {why}. {verb} {recommended}."
                : $"Needs one more license: {active} is at {tired}, and {why}. {verb} {recommended}.";
            judged.Add(Verdict(LicenseNextLicenseStates.Needed, needed, atThreshold ? LicenseTones.Bad : LicenseTones.Warning,
                tired, atThreshold, until, ready, recommended, action, why));
        }
        return judged;
    }

    /// <summary>Copy each machine's verdict onto its line of the Licenses page, as data the clients render as given.</summary>
    public static void Apply(LicenseOverviewResponse overview, IReadOnlyList<LicenseNextLicense> judged)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(judged);
        foreach (var dto in overview.Machines)
        {
            var mine = judged.FirstOrDefault(j => j.MachineId.ToString("N") == dto.MachineId);
            dto.NextLicenseState = mine?.State;
            dto.NextLicenseText = mine?.Text;
            dto.NextLicenseTone = mine?.Tone;
            dto.RecommendedAccountEmail = mine?.RecommendedAccountEmail;
            dto.RecommendedSignInAction = mine?.SignInAction;
        }
    }

    /// <summary>
    /// The license to sign a machine in to next, from <paramref name="candidates"/>: the one with the most room left this
    /// week (the lowest weekly use); of those within <see cref="CloseWithinPercent"/> of it, the one whose week resets
    /// sooner (a reset not known counts as last); then the lower weekly use; then the email in ordinal order. Null when
    /// there is no candidate with a weekly reading.
    /// </summary>
    public static string? Recommend(LicenseSnapshot snapshot, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidates);
        var readings = snapshot.Accounts.ToDictionary(r => LicenseStore.NormalizeEmail(r.AccountEmail), StringComparer.Ordinal);
        var weekly = candidates
            .Select(LicenseStore.NormalizeEmail)
            .Distinct(StringComparer.Ordinal)
            .Where(c => readings.TryGetValue(c, out var r) && r.SevenDay is not null)
            .Select(c => (Account: c, Week: readings[c].SevenDay!))
            .ToList();
        if (weekly.Count == 0)
            return null;
        var most = weekly.Min(c => c.Week.Utilization);
        return weekly
            .Where(c => c.Week.Utilization - most <= CloseWithinPercent)
            .OrderBy(c => c.Week.ResetsAtUtc ?? DateTime.MaxValue)
            .ThenBy(c => c.Week.Utilization)
            .ThenBy(c => c.Account, StringComparer.Ordinal)
            .First().Account;
    }

    // The ready licenses, best first, last at today's pace from when the license in use reaches the threshold until it
    // resets: each one's weekly room over the pace, added up. No pace or no reset known: taken as lasting (the class summary).
    private static bool Lasts(IReadOnlyList<string> ready, IReadOnlyDictionary<string, LicenseAccountReading> readings,
        double? pacePerHour, double? activeWeekly, DateTime? until, DateTime now)
    {
        if (until is not { } resets || pacePerHour is not { } pace || pace <= 0)
            return true;
        var untilSwitch = activeWeekly is { } used ? Math.Max(0, LicenseDecisionEngine.ThresholdPercent - used) / pace : 0;
        var hoursNeeded = (resets - now).TotalHours - untilSwitch;
        if (hoursNeeded <= 0)
            return true; // it resets before it would reach the threshold
        if (ready.Count == 0)
            return false;
        var hours = 0.0;
        foreach (var account in ready)
        {
            var week = readings.TryGetValue(account, out var r) ? r.SevenDay : null;
            if (week is null)
                return true; // a ready license with no weekly number: nothing says it fills
            hours += Math.Max(0, LicenseDecisionEngine.ThresholdPercent - week.Utilization) / pace;
            if (hours >= hoursNeeded)
                return true;
        }
        return false;
    }

    private sealed record LevelWindow(LicenseWindowReading Window, string Label);

    private static List<LevelWindow> WindowsAt(LicenseAccountReading reading, DateTime now, double percent)
    {
        var at = new List<LevelWindow>();
        if (reading.FiveHour is { } five && LicenseDecisionEngine.IsAtOrAbove(five, now, percent))
            at.Add(new LevelWindow(five, "5-hour window"));
        if (reading.SevenDay is { } week && LicenseDecisionEngine.IsAtOrAbove(week, now, percent))
            at.Add(new LevelWindow(week, "weekly limit"));
        return at;
    }

    private static string Join(IReadOnlyList<string> accounts) => accounts.Count switch
    {
        1 => accounts[0],
        _ => string.Join(", ", accounts.Take(accounts.Count - 1)) + " and " + accounts[^1],
    };

    private static string Percent(double value) => value.ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Clock(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Key(string machineName, string osUser)
        => machineName.Trim().ToLowerInvariant() + "\n" + osUser.Trim().ToLowerInvariant();
}
