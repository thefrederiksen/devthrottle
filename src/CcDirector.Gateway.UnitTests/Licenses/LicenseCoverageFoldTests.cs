using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE COVERAGE GRID (mission "Licenses - Always Ready, Loud When Not", step 1): every machine that reports against every
/// paid account, folded once on the Gateway. The facts go in as usage reports applied to the real store on a throwaway
/// SQLite file and come back through <see cref="LicenseSnapshotSource"/>, so this watches the store, the read model, the
/// snapshot and the fold together. A gap is yellow; red only for a machine at the switching threshold with nowhere to go.
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

    private LicenseCoverageDto Grid(DateTime now) => LicenseCoverageFold.Dto(LicenseCoverageFold.Judge(Picture(now)));

    private static LicenseCoverageCellDto Cell(LicenseCoverageDto grid, string machine, string account)
        => grid.Rows.Single(r => r.Machine.MachineName == machine).Cells.Single(c => c.AccountEmail == account);

    private static LicenseHolding Ending(LicenseHolding holding, DateTime endsAtUtc)
    {
        holding.SignInExpiresAtUtc = endsAtUtc;
        return holding;
    }

    [Fact]
    public void Judge_FullCoverage_EveryCellHeldOrInUse_AndTheGridSaysSo()
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
        Assert.All(grid.Rows, r => Assert.Equal(("Could switch to any paid account now.", LicenseTones.Good), (r.Text, r.Tone)));
    }

    [Fact]
    public void Judge_OneGap_IsYellow_OnItsCellRowAndGrid()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));
        Apply(Report("m2", Holding(B, true)));

        var grid = Grid(T0);

        var gap = Cell(grid, "m2", A);
        Assert.Equal((LicenseCoverageCellStates.NotHeld, "Not held", LicenseTones.Warning), Words(gap));
        Assert.True(gap.IsGap);
        var row = grid.Rows.Single(r => r.Machine.MachineName == "m2");
        Assert.Equal(("Missing a fresh sign-in for 1 paid account.", LicenseTones.Warning, 1), (row.Text, row.Tone, row.GapCount));
        Assert.Equal(("1 missing sign-in on 1 machine.", LicenseTones.Warning, 1), (grid.Text, grid.Tone, grid.GapCount));
    }

    [Theory]
    [InlineData(96.0, LicenseTones.Warning)]
    [InlineData(97.0, LicenseTones.Bad)]
    public void Judge_GapOnAMachineAtTheLimitWithNowhereToGo_IsRedOnlyFromTheThreshold(double fiveHour, string expected)
    {
        // m1 is on A and holds no sign-in for B, the one account with room. Below the threshold that is a warning; at it
        // the machine has stopped.
        Apply(Report("m1", Holding(A, true, fiveHour: fiveHour)));
        Apply(Report("m2", Holding(B, true, fiveHour: 5, sevenDay: 5)));

        var grid = Grid(T0);

        Assert.Equal(expected, Cell(grid, "m1", B).Tone);
        var row = grid.Rows.Single(r => r.Machine.MachineName == "m1");
        Assert.Equal(expected, row.Tone);
        Assert.Equal(expected, grid.Tone);
        if (expected == LicenseTones.Bad)
        {
            Assert.Equal($"Cannot switch: {A} is at 97% of its 5-hour window, and it holds no fresh sign-in for an account with room.", row.Text);
            Assert.StartsWith("m1 cannot switch and is at the limit.", grid.Text);
        }
    }

    // The review of pull request 4707, finding b: on a stranded machine only the gap for an account WITH room is red - the
    // one sign-in that would let it switch. A gap for an account at its own limit stays yellow.
    [Fact]
    public void Judge_StrandedMachine_AGapForAnAccountWithNoRoom_StaysYellow()
    {
        Apply(Report("m1", Holding(A, true, fiveHour: 98)));
        Apply(Report("m2", Holding(B, true, fiveHour: 5, sevenDay: 5), Holding(C, false, fiveHour: 99)));

        var grid = Grid(T0);

        Assert.Equal(LicenseTones.Bad, Cell(grid, "m1", B).Tone);
        Assert.Equal((LicenseCoverageCellStates.NotHeld, LicenseTones.Warning), (Cell(grid, "m1", C).State, Cell(grid, "m1", C).Tone));
    }

    // Finding b: a STALE machine on an account at 97% is not stranded - what it holds now is not known, and it cannot be
    // sent a switch - so its gaps stay yellow and its row says it is not reporting.
    [Fact]
    public void Judge_AStaleMachineAt97_StaysYellow()
    {
        Apply(Report("m1", Holding(A, true, fiveHour: 97)), at: T0);
        Apply(Report("m2", T0.AddMinutes(20), Holding(B, true, fiveHour: 5, sevenDay: 5, readAt: T0.AddMinutes(20))), at: T0.AddMinutes(20));

        var grid = Grid(T0.AddMinutes(21));

        var row = grid.Rows.Single(r => r.Machine.MachineName == "m1");
        Assert.Equal(LicenseTones.Warning, row.Tone);
        Assert.StartsWith("Not reporting since", row.Text);
        Assert.Equal(LicenseTones.Warning, Cell(grid, "m1", B).Tone);
        Assert.NotEqual(LicenseTones.Bad, grid.Tone);
    }

    [Fact]
    public void Judge_AnAccountWithNoPaidPlan_IsNotAColumn_SoNotHoldingItIsNoGap()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false), Holding(C, false, readError: LicenseReadErrors.NoPaidPlan)));
        Apply(Report("m2", Holding(B, true), Holding(A, false)));

        var grid = Grid(T0.AddMinutes(1));

        Assert.Equal(new[] { A, B }, grid.Accounts.Select(a => a.AccountEmail));
        Assert.Equal(0, grid.GapCount);
    }

    [Fact]
    public void Judge_AStaleMachine_IsARow_SaysItIsNotReporting_AndShowsItsLastReport()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)), at: T0);
        Apply(Report("m2", T0.AddMinutes(20), Holding(B, true, readAt: T0.AddMinutes(20)), Holding(A, false, readAt: T0.AddMinutes(20))),
            at: T0.AddMinutes(20));

        var grid = Grid(T0.AddMinutes(21));

        var row = grid.Rows.Single(r => r.Machine.MachineName == "m1");
        Assert.Equal(LicenseTones.Warning, row.Tone);
        Assert.StartsWith("Not reporting since 2026-10-11 03:12 UTC", row.Text);
        Assert.Equal(LicenseCoverageCellStates.Held, Cell(grid, "m1", B).State);
        Assert.Equal(("m1 is not reporting, so it cannot be switched.", LicenseTones.Warning), (grid.Text, grid.Tone));
    }

    // Five days, inside the seven at which renewal starts: a sign-in renewal is still working on is held, not a gap.
    [Theory]
    [InlineData(3, LicenseCoverageCellStates.Ending, "Ends in 3 days")]
    [InlineData(4, LicenseCoverageCellStates.Ending, "Ends in 4 days")]
    [InlineData(6, LicenseCoverageCellStates.Held, "Held")]
    [InlineData(8, LicenseCoverageCellStates.Held, "Held")]
    public void Judge_ASignInEndingWithinFiveDays_IsAGap(int days, string state, string text)
    {
        Apply(Report("m1", Holding(A, true), Ending(Holding(B, false), T0.AddDays(days))));
        Apply(Report("m2", Holding(B, true), Holding(A, false)));

        var cell = Cell(Grid(T0), "m1", B);

        Assert.Equal((state, text), (cell.State, cell.Text));
        Assert.Equal(state == LicenseCoverageCellStates.Ending, cell.IsGap);
    }

    [Fact]
    public void Judge_ARefusedSignIn_IsAGap()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false, readError: LicenseReadErrors.Unauthorized)));
        Apply(Report("m2", Holding(B, true), Holding(A, false)));

        var cell = Cell(Grid(T0), "m1", B);

        Assert.Equal((LicenseCoverageCellStates.Refused, "Sign-in refused", LicenseTones.Warning), Words(cell));
        Assert.True(cell.IsGap);
    }

    [Fact]
    public void Judge_AMachineOnNoAccount_IsStillARow_WithEveryAccountAGap()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));
        Apply(Report("m2"));

        var row = Grid(T0).Rows.Single(r => r.Machine.MachineName == "m2");

        Assert.Null(row.ActiveAccountEmail);
        Assert.Equal(2, row.GapCount);
    }

    [Fact]
    public void Overview_CarriesTheGrid()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));

        var overview = LicenseDisplayFold.Overview(Picture(T0));

        Assert.Equal(new[] { A, B }, overview.Coverage.Accounts.Select(a => a.AccountEmail));
        Assert.Single(overview.Coverage.Rows);
    }

    [Fact]
    public void Overview_NothingReported_TheGridSaysSo()
    {
        var overview = LicenseDisplayFold.Overview(Picture(T0));

        Assert.Equal((LicenseCoverageFold.NoMachinesText, LicenseTones.Neutral), (overview.Coverage.Text, overview.Coverage.Tone));
        Assert.Empty(overview.Coverage.Rows);
    }

    private static (string, string, string) Words(LicenseCoverageCellDto cell) => (cell.State, cell.Text, cell.Tone);
}
