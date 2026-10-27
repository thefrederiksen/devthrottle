using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// WHICH MACHINES ARE STRANDED (mission "Licenses - Loud When Stranded", step 1), as pure code: a machine on a tired
/// account that is ready for no account with room, while one has room. The picture is the 26 October 2026 one in small:
/// account A is at its weekly limit, account B has room, machine-b holds a sign-in for B and machine-a does not. What these
/// hold down: machine-a is named against B with its real reason, and the automatic decision on the same picture leaves
/// exactly that machine behind; nothing is stranded when no account has room (that is the no-room event, unchanged),
/// when the account is merely near or at its limit with every machine ready (issue #4249), or when the machine is stale,
/// mid-switch, refused, reporting it cannot run a switch, or held.
/// </summary>
public sealed class LicenseStrandedMachineTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 13, 23, 0, DateTimeKind.Utc);

    private const string A = "account-a@example.com";
    private const string B = "account-b@example.com";
    private const string C = "account-c@example.com";

    private static LicenseAccountReading Reading(string email, double fiveHour, double week, DateTime? readAt = null, bool active = false)
        => new(email, new LicenseWindowReading(fiveHour, Now.AddHours(2)), new LicenseWindowReading(week, Now.AddDays(3)),
            readAt ?? Now.AddMinutes(-1), active);

    private static LicenseMachineState Machine(string name, string? active, params string[] spares)
        => new(name, "owner", active, spares.Select(s => new LicenseSpare(s, null)).ToList(), Now.AddMinutes(-1), false, null);

    private static LicenseSnapshot Snapshot(IEnumerable<LicenseAccountReading> accounts, IEnumerable<LicenseMachineState> machines)
        => new(Now, accounts.ToList(), machines.ToList(), [], []);

    // The 26 October picture: A at 100% of its weekly limit, B with room, machine-b ready for B, machine-a not.
    private static LicenseSnapshot TwentySixthOfOctober(params LicenseMachineState[] extra)
        => Snapshot([Reading(A, 30, 100, active: true), Reading(B, 5, 2)],
            new[] { Machine("machine-a", A), Machine("machine-b", A, B) }.Concat(extra));

    // The machine the automatic switch leaves behind is stranded, named against the account it would go to, with why.
    [Fact]
    public void StrandedMachines_ATiredAccountWithAMachineReadyForNoAccountWithRoom_NamesThatMachine()
    {
        var snapshot = TwentySixthOfOctober();

        var stranded = Assert.Single(LicenseDecisionEngine.StrandedMachines(snapshot));

        Assert.Equal(("machine-a", A, B, "100% of its weekly limit", LicenseNotReadyReasons.NoSignIn),
            (stranded.MachineName, stranded.AccountEmail, stranded.NeededAccountEmail, stranded.Tired, stranded.Readiness.NotReadyReason));
    }

    // One rule, two readers: the machines named stranded are exactly the ones Decide does not switch off the account.
    [Fact]
    public void StrandedMachines_OnThePictureDecideActsOn_AreTheMachinesItLeavesBehind()
    {
        var snapshot = TwentySixthOfOctober(Machine("machine-c", A, B));

        var decided = LicenseDecisionEngine.Decide(snapshot);
        var stranded = LicenseDecisionEngine.StrandedMachines(snapshot);

        Assert.Equal(new[] { "machine-b", "machine-c" }, decided.Switches.Select(s => s.MachineName).Order().ToArray());
        Assert.Equal(new[] { "machine-a" }, stranded.Select(s => s.MachineName).ToArray());
        Assert.Empty(decided.OpenedExhaustions);
    }

    // Every machine on the account stranded: still per machine, one each, and still not the no-room event.
    [Fact]
    public void StrandedMachines_NoMachineOnTheAccountIsReady_NamesEachOne_AndOpensNoNoRoomEvent()
    {
        var snapshot = Snapshot([Reading(A, 30, 100, active: true), Reading(B, 5, 2)],
            [Machine("machine-a", A), Machine("machine-b", A, C)]);

        var stranded = LicenseDecisionEngine.StrandedMachines(snapshot);

        Assert.Equal(new[] { "machine-a", "machine-b" }, stranded.Select(s => s.MachineName).ToArray());
        Assert.All(stranded, s => Assert.Equal(B, s.NeededAccountEmail));
        Assert.Empty(LicenseDecisionEngine.Decide(snapshot).OpenedExhaustions);
    }

    // A machine ready for a worse account with room is not stranded: it moves there.
    [Fact]
    public void StrandedMachines_ReadyForAnAccountWithRoomThatIsNotTheBest_IsNotStranded()
    {
        var snapshot = Snapshot([Reading(A, 30, 100, active: true), Reading(B, 5, 2), Reading(C, 5, 40)],
            [Machine("machine-a", A, C)]);

        Assert.Empty(LicenseDecisionEngine.StrandedMachines(snapshot));
    }

    // No account has room: the no-room event keeps its meaning, and nothing is called stranded.
    [Fact]
    public void StrandedMachines_NoAccountHasRoom_IsTheNoRoomEventAndNothingIsStranded()
    {
        var snapshot = Snapshot([Reading(A, 30, 100, active: true), Reading(B, 5, 98)],
            [Machine("machine-a", A), Machine("machine-b", A, B)]);

        var decided = LicenseDecisionEngine.Decide(snapshot);

        Assert.Empty(LicenseDecisionEngine.StrandedMachines(snapshot));
        Assert.Equal(A, Assert.Single(decided.OpenedExhaustions).AccountEmail);
        Assert.Equal(LicenseStore.NoRoomHappened(A), Assert.Single(decided.Emails).Happened);
    }

    // Issue #4249: an account near or at its limit with every machine on it ready raises nothing new.
    [Theory]
    [InlineData(80, 80)]
    [InlineData(96.9, 96.9)]
    [InlineData(30, 100)]
    [InlineData(100, 100)]
    public void StrandedMachines_EveryMachineReady_AtAnyUse_NamesNothing(double fiveHour, double week)
    {
        var snapshot = Snapshot([Reading(A, fiveHour, week, active: true), Reading(B, 5, 2)],
            [Machine("machine-a", A, B), Machine("machine-b", A, B)]);

        Assert.Empty(LicenseDecisionEngine.StrandedMachines(snapshot));
    }

    // Issue #4249 again: near the limit, under the threshold, a machine that is not ready is not stranded either.
    [Fact]
    public void StrandedMachines_UnderTheThreshold_AMachineNotReady_IsNotStranded()
    {
        var snapshot = Snapshot([Reading(A, 90, 96.9, active: true), Reading(B, 5, 2)], [Machine("machine-a", A)]);

        Assert.Empty(LicenseDecisionEngine.StrandedMachines(snapshot));
    }

    // A stale reading of the tired account never names a machine: stale numbers never trigger (contract 5.2).
    [Fact]
    public void StrandedMachines_TheAccountsReadingIsStale_NamesNothing()
    {
        var snapshot = Snapshot([Reading(A, 30, 100, readAt: Now.AddMinutes(-20), active: true), Reading(B, 5, 2)],
            [Machine("machine-a", A)]);

        Assert.Empty(LicenseDecisionEngine.StrandedMachines(snapshot));
    }

    // The existing meanings are kept: a stale machine, one mid-switch, one whose reports are refused, one that reports it
    // cannot run a switch, and one held by a switch that keeps failing are each not stranded.
    [Theory]
    [InlineData("stale")]
    [InlineData("open-switch")]
    [InlineData("in-progress")]
    [InlineData("refused")]
    [InlineData("cannot-run-a-switch")]
    [InlineData("held")]
    public void StrandedMachines_AMachineThatCannotBeSentASwitchNow_IsNotStranded(string why)
    {
        var machine = Machine("machine-a", A);
        machine = why switch
        {
            "stale" => machine with { LastReportReceivedAtUtc = Now.AddHours(-1) },
            "open-switch" => machine with { HasOpenSwitch = true },
            "in-progress" => machine with { SwitchInProgressId = "switch-1" },
            "refused" => machine with { ReportsRefused = true },
            "cannot-run-a-switch" => machine with { CannotSwitchReason = "not-known" },
            "held" => machine with { Hold = new LicenseHold(Now.AddMinutes(10), A, B, "switch-failed") },
            _ => throw new ArgumentOutOfRangeException(nameof(why)),
        };
        var snapshot = Snapshot([Reading(A, 30, 100, active: true), Reading(B, 5, 2)], [machine]);

        Assert.Empty(LicenseDecisionEngine.StrandedMachines(snapshot));
    }

    // A sign-in that is refused, or that expires before a switch could finish, strands the machine with that reason.
    [Theory]
    [InlineData(true, LicenseNotReadyReasons.SignInRefused)]
    [InlineData(false, LicenseNotReadyReasons.SignInExpiresTooSoon)]
    public void StrandedMachines_ASignInThatCannotBeUsed_StrandsTheMachineWithItsReason(bool refused, string reason)
    {
        var spare = refused
            ? new LicenseSpare(B, "unauthorized")
            : new LicenseSpare(B, null, Now.AddMinutes(1));
        var machine = Machine("machine-a", A) with { Spares = [spare] };
        var snapshot = Snapshot([Reading(A, 30, 100, active: true), Reading(B, 5, 2)], [machine]);

        Assert.Equal(reason, Assert.Single(LicenseDecisionEngine.StrandedMachines(snapshot)).Readiness.NotReadyReason);
    }
}
