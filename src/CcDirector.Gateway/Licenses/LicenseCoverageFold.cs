using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>One machine and one paid account, judged (<see cref="LicenseCoverageFold.Judge"/>).</summary>
/// <param name="AccountEmail">The paid account, lower-cased.</param>
/// <param name="State">One of <see cref="LicenseCoverageCellStates"/>.</param>
/// <param name="SignInExpiresAtUtc">When the machine's sign-in for the account ends, when it holds one and said; else null.</param>
/// <param name="IsGap">The machine could not switch to this account now and keep working on it: it holds no sign-in, a
/// refused one, or one that ends within <see cref="LicenseCoverageRules.EndingWithin"/>. The account it is on is never a gap.</param>
/// <param name="IsCritical">A gap on a machine that has nowhere to go while the account it is on is at or above the switching
/// threshold, for an account with room: the one sign-in that would let it switch. Only these are red.</param>
public sealed record LicenseCoverageCell(string AccountEmail, string State, DateTime? SignInExpiresAtUtc, bool IsGap, bool IsCritical);

/// <summary>One machine's row of the coverage grid (<see cref="LicenseCoverageFold.Judge"/>).</summary>
/// <param name="Label">What the machine is called (<see cref="LicenseSignInFold.Label"/>).</param>
/// <param name="ActiveAccountEmail">The account it is on, lower-cased; null when on none.</param>
/// <param name="IsStale">Its last report is older than <see cref="LicenseContract.MachineStaleAfter"/>.</param>
/// <param name="ReportsRefused">Its reports are being refused, so what it holds now is not known.</param>
/// <param name="Stranded">When it is on an account at or above the switching threshold, some account has room, and it is
/// ready for none of them: the engine's own verdict (<see cref="LicenseDecisionEngine.StrandedMachines"/>); else null.</param>
/// <param name="Cells">One per paid account, in <see cref="LicenseCoverage.Accounts"/> order.</param>
public sealed record LicenseCoverageRow(
    Guid MachineId,
    string MachineName,
    string OsUser,
    string DirectorId,
    string Label,
    string? ActiveAccountEmail,
    bool IsStale,
    bool ReportsRefused,
    DateTime LastReportAtUtc,
    LicenseStrandedMachine? Stranded,
    IReadOnlyList<LicenseCoverageCell> Cells)
{
    /// <summary>The cells of this row that are gaps.</summary>
    public IReadOnlyList<LicenseCoverageCell> Gaps => Cells.Where(c => c.IsGap).ToList();

    /// <summary>True when any cell of this row is a gap.</summary>
    public bool HasGap => Cells.Any(c => c.IsGap);
}

/// <summary>A paid account: a column of the grid.</summary>
public sealed record LicenseCoverageAccount(string AccountEmail, string? Plan);

/// <summary>The whole grid of one tenant at one moment.</summary>
public sealed record LicenseCoverage(DateTime NowUtc, IReadOnlyList<LicenseCoverageAccount> Accounts, IReadOnlyList<LicenseCoverageRow> Rows)
{
    /// <summary>Every gap of every row.</summary>
    public int GapCount => Rows.Sum(r => r.Gaps.Count);
}

