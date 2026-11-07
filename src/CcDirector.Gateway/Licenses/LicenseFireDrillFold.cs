using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>One thing the fire drill found that would stop a machine switching now (<see cref="LicenseFireDrillFold.Holes"/>).</summary>
/// <param name="MachineId">The machine.</param>
/// <param name="Label">What the machine is called (<see cref="LicenseSignInFold.Label"/>).</param>
/// <param name="AccountEmail">The paid account the hole is for, lower-cased; null when the whole machine is the hole
/// (it is not reporting, or its reports are refused).</param>
/// <param name="Text">One finished sentence.</param>
public sealed record LicenseFireDrillHole(Guid MachineId, string Label, string? AccountEmail, string Text);

/// <summary>The step of the drill's switch that failed, and why (<see cref="LicenseFireDrillFold"/>).</summary>
/// <param name="Step">One of the <see cref="LicenseFireDrillFold"/> step names, e.g. <see cref="LicenseFireDrillFold.StepOut"/>.</param>
/// <param name="Why">What went wrong, as a clause with no full stop.</param>
public sealed record LicenseFireDrillFailure(string Step, string Why);

/// <summary>What one drill found (<see cref="LicenseFireDrillService"/>).</summary>
/// <param name="MachineId">The drill machine as the setting names it.</param>
/// <param name="MachineLabel">What it is called, or null when it is not on the Licenses page.</param>
/// <param name="Holes">Every hole of every reporting machine.</param>
/// <param name="Failure">The step of the switch that failed; null when the switch went out and came back.</param>
/// <param name="FromAccountEmail">The account the drill machine was on; empty when the drill did not get that far.</param>
/// <param name="SpareAccountEmail">The spare it went to; empty when the drill did not get that far.</param>
public sealed record LicenseFireDrillResult(
    Guid MachineId,
    string? MachineLabel,
    IReadOnlyList<LicenseFireDrillHole> Holes,
    LicenseFireDrillFailure? Failure,
    string FromAccountEmail,
    string SpareAccountEmail)
{
    /// <summary>Every machine could switch now, and the drill machine switched out and back.</summary>
    public bool Passed => Holes.Count == 0 && Failure is null;
}

/// <summary>
/// THE DAILY FIRE DRILL'S VERDICT, AS PURE CODE (mission "Licenses - Always Ready, Loud When Not", step 7). Four earlier
/// attempts proved their own piece with tests and closed with "live check still owed"; the drill is that live check, run
/// by the Gateway every day (<see cref="LicenseFireDrillService"/>).
///
/// A HOLE is anything that would stop a reporting machine switching to a paid account now. The coverage fold decides
/// which sign-ins a machine holds (<see cref="LicenseCoverageFold.Judge"/>, the one definition of a gap); the drill asks
/// more of every sign-in it holds - the one in use included - than the grid does: that sign-in must have answered a
/// usage read, recently (<see cref="LicenseStaleness.IsHoldingStale"/>), with no error. A sign-in that is on disk but has
/// not answered a usage read is not known to work, and a switch to it is the moment to find out it does not.
/// A machine that is not reporting, or whose reports are refused, is one hole for the whole machine.
///
/// The words are folded here, once: the log row, the daily report's line and the alarm read them as given.
/// </summary>
public static class LicenseFireDrillFold
{
    /// <summary>The step that finds the drill machine on the Licenses page.</summary>
    public const string StepMachine = "finding the drill machine";

    /// <summary>The step that picks a spare with room the drill machine holds a fresh sign-in for.</summary>
    public const string StepChooseSpare = "choosing a spare with room";

    /// <summary>The switch from the account in use to the spare.</summary>
    public const string StepOut = "the switch out to the spare";

    /// <summary>The switch from the spare back to the account it was on.</summary>
    public const string StepBack = "the switch back";

    /// <summary>A drill the Gateway stopped in the middle of.</summary>
    public const string StepInterrupted = "the drill itself";

