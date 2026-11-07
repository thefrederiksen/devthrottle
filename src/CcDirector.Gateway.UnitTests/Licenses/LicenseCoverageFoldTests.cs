using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE COVERAGE GRID (mission "Licenses - Always Ready, Loud When Not"): every machine that reports against every paid
/// account, folded once on the Gateway. The facts go in as usage reports applied to the real store on a throwaway SQLite
/// file and come back through <see cref="LicenseSnapshotSource"/>. A GAP is the owner's ruling of 7 November 2026: only a
/// machine at 80% or more with no READY next license, and only on the license it is told to sign in to - never a license
/// that is merely not held. The count is of machines.
/// </summary>
public sealed class LicenseCoverageFoldTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _source;

    private static readonly TenantId Tenant = new("acct-a");

    public LicenseCoverageFoldTests()
    {
        _store = new LicenseStore(_harness.Open());
        _source = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
    }

    public void Dispose() => _harness.Dispose();

    private void Apply(LicenseUsageReport report, long seq = 1, DateTime? at = null)
        => _store.ApplyReport(Tenant, "dir-" + report.MachineName, seq, report, at ?? T0, TimeSpan.Zero);

    private LicensePicture Picture(DateTime now) => _source.ReadPicture(Tenant, now, Array.Empty<LicenseExhaustion>());

    private LicenseCoverageDto Grid(DateTime now) => LicenseCoverageFold.Dto(CoverageOf(Picture(now)));

    private static LicenseCoverageCellDto Cell(LicenseCoverageDto grid, string machine, string account)
        => grid.Rows.Single(r => r.Machine.MachineName == machine).Cells.Single(c => c.AccountEmail == account);

    private static LicenseCoverageRowDto Row(LicenseCoverageDto grid, string machine)
        => grid.Rows.Single(r => r.Machine.MachineName == machine);

    private static LicenseHolding Ending(LicenseHolding holding, DateTime endsAtUtc)
    {
        holding.SignInExpiresAtUtc = endsAtUtc;
        return holding;
    }

    [Fact]
    public void Judge_EveryMachineHoldsEverything_NoGap_AndTheGridSaysSo()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));
        Apply(Report("m2", Holding(B, true), Holding(A, false)));

        var grid = Grid(T0);

        Assert.Equal(new[] { A, B }, grid.Accounts.Select(a => a.AccountEmail));
        Assert.Equal("Max", grid.Accounts[0].PlanText);
        Assert.Equal(0, grid.GapCount);
        Assert.Equal((LicenseCoverageFold.FullCoverageText, LicenseTones.Good), (grid.Text, grid.Tone));
        Assert.Equal((LicenseCoverageCellStates.InUse, "In use", LicenseTones.Good), Words(Cell(grid, "m1", A)));
        Assert.Equal((LicenseCoverageCellStates.Held, "Held", LicenseTones.Good), Words(Cell(grid, "m1", B)));
    }

    // The defect the ruling ends: a machine under 80% that does not hold a license is NO gap. Before, this was "1 missing
    // sign-in" and a yellow cell.
    [Fact]
    public void Judge_ALicenseNotHeld_Under80_IsNoGap_AndNotYellow()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));
        Apply(Report("m2", Holding(B, true)));

        var grid = Grid(T0);

        var cell = Cell(grid, "m2", A);
        Assert.Equal((LicenseCoverageCellStates.NotHeld, "Not held", LicenseTones.Neutral), Words(cell));
        Assert.False(cell.IsGap);
        Assert.Equal((0, LicenseNextLicenseStates.NotNeeded), (Row(grid, "m2").GapCount, CoverageOf(Picture(T0)).Rows.Single(r => r.MachineName == "m2").Next!.State));
        Assert.Equal((LicenseCoverageFold.FullCoverageText, LicenseTones.Good, 0), (grid.Text, grid.Tone, grid.GapCount));
    }

    // At 80% or more with no ready next license: one gap, on the license it is told to sign in to, yellow.
    [Fact]
    public void Judge_At85WithNoReadyNextLicense_OneGap_OnTheRecommendedLicense()
    {
        Apply(Report("m1", Holding(A, true, sevenDay: 85)));
        Apply(Report("m2", Holding(B, true, sevenDay: 5), Holding(C, false, sevenDay: 50)));

        var grid = Grid(T0);

        Assert.True(Cell(grid, "m1", B).IsGap);
        Assert.Equal(LicenseTones.Warning, Cell(grid, "m1", B).Tone);
        Assert.False(Cell(grid, "m1", C).IsGap);
        Assert.Equal(LicenseTones.Neutral, Cell(grid, "m1", C).Tone);
        var row = Row(grid, "m1");
        Assert.Equal((LicenseTones.Warning, 1), (row.Tone, row.GapCount));
        Assert.StartsWith("Needs a next license:", row.Text);
        Assert.Equal(("m1 needs a ready next license.", LicenseTones.Warning, 1), (grid.Text, grid.Tone, grid.GapCount));
    }

    // At 85% with a ready next license held: no gap at all, whatever else it does not hold.
    [Fact]
    public void Judge_At85WithAReadyNextLicense_IsNoGap()
    {
        Apply(Report("m1", Holding(A, true, sevenDay: 85), Holding(B, false, sevenDay: 5)));
        Apply(Report("m2", Holding(C, true, sevenDay: 10)));

        var grid = Grid(T0);

        Assert.Equal(0, grid.GapCount);
        Assert.False(Cell(grid, "m1", C).IsGap);
        Assert.StartsWith("Ready:", Row(grid, "m1").Text);
    }

    // The count is of MACHINES that will be stuck, never of licenses not held: two machines needing one, with four
    // licenses between them not held, is 2.
    [Fact]
    public void GapCount_CountsMachines_NotLicensesNotHeld()
    {
        Apply(Report("m1", Holding(A, true, sevenDay: 90)));
        Apply(Report("m2", Holding(A, true, sevenDay: 90)));
        Apply(Report("m3", Holding(B, true, sevenDay: 5), Holding(C, false, sevenDay: 6)));

        var coverage = CoverageOf(Picture(T0));

        Assert.Equal(4, coverage.Rows.Where(r => r.MachineName is "m1" or "m2").SelectMany(r => r.Cells)
            .Count(c => c.State == LicenseCoverageCellStates.NotHeld));
        Assert.Equal(2, coverage.GapCount);
        Assert.Equal(2, LicenseCoverageFold.Dto(coverage).GapCount);
    }

    [Theory]
    [InlineData(96.0, LicenseTones.Warning)]
    [InlineData(97.0, LicenseTones.Bad)]
    public void Judge_TheGapIsRedOnlyFromTheThreshold(double fiveHour, string expected)
    {
        Apply(Report("m1", Holding(A, true, fiveHour: fiveHour)));
        Apply(Report("m2", Holding(B, true, fiveHour: 5, sevenDay: 5)));

        var grid = Grid(T0);

        Assert.Equal(expected, Cell(grid, "m1", B).Tone);
        Assert.Equal(expected, Row(grid, "m1").Tone);
        Assert.Equal(expected, grid.Tone);
        if (expected == LicenseTones.Bad)
        {
            Assert.Equal($"Cannot switch: {A} is at 97% of its 5-hour window, and it holds no fresh sign-in for an account with room.", Row(grid, "m1").Text);
            Assert.StartsWith("m1 cannot switch and is at the limit.", grid.Text);
        }
    }

    // A STALE machine is not judged: what it holds now is not known. No gap, and its row says it is not reporting.
    [Fact]
    public void Judge_AStaleMachineAt97_HasNoGap_AndSaysItIsNotReporting()
    {
        Apply(Report("m1", Holding(A, true, fiveHour: 97)), at: T0);
        Apply(Report("m2", T0.AddMinutes(20), Holding(B, true, fiveHour: 5, sevenDay: 5, readAt: T0.AddMinutes(20))), at: T0.AddMinutes(20));

        var grid = Grid(T0.AddMinutes(21));

        var row = Row(grid, "m1");
        Assert.Equal((LicenseTones.Warning, 0), (row.Tone, row.GapCount));
        Assert.StartsWith("Not reporting since", row.Text);
        Assert.False(Cell(grid, "m1", B).IsGap);
        Assert.NotEqual(LicenseTones.Bad, grid.Tone);
    }

    [Fact]
    public void Judge_AnAccountWithNoPaidPlan_IsNotAColumn()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false), Holding(C, false, readError: LicenseReadErrors.NoPaidPlan)));
        Apply(Report("m2", Holding(B, true), Holding(A, false)));

        var grid = Grid(T0.AddMinutes(1));

        Assert.Equal(new[] { A, B }, grid.Accounts.Select(a => a.AccountEmail));
        Assert.Equal(0, grid.GapCount);
    }

    // A sign-in ending soon or refused is a fact the cell shows; it is not a gap unless the machine needs that license.
    [Theory]
    [InlineData(3, LicenseCoverageCellStates.Ending, "Ends in 3 days")]
    [InlineData(6, LicenseCoverageCellStates.Held, "Held")]
    public void Judge_ASignInEndingWithinFiveDays_IsShown_AndIsNoGapUnder80(int days, string state, string text)
    {
        Apply(Report("m1", Holding(A, true), Ending(Holding(B, false), T0.AddDays(days))));
        Apply(Report("m2", Holding(B, true), Holding(A, false)));

        var cell = Cell(Grid(T0), "m1", B);

        Assert.Equal((state, text), (cell.State, cell.Text));
        Assert.False(cell.IsGap);
    }

    [Fact]
    public void Judge_ARefusedSignIn_Under80_IsShownNeutral_AndIsNoGap()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false, readError: LicenseReadErrors.Unauthorized)));
        Apply(Report("m2", Holding(B, true), Holding(A, false)));

        var cell = Cell(Grid(T0), "m1", B);

        Assert.Equal((LicenseCoverageCellStates.Refused, "Sign-in refused", LicenseTones.Neutral), Words(cell));
        Assert.False(cell.IsGap);
    }

    [Fact]
    public void Judge_AMachineOnNoAccount_IsStillARow_WithNoGap()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));
        Apply(Report("m2"));

        var row = Row(Grid(T0), "m2");

        Assert.Null(row.ActiveAccountEmail);
        Assert.Equal((0, LicenseCoverageFold.NoLicenseText), (row.GapCount, row.Text));
    }

    [Fact]
    public void Overview_CarriesTheGrid_AndEachMachinesNextLicense()
    {
        Apply(Report("m1", Holding(A, true, sevenDay: 85)));
        Apply(Report("m2", Holding(B, true, sevenDay: 5)));

        var overview = OverviewOf(Picture(T0));

        Assert.Equal(new[] { A, B }, overview.Coverage.Accounts.Select(a => a.AccountEmail));
        var m1 = overview.Machines.Single(m => m.MachineName == "m1");
        Assert.Equal((LicenseNextLicenseStates.Needed, B, LicenseNextLicenseActions.AddSpare, LicenseTones.Warning),
            (m1.NextLicenseState, m1.RecommendedAccountEmail, m1.RecommendedSignInAction, m1.NextLicenseTone));
        var m2 = overview.Machines.Single(m => m.MachineName == "m2");
        Assert.Equal((LicenseNextLicenseStates.NotNeeded, null, null), (m2.NextLicenseState, m2.RecommendedAccountEmail, m2.RecommendedSignInAction));
    }

    [Fact]
    public void Overview_NothingReported_TheGridSaysSo()
    {
        var overview = OverviewOf(Picture(T0));

        Assert.Equal((LicenseCoverageFold.NoMachinesText, LicenseTones.Neutral), (overview.Coverage.Text, overview.Coverage.Tone));
        Assert.Empty(overview.Coverage.Rows);
    }

    private static (string, string, string) Words(LicenseCoverageCellDto cell) => (cell.State, cell.Text, cell.Tone);
}
