using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>One machine and one paid account, judged (<see cref="LicenseCoverageFold.Judge"/>).</summary>
/// <param name="AccountEmail">The paid account, lower-cased.</param>
/// <param name="State">One of <see cref="LicenseCoverageCellStates"/>.</param>
/// <param name="SignInExpiresAtUtc">When the machine's sign-in for the account ends, when it holds one and said; else null.</param>
/// <param name="IsGap">The machine NEEDS this account as its next license: it is at or above
/// <see cref="LicenseNextLicenseFold.WarnPercent"/> with no READY next license, and this is the license it is told to sign
/// in to (<see cref="LicenseNextLicense.RecommendedAccountEmail"/>). A license that is merely not held is never a gap (the
/// owner, 7 November 2026).</param>
/// <param name="IsCritical">A gap on a machine whose license is at or above the switching threshold: red.</param>
public sealed record LicenseCoverageCell(string AccountEmail, string State, DateTime? SignInExpiresAtUtc, bool IsGap, bool IsCritical);

/// <summary>One machine's row of the coverage grid (<see cref="LicenseCoverageFold.Judge"/>).</summary>
/// <param name="MachineId">The identifier of its row on the Licenses page (<see cref="LicenseMachineDto.MachineId"/>): its
/// database identifier, or for a Director whose reports were only ever refused, <see cref="LicenseDisplayFold.RefusedMachineId"/>.</param>
/// <param name="LastReportAtUtc">When its last report was accepted; null when none ever was.</param>
/// <param name="Label">What the machine is called (<see cref="LicenseSignInFold.Label"/>).</param>
/// <param name="ActiveAccountEmail">The account it is on, lower-cased; null when on none.</param>
/// <param name="IsStale">Its last report is older than <see cref="LicenseContract.MachineStaleAfter"/>.</param>
/// <param name="ReportsRefused">Its reports are being refused, so what it holds now is not known.</param>
/// <param name="Stranded">When it is on an account at or above the switching threshold, some account has room, and it is
/// ready for none of them: the engine's own verdict (<see cref="LicenseDecisionEngine.StrandedMachines"/>); else null.</param>
/// <param name="Next">Its next-license verdict (<see cref="LicenseNextLicenseFold.Judge"/>); null when it is on no license,
/// or it is a Director whose reports were only ever refused.</param>
/// <param name="Cells">One per paid account, in <see cref="LicenseCoverage.Accounts"/> order.</param>
public sealed record LicenseCoverageRow(
    string MachineId,
    string MachineName,
    string OsUser,
    string DirectorId,
    string Label,
    string? ActiveAccountEmail,
    bool IsStale,
    bool ReportsRefused,
    DateTime? LastReportAtUtc,
    LicenseStrandedMachine? Stranded,
    LicenseNextLicense? Next,
    IReadOnlyList<LicenseCoverageCell> Cells)
{
    /// <summary>True when the machine will be stuck: it needs a ready next license, or its switch keeps failing
    /// (<see cref="LicenseNextLicense.IsProblem"/>).</summary>
    public bool HasGap => Next?.IsProblem == true;
}

/// <summary>A paid account: a column of the grid.</summary>
public sealed record LicenseCoverageAccount(string AccountEmail, string? Plan);

/// <summary>The whole grid of one tenant at one moment.</summary>
public sealed record LicenseCoverage(DateTime NowUtc, IReadOnlyList<LicenseCoverageAccount> Accounts, IReadOnlyList<LicenseCoverageRow> Rows)
{
    /// <summary>How many MACHINES will be stuck: each needs a ready next license. Never a count of licenses not held.</summary>
    public int GapCount => Rows.Count(r => r.HasGap);
}