    /// <summary>The kind every alarm of the drill is raised under: a warning that repeats every hour until a drill no
    /// longer finds it, yellow, like the cannot-switch warning (pull request 4720).</summary>
    public static readonly LicenseAlarmKind AlarmKind = new("fire-drill-failed", "The daily license drill failed",
        repeatsHourly: true, tone: LicenseAlarmTones.Warning);

    /// <summary>Every hole of every machine in <paramref name="coverage"/>, in the grid's order.</summary>
    public static List<LicenseFireDrillHole> Holes(LicensePicture picture, LicenseCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(coverage);
        var now = coverage.NowUtc;
        var machines = picture.Overview.Machines.ToDictionary(m => m.MachineId);
        var holes = new List<LicenseFireDrillHole>();
        foreach (var row in coverage.Rows)
        {
            var id = MachineIdOf(row);
            if (row.ReportsRefused)
            {
                holes.Add(new LicenseFireDrillHole(id, row.Label, null,
                    $"{row.Label}: its reports are being refused, so it cannot be switched."));
                continue;
            }
            if (row.IsStale)
            {
                holes.Add(new LicenseFireDrillHole(id, row.Label, null, row.LastReportAtUtc is { } last
                    ? $"{row.Label}: not reporting since {Clock(last)}, so it cannot be switched."
                    : $"{row.Label}: no report from it was ever accepted, so it cannot be switched."));
                continue;
            }
            var holdings = machines[id].Holdings;
            foreach (var cell in row.Cells)
            {
                var holding = holdings.FirstOrDefault(h => LicenseStore.NormalizeEmail(h.AccountEmail) == cell.AccountEmail);
                if (CellHole(row.Label, cell, holding, now) is { } text)
                    holes.Add(new LicenseFireDrillHole(id, row.Label, cell.AccountEmail, text));
            }
        }
        return holes;
    }

    private static string? CellHole(string label, LicenseCoverageCell cell, LicenseHoldingView? holding, DateTime now)
    {
        var account = cell.AccountEmail;
        switch (cell.State)
        {
            case LicenseCoverageCellStates.NotHeld:
                return $"{label}: holds no sign-in for {account}.";
            case LicenseCoverageCellStates.Refused:
                return $"{label}: its sign-in for {account} is refused and needs a new sign-in.";
            case LicenseCoverageCellStates.Ending:
                return cell.SignInExpiresAtUtc is { } ends && ends > now
                    ? $"{label}: its sign-in for {account} ends {Clock(ends)}."
                    : $"{label}: its sign-in for {account} has ended.";
            case LicenseCoverageCellStates.Held or LicenseCoverageCellStates.InUse:
                if (holding is null)
                    throw new InvalidOperationException($"The coverage grid says {label} holds {account}, and the machine lists no such sign-in.");
                if (holding.ReadError is { } error)
                    return $"{label}: its sign-in for {account} failed its last usage read: {ReadErrorWhy(error)}.";
                if (holding.IsReadingStale)
                    return $"{label}: its sign-in for {account} has not answered a usage read recently.";
                return null;
            default:
                throw new InvalidOperationException($"A coverage cell state the fire drill does not know: {cell.State}");
        }
    }

    /// <summary>
    /// The machine a coverage row is about, as the alarm and the drill key it. A row's identifier is the one the Licenses
    /// page gives it: a machine's database identifier, or - for a Director whose reports were only ever refused, which
    /// has no machine row - <see cref="LicenseDisplayFold.RefusedMachineId"/>, a stable identifier of the same shape made
    /// from the refusal. Both read as an identifier here, so a refused-only Director is one hole keyed the way the
    /// refused-report alarm keys it. Such a row can never be the drill machine: only a machine on the page can be chosen.
    /// </summary>
    public static Guid MachineIdOf(LicenseCoverageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Guid.TryParseExact(row.MachineId, "N", out var id)
            ? id
            : throw new InvalidOperationException($"The coverage row of {row.Label} has the identifier '{row.MachineId}', which is not the shape the Licenses page gives.");
    }

