using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// The license switching decision (MISSION.md section 7, "the check"): each named case of the rule, plus
/// every machine on the tired account, a machine missing a spare, and the staleness rules of contract 5.2.
/// </summary>
public sealed class LicenseDecisionEngineTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FiveHourReset = Now.AddHours(2);
    private static readonly DateTime WeekReset = Now.AddDays(3);

    private const string A = "account-a@example.com";
    private const string B = "account-b@example.com";
    private const string C = "account-c@example.com";

    private static LicenseAccountReading Reading(string email, double fiveHour, double week,
        DateTime? readAt = null, bool active = false) =>
        new(email, new LicenseWindowReading(fiveHour, FiveHourReset), new LicenseWindowReading(week, WeekReset),
            readAt ?? Now.AddMinutes(-1), active);

    private static LicenseMachineState Machine(string name, string? active, params string[] spares) =>
        new(name, "owner", active, spares.Select(s => new LicenseSpare(s, null)).ToList(), Now.AddMinutes(-1), false, null);

    private static LicenseSnapshot Snapshot(IEnumerable<LicenseAccountReading> accounts, IEnumerable<LicenseMachineState> machines,
        IEnumerable<LicenseBar>? bars = null, IEnumerable<LicenseExhaustion>? exhaustions = null, DateTime? now = null) =>
        new(now ?? Now, accounts.ToList(), machines.ToList(), (bars ?? []).ToList(), (exhaustions ?? []).ToList());

    // ----- the named cases of the check -----

    [Fact]
    public void Decide_UnderThreshold_DoesNothing()
    {
        var snapshot = Snapshot(
            [Reading(A, 96.9, 96.9, active: true), Reading(B, 10, 10)],
            [Machine("windows", A, B)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Empty(result.Switches);
        Assert.Empty(result.Emails);
        Assert.Empty(result.NewBars);
        Assert.Empty(result.OpenedExhaustions);
    }

    [Fact]
    public void Decide_FiveHourWindowAtThreshold_Switches()
    {
        var snapshot = Snapshot(
            [Reading(A, 97.0, 40, active: true), Reading(B, 10, 10)],
            [Machine("windows", A, B)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        var decision = Assert.Single(result.Switches);
        Assert.Equal(("windows", A, B, LicenseDecisionReasons.Threshold),
            (decision.MachineName, decision.FromAccountEmail, decision.TargetAccountEmail, decision.Reason));
        var bar = Assert.Single(result.NewBars);
        Assert.Equal((A, LicenseWindowNames.FiveHour, FiveHourReset), (bar.AccountEmail, bar.Window, bar.BarredUntilUtc));
        Assert.Empty(result.Emails);
    }

    [Fact]
    public void Decide_WeekAtThreshold_Switches()
    {
        var snapshot = Snapshot(
            [Reading(A, 20, 98.5, active: true), Reading(B, 10, 10)],
            [Machine("windows", A, B)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(B, Assert.Single(result.Switches).TargetAccountEmail);
        var bar = Assert.Single(result.NewBars);
        Assert.Equal((LicenseWindowNames.SevenDay, WeekReset), (bar.Window, bar.BarredUntilUtc));
    }

    [Fact]
    public void Decide_BothWindowsAtThreshold_BarsBoth()
    {
        var snapshot = Snapshot(
            [Reading(A, 99, 99, active: true), Reading(B, 10, 10)],
            [Machine("windows", A, B)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Single(result.Switches);
        Assert.Equal(new[] { LicenseWindowNames.FiveHour, LicenseWindowNames.SevenDay },
            result.NewBars.Select(b => b.Window).OrderBy(w => w).ToArray());
    }

    [Theory]
    // lowest weekly use wins, whatever the 5-hour use
    [InlineData(50, 30, 5, 60, B)]
    // equal weekly use: lowest 5-hour use wins
    [InlineData(20, 40, 10, 40, C)]
    // equal on both: the account email in ordinal order (account-b before account-c)
    [InlineData(10, 40, 10, 40, B)]
    public void Decide_TargetChoiceOrder_LowestWeekThenFiveHourThenEmail(
        double bFive, double bWeek, double cFive, double cWeek, string expected)
    {
        // C is listed before B so the input order cannot be what decides the tie.
        var snapshot = Snapshot(
            [Reading(A, 97, 50, active: true), Reading(C, cFive, cWeek), Reading(B, bFive, bWeek)],
            [Machine("windows", A, C, B)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(expected, Assert.Single(result.Switches).TargetAccountEmail);
    }

    [Fact]
    public void Decide_NoTarget_SendsOneEmailAndSwitchesNothing()
    {
        var snapshot = Snapshot(
            [Reading(A, 97.5, 50, active: true), Reading(B, 98, 10)],
            [Machine("windows", A, B)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Empty(result.Switches);
        Assert.Empty(result.NewBars);
        var email = Assert.Single(result.Emails);
        Assert.Equal(A, email.AccountEmail);
        Assert.Contains("no Claude account has room", email.Subject);
        Assert.Contains($"{B} was not chosen: it is at 98% of its 5-hour window", email.BodyText);
        var exhaustion = Assert.Single(result.OpenedExhaustions);
        Assert.Equal((A, Now, FiveHourReset), (exhaustion.AccountEmail, exhaustion.StartedAtUtc, exhaustion.EndsAtUtc));
    }

    [Fact]
    public void Decide_NoTargetOnTheSecondTick_SendsNoSecondEmail()
    {
        var accounts = new[] { Reading(A, 97.5, 50, active: true), Reading(B, 98, 10) };
        var machines = new[] { Machine("windows", A, B) };
        var first = LicenseDecisionEngine.Decide(Snapshot(accounts, machines));

        var second = LicenseDecisionEngine.Decide(Snapshot(accounts, machines,
            exhaustions: first.OpenedExhaustions, now: Now.AddMinutes(1)));

        Assert.Single(first.Emails);
        Assert.Empty(second.Emails);
        Assert.Empty(second.OpenedExhaustions);
        Assert.Empty(second.ClosedExhaustions);
        Assert.Empty(second.Switches);
    }

    [Fact]
    public void Decide_ExhaustionAfterItsWindowResets_ClosesAndANewEventEmailsAgain()
    {
        var open = new LicenseExhaustion(A, Now.AddHours(-3), Now.AddMinutes(-1));
        var snapshot = Snapshot(
            [Reading(A, 97.5, 50, active: true), Reading(B, 98, 10)],
            [Machine("windows", A, B)], exhaustions: [open]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(new[] { A }, result.ClosedExhaustions);
        Assert.Single(result.OpenedExhaustions);
        Assert.Single(result.Emails);
    }

    [Fact]
    public void Decide_ExhaustionWhenNoMachineIsOnTheAccountAnyMore_Closes()
    {
        var open = new LicenseExhaustion(A, Now.AddMinutes(-30), FiveHourReset);
        var snapshot = Snapshot(
            [Reading(A, 97.5, 50), Reading(B, 10, 10, active: true)],
            [Machine("windows", B, A)], exhaustions: [open]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(new[] { A }, result.ClosedExhaustions);
        Assert.Empty(result.Emails);
    }

    [Fact]
    public void Decide_AccountThatTriggered_IsNotChosenBeforeItsResetAndIsAfter()
    {
        // B triggered earlier on its 5-hour window and is barred until that window resets. B now reads 5% on
        // both windows (better than C), so only the bar keeps it from being chosen.
        var bar = new LicenseBar(B, LicenseWindowNames.FiveHour, FiveHourReset);
        var accounts = new[] { Reading(A, 97, 50, active: true), Reading(B, 5, 5), Reading(C, 30, 30) };
        var machines = new[] { Machine("windows", A, B, C) };

        var before = LicenseDecisionEngine.Decide(Snapshot(accounts, machines, bars: [bar]));
        var after = LicenseDecisionEngine.Decide(Snapshot(
            accounts.Select(r => r with { ReadAtUtc = FiveHourReset }),
            machines.Select(m => m with { LastReportReceivedAtUtc = FiveHourReset }),
            bars: [bar], now: FiveHourReset.AddSeconds(1)));

        Assert.Equal(C, Assert.Single(before.Switches).TargetAccountEmail);
        Assert.Equal(B, Assert.Single(after.Switches).TargetAccountEmail);
    }

    [Fact]
    public void Decide_BarAlreadyRecordedForTheWindow_IsNotRecordedAgain()
    {
        var snapshot = Snapshot(
            [Reading(A, 97, 50, active: true), Reading(B, 10, 10)],
            [Machine("windows", A, B), Machine("mac", A, B) with { HasOpenSwitch = true }],
            bars: [new LicenseBar(A, LicenseWindowNames.FiveHour, FiveHourReset)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Single(result.Switches);
        Assert.Empty(result.NewBars);
    }

    [Fact]
    public void Decide_WindowWithNoResetTime_IsBarredForTheWholeWindowLength()
    {
        var tired = new LicenseAccountReading(A, new LicenseWindowReading(97, null), new LicenseWindowReading(50, WeekReset), Now.AddMinutes(-1), true);
        var snapshot = Snapshot([tired, Reading(B, 10, 10)], [Machine("windows", A, B)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(Now + LicenseDecisionEngine.FiveHourWindowLength, Assert.Single(result.NewBars).BarredUntilUtc);
    }

    // ----- every machine, and the spare each must hold -----

    [Fact]
    public void Decide_EveryMachineOnTheTiredAccount_IsSwitched()
    {
        var snapshot = Snapshot(
            [Reading(A, 97, 50, active: true), Reading(B, 10, 10)],
            [Machine("windows", A, B), Machine("mac", A, B), Machine("linux", B, A)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(new[] { "mac", "windows" }, result.Switches.Select(s => s.MachineName).OrderBy(n => n).ToArray());
        Assert.All(result.Switches, s => Assert.Equal((A, B), (s.FromAccountEmail, s.TargetAccountEmail)));
    }

    [Fact]
    public void Decide_MachineMissingTheSpare_BlocksThatTarget()
    {
        // B is the better account, but the Mac holds no spare for it; C is held by both.
        var snapshot = Snapshot(
            [Reading(A, 97, 50, active: true), Reading(B, 5, 5), Reading(C, 30, 30)],
            [Machine("windows", A, B, C), Machine("mac", A, C)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(2, result.Switches.Count);
        Assert.All(result.Switches, s => Assert.Equal(C, s.TargetAccountEmail));
    }

    [Fact]
    public void Decide_NoTargetHeldByEveryMachine_EmailsAndSwitchesNothing()
    {
        var snapshot = Snapshot(
            [Reading(A, 97, 50, active: true), Reading(B, 5, 5), Reading(C, 30, 30)],
            [Machine("windows", A, B), Machine("mac", A, C)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Empty(result.Switches);
        var email = Assert.Single(result.Emails);
        Assert.Contains($"{B} was not chosen: mac holds no usable spare for it", email.BodyText);
        Assert.Contains($"{C} was not chosen: windows holds no usable spare for it", email.BodyText);
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("refresh-failed")]
    public void Decide_SpareWhoseSignInIsRefused_IsNotChosen(string readError)
    {
        var machine = Machine("windows", A) with
        {
            Spares = [new LicenseSpare(B, readError), new LicenseSpare(C, "network")],
        };
        var snapshot = Snapshot([Reading(A, 97, 50, active: true), Reading(B, 5, 5), Reading(C, 30, 30)], [machine]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(C, Assert.Single(result.Switches).TargetAccountEmail);
    }

    // ----- staleness (contract 5.2) -----

    [Fact]
    public void Decide_StaleReadingOfTheAccountInUse_DoesNotTrigger()
    {
        var snapshot = Snapshot(
            [Reading(A, 99, 99, readAt: Now.AddMinutes(-7), active: true), Reading(B, 10, 10)],
            [Machine("windows", A, B)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Empty(result.Switches);
        Assert.Empty(result.Emails);
    }

    [Fact]
    public void Decide_StaleCandidate_IsNeverChosen()
    {
        // B is the better account but its spare reading is 31 minutes old; C's is 29 minutes old and still fresh.
        var snapshot = Snapshot(
            [Reading(A, 97, 50, active: true), Reading(B, 5, 5, readAt: Now.AddMinutes(-31)), Reading(C, 30, 30, readAt: Now.AddMinutes(-29))],
            [Machine("windows", A, B, C)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(C, Assert.Single(result.Switches).TargetAccountEmail);
    }

    [Fact]
    public void Decide_StaleMachine_IsSkippedAndTheOthersStillGo()
    {
        // The stale Mac holds no spare for B; it must neither be switched nor veto B for the Windows machine.
        var staleMac = Machine("mac", A) with { LastReportReceivedAtUtc = Now.AddMinutes(-7) };
        var snapshot = Snapshot(
            [Reading(A, 97, 50, active: true), Reading(B, 10, 10)],
            [Machine("windows", A, B), staleMac]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal("windows", Assert.Single(result.Switches).MachineName);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(("mac", "its last report is too old"), (skipped.MachineName, skipped.Why));
    }

    [Fact]
    public void Decide_MachineWithAnOpenOrReportedSwitch_IsSkippedAndTheOthersStillGo()
    {
        var snapshot = Snapshot(
            [Reading(A, 97, 50, active: true), Reading(B, 10, 10)],
            [
                Machine("windows", A, B),
                Machine("mac", A, B) with { HasOpenSwitch = true },
                Machine("linux", A, B) with { SwitchInProgressId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40" },
            ]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal("windows", Assert.Single(result.Switches).MachineName);
        Assert.Equal(new[] { "linux", "mac" }, result.Skipped.Select(s => s.MachineName).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Decide_EveryMachineOnTheTiredAccountSkipped_NeitherSwitchesNorEmails()
    {
        var snapshot = Snapshot(
            [Reading(A, 97, 50, active: true), Reading(B, 98, 10)],
            [Machine("windows", A, B) with { HasOpenSwitch = true }]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Empty(result.Switches);
        Assert.Empty(result.Emails);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void IsReadingStale_ActiveAndSpareLimits_AndNeverRead()
    {
        Assert.False(LicenseDecisionEngine.IsReadingStale(Reading(A, 1, 1, Now.AddMinutes(-6), active: true), Now));
        Assert.True(LicenseDecisionEngine.IsReadingStale(Reading(A, 1, 1, Now.AddMinutes(-6).AddSeconds(-1), active: true), Now));
        Assert.False(LicenseDecisionEngine.IsReadingStale(Reading(A, 1, 1, Now.AddMinutes(-30)), Now));
        Assert.True(LicenseDecisionEngine.IsReadingStale(Reading(A, 1, 1, Now.AddMinutes(-30).AddSeconds(-1)), Now));
        Assert.True(LicenseDecisionEngine.IsReadingStale(Reading(A, 1, 1) with { ReadAtUtc = null }, Now));
    }

    [Fact]
    public void Decide_CandidateWithAnUnknownWindow_IsNotChosen()
    {
        var unknownWeek = new LicenseAccountReading(B, new LicenseWindowReading(5, FiveHourReset), null, Now.AddMinutes(-1), false);
        var snapshot = Snapshot([Reading(A, 97, 50, active: true), unknownWeek, Reading(C, 30, 30)], [Machine("windows", A, B, C)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(C, Assert.Single(result.Switches).TargetAccountEmail);
    }

    [Fact]
    public void Decide_TwoReadingsForOneAccount_Throws()
    {
        var snapshot = Snapshot([Reading(A, 1, 1), Reading("Account-A@example.com", 2, 2)], [Machine("windows", A)]);

        Assert.Throws<ArgumentException>(() => LicenseDecisionEngine.Decide(snapshot));
    }

    // ----- Switch now -----

    [Fact]
    public void DecideManual_NoTargetNamed_SwitchesEveryMachineToTheAutomaticChoice()
    {
        var snapshot = Snapshot(
            [Reading(A, 40, 40, active: true), Reading(B, 5, 30), Reading(C, 5, 10)],
            [Machine("windows", A, B, C), Machine("mac", A, B, C)]);

        var verdict = LicenseDecisionEngine.DecideManual(snapshot, A, null);

        Assert.True(verdict.Accepted);
        Assert.Equal(2, verdict.Switches.Count);
        Assert.All(verdict.Switches, s => Assert.Equal((C, LicenseDecisionReasons.Manual), (s.TargetAccountEmail, s.Reason)));
    }

    [Fact]
    public void DecideManual_TargetNamed_SwitchesToItEvenWhenBarred()
    {
        var snapshot = Snapshot(
            [Reading(A, 40, 40, active: true), Reading(B, 5, 30), Reading(C, 5, 10)],
            [Machine("windows", A, B, C)],
            bars: [new LicenseBar(B, LicenseWindowNames.FiveHour, FiveHourReset)]);

        var verdict = LicenseDecisionEngine.DecideManual(snapshot, A, "Account-B@example.com");

        Assert.Equal(B, Assert.Single(verdict.Switches).TargetAccountEmail);
    }

    [Theory]
    [InlineData(C, LicenseManualRefusals.TargetNotHeld)]
    [InlineData(A, LicenseManualRefusals.TargetIsSource)]
    public void DecideManual_TargetNotHeldOrIsTheSource_IsRefused(string target, string code)
    {
        var snapshot = Snapshot(
            [Reading(A, 40, 40, active: true), Reading(B, 5, 30), Reading(C, 5, 10)],
            [Machine("windows", A, B, C), Machine("mac", A, B)]);

        var verdict = LicenseDecisionEngine.DecideManual(snapshot, A, target);

        Assert.False(verdict.Accepted);
        Assert.Equal(code, verdict.RefusalCode);
        Assert.Empty(verdict.Switches);
    }

    [Fact]
    public void DecideManual_TargetStale_IsRefused()
    {
        var snapshot = Snapshot(
            [Reading(A, 40, 40, active: true), Reading(B, 5, 5, readAt: Now.AddMinutes(-31))],
            [Machine("windows", A, B)]);

        var verdict = LicenseDecisionEngine.DecideManual(snapshot, A, B);

        Assert.Equal(LicenseManualRefusals.TargetStale, verdict.RefusalCode);
    }

    [Fact]
    public void DecideManual_TargetAtThreshold_IsRefused()
    {
        var snapshot = Snapshot(
            [Reading(A, 40, 40, active: true), Reading(B, 5, 97)],
            [Machine("windows", A, B)]);

        var verdict = LicenseDecisionEngine.DecideManual(snapshot, A, B);

        Assert.Equal(LicenseManualRefusals.TargetFull, verdict.RefusalCode);
    }

    [Fact]
    public void DecideManual_NoMachineCanBeSwitched_IsRefused()
    {
        var snapshot = Snapshot(
            [Reading(A, 40, 40, active: true), Reading(B, 5, 5)],
            [Machine("windows", A, B) with { HasOpenSwitch = true }]);

        var verdict = LicenseDecisionEngine.DecideManual(snapshot, A, B);

        Assert.Equal(LicenseManualRefusals.NoMachineToSwitch, verdict.RefusalCode);
        Assert.Single(verdict.Skipped);
    }

    [Fact]
    public void DecideManual_NoTargetQualifies_IsRefused()
    {
        var snapshot = Snapshot(
            [Reading(A, 40, 40, active: true), Reading(B, 98, 5)],
            [Machine("windows", A, B)]);

        var verdict = LicenseDecisionEngine.DecideManual(snapshot, A, null);

        Assert.Equal(LicenseManualRefusals.NoTarget, verdict.RefusalCode);
    }
}
