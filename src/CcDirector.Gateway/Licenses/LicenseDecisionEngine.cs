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
/// above <see cref="ThresholdPercent"/>, every machine on it that can be sent a switch and is READY is switched.
///
/// AN ACCOUNT WITH ROOM is chosen on the ACCOUNT's own merits (<see cref="TargetsWithRoom"/>): not the account in
/// use, a fresh reading with both windows under the threshold, and not barred. Best is the lowest weekly use, then
/// the lowest 5-hour use, then the account email in ordinal order so the choice never depends on input order. The
/// best of them is <see cref="NextTarget"/>. Which sign-ins the machines hold never decides whether an account
/// has room; it decides which machines can move to it.
///
/// A MACHINE IS READY for an account when it holds a usable sign-in for it whose expiry does not come at or before
/// the command's <see cref="LicenseContract.SwitchNotAfter"/> (contract 5.7). <see cref="Readiness"/> is the one
/// definition of ready; nothing else in the Gateway works it out.
///
/// PER MACHINE (the owner's ruling of 16 October 2026: "Switch every ready machine at once; the one left behind
/// keeps trying and switches the moment it is ready"). Each machine goes to the best account with room that IT is
/// ready for. A machine ready for none is left behind: it is not switched, it holds back no other machine, and it
/// is switched on a later tick by this same rule the moment it is ready, while the account is still tired. Only when
/// NO account has room is it the no-room event and its one owner email; an account with room that some machine is
/// not ready for is no such event.
///
/// Who decides "which machines": the machines that are on the account and can be sent a switch now. A
/// stale machine (no accepted report for <see cref="LicenseContract.MachineStaleAfter"/>) or one with an
/// open switch is skipped, because its holdings are not known to be current.
///
/// A SWITCH THAT KEEPS FAILING. A machine whose latest switch was at least the second in a row to fail the same
/// way is HELD (<see cref="IsHeld"/>): the automatic decision leaves it out until its hold has passed, so the same
/// switch is not decided again every minute for as long as a window stays tired. When an account has room and every
/// machine that could be sent a switch is held, nothing can be switched, and that is treated as the no-room case: the
/// same event, and its one owner email, which then says that the switch keeps failing. Switch now ignores a hold, as
/// it ignores a bar: a person asking has decided to try.
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
            if (JudgeTired(snapshot, readings, source) is not { } judged)
                continue; // stale numbers never trigger (contract 5.2), and an account under the threshold is left alone

            var tired = judged.Tired;
            skipped.AddRange(judged.Skipped);
            if (NothingSwitchedEmail(snapshot, readings, source, judged) is { } email)
            {
                // No account has room, or every machine that could be sent a switch is held because its switch keeps failing.
                if (!stillOpen.Contains(source))
                {
                    var exhaustion = new LicenseExhaustion(source, now, ExhaustionEnd(tired, now));
                    opened.Add(exhaustion);
                    stillOpen.Add(source);
                    emails.Add(email);
                }
                continue;
            }
            if (judged.Ranked.Count == 0 || judged.Free.Count == 0)
                continue; // every machine on it is stale or mid-switch; nothing can be sent this tick

            // Every ready machine moves; one left behind is skipped with its reason and moves on a later tick.
            var plan = PlanMoves(judged.Free, judged.Ranked, now);
            skipped.AddRange(plan.LeftBehind.Select(r => new LicenseSkippedMachine(r.MachineName, r.OsUser, source,
                "it " + NotReadyClause(r, r.TargetAccountEmail) + " and " + SwitchesWhenReady(r))));
            if (plan.Moves.Count == 0)
                continue; // an account has room and no machine on this one is ready for it yet: no event, no email

            var explanation = ThresholdExplanation(snapshot, source, tired, judged.Ranked[0], plan);
            foreach (var (machine, target) in plan.Moves)
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
    /// others on it. With a target named, the machines ready for it (<see cref="Readiness"/>) move to it, and each
    /// machine that is not ready stays where it is, listed in <see cref="LicenseManualSwitchVerdict.Skipped"/> with
    /// its reason. A named target is refused when it is the account in use, when its reading is stale, when either
    /// window is at or over the threshold or unknown, or when no machine to be switched is ready for it. A named
    /// target that is barred is allowed - the bar exists to stop the AUTOMATIC choice from bouncing back, and a
    /// person naming it has decided otherwise. With no target named, the moves are exactly the automatic ones, bars
    /// included: each machine to the best account with room it is ready for. Switch now records no bar: nothing
    /// triggered.
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

        IReadOnlyList<string> ranked;
        if (target is null)
        {
            ranked = TargetsWithRoom(snapshot, readings, source);
            if (ranked.Count == 0)
                return Refuse(skipped, LicenseManualRefusals.NoTarget, NoAccountHasRoomText(snapshot, readings, source));
        }
        else
        {
            if (SameAccount(target, source))
                return Refuse(skipped, LicenseManualRefusals.TargetIsSource, $"The machines are already on {target}.");
            if (!readings.TryGetValue(target, out var targetReading) || IsReadingStale(targetReading, snapshot.NowUtc))
                return Refuse(skipped, LicenseManualRefusals.TargetStale,
                    $"The latest reading of {target} is too old to show it has room.");
            if (!HasRoom(targetReading))
                return Refuse(skipped, LicenseManualRefusals.TargetFull,
                    $"{target} has no room: {NoRoomReason(targetReading, snapshot.NowUtc)}.");
            ranked = [target];
        }

        var plan = PlanMoves(eligible, ranked, snapshot.NowUtc);
        if (plan.Moves.Count == 0)
        {
            // The account has room and no machine on the source is ready for it: the refusal says which and why, for
            // every one, so they are not listed again as skipped.
            var code = plan.LeftBehind.All(r => r.NotReadyReason == LicenseNotReadyReasons.SignInExpiresTooSoon)
                ? LicenseManualRefusals.TargetSignInExpiring
                : LicenseManualRefusals.TargetNotHeld;
            return Refuse(skipped, code, $"{ranked[0]} has room; "
                + string.Join("; ", plan.LeftBehind.Select(r => $"{Label(snapshot, r.MachineName, r.OsUser)} {NotReadyClause(r, "it")}")) + ".");
        }

        skipped.AddRange(plan.LeftBehind.Select(r => new LicenseSkippedMachine(r.MachineName, r.OsUser, source,
            "it " + NotReadyClause(r, r.TargetAccountEmail))));
        var explanation = ManualExplanation(snapshot, source, plan);
        var switches = plan.Moves
            .Select(m => new LicenseSwitchDecision(m.Machine.MachineName, m.Machine.OsUser, source, m.Target, LicenseSwitchReasons.Manual, explanation))
            .ToList();
        return new LicenseManualSwitchVerdict(switches, skipped, null, null);
    }

    /// <summary>
    /// The owner email for <paramref name="accountEmail"/>'s no-room event as things stand NOW, or null when it
    /// cannot truthfully be written now: the account's reading is stale or shows no tired window, no machine on
    /// it can be sent a switch, or a switch can be sent - a target exists and a machine that is not held can take
    /// it. It is the "no account has room" email, or the "switch keeps failing" one when a target has room and
    /// every machine that could be sent the switch is held. <see cref="Decide"/> writes the email once, on the tick that
    /// opens the event; this is for the minute check to write it again when that send failed, from the facts
    /// of the later tick rather than a stored copy of the first one.
    /// </summary>
    public static LicenseOwnerEmail? NoRoomEmail(LicenseSnapshot snapshot, string accountEmail)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(accountEmail))
            throw new ArgumentException("The account the event is about is required", nameof(accountEmail));
        var source = Normalize(accountEmail);
        var readings = IndexReadings(snapshot.Accounts);
        return JudgeTired(snapshot, readings, source) is { } judged
            ? NothingSwitchedEmail(snapshot, readings, source, judged)
            : null;
    }

    /// <summary>
    /// True when the automatic decision leaves <paramref name="machine"/> out at <paramref name="nowUtc"/> because its
    /// switch keeps failing: its hold is still ahead, and it is still on the account the failing switch was to leave.
    /// A machine that has since been moved to another account is a new case, and is not held.
    /// </summary>
    public static bool IsHeld(LicenseMachineState machine, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return machine.Hold is { } hold && hold.HeldUntilUtc > nowUtc
            && machine.ActiveAccountEmail is { } active && SameAccount(active, hold.FromAccountEmail);
    }

    /// <summary>
    /// What the Gateway says of a held machine, as one clause with no full stop, for a sentence that names the machine
    /// itself: which switch keeps failing, why in the Gateway's own words, and until when it is held. Null when the
    /// machine is not held at <paramref name="nowUtc"/>.
    /// </summary>
    public static string? HeldWhy(LicenseMachineState machine, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (!IsHeld(machine, nowUtc) || machine.Hold is not { } hold)
            return null;
        return $"its switch from {hold.FromAccountEmail} to {hold.ToAccountEmail} has failed more than once in a row{HoldReason(hold)}, "
               + $"so the automatic switch leaves it out until {Time(hold.HeldUntilUtc)}";
    }

    /// <summary>
    /// THE ONE DEFINITION OF READY. <paramref name="machine"/> is ready to move to <paramref name="targetAccountEmail"/>
    /// at <paramref name="nowUtc"/> when its latest report shows a sign-in for that account whose last read was not
    /// refused (unauthorized, or the refresh failed), and whose expiry, when known, comes after the switch command's
    /// not-after time (now plus <see cref="LicenseContract.SwitchNotAfter"/>, contract 5.7). An unknown expiry does
    /// not make a machine unready; the Director still refuses an expired sign-in itself. The answer says why not, as
    /// a fact (<see cref="LicenseNotReadyReasons"/>), and <see cref="NotReadyWhy"/> puts it into words.
    /// The automatic switch, Switch now, the Licenses page and the license alarm all ask this; none works it out
    /// for itself.
    /// </summary>
    public static LicenseReadiness Readiness(LicenseMachineState machine, string targetAccountEmail, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (string.IsNullOrWhiteSpace(targetAccountEmail))
            throw new ArgumentException("The account to move to is required", nameof(targetAccountEmail));
        var target = Normalize(targetAccountEmail);
        var spares = machine.Spares.Where(s => SameAccount(s.AccountEmail, target)).ToList();
        var usable = spares.Where(s => s.ReadError is not (LicenseReadErrors.Unauthorized or LicenseReadErrors.RefreshFailed)).ToList();
        if (usable.Count == 0)
            return new LicenseReadiness(machine.MachineName, machine.OsUser, target,
                spares.Count == 0 ? LicenseNotReadyReasons.NoSignIn : LicenseNotReadyReasons.SignInRefused,
                spares.Select(s => s.SignInExpiresAtUtc).FirstOrDefault(e => e is not null));
        var tooSoon = usable.FirstOrDefault(s => s.SignInExpiresAtUtc is { } expires && expires <= nowUtc + LicenseContract.SwitchNotAfter);
        return tooSoon is not null
            ? new LicenseReadiness(machine.MachineName, machine.OsUser, target, LicenseNotReadyReasons.SignInExpiresTooSoon, tooSoon.SignInExpiresAtUtc)
            : new LicenseReadiness(machine.MachineName, machine.OsUser, target, null, usable[0].SignInExpiresAtUtc);
    }

    /// <summary>
    /// Why a machine is not ready, as a clause with no subject and no full stop, for a sentence that names the
    /// machine first: "holds no sign-in for alice@example.com", "holds a sign-in for alice@example.com that was
    /// refused", "holds a sign-in for alice@example.com that expires at 2026-10-16 03:05 UTC, before a switch could
    /// finish". Null when the machine is ready.
    /// </summary>
    public static string? NotReadyWhy(LicenseReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        return readiness.IsReady ? null : NotReadyClause(readiness, readiness.TargetAccountEmail);
    }

    /// <summary>
    /// Every account that has room for the machines on <paramref name="sourceAccountEmail"/>, best first, judged on
    /// each ACCOUNT's own merits: not the source, a fresh reading with both windows under the threshold, and not
    /// barred. Best is the lowest weekly use, then the lowest 5-hour use, then the account email in ordinal order.
    /// Which sign-ins the machines hold plays no part: that decides which machines can move (<see cref="Readiness"/>),
    /// not whether an account has room. Empty means no account has room - the one case that is the no-room event.
    /// </summary>
    public static IReadOnlyList<string> TargetsWithRoom(LicenseSnapshot snapshot, string sourceAccountEmail)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(sourceAccountEmail))
            throw new ArgumentException("The account in use is required", nameof(sourceAccountEmail));
        return TargetsWithRoom(snapshot, IndexReadings(snapshot.Accounts), Normalize(sourceAccountEmail));
    }

    /// <summary>
    /// The account the machines on <paramref name="sourceAccountEmail"/> would switch to next if the switch happened
    /// now: the best of <see cref="TargetsWithRoom"/>, or null when no account has room. A machine that is not ready
    /// for it moves to the best account with room it IS ready for, or stays until it is ready.
    /// </summary>
    public static string? NextTarget(LicenseSnapshot snapshot, string sourceAccountEmail)
        => TargetsWithRoom(snapshot, sourceAccountEmail).FirstOrDefault();

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

    /// <summary>What one account in use comes to when a fresh reading shows a tired window.</summary>
    /// <param name="Tired">The windows at or over the threshold.</param>
    /// <param name="Free">The machines on it that can be sent a switch and are not held.</param>
    /// <param name="Held">The machines on it that could be sent a switch, and are held because theirs keeps failing.</param>
    /// <param name="Skipped">Every machine on it that is not sent a switch now, and why - the held ones included.</param>
    /// <param name="Ranked">Every account with room, best first (<see cref="TargetsWithRoom"/>); empty when none has.</param>
    private sealed record TiredAccount(List<TiredWindow> Tired, List<LicenseMachineState> Free, List<LicenseMachineState> Held,
        List<LicenseSkippedMachine> Skipped, IReadOnlyList<string> Ranked)
    {
        /// <summary>The machines nothing-switched is judged for: the free ones - and when there is none, the held
        /// ones, because whether an account has room says WHICH reason nothing can be switched for.</summary>
        public List<LicenseMachineState> Deciding => Free.Count > 0 ? Free : Held;
    }

    /// <summary>What the per-machine rule comes to: each ready machine with the account it moves to, and each machine
    /// ready for no account with room, with its readiness for the BEST one - the reason it gives.</summary>
    private sealed record MovePlan(List<(LicenseMachineState Machine, string Target)> Moves, List<LicenseReadiness> LeftBehind);

    // Each machine to the best account in ranked that it is ready for. ranked must not be empty.
    private static MovePlan PlanMoves(IReadOnlyList<LicenseMachineState> machines, IReadOnlyList<string> ranked, DateTime now)
    {
        var moves = new List<(LicenseMachineState, string)>();
        var leftBehind = new List<LicenseReadiness>();
        foreach (var machine in machines)
        {
            var target = ranked.FirstOrDefault(t => Readiness(machine, t, now).IsReady);
            if (target is not null)
                moves.Add((machine, target));
            else
                leftBehind.Add(Readiness(machine, ranked[0], now));
        }
        return new MovePlan(moves, leftBehind);
    }

    // Null when the account's reading is stale or shows no tired window.
    private static TiredAccount? JudgeTired(LicenseSnapshot snapshot, IReadOnlyDictionary<string, LicenseAccountReading> readings, string source)
    {
        var now = snapshot.NowUtc;
        if (!readings.TryGetValue(source, out var reading) || IsReadingStale(reading, now))
            return null;
        var tired = TiredWindows(reading, now);
        if (tired.Count == 0)
            return null;
        var (eligible, skipped) = SplitMachines(snapshot, source);
        var free = eligible.Where(m => !IsHeld(m, now)).ToList();
        var held = eligible.Where(m => IsHeld(m, now)).ToList();
        skipped.AddRange(held.Select(m => new LicenseSkippedMachine(m.MachineName, m.OsUser, source,
            HeldWhy(m, now) ?? throw new InvalidOperationException("A held machine has no hold to describe."))));
        return new TiredAccount(tired, free, held, skipped, TargetsWithRoom(snapshot, readings, source));
    }

    // The one owner email of the event when nothing can be switched for a tired account, or null when something
    // can - or when no machine on it could be sent a switch at all (stale or mid-switch), which is no event. An
    // account with room that no free machine is ready for is no event either: the owner is not told "no room" when
    // there is room (the 16 October 2026 defect).
    private static LicenseOwnerEmail? NothingSwitchedEmail(LicenseSnapshot snapshot,
        IReadOnlyDictionary<string, LicenseAccountReading> readings, string source, TiredAccount judged)
    {
        if (judged.Deciding.Count == 0)
            return null;
        if (judged.Ranked.Count == 0)
            return BuildNoRoomEmail(snapshot, readings, source, judged.Tired, judged.Deciding,
                judged.Skipped.Where(s => !judged.Deciding.Any(m => IsMachine(m, s))).ToList());
        return judged.Free.Count == 0 ? BuildSwitchKeepsFailingEmail(snapshot, source, judged) : null;
    }

    private static bool IsMachine(LicenseMachineState machine, LicenseSkippedMachine skipped)
        => machine.MachineName == skipped.MachineName && machine.OsUser == skipped.OsUser;

    private static IReadOnlyList<string> TargetsWithRoom(LicenseSnapshot snapshot, IReadOnlyDictionary<string, LicenseAccountReading> readings,
        string source)
    {
        // A qualifying reading has both windows (HasRoom checked them), so a missing one is a defect here.
        return readings.Values
            .Where(r => WhyNotTarget(snapshot, r, source) is null)
            .Select(r => r is { SevenDay: { } week, FiveHour: { } five }
                ? (r.AccountEmail, Week: week.Utilization, Five: five.Utilization)
                : throw new InvalidOperationException($"Target candidate {r.AccountEmail} passed the room check without both windows."))
            .OrderBy(c => c.Week)
            .ThenBy(c => c.Five)
            .ThenBy(c => c.AccountEmail, StringComparer.Ordinal)
            .Select(c => c.AccountEmail)
            .ToList();
    }

    // Null when the account has room as a target, on its own merits; otherwise one plain clause saying why not,
    // which is also what the owner's no-room email lists for each account. No machine's sign-ins are looked at.
    private static string? WhyNotTarget(LicenseSnapshot snapshot, LicenseAccountReading candidate, string source)
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
            // Refused first: it is also why the machine's last accepted report grows old.
            if (machine.ReportsRefused)
                why = ReportsRefusedWhy;
            else if (IsMachineStale(machine, snapshot.NowUtc))
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

    /// <summary>Why a machine whose reports are refused is not sent a switch, as a clause the skip sentences end with.</summary>
    public const string ReportsRefusedWhy = "its reports are being refused, so what it holds now is not known";

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

    // The switch log's sentence for a threshold switch: which machines moved where and why there, and each machine
    // left behind with its real reason. "A reached 97% of its weekly limit; machine-a and machine-b switched to B, the
    // account with the lowest weekly use that has room; machine-c holds no sign-in for it and switches as soon as it does."
    private static string ThresholdExplanation(LicenseSnapshot snapshot, string source, IReadOnlyList<TiredWindow> tired,
        string best, MovePlan plan)
    {
        var parts = new List<string>();
        var groups = plan.Moves.GroupBy(m => m.Target, StringComparer.Ordinal).ToList();
        foreach (var group in groups)
        {
            var names = group.Select(m => Label(snapshot, m.Machine)).ToList();
            parts.Add(group.Key == best
                ? $"{Join(names)} switched to {best}, the account with the lowest weekly use that has room"
                : $"{Join(names)} switched to {group.Key}, the best account with room {(names.Count == 1 ? "it holds" : "they hold")} a sign-in for");
        }
        // A machine left behind gives its reason for the BEST account. "it" can only stand for that account when it is the
        // one account named before: with two named, "it" would read as the last one, so the account is named again.
        var itIsBest = groups.Count == 1 && groups[0].Key == best;
        parts.AddRange(plan.LeftBehind.Select(r =>
            $"{Label(snapshot, r.MachineName, r.OsUser)} {NotReadyClause(r, itIsBest ? "it" : r.TargetAccountEmail)} and {SwitchesWhenReady(r)}"));
        return $"{source} reached {DescribeWindows(tired)}; {string.Join("; ", parts)}.";
    }

    // The switch log's sentence for Switch now: where the machines went, and each one that stays, with its reason.
    private static string ManualExplanation(LicenseSnapshot snapshot, string source, MovePlan plan)
    {
        var targets = plan.Moves.Select(m => m.Target).Distinct(StringComparer.Ordinal).ToList();
        var text = targets.Count == 1
            ? $"Switch now: from {source} to {targets[0]}"
            : "Switch now: " + string.Join("; ", plan.Moves.GroupBy(m => m.Target, StringComparer.Ordinal)
                .Select(g => $"{Join(g.Select(m => Label(snapshot, m.Machine)).ToList())} from {source} to {g.Key}"));
        foreach (var r in plan.LeftBehind)
            text += $"; {Label(snapshot, r.MachineName, r.OsUser)} {NotReadyClause(r, r.TargetAccountEmail)}, so it stays on {source}";
        return text + ".";
    }

    // Why a machine is not ready, with the account called what the sentence calls it ("it", or its email).
    private static string NotReadyClause(LicenseReadiness readiness, string target) => readiness.NotReadyReason switch
    {
        LicenseNotReadyReasons.NoSignIn => $"holds no sign-in for {target}",
        LicenseNotReadyReasons.SignInRefused => $"holds a sign-in for {target} that was refused",
        LicenseNotReadyReasons.SignInExpiresTooSoon => readiness.SignInExpiresAtUtc is { } at
            ? $"holds a sign-in for {target} that expires at {Time(at)}, before a switch could finish"
            : throw new InvalidOperationException("A sign-in that expires too soon carries no expiry."),
        _ => throw new InvalidOperationException($"No words for a machine that is not ready because '{readiness.NotReadyReason}'."),
    };

    // What happens to a machine left behind by the automatic switch: it moves on a later tick, the moment it is ready.
    private static string SwitchesWhenReady(LicenseReadiness readiness) => readiness.NotReadyReason switch
    {
        LicenseNotReadyReasons.NoSignIn => "switches as soon as it does",
        LicenseNotReadyReasons.SignInRefused => "switches as soon as it is signed in again",
        _ => "switches as soon as it is renewed",
    };

    // Switch now's refusal when no account has room - the one true no-room case - with each account's reason.
    private static string NoAccountHasRoomText(LicenseSnapshot snapshot, IReadOnlyDictionary<string, LicenseAccountReading> readings, string source)
    {
        var others = readings.Values
            .Where(r => !SameAccount(r.AccountEmail, source))
            .OrderBy(r => r.AccountEmail, StringComparer.Ordinal)
            .Select(r => $"for {r.AccountEmail}, {WhyNotTarget(snapshot, r, source)}")
            .ToList();
        return others.Count == 0
            ? $"No other account has room for the machines on {source}: no other account is known."
            : $"No other account has room for the machines on {source}: {string.Join("; ", others)}.";
    }

    // "a", "a and b", "a, b and c".
    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => throw new InvalidOperationException("A list of machines to name is empty."),
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

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
            body.Append(CultureInfo.InvariantCulture, $"{other.AccountEmail} was not chosen: {WhyNotTarget(snapshot, other, source)}.\r\n");
        body.Append("\r\nThis is the only email for this event. Sessions on these machines will stop when the limit is reached.");
        return new LicenseOwnerEmail(source, subject, body.ToString(), LicenseStore.NoRoomHappened(source));
    }

    // The email of the same event when a target HAS room and every machine that could be sent the switch is held
    // because its switch keeps failing.
    private static LicenseOwnerEmail BuildSwitchKeepsFailingEmail(LicenseSnapshot snapshot, string source, TiredAccount judged)
    {
        var tired = judged.Tired;
        var subject = $"DevThrottle: the switch away from {source} keeps failing - it is at {DescribeWindows(tired)}";
        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"{source} has reached {DescribeWindows(tired)}. {judged.Ranked[0]} has room for its machines, but the switch keeps failing, so nothing is being switched for now.\r\n\r\n");
        foreach (var skipped in judged.Skipped)
        {
            var held = judged.Held.Any(m => IsMachine(m, skipped));
            body.Append(CultureInfo.InvariantCulture, $"{Label(snapshot, skipped.MachineName, skipped.OsUser)}: {(held ? skipped.Why : "not sent a switch, because " + skipped.Why)}.\r\n");
        }
        body.Append("\r\n");
        foreach (var window in tired)
            body.Append(CultureInfo.InvariantCulture, $"Its {WindowLabel(window.Name)} resets {(window.ResetsAtUtc is { } r ? "at " + Time(r) : "at a time the provider did not give")}.\r\n");
        body.Append("\r\nThe switch is tried again by itself each time a machine's hold has passed, and the hold grows each time the switch fails the same way, up to one hour. ");
        body.Append("Switch now on the Licenses page of the Cockpit sends the switch at once, and the switch log there has a row for each try.\r\n");
        body.Append("\r\nThis is the only email for this event. Sessions on these machines will stop when the limit is reached.");
        return new LicenseOwnerEmail(source, subject, body.ToString(), SwitchKeepsFailingHappened(source));
    }

    /// <summary>What the event's row of the Licenses log says happened when every machine that could be switched is
    /// held: one clause with no full stop.</summary>
    public static string SwitchKeepsFailingHappened(string accountEmail)
        => $"The switch of the machines on {Normalize(accountEmail)} keeps failing and is tried again later each time, so nothing is switched for now";

    // Why the held switch failed, in brackets at the end of a clause: the Gateway's own sentence for the reason
    // word, or the word itself when this build does not know it.
    internal static string HoldReason(LicenseHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        if (string.IsNullOrWhiteSpace(hold.ReasonCode))
            return "";
        var text = LicenseDisplayFold.ReasonText(hold.ReasonCode);
        // The sentence is put inside another, so it loses its capital and its full stop.
        return text is null || text == hold.ReasonCode
            ? $" (the machine's reason word: {hold.ReasonCode})"
            : $" ({char.ToLowerInvariant(text[0])}{text[1..].TrimEnd('.')})";
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