/// <summary>
/// COVERAGE: EVERY MACHINE AGAINST EVERY PAID ACCOUNT (mission "Licenses - Always Ready, Loud When Not", step 1).
///
/// On 6 November 2026 a machine reached 99% of its weekly limit and could not switch: it held no sign-in for the one
/// account that had room, and nothing on the Licenses page showed the hole until it was too late. This fold asks, for
/// every machine that reports and every PAID account, whether the machine holds a fresh sign-in for it - so the owner
/// sees at a glance whether every machine could switch now, whatever the usage.
///
/// THIS IS THE ONE PLACE THAT DECIDES "GAP". The Licenses page draws it, and the repeating cannot-switch warning and the
/// daily fire drill read the same <see cref="Judge"/>. Whether a sign-in is usable is
/// <see cref="LicenseDecisionEngine.Readiness"/> (the one definition of ready); this adds only that a sign-in ending within
/// <see cref="LicenseCoverageRules.EndingWithin"/> is a gap too, because it will not be there when it is needed.
///
/// COLOUR (the owner, 6 November 2026, verbatim): "I think a warning should be yellow, not red. It doesn't go red until
/// it goes above 97%." A gap is <see cref="LicenseTones.Warning"/>. A cell is <see cref="LicenseTones.Bad"/> only when the
/// machine is stranded - on an account at or above <see cref="LicenseDecisionEngine.ThresholdPercent"/> with nowhere to go
/// (<see cref="LicenseDecisionEngine.StrandedMachines"/>, the same rule the switch acts on) - and the cell is a gap for an
/// account with room. At or above, because at the threshold the switch is due: a machine that cannot make it has stopped.
///
/// Only paid accounts are columns: an account with no paid plan is no place to switch to, so not holding it is no gap.
/// Every machine that reports is a row, a test Director's machine included: a missing sign-in is a gap whatever the machine.
/// Pure: facts go in, an answer comes out.
/// </summary>
public static class LicenseCoverageFold
{
    /// <summary>Judge every machine of <paramref name="picture"/> against every paid account.</summary>
    public static LicenseCoverage Judge(LicensePicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
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
            var withRoom = stuck is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : LicenseDecisionEngine.TargetsWithRoom(snapshot, stuck.AccountEmail).ToHashSet(StringComparer.Ordinal);
            var cells = paid.Select(p => Cell(state, active, p.AccountEmail, withRoom, now)).ToList();
            rows.Add(new LicenseCoverageRow(machine.MachineId, machine.MachineName, machine.OsUser, machine.DirectorId,
                LicenseSignInFold.Label(machines, machine.MachineName, machine.OsUser), active,
                LicenseDecisionEngine.IsMachineStale(state, now), state.ReportsRefused, Utc(machine.LastReportAtUtc), stuck, cells));
        }
        return new LicenseCoverage(now, paid, rows);
    }

    private static LicenseCoverageCell Cell(LicenseMachineState state, string? active, string account, HashSet<string> withRoom, DateTime now)
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
        var gap = cellState != LicenseCoverageCellStates.Held;
        return new LicenseCoverageCell(account, cellState, readiness.SignInExpiresAtUtc, gap, gap && withRoom.Contains(account));
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
        FileLog.Write($"[LicenseCoverageFold] Dto: accounts={dto.Accounts.Count}, rows={dto.Rows.Count}, gaps={dto.GapCount}, tone={dto.Tone}");
        return dto;
    }

    public const string NoMachinesText = "No machine has reported yet.";

    public const string NoPaidAccountsText = "No paid account is known yet.";

    public const string FullCoverageText = "Every machine holds a fresh sign-in for every paid account.";

    private static (string Text, string Tone) Summary(LicenseCoverage coverage)
    {
        if (coverage.Rows.Count == 0)
            return (NoMachinesText, LicenseTones.Neutral);
        if (coverage.Accounts.Count == 0)
            return (NoPaidAccountsText, LicenseTones.Neutral);
        var stranded = coverage.Rows.Where(r => r.Stranded is not null).ToList();
        var gappy = coverage.Rows.Where(r => r.HasGap).ToList();
        var silent = coverage.Rows.Where(r => r.IsStale || r.ReportsRefused).ToList();
        if (stranded.Count == 0 && gappy.Count == 0 && silent.Count == 0)
            return (FullCoverageText, LicenseTones.Good);
        var parts = new List<string>();
        if (stranded.Count > 0)
            parts.Add($"{Names(stranded)} cannot switch and {(stranded.Count == 1 ? "is" : "are")} at the limit.");
        if (gappy.Count > 0)
            parts.Add($"{Count(coverage.GapCount, "missing sign-in")} on {Count(gappy.Count, "machine")}.");
        if (silent.Count > 0)
            parts.Add($"{Names(silent)} {(silent.Count == 1 ? "is" : "are")} not reporting, so {(silent.Count == 1 ? "it" : "they")} cannot be switched.");
        return (string.Join(" ", parts), stranded.Count > 0 ? LicenseTones.Bad : LicenseTones.Warning);
    }

    private static LicenseCoverageRowDto Row(LicenseCoverageRow row, DateTime now)
    {
        var (text, tone) = RowWords(row, now);
        return new LicenseCoverageRowDto
        {
            Machine = new LicenseMachineRefDto
            {
                MachineId = row.MachineId.ToString("N"),
                MachineName = row.MachineName,
                OsUser = row.OsUser,
                Label = row.Label,
            },
            ActiveAccountEmail = row.ActiveAccountEmail,
            Text = text,
            Tone = tone,
            GapCount = row.Gaps.Count,
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
        else if (row.IsStale)
            sentences.Add($"Not reporting since {Clock(row.LastReportAtUtc)}, so it cannot be switched until it reports again; the cells show its last report.");
        if (row.Stranded is null && row.HasGap)
            sentences.Add($"Missing a fresh sign-in for {Count(row.Gaps.Count, "paid account")}.");
        if (sentences.Count == 0)
            return (row.Cells.Count == 0 ? NoPaidAccountsText : "Could switch to any paid account now.", row.Cells.Count == 0 ? LicenseTones.Neutral : LicenseTones.Good);
        return (string.Join(" ", sentences), row.Stranded is not null ? LicenseTones.Bad : LicenseTones.Warning);
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
        Tone = cell.IsCritical ? LicenseTones.Bad : cell.IsGap ? LicenseTones.Warning : LicenseTones.Good,
        IsGap = cell.IsGap,
    };

    private static string Names(IReadOnlyList<LicenseCoverageRow> rows) => rows.Count switch
    {
        1 => rows[0].Label,
        _ => string.Join(", ", rows.Take(rows.Count - 1).Select(r => r.Label)) + " and " + rows[^1].Label,
    };

    private static string Count(int number, string unit) => number == 1 ? $"1 {unit}" : $"{number} {unit}s";

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