/// <summary>
/// COVERAGE: EVERY MACHINE AGAINST EVERY PAID ACCOUNT (mission "Licenses - Always Ready, Loud When Not", step 1).
///
/// The grid shows, for every machine that reports and every PAID account, which sign-ins the machine holds. What counts
/// as a GAP is the owner's ruling of 7 November 2026: "We don't need all licenses logged in at all times. When we run
/// out, we just need a new one that becomes available when the one we have will expire soon." So a license that is not
/// held is never a gap by itself. A machine has a gap only when <see cref="LicenseNextLicenseFold"/> says it needs a READY
/// next license - at or above <see cref="LicenseNextLicenseFold.WarnPercent"/> on the license it is on, with none - and
/// the gap is the one cell of the license it is told to sign in to. The count is of MACHINES that will be stuck.
///
/// COLOUR (the owner, 6 November 2026, verbatim): "I think a warning should be yellow, not red. It doesn't go red until
/// it goes above 97%." A gap is <see cref="LicenseTones.Warning"/>; it is <see cref="LicenseTones.Bad"/> when the license
/// the machine is on is at or above <see cref="LicenseDecisionEngine.ThresholdPercent"/>. A sign-in that is not held,
/// refused or ending and is not needed is <see cref="LicenseTones.Neutral"/>: a fact, not a problem.
///
/// Only paid accounts are columns: an account with no paid plan is no place to switch to. Pure: facts go in, an answer
/// comes out.
/// </summary>
public static class LicenseCoverageFold
{
    /// <summary>Judge every machine of <paramref name="picture"/> against every paid account.</summary>
    /// <param name="next">Every machine's next-license verdict on the same picture (<see cref="LicenseNextLicenseFold.Judge"/>).</param>
    public static LicenseCoverage Judge(LicensePicture picture, IReadOnlyList<LicenseNextLicense> next)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(next);
        var snapshot = picture.Snapshot;
        var now = snapshot.NowUtc;
        var plans = picture.Overview.Accounts.ToDictionary(a => LicenseStore.NormalizeEmail(a.AccountEmail), a => a.Plan, StringComparer.Ordinal);
        var paid = snapshot.Accounts
            .Where(a => a.NoPaidPlanSinceUtc is null)
            .Select(a => LicenseStore.NormalizeEmail(a.AccountEmail))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .Select(a => new LicenseCoverageAccount(a, plans.TryGetValue(a, out var plan) ? plan : null))
            .ToList();
        var states = snapshot.Machines.ToDictionary(m => Key(m.MachineName, m.OsUser), StringComparer.Ordinal);
        var stranded = LicenseDecisionEngine.StrandedMachines(snapshot);
        var machines = picture.Overview.Machines;
        var rows = new List<LicenseCoverageRow>();
        foreach (var machine in machines)
        {
            var state = states[Key(machine.MachineName, machine.OsUser)];
            var active = state.ActiveAccountEmail is { } a ? LicenseStore.NormalizeEmail(a) : null;
            var stuck = stranded.FirstOrDefault(s => Key(s.MachineName, s.OsUser) == Key(machine.MachineName, machine.OsUser));
            var verdict = next.FirstOrDefault(n => n.MachineId == machine.MachineId);
            var cells = paid.Select(p => Cell(state, active, p.AccountEmail, verdict, now)).ToList();
            rows.Add(new LicenseCoverageRow(machine.MachineId.ToString("N"), machine.MachineName, machine.OsUser, machine.DirectorId,
                LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser), active,
                LicenseDecisionEngine.IsMachineStale(state, now), state.ReportsRefused, Utc(machine.LastReportAtUtc), stuck, verdict, cells));
        }
        // A Director whose reports were only ever refused has no machine row, and the Licenses page shows it from its
        // refusal. It is a row here too, with nothing known in it - and no gap, because nothing says it is running out.
        foreach (var refusal in LicenseDisplayFold.RefusedWithNoMachine(picture))
        {
            var shown = LicenseDisplayFold.RefusedMachine(refusal, now);
            rows.Add(new LicenseCoverageRow(shown.MachineId, shown.MachineName, shown.OsUser, refusal.DirectorId ?? "", shown.Label,
                null, IsStale: true, ReportsRefused: true, LastReportAtUtc: null, Stranded: null, Next: null,
                paid.Select(p => new LicenseCoverageCell(p.AccountEmail, LicenseCoverageCellStates.NotHeld, null, false, false)).ToList()));
        }
        return new LicenseCoverage(now, paid, rows);
    }

    private static LicenseCoverageCell Cell(LicenseMachineState state, string? active, string account, LicenseNextLicense? next, DateTime now)
    {
        if (active == account)
            return new LicenseCoverageCell(account, LicenseCoverageCellStates.InUse, state.ActiveSignInExpiresAtUtc, false, false);
        var readiness = LicenseDecisionEngine.Readiness(state, account, now);
        var cellState = readiness.NotReadyReason switch
        {
            LicenseNotReadyReasons.NoSignIn => LicenseCoverageCellStates.NotHeld,
            LicenseNotReadyReasons.SignInRefused => LicenseCoverageCellStates.Refused,
            LicenseNotReadyReasons.SignInExpiresTooSoon => LicenseCoverageCellStates.Ending,
            null when readiness.SignInExpiresAtUtc is { } ends && ends - now <= LicenseCoverageRules.EndingWithin => LicenseCoverageCellStates.Ending,
            null => LicenseCoverageCellStates.Held,
            _ => throw new InvalidOperationException($"Readiness gave a reason the coverage grid does not know: {readiness.NotReadyReason}"),
        };
        // The one gap of a machine is the license it is told to sign in to next; nothing else it does not hold is a gap.
        var gap = next is { IsNeeded: true } && next.RecommendedAccountEmail == account;
        return new LicenseCoverageCell(account, cellState, readiness.SignInExpiresAtUtc, gap, gap && next!.AtThreshold);
    }

    // ----- the words -----

    /// <summary>The grid as the Licenses page draws it.</summary>
    public static LicenseCoverageDto Dto(LicenseCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        var now = coverage.NowUtc;
        var dto = new LicenseCoverageDto
        {
            GapCount = coverage.GapCount,
            Accounts = coverage.Accounts.Select(a => new LicenseCoverageAccountDto
            {
                AccountEmail = a.AccountEmail,
                PlanText = LicenseDisplayFold.PlanText(a.Plan),
            }).ToList(),
            Rows = coverage.Rows.Select(r => Row(r, now)).ToList(),
        };
        (dto.Text, dto.Tone) = Summary(coverage);
        FileLog.Write($"[LicenseCoverageFold] Dto: accounts={dto.Accounts.Count}, rows={dto.Rows.Count}, machinesStuck={dto.GapCount}, tone={dto.Tone}");
        return dto;
    }

    public const string NoMachinesText = "No machine has reported yet.";

    public const string NoPaidAccountsText = "No paid account is known yet.";

    public const string FullCoverageText = "Every machine has a ready next license, or does not need one yet.";

    public const string NoLicenseText = "Not on a license, so it needs no next license.";

    private static (string Text, string Tone) Summary(LicenseCoverage coverage)
    {
        if (coverage.Rows.Count == 0)
            return (NoMachinesText, LicenseTones.Neutral);
        if (coverage.Accounts.Count == 0)
            return (NoPaidAccountsText, LicenseTones.Neutral);
        var stranded = coverage.Rows.Where(r => r.Stranded is not null).ToList();
        var needing = coverage.Rows.Where(r => r.Next is { IsNeeded: true } && r.Stranded is null).ToList();
        var failing = coverage.Rows.Where(r => r.Next?.State == LicenseNextLicenseStates.SwitchFailing).ToList();
        var silent = coverage.Rows.Where(r => r.IsStale || r.ReportsRefused).ToList();
        if (stranded.Count == 0 && needing.Count == 0 && failing.Count == 0 && silent.Count == 0)
            return (FullCoverageText, LicenseTones.Good);
        var parts = new List<string>();
        if (stranded.Count > 0)
            parts.Add($"{Names(stranded)} cannot switch and {(stranded.Count == 1 ? "is" : "are")} at the limit.");
        if (failing.Count > 0)
            parts.Add($"{Names(failing)} cannot switch: {(failing.Count == 1 ? "its switch keeps" : "their switches keep")} failing.");
        if (needing.Count > 0)
            parts.Add($"{Names(needing)} {(needing.Count == 1 ? "needs" : "need")} a ready next license.");
        if (silent.Count > 0)
            parts.Add($"{Names(silent)} {(silent.Count == 1 ? "is" : "are")} not reporting, so {(silent.Count == 1 ? "it" : "they")} cannot be switched.");
        var bad = stranded.Count > 0 || failing.Count > 0 || needing.Any(r => r.Next!.AtThreshold);
        return (string.Join(" ", parts), bad ? LicenseTones.Bad : LicenseTones.Warning);
    }

    private static LicenseCoverageRowDto Row(LicenseCoverageRow row, DateTime now)
    {
        var (text, tone) = RowWords(row, now);
        return new LicenseCoverageRowDto
        {
            Machine = new LicenseMachineRefDto
            {
                MachineId = row.MachineId,
                MachineName = row.MachineName,
                OsUser = row.OsUser,
                Label = row.Label,
            },
            ActiveAccountEmail = row.ActiveAccountEmail,
            Text = text,
            Tone = tone,
            GapCount = row.HasGap ? 1 : 0,
            Cells = row.Cells.Select(c => CellDto(c, now)).ToList(),
        };
    }

    private static (string Text, string Tone) RowWords(LicenseCoverageRow row, DateTime now)
    {
        var sentences = new List<string>();
        if (row.Stranded is { } stuck)
            sentences.Add($"Cannot switch: {stuck.AccountEmail} is at {stuck.Tired}, and it holds no fresh sign-in for an account with room.");
        if (row.ReportsRefused)
            sentences.Add($"Its reports are being refused, so what it holds now is not known and it cannot be switched.");
        else if (row.IsStale && row.LastReportAtUtc is { } last)
            sentences.Add($"Not reporting since {Clock(last)}, so it cannot be switched until it reports again; the cells show its last report.");
        if (row.Stranded is null && row.Next is { IsProblem: true } needed && !row.ReportsRefused)
            sentences.Add(needed.Text);
        if (sentences.Count == 0)
        {
            if (row.Cells.Count == 0)
                return (NoPaidAccountsText, LicenseTones.Neutral);
            return row.Next is { } verdict ? (verdict.Text, verdict.Tone) : (NoLicenseText, LicenseTones.Neutral);
        }
        var bad = row.Stranded is not null || row.Next is { IsNeeded: true, AtThreshold: true }
                  || row.Next?.State == LicenseNextLicenseStates.SwitchFailing;
        return (string.Join(" ", sentences), bad ? LicenseTones.Bad : LicenseTones.Warning);
    }

    private static LicenseCoverageCellDto CellDto(LicenseCoverageCell cell, DateTime now) => new()
    {
        AccountEmail = cell.AccountEmail,
        State = cell.State,
        Text = cell.State switch
        {
            LicenseCoverageCellStates.InUse => "In use",
            LicenseCoverageCellStates.Held => "Held",
            LicenseCoverageCellStates.Ending => cell.SignInExpiresAtUtc is { } ends && ends > now
                ? $"Ends in {LicenseDisplayFold.Span(ends - now)}"
                : "Sign-in has ended",
            LicenseCoverageCellStates.NotHeld => "Not held",
            LicenseCoverageCellStates.Refused => "Sign-in refused",
            _ => throw new InvalidOperationException($"A coverage cell state the words do not know: {cell.State}"),
        },
        Tone = cell.IsCritical ? LicenseTones.Bad
            : cell.IsGap ? LicenseTones.Warning
            : cell.State is LicenseCoverageCellStates.InUse or LicenseCoverageCellStates.Held ? LicenseTones.Good
            : LicenseTones.Neutral,
        IsGap = cell.IsGap,
    };

    private static string Names(IReadOnlyList<LicenseCoverageRow> rows) => rows.Count switch
    {
        1 => rows[0].Label,
        _ => string.Join(", ", rows.Take(rows.Count - 1).Select(r => r.Label)) + " and " + rows[^1].Label,
    };

    private static string Clock(DateTime utc) => Utc(utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Key(string machineName, string osUser)
        => machineName.Trim().ToLowerInvariant() + "\n" + osUser.Trim().ToLowerInvariant();

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
