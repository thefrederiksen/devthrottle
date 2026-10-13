using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE LICENSE READ MODEL as a pure fold (License Switching, contract section 5.2): an account's numbers are its
/// freshest reading from any machine; an active reading is stale after 6 minutes, a spare's after 30, a machine after
/// 6 minutes without a report; a holding never read is stale.
/// </summary>
public sealed class LicenseReadModelTests
{
    private static LicenseMachineEntity Machine(string name, DateTime lastReport, string? active)
        => new() { MachineName = name, OsUser = "owner", MachineKey = name, OsUserKey = "owner", DirectorId = "dir-" + name,
                   LastReportAtUtc = lastReport, ActiveAccountEmail = active };

    private static LicenseHoldingEntity Held(LicenseMachineEntity m, string email, bool active, DateTime? judgedReadAt, double fiveHour = 10)
        => new() { MachineId = m.Id, AccountEmail = email, IsActive = active, FiveHourUtilization = fiveHour,
                   ReadAtUtc = judgedReadAt, JudgedReadAtUtc = judgedReadAt };

    private static LicenseAccountEntity Account(string email) => new() { AccountEmail = email, Plan = "max" };

    [Theory]
    [InlineData(true, 6, false)]
    [InlineData(true, 7, true)]
    [InlineData(false, 30, false)]
    [InlineData(false, 31, true)]
    public void IsHoldingStale_UsesTheActiveOrSpareLimit(bool active, int minutesOld, bool stale)
        => Assert.Equal(stale, LicenseStaleness.IsHoldingStale(active, T0.AddMinutes(-minutesOld), T0));

    [Theory]
    [InlineData(null, LicenseSignInExpiry.NotKnown)]
    [InlineData(-1, LicenseSignInExpiry.Expired)]
    [InlineData(0, LicenseSignInExpiry.Expired)]
    [InlineData(72, LicenseSignInExpiry.ExpiresSoon)]
    [InlineData(73, LicenseSignInExpiry.Fine)]
    public void SignInExpiryVerdict_FollowsContractSectionFiveSeven(int? hoursFromNow, string verdict)
        => Assert.Equal(verdict, LicenseSignInExpiry.Verdict(hoursFromNow is { } h ? T0.AddHours(h) : null, T0));

    [Fact]
    public void IsHoldingStale_NeverRead_IsStale()
        => Assert.True(LicenseStaleness.IsHoldingStale(false, null, T0));

    [Theory]
    [InlineData(6, false)]
    [InlineData(7, true)]
    public void IsMachineStale_SixMinutesWithoutAReport(int minutesOld, bool stale)
        => Assert.Equal(stale, LicenseStaleness.IsMachineStale(T0.AddMinutes(-minutesOld), T0));

    [Fact]
    public void Fold_AccountNumbers_AreTheFreshestReadingFromAnyMachine()
    {
        var m1 = Machine("m1", T0, A);
        var m2 = Machine("m2", T0, B);

        var overview = LicenseReadModel.Fold(new[] { Account(A), Account(B) }, new[] { m1, m2 },
            new[] { Held(m1, A, true, T0.AddMinutes(-5), 90), Held(m2, A, false, T0.AddMinutes(-1), 91), Held(m2, B, true, T0) }, T0);

        var a = overview.Accounts.Single(x => x.AccountEmail == A);
        Assert.Equal(("m2", 91.0, false), (a.Freshest!.MachineName, a.Freshest.FiveHourUtilization!.Value, a.IsStale));
        Assert.Equal(new[] { "m1" }, a.ActiveOnMachines);
        Assert.Equal(new[] { "m2" }, a.SpareOnMachines);
    }

    [Fact]
    public void Fold_ActiveReadingSevenMinutesOld_IsStale_SpareReadingSevenMinutesOld_IsNot()
    {
        var m1 = Machine("m1", T0, A);

        var overview = LicenseReadModel.Fold(new[] { Account(A), Account(B) }, new[] { m1 },
            new[] { Held(m1, A, true, T0.AddMinutes(-7)), Held(m1, B, false, T0.AddMinutes(-7)) }, T0);

        Assert.True(overview.Accounts.Single(x => x.AccountEmail == A).IsStale);
        Assert.False(overview.Accounts.Single(x => x.AccountEmail == B).IsStale);
    }

    [Fact]
    public void Fold_AccountNoMachineHolds_IsListed_WithNoReading_AndStale()
    {
        var overview = LicenseReadModel.Fold(new[] { Account(C) }, Array.Empty<LicenseMachineEntity>(), Array.Empty<LicenseHoldingEntity>(), T0);

        var c = Assert.Single(overview.Accounts);
        Assert.Null(c.Freshest);
        Assert.True(c.IsStale);
    }

    [Fact]
    public void Fold_Machine_HasItsActiveAccountSparesAndStaleness()
    {
        var m1 = Machine("m1", T0.AddMinutes(-10), A);

        var machine = Assert.Single(LicenseReadModel.Fold(new[] { Account(A), Account(B), Account(C) }, new[] { m1 },
            new[] { Held(m1, A, true, T0), Held(m1, C, false, T0), Held(m1, B, false, T0) }, T0).Machines);

        Assert.Equal(A, machine.ActiveAccountEmail);
        Assert.Equal(new[] { B, C }, machine.Spares);
        Assert.Equal(new[] { A, B, C }, machine.Holdings.Select(h => h.AccountEmail));
        Assert.True(machine.Holdings[0].IsActive);
        Assert.True(machine.IsStale);
    }
}
