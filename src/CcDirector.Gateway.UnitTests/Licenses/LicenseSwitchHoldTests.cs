using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// A SWITCH THAT KEEPS FAILING (License Switching; the review of phases 2 and 4 as a whole, finding 1), part by part:
/// the store that records the hold on the failed row, the picture that carries it to the engine, the engine that
/// leaves a held machine out of the AUTOMATIC decision and treats "every machine held" as the no-room case, and the
/// words the Licenses page is given. The minute check and Switch now over all of them are in
/// <see cref="LicenseSwitchHoldServiceTests"/>.
///
/// The store tests run over the real EF store on a throwaway SQLite file; the engine tests are pure. No test waits:
/// every time is a value.
/// </summary>
public sealed class LicenseSwitchHoldTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-a");
    private static readonly TenantId OtherTenant = new("acct-b");
    private const string Restored = LicenseSwitchReasonCodes.ExchangeFailedRestored;

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private long _sequence;

    public LicenseSwitchHoldTests()
    {
        _store = new LicenseStore(_harness.Open());
    }

    public void Dispose() => _harness.Dispose();

    // ----- helpers -----

    private Guid Machine(string name = "m1", TenantId? tenant = null, DateTime? at = null)
        => _store.ApplyReport(tenant ?? Tenant, "dir-" + name, ++_sequence,
            Report(name, at ?? T0, Holding(A, true, 98, 20, at ?? T0), Holding(B, false, 10, 30, at ?? T0), Holding(C, false, 10, 40, at ?? T0)),
            at ?? T0, TimeSpan.Zero).MachineId;

    /// <summary>One switch of the machine that its Director answered: written at <paramref name="at"/> and ended a
    /// second later with <paramref name="outcome"/>. Returns its switch identifier.</summary>
    private string Answered(Guid machine, DateTime at, string outcome, string? reasonCode = null, string from = A, string to = B,
        TenantId? tenant = null)
    {
        var row = _store.CreatePendingSwitch(tenant ?? Tenant, machine, from, to, LicenseSwitchReasons.Threshold, at)
            ?? throw new InvalidOperationException("The machine already has an open switch.");
        Assert.True(_store.TrySettle(tenant ?? Tenant, new LicenseSwitchResult
        {
            SwitchId = row.SwitchId!,
            Outcome = outcome,
            ReasonCode = reasonCode,
            Message = "What the Director said.",
            ActiveAccountEmailAfter = outcome == LicenseSwitchOutcomes.Failed ? from : to,
            StartedAtUtc = at,
            FinishedAtUtc = at,
        }, at.AddSeconds(1)));
        return row.SwitchId!;
    }

    private string Failed(Guid machine, DateTime at, string reasonCode = Restored, string from = A, string to = B, TenantId? tenant = null)
        => Answered(machine, at, LicenseSwitchOutcomes.Failed, reasonCode, from, to, tenant);

    private DateTime? Hold(string switchId, TenantId? tenant = null) => _store.HoldAfterRepeatedFailure(tenant ?? Tenant, switchId);

    // ----- the wait grows -----

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, null)]     // one failure holds nothing: only a failure that repeats does
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 4)]
    [InlineData(5, 8)]
    [InlineData(6, 16)]
    [InlineData(7, 32)]
    [InlineData(8, 60)]       // never longer than an hour
    [InlineData(9, 60)]
    [InlineData(500, 60)]
    public void FailureHold_GrowsOneMinuteTwoFour_UpToAnHour(int failuresInARow, int? minutes)
    {
        Assert.Equal(minutes is { } m ? TimeSpan.FromMinutes(m) : (TimeSpan?)null, LicenseStore.FailureHold(failuresInARow));
    }

    // ----- the store records the hold on the failed row -----

    [Fact]
    public void HoldAfterRepeatedFailure_TheFirstFailure_HoldsNothing()
    {
        var machine = Machine();

        var held = Hold(Failed(machine, T0));

        Assert.Null(held);
        Assert.Null(Assert.Single(_store.ListLog(Tenant, T0.AddDays(-1))).HeldUntilUtc);
        Assert.Empty(_store.ListHolds(Tenant, T0));
    }

    [Fact]
    public void HoldAfterRepeatedFailure_TheSecondSameFailureInARow_HoldsOneMinuteFromWhenItEnded_OnTheRowItself()
    {
        var machine = Machine();
        Hold(Failed(machine, T0));
        var second = Failed(machine, T0.AddMinutes(1));

        var held = Hold(second);

        // The row ended one second after it was written; the hold is one minute from then.
        Assert.Equal(T0.AddMinutes(2).AddSeconds(1), held);
        Assert.Equal(held, _store.FindSwitch(Tenant, second)!.HeldUntilUtc);
        var listed = Assert.Single(_store.ListHolds(Tenant, T0.AddMinutes(2)));
        Assert.Equal((second, machine, A, B, Restored), (listed.SwitchId, listed.MachineId, listed.FromAccountEmail, listed.ToAccountEmail, listed.ReasonCode));
    }

    [Fact]
    public void HoldAfterRepeatedFailure_EachFurtherSameFailure_HoldsTwiceAsLong_UpToAnHour()
    {
        var machine = Machine();
        var holds = new List<TimeSpan?>();
        for (var failure = 0; failure < 10; failure++)
        {
            var at = T0.AddHours(2 * failure);
            holds.Add(Hold(Failed(machine, at)) - at.AddSeconds(1));
        }

        Assert.Equal(new TimeSpan?[] { null }.Concat(new[] { 1, 2, 4, 8, 16, 32, 60, 60, 60 }.Select(m => (TimeSpan?)TimeSpan.FromMinutes(m))), holds);
    }

    [Theory]
    [InlineData(LicenseSwitchReasonCodes.VerifyFailed, A, B)]   // another reason
    [InlineData(Restored, A, C)]                                // another target
    public void HoldAfterRepeatedFailure_AFailureThatIsNotTheSameAsTheOneBeforeIt_StartsAgainAndHoldsNothing(string reasonCode, string from, string to)
    {
        var machine = Machine();
        Hold(Failed(machine, T0));
        Hold(Failed(machine, T0.AddMinutes(1)));

        var held = Hold(Failed(machine, T0.AddMinutes(5), reasonCode, from, to));

        Assert.Null(held);
        // And the earlier hold is over with it: the machine's latest switch row carries none.
        Assert.Empty(_store.ListHolds(Tenant, T0.AddMinutes(1).AddSeconds(30)));
    }

    [Fact]
    public void HoldAfterRepeatedFailure_ASwitchThatWorkedInBetween_EndsTheRun()
    {
        var machine = Machine();
        Hold(Failed(machine, T0));
        Answered(machine, T0.AddMinutes(1), LicenseSwitchOutcomes.Switched);

        Assert.Null(Hold(Failed(machine, T0.AddMinutes(2))));
    }

    [Fact]
    public void HoldAfterRepeatedFailure_ASwitchThatWasNeverSent_IsNotAFailureOfTheRun_AndIsNotHeldItself()
    {
        var machine = Machine();
        Hold(Failed(machine, T0));
        // The Director was not connected until the switch expired: the Gateway ends the row itself.
        var neverSent = _store.CreatePendingSwitch(Tenant, machine, A, B, LicenseSwitchReasons.Threshold, T0.AddMinutes(1))!;
        Assert.True(_store.TryMark(Tenant, neverSent.SwitchId!, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorOffline, "Never sent.", T0.AddMinutes(11)));

        Assert.Null(Hold(neverSent.SwitchId!));
        Assert.Null(Hold(Failed(machine, T0.AddMinutes(12))));
    }

    [Fact]
    public void HoldAfterRepeatedFailure_TwoSwitchesInARowThatWereNeverSent_HoldNothing()
    {
        var machine = Machine();
        var ids = new List<string>();
        foreach (var minute in new[] { 0, 20 })
        {
            var neverSent = _store.CreatePendingSwitch(Tenant, machine, A, B, LicenseSwitchReasons.Threshold, T0.AddMinutes(minute))!;
            Assert.True(_store.TryMark(Tenant, neverSent.SwitchId!, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorOffline,
                "Never sent.", T0.AddMinutes(minute + 10)));
            ids.Add(neverSent.SwitchId!);
        }

        // The same reason word, the same from and to, twice in a row - and still nothing the machine answered.
        Assert.Null(Hold(ids[0]));
        Assert.Null(Hold(ids[1]));
        Assert.Empty(_store.ListHolds(Tenant, T0.AddMinutes(30)));
    }

    [Fact]
    public void HoldAfterRepeatedFailure_ASwitchThatDidNotFail_IsNeverHeld()
    {
        var machine = Machine();
        Hold(Failed(machine, T0));
        Hold(Failed(machine, T0.AddMinutes(1)));

        Assert.Null(Hold(Answered(machine, T0.AddMinutes(5), LicenseSwitchOutcomes.Switched)));
    }

    [Fact]
    public void HoldAfterRepeatedFailure_TheFailuresOfAnotherMachineOrAnotherAccount_DoNotCount()
    {
        var mine = Machine("m1");
        var other = Machine("m2");
        var foreign = Machine("m1", OtherTenant);
        Hold(Failed(other, T0));
        Hold(Failed(foreign, T0, tenant: OtherTenant), OtherTenant);

        Assert.Null(Hold(Failed(mine, T0.AddMinutes(1))));
        Assert.Empty(_store.ListHolds(OtherTenant, T0));
    }

    // ----- which holds are in force -----

    [Fact]
    public void ListHolds_AHoldWhoseTimeHasCome_IsNotListed()
    {
        var machine = Machine();
        Hold(Failed(machine, T0));
        var until = Hold(Failed(machine, T0.AddMinutes(1)))!.Value;

        Assert.Single(_store.ListHolds(Tenant, until.AddSeconds(-1)));
        Assert.Empty(_store.ListHolds(Tenant, until));
    }

    [Fact]
    public void ListHolds_ALaterSwitchRowOfTheMachine_EndsTheHold_WhateverItsState()
    {
        var machine = Machine();
        Hold(Failed(machine, T0));
        Hold(Failed(machine, T0.AddMinutes(1)));
        Hold(Failed(machine, T0.AddMinutes(2)));   // held two minutes, until 03:16:01
        Assert.Single(_store.ListHolds(Tenant, T0.AddMinutes(3)));

        // A Switch now is written for the machine while it is held: an open row, and a new fact about the machine.
        _store.CreatePendingSwitch(Tenant, machine, A, B, LicenseSwitchReasons.Manual, T0.AddMinutes(3));

        Assert.Empty(_store.ListHolds(Tenant, T0.AddMinutes(3)));
    }

    [Fact]
    public void ListHolds_OneAccount_NeverSeesTheHoldsOfAnother()
    {
        var machine = Machine();
        Hold(Failed(machine, T0));
        Hold(Failed(machine, T0.AddMinutes(1)));

        Assert.Single(_store.ListHolds(Tenant, T0.AddMinutes(1).AddSeconds(30)));
        Assert.Empty(_store.ListHolds(OtherTenant, T0.AddMinutes(1).AddSeconds(30)));
    }

    // ----- the picture carries the hold to the engine and to the page -----

    private LicensePicture Picture(DateTime now)
        => new LicenseSnapshotSource(_store, new LicenseReadModel(_store)).ReadPicture(Tenant, now, Array.Empty<LicenseExhaustion>());

    /// <summary>The machine "m1" has failed the same switch from A to B three times, the last at 03:14, so it is held
    /// two minutes: until 03:16:01. It reported at 03:14 too, so its numbers are fresh.</summary>
    private Guid HeldSince0314()
    {
        var machine = Machine();
        Hold(Failed(machine, T0));
        Hold(Failed(machine, T0.AddMinutes(1)));
        Hold(Failed(machine, T0.AddMinutes(2)));
        Machine(at: T0.AddMinutes(2));
        return machine;
    }

    [Fact]
    public void ReadPicture_AMachineWhoseLatestSwitchRowCarriesAHold_HasItInTheEnginesPicture()
    {
        HeldSince0314();

        var machine = Assert.Single(Picture(T0.AddMinutes(3)).Snapshot.Machines);

        Assert.Equal(new LicenseHold(T0.AddMinutes(4).AddSeconds(1), A, B, Restored), machine.Hold);
        Assert.True(LicenseDecisionEngine.IsHeld(machine, T0.AddMinutes(3)));
        Assert.False(LicenseDecisionEngine.IsHeld(machine, T0.AddMinutes(4).AddSeconds(1)));
    }

    [Fact]
    public void Machine_Held_IsGivenTheSentenceSayingSoWhyAndUntilWhen_AndNoneOnceTheHoldHasPassed()
    {
        HeldSince0314();

        var held = Assert.Single(LicenseDisplayFold.Overview(Picture(T0.AddMinutes(3))).Machines);
        var after = Assert.Single(LicenseDisplayFold.Overview(Picture(T0.AddMinutes(5))).Machines);

        Assert.Equal(T0.AddMinutes(4).AddSeconds(1), held.HeldUntilUtc);
        Assert.Equal("Held out of the automatic switch for 1 minute, until 2026-10-11 03:16 UTC: its switch from account-a@example.com to "
                     + "account-b@example.com has failed more than once in a row (the exchange failed and was undone; the machine is on the account it was on). "
                     + "Switch now still sends it at once.", held.HeldText);
        Assert.Equal((null, null), (after.HeldUntilUtc, after.HeldText));
    }

    [Fact]
    public void Overview_AHeldMachine_IsStillOfferedSwitchNow()
    {
        HeldSince0314();

        var account = LicenseDisplayFold.Overview(Picture(T0.AddMinutes(3))).Accounts.Single(a => a.AccountEmail == A);

        Assert.True(account.CanSwitchNow);
        Assert.Equal(B, account.SwitchNowTargetAccountEmail);
    }

    [Fact]
    public void Forecast_AHeldMachine_SaysItIsHeld_WhereThePageShowsWhatIsNext()
    {
        HeldSince0314();
        var picture = Picture(T0.AddMinutes(3));

        var forecast = LicenseDisplayFold.Forecast(picture, LicenseForecaster.Forecast(picture.Snapshot, new LicenseUsageHistory().Read(Tenant, T0.AddMinutes(3))));

        var machine = Assert.Single(forecast.Machines);
        Assert.StartsWith("Held out of the automatic switch for 1 minute, until 2026-10-11 03:16 UTC: ", machine.Text);
        Assert.Equal(B, machine.NextAccountEmail);
        var account = forecast.Accounts.Single(a => a.AccountEmail == A);
        Assert.EndsWith(" The automatic switch keeps failing and is held: it is tried again in 1 minute, at 2026-10-11 03:16 UTC. Switch now still sends it at once.", account.NextSwitchText);
    }

    [Fact]
    public void LogEntry_AFailedSwitchThatCarriesAHold_SaysSoAsAMoment_AndOneWithNoneDoesNot()
    {
        var machine = Machine();
        var first = Failed(machine, T0);
        Hold(first);
        var second = Failed(machine, T0.AddMinutes(1));
        Hold(second);

        var plain = LicenseDisplayFold.LogEntry(_store.FindSwitch(Tenant, first)!);
        var held = LicenseDisplayFold.LogEntry(_store.FindSwitch(Tenant, second)!);

        Assert.Equal("m1 was not switched from account-a@example.com to account-b@example.com.", plain.Summary);
        Assert.Null(plain.HeldUntilUtc);
        Assert.Equal("m1 was not switched from account-a@example.com to account-b@example.com. It was not the first time in a row that this "
                     + "switch failed this way, so the automatic switch left m1 out until 2026-10-11 03:14 UTC.", held.Summary);
        Assert.Equal(T0.AddMinutes(2).AddSeconds(1), held.HeldUntilUtc);
    }

    // ----- the engine: pure -----

    private static readonly DateTime Now = new(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WeekReset = Now.AddDays(3);

    private static LicenseAccountReading Reading(string email, double fiveHour, double week, bool active = false)
        => new(email, new LicenseWindowReading(fiveHour, Now.AddHours(2)), new LicenseWindowReading(week, WeekReset), Now.AddMinutes(-1), active);

    private static LicenseMachineState On(string name, string? active, params string[] spares)
        => new(name, "owner", active, spares.Select(s => new LicenseSpare(s, null)).ToList(), Now.AddMinutes(-1), false, null);

    private static LicenseMachineState HeldUntil(LicenseMachineState machine, DateTime until, string from = A, string to = B, string? reasonCode = Restored)
        => machine with { Hold = new LicenseHold(until, from, to, reasonCode) };

    private static LicenseSnapshot Snapshot(IEnumerable<LicenseAccountReading> accounts, IEnumerable<LicenseMachineState> machines,
        IEnumerable<LicenseExhaustion>? exhaustions = null)
        => new(Now, accounts.ToList(), machines.ToList(), new List<LicenseBar>(), (exhaustions ?? []).ToList());

    private static readonly LicenseAccountReading[] ATiredOnItsWeek = [Reading(A, 20, 98, active: true), Reading(B, 10, 10)];

    [Fact]
    public void Decide_AHeldMachine_IsLeftOut_AndTheMachinesThatAreNotHeldAreSwitched()
    {
        var snapshot = Snapshot(ATiredOnItsWeek,
            [HeldUntil(On("windows", A, B), Now.AddMinutes(4)), On("mac", A, B)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(("mac", B), (Assert.Single(result.Switches).MachineName, result.Switches[0].TargetAccountEmail));
        Assert.Empty(result.Emails);
        Assert.Empty(result.OpenedExhaustions);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(("windows", "its switch from account-a@example.com to account-b@example.com has failed more than once in a row "
                                 + "(the exchange failed and was undone; the machine is on the account it was on), "
                                 + "so the automatic switch leaves it out until 2026-10-11 03:04 UTC"), (skipped.MachineName, skipped.Why));
    }

    [Fact]
    public void Decide_AHeldMachine_TakesNoPartInTheTargetChoice()
    {
        // The held machine holds no spare for B. It is not being switched, so it does not veto B for the one that is.
        var snapshot = Snapshot([Reading(A, 20, 98, active: true), Reading(B, 10, 10), Reading(C, 10, 50)],
            [HeldUntil(On("windows", A, C), Now.AddMinutes(4), to: C), On("mac", A, B, C)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Equal(("mac", B), (Assert.Single(result.Switches).MachineName, result.Switches[0].TargetAccountEmail));
    }

    [Fact]
    public void Decide_EveryMachineThatCouldBeSwitchedIsHeld_SwitchesNothing_AndOpensTheEventWithItsOneEmail()
    {
        var snapshot = Snapshot(ATiredOnItsWeek,
            [HeldUntil(On("windows", A, B), Now.AddMinutes(4)), HeldUntil(On("mac", A, B), Now.AddMinutes(30), reasonCode: "a-word-of-a-later-director")]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Empty(result.Switches);
        Assert.Empty(result.NewBars);
        Assert.Equal(new LicenseExhaustion(A, Now, WeekReset), Assert.Single(result.OpenedExhaustions));
        var email = Assert.Single(result.Emails);
        Assert.Equal(A, email.AccountEmail);
        Assert.Equal("DevThrottle: the switch away from account-a@example.com keeps failing - it is at 98% of its weekly limit", email.Subject);
        Assert.Equal(string.Join("\r\n",
            "account-a@example.com has reached 98% of its weekly limit. account-b@example.com has room for its machines, but the switch keeps failing, so nothing is being switched for now.",
            "",
            "windows: its switch from account-a@example.com to account-b@example.com has failed more than once in a row (the exchange failed and was undone; the machine is on the account it was on), so the automatic switch leaves it out until 2026-10-11 03:04 UTC.",
            "mac: its switch from account-a@example.com to account-b@example.com has failed more than once in a row (the machine's reason word: a-word-of-a-later-director), so the automatic switch leaves it out until 2026-10-11 03:30 UTC.",
            "",
            "Its weekly limit resets at 2026-10-14 03:00 UTC.",
            "",
            "The switch is tried again by itself each time a machine's hold has passed, and the hold grows each time the switch fails the same way, up to one hour. Switch now on the Licenses page of the Cockpit sends the switch at once, and the switch log there has a row for each try.",
            "",
            "This is the only email for this event. Sessions on these machines will stop when the limit is reached."), email.BodyText);
        Assert.Equal("The switch of the machines on account-a@example.com keeps failing and is tried again later each time, so nothing is switched for now", email.Happened);
    }

    [Fact]
    public void Decide_EveryMachineHeld_WhileItsEventIsAlreadyOpen_EmailsNothingMore_AndKeepsTheEventOpen()
    {
        var snapshot = Snapshot(ATiredOnItsWeek, [HeldUntil(On("windows", A, B), Now.AddMinutes(4))],
            [new LicenseExhaustion(A, Now.AddMinutes(-20), WeekReset)]);

        var result = LicenseDecisionEngine.Decide(snapshot);

        Assert.Empty(result.Switches);
        Assert.Empty(result.Emails);
        Assert.Empty(result.OpenedExhaustions);
        Assert.Empty(result.ClosedExhaustions);
    }

    [Fact]
    public void Decide_AHoldWhoseTimeHasCome_SwitchesTheMachineAgain_AtExactlyThatTime()
    {
        var stillHeld = Snapshot(ATiredOnItsWeek, [HeldUntil(On("windows", A, B), Now.AddSeconds(1))]);
        var over = Snapshot(ATiredOnItsWeek, [HeldUntil(On("windows", A, B), Now)]);

        Assert.Empty(LicenseDecisionEngine.Decide(stillHeld).Switches);
        Assert.Equal("windows", Assert.Single(LicenseDecisionEngine.Decide(over).Switches).MachineName);
    }

    [Fact]
    public void Decide_AHoldForASwitchAwayFromAnAccountTheMachineIsNoLongerOn_HoldsNothing()
    {
        // The failing switch was away from C; someone has since moved the machine to A, which is tired.
        var snapshot = Snapshot(ATiredOnItsWeek, [HeldUntil(On("windows", A, B), Now.AddMinutes(30), from: C)]);

        Assert.Equal("windows", Assert.Single(LicenseDecisionEngine.Decide(snapshot).Switches).MachineName);
    }

    [Fact]
    public void Decide_HeldMachines_AndNoAccountWithRoom_IsTheNoRoomEvent_BecauseThatIsWhatIsTrue()
    {
        var snapshot = Snapshot([Reading(A, 20, 98, active: true), Reading(B, 99, 10)], [HeldUntil(On("windows", A, B), Now.AddMinutes(4))]);

        var email = Assert.Single(LicenseDecisionEngine.Decide(snapshot).Emails);

        Assert.StartsWith("DevThrottle: no Claude account has room - ", email.Subject);
        Assert.Equal(LicenseStore.NoRoomHappened(A), email.Happened);
    }

    [Fact]
    public void DecideManual_AHeldMachine_IsSwitchedAllTheSame_WithATargetNamedOrNot()
    {
        var snapshot = Snapshot(ATiredOnItsWeek, [HeldUntil(On("windows", A, B), Now.AddMinutes(45))]);

        var automatic = LicenseDecisionEngine.DecideManual(snapshot, A, null);
        var named = LicenseDecisionEngine.DecideManual(snapshot, A, B);

        Assert.Equal(("windows", B, LicenseSwitchReasons.Manual),
            (Assert.Single(automatic.Switches).MachineName, automatic.Switches[0].TargetAccountEmail, automatic.Switches[0].Reason));
        Assert.Equal("windows", Assert.Single(named.Switches).MachineName);
        Assert.Empty(automatic.Skipped);
    }

    [Fact]
    public void NoRoomEmail_EveryMachineHeld_IsTheKeepsFailingEmail_AndNullOnceAMachineCanBeSwitchedAgain()
    {
        var held = Snapshot(ATiredOnItsWeek, [HeldUntil(On("windows", A, B), Now.AddMinutes(4))]);
        var free = Snapshot(ATiredOnItsWeek, [HeldUntil(On("windows", A, B), Now)]);

        Assert.StartsWith("DevThrottle: the switch away from account-a@example.com keeps failing - ", LicenseDecisionEngine.NoRoomEmail(held, A)!.Subject);
        Assert.Null(LicenseDecisionEngine.NoRoomEmail(free, A));
    }

    [Fact]
    public void Build_AHoldRow_IsOnItsOwnMachineOnly()
    {
        var one = Machine("m1");
        Machine("m2");
        var overview = new LicenseReadModel(_store).Read(Tenant, T0);
        var hold = new LicenseSwitchLogEntity
        {
            Kind = LicenseLogKinds.Switch, MachineId = one, FromAccountEmail = A, ToAccountEmail = B, State = LicenseLogStates.Failed,
            ReasonCode = Restored, CreatedAtUtc = T0, HeldUntilUtc = T0.AddMinutes(8),
        };

        var snapshot = LicenseSnapshotSource.Build(overview, [], [], [], T0, [hold]);

        Assert.Equal(new LicenseHold(T0.AddMinutes(8), A, B, Restored), snapshot.Machines.Single(m => m.MachineName == "m1").Hold);
        Assert.Null(snapshot.Machines.Single(m => m.MachineName == "m2").Hold);
    }
}
