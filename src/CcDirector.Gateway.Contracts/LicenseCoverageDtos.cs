namespace CcDirector.Gateway.Contracts;

/// <summary>What one cell of the coverage grid says of one machine and one paid account (<see cref="LicenseCoverageCellDto.State"/>).</summary>
public static class LicenseCoverageCellStates
{
    /// <summary>The machine is on this account now.</summary>
    public const string InUse = "in-use";

    /// <summary>The machine holds a sign-in for this account that it could switch to now, ending more than
    /// <see cref="LicenseCoverageRules.EndingWithin"/> from now (or not saying when it ends).</summary>
    public const string Held = "held";

    /// <summary>The machine holds a sign-in for this account that ends within <see cref="LicenseCoverageRules.EndingWithin"/>,
    /// or has ended already.</summary>
    public const string Ending = "ending";

    /// <summary>The machine holds no sign-in for this account.</summary>
    public const string NotHeld = "not-held";

    /// <summary>The machine holds a sign-in for this account, and its last read was refused.</summary>
    public const string Refused = "refused";
}

/// <summary>The numbers the coverage grid is judged by.</summary>
public static class LicenseCoverageRules
{
    /// <summary>
    /// A sign-in that ends within this of now is a gap: it is held, and it is ending. Shorter than the 7 days ahead at
    /// which renewal starts (the Delivery Lead's ruling, 7 November 2026), so a sign-in that renewal is still working on
    /// is not shown yellow; at 5 days, a gap means two daily renewals have already failed.
    /// </summary>
    public static readonly TimeSpan EndingWithin = TimeSpan.FromDays(5);
}

/// <summary>
/// THE COVERAGE GRID of the Licenses page (mission "Licenses - Always Ready, Loud When Not", step 1): one row per
/// machine that reports, one column per PAID account, each cell saying whether that machine holds a fresh sign-in for
/// that account. Folded on the Gateway; the page draws every word and tone as given and decides nothing.
/// A gap is <see cref="LicenseTones.Warning"/> (yellow). A cell is <see cref="LicenseTones.Bad"/> (red) only when the
/// machine is on an account at or above the switching threshold and has nowhere to go.
/// </summary>
public sealed class LicenseCoverageDto
{
    /// <summary>One sentence for the whole grid: "Every machine holds a fresh sign-in for every paid account.", or how
    /// many gaps there are and on how many machines.</summary>
    public string Text { get; set; } = "";

    /// <summary>One of <see cref="LicenseTones"/> for <see cref="Text"/>.</summary>
    public string Tone { get; set; } = "";

    /// <summary>How many cells are gaps, over every row.</summary>
    public int GapCount { get; set; }

    /// <summary>The columns: every paid account, in account email order.</summary>
    public List<LicenseCoverageAccountDto> Accounts { get; set; } = new();

    /// <summary>The rows: every machine that reports, in the overview's machine order.</summary>
    public List<LicenseCoverageRowDto> Rows { get; set; } = new();
}

/// <summary>One column of the coverage grid.</summary>
public sealed class LicenseCoverageAccountDto
{
    public string AccountEmail { get; set; } = "";

    /// <summary>The plan, ready to show: "Max", "Pro", or "Not known".</summary>
    public string PlanText { get; set; } = "";
}

/// <summary>One row of the coverage grid: one machine.</summary>
public sealed class LicenseCoverageRowDto
{
    public LicenseMachineRefDto Machine { get; set; } = new();

    /// <summary>The account the machine is on; null when it is signed in to none.</summary>
    public string? ActiveAccountEmail { get; set; }

    /// <summary>What the row says as a whole: whether the machine could switch now, and if not, why.</summary>
    public string Text { get; set; } = "";

    /// <summary>One of <see cref="LicenseTones"/> for <see cref="Text"/>.</summary>
    public string Tone { get; set; } = "";

    /// <summary>How many of its cells are gaps.</summary>
    public int GapCount { get; set; }

    /// <summary>One cell per column of <see cref="LicenseCoverageDto.Accounts"/>, in the same order.</summary>
    public List<LicenseCoverageCellDto> Cells { get; set; } = new();
}

/// <summary>One cell of the coverage grid: one machine and one paid account.</summary>
public sealed class LicenseCoverageCellDto
{
    public string AccountEmail { get; set; } = "";

    /// <summary>One of <see cref="LicenseCoverageCellStates"/>.</summary>
    public string State { get; set; } = "";

    /// <summary>The words to show in the cell: "In use", "Held", "Ends in 2 days 4 hours", "Not held", "Sign-in refused".</summary>
    public string Text { get; set; } = "";

    /// <summary>One of <see cref="LicenseTones"/>.</summary>
    public string Tone { get; set; } = "";

    /// <summary>True when the cell is a gap: the machine could not switch to this account now and keep working on it.</summary>
    public bool IsGap { get; set; }
}
