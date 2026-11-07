using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>One machine the daily check found will be stuck (<see cref="LicenseFireDrillFold.Holes"/>).</summary>
/// <param name="MachineId">The machine.</param>
/// <param name="Label">What the machine is called (<see cref="LicenseSignInFold.Label"/>).</param>
/// <param name="Text">One finished sentence.</param>
public sealed record LicenseFireDrillHole(Guid MachineId, string Label, string Text);

/// <summary>What one daily check found (<see cref="LicenseFireDrillService"/>).</summary>
/// <param name="MachinesChecked">How many machines on a license were judged.</param>
/// <param name="Holes">Every machine that will be stuck.</param>
public sealed record LicenseFireDrillResult(int MachinesChecked, IReadOnlyList<LicenseFireDrillHole> Holes)
{
    /// <summary>Every machine is under 80% on the license it is on, or holds a ready next license.</summary>
    public bool Passed => Holes.Count == 0;
}

/// <summary>
/// THE DAILY LICENSE CHECK'S VERDICT, AS PURE CODE (mission "Licenses - Always Ready, Loud When Not"). Once a day the
/// Gateway writes down, in the Licenses log and the daily report, whether every machine is ready for when it runs out.
///
/// THE RULE is the owner's of 7 November 2026 (<see cref="LicenseNextLicenseFold"/>): a machine passes when it is under
/// 80% on the license it is on, or holds a READY next license. It fails when it NEEDS one
/// (<see cref="LicenseNextLicenseStates.Needed"/>) or its switch keeps failing (<see cref="LicenseNextLicenseStates.SwitchFailing"/>). Never "every license on every machine": a license that is not held is
/// no failure. A machine that cannot be judged now, or that has no other license with room to go to, is not a failure of
/// this check either - the Licenses page says what it is.
///
/// IT SWITCHES NOTHING AND RAISES NO ALARM. The earlier drill switched one machine out to a spare and back every day,
/// from a drill machine the owner chose; the owner tests the licensing himself, so that switch, its setting and its
/// routes are gone. A machine that will be stuck is the cannot-switch warning's, every hour; the daily check only writes
/// the day's line. Its old alarm kind is raised empty (<see cref="RetiredAlarmKind"/>) so alarms stored by the drill close.
/// </summary>
public static class LicenseFireDrillFold
{
    /// <summary>The kind the retired drill raised its alarms under: raised empty, so every stored one closes.</summary>
    public static readonly LicenseAlarmKind RetiredAlarmKind = new("fire-drill-failed", "The daily license drill failed",
        repeatsHourly: true, tone: LicenseAlarmTones.Warning);

    /// <summary>Every machine of <paramref name="next"/> that will be stuck - it needs a ready next license, or its switch
    /// keeps failing - in the picture's order.</summary>
    public static List<LicenseFireDrillHole> Holes(IReadOnlyList<LicenseNextLicense> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return next.Where(n => n.IsProblem)
            .Select(n => new LicenseFireDrillHole(n.MachineId, n.Label, $"{n.Label}: {n.Text}"))
            .ToList();
    }

    // ----- the words -----

    /// <summary>The check's one finished sentence, for the Licenses log row and the daily report. At most
    /// <see cref="LicenseStore.MaxMessageLength"/> characters: machines beyond what fits are counted, never cut mid-word.</summary>
    public static string Message(LicenseFireDrillResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Passed)
            return $"Passed: every machine on a license is under {Percent(LicenseNextLicenseFold.WarnPercent)} or holds a ready next "
                   + $"license ({Count(result.MachinesChecked, "machine")} checked).";
        var head = $"Failed: {Count(result.Holes.Count, "machine")} will be stuck without a ready next license.";
        var listed = new List<string>();
        foreach (var hole in result.Holes)
        {
            var rest = result.Holes.Count - listed.Count - 1;
            var candidate = string.Join(" ", listed.Append(hole.Text));
            if ($"{head} {candidate}{More(rest)}".Length > LicenseStore.MaxMessageLength)
                break;
            listed.Add(hole.Text);
        }
        var text = listed.Count == 0 ? $"{head}{More(result.Holes.Count)}" : $"{head} {string.Join(" ", listed)}{More(result.Holes.Count - listed.Count)}";
        return text.Length <= LicenseStore.MaxMessageLength
            ? text
            : "Failed: the check's findings are too long to fit here; the Licenses page shows every machine.";
    }

    private static string More(int left)
        => left > 0 ? $" And {Count(left, "more machine")}; the Licenses page shows them all." : "";

    /// <summary>The sentence of a check the Gateway stopped in the middle of.</summary>
    public const string InterruptedMessage =
        "Failed: the Gateway stopped while the daily license check was running, so it did not finish. The next one runs in a day.";

    /// <summary>
    /// The daily report's line: the newest check, or why there is none. Null for an account none of whose machines
    /// reports - it has no licenses to check.
    /// </summary>
    /// <param name="newest">The newest check rows, newest first (<see cref="LicenseFireDrillLog.Newest"/>).</param>
    public static LicenseFireDrillAttentionDto? ReportLine(bool hasMachines, IReadOnlyList<LicenseSwitchLogEntity> newest, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(newest);
        if (!hasMachines)
            return null;
        var latest = newest.FirstOrDefault(r => r.FinishedAtUtc is not null);
        if (latest is null || nowUtc - latest.FinishedAtUtc!.Value > ReportLooksBack)
            return new LicenseFireDrillAttentionDto
            {
                Passed = false,
                RanAtUtc = latest?.FinishedAtUtc,
                Text = latest is null
                    ? "The daily license check has not run yet."
                    : $"The daily license check has not run since {Clock(latest.FinishedAtUtc!.Value)}.",
            };
        return new LicenseFireDrillAttentionDto
        {
            Passed = latest.State == LicenseLogStates.Passed,
            RanAtUtc = latest.FinishedAtUtc,
            Text = $"Daily license check, {Clock(latest.FinishedAtUtc!.Value)}: {latest.Message}",
        };
    }

    /// <summary>How old the newest check may be and still be the daily report's line: a day, and two hours for a check
    /// that ran a little late.</summary>
    public static readonly TimeSpan ReportLooksBack = TimeSpan.FromHours(26);

    private static string Percent(double value) => value.ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Clock(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
