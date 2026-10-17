using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE PER-MACHINE SWITCH (License Switching phase 6, track A, the switch rule). On 16 October 2026 one account was
/// at 97% and another at 0%; three machines held a sign-in for the empty account and one did not, and because a
/// target had to be held by EVERY machine nothing switched and the owner was told no account had room - which was
/// false. The owner's ruling: "Switch every ready machine at once; the one left behind keeps trying and switches the
/// moment it is ready." These tests reproduce that day with made-up names and pin each sentence the owner reads.
/// </summary>
public sealed class LicensePerMachineSwitchTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FiveHourReset = Now.AddHours(2);
    private static readonly DateTime WeekReset = Now.AddDays(3);

    private const string Tired = "account-a@example.com";
    private const string Empty = "account-b@example.com";
    private const string Third = "account-c@example.com";

    private static LicenseAccountReading Reading(string email, double fiveHour, double week, bool active = false) =>
        new(email, new LicenseWindowReading(fiveHour, FiveHourReset), new LicenseWindowReading(week, WeekReset), Now.AddMinutes(-1), active);

    private static LicenseMachineState Machine(string name, string? active, params string[] spares) =>
        new(name, "owner", active, spares.Select(s => new LicenseSpare(s, null)).ToList(), Now.AddMinutes(-1), false, null);

    private static LicenseSnapshot Snapshot(IEnumerable<LicenseAccountReading> accounts, IEnumerable<LicenseMachineState> machines,
        IEnumerable<LicenseBar>? bars = null, IEnumerable<LicenseExhaustion>? exhaustions = null) =>
        new(Now, accounts.ToList(), machines.ToList(), (bars ?? []).ToList(), (exhaustions ?? []).ToList());

    // 16 October: the account in use at 97% of its week, the other at 0%, three machines holding a sign-in for it.
    private static LicenseSnapshot SixteenOctober(double tiredWeek = 97) => Snapshot(
        [Reading(Tired, 20, tiredWeek, active: true), Reading(Empty, 0, 0)],
        [Machine("machine-a", Tired, Empty), Machine("machine-b", Tired, Empty), Machine("machine-c", Tired, Empty), Machine("machine-d", Tired)]);

    // ----- one definition of ready -----

    [Fact]
    public void Readiness_HoldsAUsableSignIn_IsReady()
    {
        var readiness = LicenseDecisionEngine.Readiness(Machine("machine-a", Tired, Empty), "Account-B@Example.com", Now);

        Assert.True(readiness.IsReady);
        Assert.Equal((Empty, (string?)null), (readiness.TargetAccountEmail, readiness.NotReadyReason));
        Assert.Null(LicenseDecisionEngine.NotReadyWhy(readiness));
    }

    [Fact]
    public void Readiness_HoldsNoSignIn_IsNotReady_AndSaysSo()
    {
        var readiness = LicenseDecisionEngine.Readiness(Machine("machine-d", Tired), Empty, Now);

        Assert.False(readiness.IsReady);
        Assert.Equal(LicenseNotReadyReasons.NoSignIn, readiness.NotReadyReason);
        Assert.Equal($"holds no sign-in for {Empty}", LicenseDecisionEngine.NotReadyWhy(readiness));
    }

    [Theory]
    [InlineData(LicenseReadErrors.Unauthorized)]
    [InlineData(LicenseReadErrors.RefreshFailed)]
    public void Readiness_SignInRefused_IsNotReady_AndSaysSo(string readError)
    {
        var machine = Machine("machine-a", Tired) with { Spares = [new LicenseSpare(Empty, readError)] };

        var readiness = LicenseDecisionEngine.Readiness(machine, Empty, Now);

        Assert.Equal(LicenseNotReadyReasons.SignInRefused, readiness.NotReadyReason);
        Assert.Equal($"holds a sign-in for {Empty} that was refused", LicenseDecisionEngine.NotReadyWhy(readiness));
    }

    [Fact]
    public void Readiness_SignInExpiringBeforeTheSwitchCouldFinish_IsNotReady_AndNamesTheExpiry()
    {
        // The command's not-after time is now + 10 minutes: a sign-in expiring exactly then is too soon, a second later is not.
        var atTheLimit = Now + LicenseContract.SwitchNotAfter;
        var expiring = Machine("machine-a", Tired) with { Spares = [new LicenseSpare(Empty, null, atTheLimit)] };
        var justAfter = Machine("machine-a", Tired) with { Spares = [new LicenseSpare(Empty, null, atTheLimit.AddSeconds(1))] };

        var readiness = LicenseDecisionEngine.Readiness(expiring, Empty, Now);

        Assert.Equal((LicenseNotReadyReasons.SignInExpiresTooSoon, (DateTime?)atTheLimit), (readiness.NotReadyReason, readiness.SignInExpiresAtUtc));
        Assert.Equal($"holds a sign-in for {Empty} that expires at 2026-10-16 03:10 UTC, before a switch could finish",
            LicenseDecisionEngine.NotReadyWhy(readiness));
        Assert.True(LicenseDecisionEngine.Readiness(justAfter, Empty, Now).IsReady);
    }

    [Fact]
    public void Readiness_SignInWithNoKnownExpiry_IsReady()
    {
        // Contract 5.7: an unknown expiry does not exclude a target on its own; the Director refuses an expired one itself.
        Assert.True(LicenseDecisionEngine.Readiness(Machine("machine-a", Tired, Empty), Empty, Now).IsReady);
    }

    // ----- the account it would switch to next, on the account's own merits -----

    [Fact]
    public void NextTarget_IsChosenOnTheAccountsMerits_EvenWhenOneMachineHoldsNoSignInForIt()
    {
        Assert.Equal(Empty, LicenseDecisionEngine.NextTarget(SixteenOctober(), Tired));
        Assert.Equal(new[] { Empty }, LicenseDecisionEngine.TargetsWithRoom(SixteenOctober(), Tired));
    }

    [Fact]
    public void NextTarget_IsNull_OnlyWhenNoAccountHasRoom()
    {
        var full = Snapshot([Reading(Tired, 20, 97, active: true), Reading(Empty, 0, 98)], [Machine("machine-a", Tired, Empty)]);

        Assert.Null(LicenseDecisionEngine.NextTarget(full, Tired));
        Assert.Empty(LicenseDecisionEngine.TargetsWithRoom(full, Tired));
    }

    // ----- at 97%: every ready machine, at once -----

    [Fact]
    public void Decide_SixteenOctober_TheThreeReadyMachinesSwitch_TheFourthIsLeftBehind_AndNobodyIsToldThereIsNoRoom()
    {
        var result = LicenseDecisionEngine.Decide(SixteenOctober());

        Assert.Equal(new[] { "machine-a", "machine-b", "machine-c" }, result.Switches.Select(s => s.MachineName).ToArray());
        Assert.All(result.Switches, s => Assert.Equal((Tired, Empty, LicenseSwitchReasons.Threshold), (s.FromAccountEmail, s.TargetAccountEmail, s.Reason)));
        Assert.Empty(result.Emails);
        Assert.Empty(result.OpenedExhaustions);
        var left = Assert.Single(result.Skipped);
        Assert.Equal(("machine-d", $"it holds no sign-in for {Empty} and switches as soon as it does"), (left.MachineName, left.Why));
        // The switch log's sentence, on each row: who moved, where, and who stayed and why.
        Assert.All(result.Switches, s => Assert.Equal(
            $"{Tired} reached 97% of its weekly limit; machine-a, machine-b and machine-c switched to {Empty}, the account with the lowest "
            + "weekly use that has room; machine-d holds no sign-in for it and switches as soon as it does.", s.Explanation));
        var bar = Assert.Single(result.NewBars);
        Assert.Equal((Tired, LicenseWindowNames.SevenDay), (bar.AccountEmail, bar.Window));
    }

    [Fact]
    public void Decide_ATickLater_TheMachineLeftBehindNowHoldingTheSignIn_IsSwitched_ByTheSameRule()
    {
        // The three that moved are on the empty account now; the fourth has since been given a sign-in for it.
        var first = LicenseDecisionEngine.Decide(SixteenOctober());
        var later = Snapshot(
            [Reading(Tired, 20, 97, active: true), Reading(Empty, 3, 1)],
            [Machine("machine-a", Empty, Tired), Machine("machine-b", Empty, Tired), Machine("machine-c", Empty, Tired), Machine("machine-d", Tired, Empty)],
            bars: first.NewBars);

        var result = LicenseDecisionEngine.Decide(later);

        var decision = Assert.Single(result.Switches);
        Assert.Equal(("machine-d", Tired, Empty), (decision.MachineName, decision.FromAccountEmail, decision.TargetAccountEmail));
        Assert.Equal($"{Tired} reached 97% of its weekly limit; machine-d switched to {Empty}, the account with the lowest weekly use that has room.",
            decision.Explanation);
        Assert.Empty(result.NewBars);   // the bar from the first tick still stands
        Assert.Empty(result.Emails);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Decide_UnderTheThreshold_NothingSwitches()
    {
        var result = LicenseDecisionEngine.Decide(SixteenOctober(tiredWeek: 96.9));

        Assert.Empty(result.Switches);
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Emails);
    }

    [Fact]
    public void Decide_AnAccountHasRoomAndNoMachineIsReadyForItYet_SwitchesNothing_AndOpensNoNoRoomEvent()
    {
        var snapshot = Snapshot([Reading(Tired, 20, 97, active: true), Reading(Empty, 0, 0)], [Machine("machine-a", Tired), Machine("machine-b", Tired)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Empty(result.Switches);
        Assert.Empty(result.Emails);
        Assert.Empty(result.OpenedExhaustions);
        Assert.Empty(result.NewBars);
        Assert.Equal(new[] { "machine-a", "machine-b" }, result.Skipped.Select(s => s.MachineName).ToArray());
        Assert.Null(LicenseDecisionEngine.NoRoomEmail(snapshot, Tired));
    }

    [Fact]
    public void Decide_TheTieCase_EachMachineGoesToTheBestAccountItIsReadyFor_AndNoneWaits()
    {
        // The best account (lowest weekly use) is held by one machine only; the second best by all three. The owner's
        // rule is "switch every ready machine": machine-a takes the best, the other two take the second best at once.
        var snapshot = Snapshot(
            [Reading(Tired, 97, 50, active: true), Reading(Empty, 5, 5), Reading(Third, 10, 20)],
            [Machine("machine-a", Tired, Empty, Third), Machine("machine-b", Tired, Third), Machine("machine-c", Tired, Third)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(new[] { ("machine-a", Empty), ("machine-b", Third), ("machine-c", Third) },
            result.Switches.Select(s => (s.MachineName, s.TargetAccountEmail)).ToArray());
        Assert.Equal($"{Tired} reached 97% of its 5-hour window; machine-a switched to {Empty}, the account with the lowest weekly use that has room; "
                     + $"machine-b and machine-c switched to {Third}, the best account with room they hold a sign-in for.", result.Switches[0].Explanation);
        Assert.Empty(result.Emails);
    }

    // ----- the no-room event only when it is true -----

    [Fact]
    public void Decide_TrulyNoRoom_OpensTheEventWithExactlyOneEmail_InTheExistingWords()
    {
        // Every other account is tired or barred.
        var snapshot = Snapshot(
            [Reading(Tired, 20, 97, active: true), Reading(Empty, 0, 98), Reading(Third, 0, 0)],
            [Machine("machine-a", Tired, Empty, Third), Machine("machine-b", Tired, Empty)],
            bars: [new LicenseBar(Third, LicenseWindowNames.FiveHour, FiveHourReset)]);

        var first = LicenseDecisionEngine.Decide(snapshot);
        var second = LicenseDecisionEngine.Decide(snapshot with { OpenExhaustions = first.OpenedExhaustions });

        Assert.Empty(first.Switches);
        var email = Assert.Single(first.Emails);
        Assert.Equal($"DevThrottle: no Claude account has room - {Tired} is at 97% of its weekly limit", email.Subject);
        Assert.StartsWith($"{Tired} has reached 97% of its weekly limit, and no other account can take its machines, so nothing was switched.", email.BodyText);
        Assert.Contains($"{Empty} was not chosen: it is at 98% of its weekly limit.", email.BodyText);
        Assert.Contains($"{Third} was not chosen: it triggered a switch and is barred until its 5-hour window resets at 2026-10-16 05:00 UTC.", email.BodyText);
        Assert.Single(first.OpenedExhaustions);
        Assert.Empty(second.Emails);
        Assert.Empty(second.OpenedExhaustions);
    }

    // ----- Switch now: the ready machines move, each one left says why -----

    [Fact]
    public void PreviewManual_SixteenOctober_MovesTheReadyMachines_AndSaysWhichOneStaysAndWhy()
    {
        var verdict = LicenseDecisionEngine.PreviewManual(SixteenOctober(), Tired, Empty);

        Assert.True(verdict.Accepted);
        Assert.Equal(new[] { "machine-a", "machine-b", "machine-c" }, verdict.Switches.Select(s => s.MachineName).ToArray());
        var left = Assert.Single(verdict.Skipped);
        Assert.Equal(("machine-d", $"it holds no sign-in for {Empty}"), (left.MachineName, left.Why));
        Assert.Equal($"Switch now: from {Tired} to {Empty}; machine-d holds no sign-in for {Empty}, so it stays on {Tired}.", verdict.Switches[0].Explanation);
        // With no target named, the moves are the automatic ones.
        Assert.Equal(verdict.Switches.Select(s => (s.MachineName, s.TargetAccountEmail)),
            LicenseDecisionEngine.PreviewManual(SixteenOctober(), Tired, null).Switches.Select(s => (s.MachineName, s.TargetAccountEmail)));
    }

    [Fact]
    public void PreviewManual_AnAccountHasRoomAndNoMachineIsReady_RefusesWithTheRealReason()
    {
        var snapshot = Snapshot([Reading(Tired, 20, 50, active: true), Reading(Empty, 0, 0)], [Machine("machine-a", Tired)]);

        var automatic = LicenseDecisionEngine.PreviewManual(snapshot, Tired, null);
        var named = LicenseDecisionEngine.PreviewManual(snapshot, Tired, Empty);

        Assert.Equal((LicenseManualRefusals.TargetNotHeld, $"{Empty} has room; machine-a holds no sign-in for it."),
            (automatic.RefusalCode, automatic.RefusalMessage));
        Assert.Equal((automatic.RefusalCode, automatic.RefusalMessage), (named.RefusalCode, named.RefusalMessage));
        Assert.Empty(automatic.Skipped);   // the refusal names the machine already
    }

    [Fact]
    public void PreviewManual_TrulyNoRoom_RefusesWithEachAccountsReason()
    {
        var snapshot = Snapshot([Reading(Tired, 20, 50, active: true), Reading(Empty, 0, 98)], [Machine("machine-a", Tired, Empty)]);

        var verdict = LicenseDecisionEngine.PreviewManual(snapshot, Tired, null);

        Assert.Equal((LicenseManualRefusals.NoTarget, $"No other account has room for the machines on {Tired}: for {Empty}, it is at 98% of its weekly limit."),
            (verdict.RefusalCode, verdict.RefusalMessage));
    }
}