    /// <summary>A read error word as a clause.</summary>
    internal static string ReadErrorWhy(string error) => error switch
    {
        LicenseReadErrors.Unauthorized => "the provider refused it",
        LicenseReadErrors.RefreshFailed => "it could not be renewed",
        LicenseReadErrors.RateLimited => "the provider limited how often usage can be read",
        LicenseReadErrors.Network => "the machine could not reach the provider",
        LicenseReadErrors.UnexpectedAnswer => "the provider's answer could not be understood",
        LicenseReadErrors.StoreUnreadable => "the machine could not read the stored sign-in",
        LicenseReadErrors.NoPaidPlan => "the account has no paid plan",
        _ => $"it failed ({error})",
    };

    /// <summary>
    /// The spare the drill machine goes out to: the best account with room for the account it is on
    /// (<see cref="LicenseDecisionEngine.TargetsWithRoom"/>, the same rule a real switch takes), among the accounts the
    /// machine holds a sign-in for that is no hole. Null, with the reason, when there is none.
    /// </summary>
    public static (string? Spare, LicenseFireDrillFailure? Failure) ChooseSpare(LicensePicture picture, LicenseCoverageRow row,
        IReadOnlyList<LicenseFireDrillHole> holes)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(holes);
        var id = MachineIdOf(row);
        if (holes.Any(h => h.MachineId == id && h.AccountEmail is null))
            return (null, new LicenseFireDrillFailure(StepMachine, $"{row.Label} cannot be switched now"));
        if (row.ActiveAccountEmail is not { } active)
            return (null, new LicenseFireDrillFailure(StepChooseSpare, $"{row.Label} is on no account"));
        var fresh = row.Cells
            .Where(c => c.State == LicenseCoverageCellStates.Held)
            .Where(c => !holes.Any(h => h.MachineId == id && h.AccountEmail == c.AccountEmail))
            .Select(c => c.AccountEmail)
            .ToHashSet(StringComparer.Ordinal);
        var spare = LicenseDecisionEngine.TargetsWithRoom(picture.Snapshot, active)
            .Select(LicenseStore.NormalizeEmail)
            .FirstOrDefault(fresh.Contains);
        return spare is null
            ? (null, new LicenseFireDrillFailure(StepChooseSpare,
                $"{row.Label} holds no fresh sign-in for an account with room besides {active}"))
            : (spare, null);
    }

    /// <summary>
    /// Whether a drill switch did what it was sent to do, verified by a usage read: the Director answered that it
    /// switched, the machine's active account read back after the exchange is the target, and the new sign-in answered a
    /// usage read. Null when it did; else why not, as a clause.
    /// </summary>
    public static string? SwitchFailure(LicenseSwitchLogEntity? row, string targetAccountEmail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetAccountEmail);
        if (row is null)
            return "the machine already had a switch that had not finished, or it is no longer on the Licenses page";
        var target = LicenseStore.NormalizeEmail(targetAccountEmail);
        return row.State switch
        {
            LicenseLogStates.Switched when row.VerifiedByUsageRead != true =>
                $"it switched, and the sign-in for {target} did not answer a usage read",
            LicenseLogStates.Switched when !string.Equals(LicenseStore.NormalizeEmail(row.ActiveAccountEmailAfter ?? ""), target, StringComparison.Ordinal) =>
                $"it answered switched, and the machine read back {(string.IsNullOrEmpty(row.ActiveAccountEmailAfter) ? "no account" : row.ActiveAccountEmailAfter)} in use",
            LicenseLogStates.Switched => null,
            LicenseLogStates.AlreadyActive => $"the machine was already on {target}, so nothing was switched",
            LicenseLogStates.Unknown => "no answer came back in time; the minute check settles it from the machine's report",
            LicenseLogStates.Failed => Clause(row.Message) ?? $"it failed ({row.ReasonCode ?? "no reason given"})",
            _ => $"it ended as '{row.State}'",
        };
    }

    // ----- the words -----

    /// <summary>The drill's one finished sentence, for the Licenses log row and the daily report. At most
    /// <see cref="LicenseStore.MaxMessageLength"/> characters: holes beyond what fits are counted, never cut mid-word.</summary>
    public static string Message(LicenseFireDrillResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Passed)
            return $"Passed: every machine holds a fresh sign-in for every paid account, verified by a usage read, and "
                   + $"{result.MachineLabel} switched to {result.SpareAccountEmail} and back to {result.FromAccountEmail}, each verified by a usage read.";
        var parts = new List<string>();
        if (result.Failure is { } failure)
            parts.Add($"The drill switch failed at {failure.Step}: {failure.Why}.");
        else
            parts.Add($"{result.MachineLabel} switched to {result.SpareAccountEmail} and back, each verified by a usage read.");
        var head = $"Failed: {Count(result.Holes.Count, "hole")}. {string.Join(" ", parts)}";
        if (result.Holes.Count == 0)
            return Fit(head);
        var listed = new List<string>();
        foreach (var hole in result.Holes)
        {
            var rest = result.Holes.Count - listed.Count - 1;
            var candidate = string.Join(" ", listed.Append(hole.Text));
            var tail = rest > 0 ? $" And {Count(rest, "more hole")}; the coverage grid on the Licenses page shows them all." : "";
            if ($"{head} {candidate}{tail}".Length > LicenseStore.MaxMessageLength)
                break;
            listed.Add(hole.Text);
        }
        var left = result.Holes.Count - listed.Count;
        var more = left > 0 ? $" And {Count(left, "more hole")}; the coverage grid on the Licenses page shows them all." : "";
        return Fit(listed.Count == 0 ? $"{head}{more}" : $"{head} {string.Join(" ", listed)}{more}");
    }

    /// <summary>The sentence of a drill the Gateway stopped in the middle of.</summary>
    public const string InterruptedMessage =
        "Failed: the Gateway stopped while the drill was running, so it did not finish. It counts as the day's drill: run it again now from the fire drill route, or the next one runs in a day.";

    /// <summary>
    /// The alarms a drill raises: one for each hole, keyed on its machine and account, and one for a failed switch, keyed
    /// on the drill machine alone. A failed switch on a machine whose whole machine is a hole adds nothing beside that hole.
    /// Empty when the drill passed, which closes every alarm the last drill opened.
    /// </summary>
    public static List<LicenseAlarmCondition> Conditions(LicenseFireDrillResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var conditions = new List<LicenseAlarmCondition>();
        foreach (var hole in result.Holes)
        {
            var problem = FitAlarm($"The daily license drill found a hole. {hole.Text}",
                $"The daily license drill found a hole on {hole.Label}; the coverage grid on the Licenses page shows it.",
                "The daily license drill found a hole on a machine whose name is too long to say here; the coverage grid on the Licenses page shows it.");
            var action = hole.AccountEmail is null
                ? "Bring the machine back to reporting, or forget it on the Licenses page. The next daily drill closes this alarm when it is fixed."
                : "Sign in to that account on that machine from the Licenses page. The next daily drill closes this alarm when the sign-in answers a usage read.";
            conditions.Add(new LicenseAlarmCondition(hole.MachineId, hole.Label, hole.AccountEmail, problem, action, LoudAtNight: false));
        }
        if (result.Failure is { } failure
            && !result.Holes.Any(h => h.MachineId == result.MachineId && h.AccountEmail is null))
        {
            var label = result.MachineLabel ?? "the drill machine";
            var problem = FitAlarm($"The daily license drill could not switch {label}: {failure.Step} failed: {failure.Why}.",
                $"The daily license drill could not switch {label}: {failure.Step} failed. The Licenses log says why.",
                $"The daily license drill could not switch its drill machine: {failure.Step} failed. The Licenses log says which machine and why.");
            conditions.Add(new LicenseAlarmCondition(result.MachineId, result.MachineLabel ?? "the drill machine", null, problem,
                "Read the drill's switch on the Licenses log and put right what it says. The next daily drill closes this alarm when its switch works.",
                LoudAtNight: false));
        }
        return conditions;
    }

    /// <summary>
    /// The daily report's line: the newest drill, or why there is none. Null for an account none of whose machines
    /// reports - it has no licenses to drill.
    /// </summary>
    /// <param name="newest">The newest drill rows, newest first (<see cref="LicenseFireDrillLog.Newest"/>).</param>
    /// <param name="machineSet">Whether a drill machine is chosen.</param>
    /// <param name="settingUnreadable">What is stored as the drill machine is not a machine identifier.</param>
    public static LicenseFireDrillAttentionDto? ReportLine(bool hasMachines, bool machineSet, bool settingUnreadable,
        IReadOnlyList<LicenseSwitchLogEntity> newest, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(newest);
        if (!hasMachines)
            return null;
        var latest = newest.FirstOrDefault(r => r.FinishedAtUtc is not null);
        var streak = newest.TakeWhile(r => r.FinishedAtUtc is null || r.State == LicenseLogStates.Passed)
            .Count(r => r.State == LicenseLogStates.Passed);
        if (settingUnreadable)
            return new LicenseFireDrillAttentionDto
            {
                Passed = false,
                Text = "The license fire drill does not run: its drill machine setting is not a machine identifier. Choose the drill machine again.",
            };
        if (!machineSet)
            return new LicenseFireDrillAttentionDto
            {
                Passed = false,
                Text = "The license fire drill is not set up: no drill machine is chosen, so it does not run.",
            };
        if (latest is null || nowUtc - latest.FinishedAtUtc!.Value > ReportLooksBack)
            return new LicenseFireDrillAttentionDto
            {
                Passed = false,
                RanAtUtc = latest?.FinishedAtUtc,
                Text = latest is null
                    ? "The license fire drill has not run yet."
                    : $"The license fire drill has not run since {Clock(latest.FinishedAtUtc!.Value)}.",
            };
        var passed = latest.State == LicenseLogStates.Passed;
        return new LicenseFireDrillAttentionDto
        {
            Passed = passed,
            RanAtUtc = latest.FinishedAtUtc,
            PassedInARow = streak,
            Text = $"License fire drill, {Clock(latest.FinishedAtUtc!.Value)}, on {latest.MachineName}: {latest.Message}"
                   + (passed ? $" {Count(streak, "drill")} passed in a row." : ""),
        };
    }

    /// <summary>How old the newest drill may be and still be the daily report's line: a day, and two hours for a drill
    /// that ran a little late.</summary>
    public static readonly TimeSpan ReportLooksBack = TimeSpan.FromHours(26);

    private static string? Clause(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        var text = message.Trim().TrimEnd('.');
        return text.Length > 0 ? text : null;
    }

    private static string Fit(string text) => Fit(text, LicenseStore.MaxMessageLength,
        "Failed: the drill's findings are too long to fit here; the coverage grid and the Licenses log show them.");

    private static string Fit(string text, int max, string shorter) => text.Length <= max ? text : shorter;

    // An alarm sentence: the whole one, else a shorter one naming the machine, else one that names nothing variable and
    // always fits.
    private static string FitAlarm(string whole, string shorter, string shortest)
        => whole.Length <= LicenseAlarmService.MaxSentenceLength ? whole
            : shorter.Length <= LicenseAlarmService.MaxSentenceLength ? shorter
            : shortest;

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Clock(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
