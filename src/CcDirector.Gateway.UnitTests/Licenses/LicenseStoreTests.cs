using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE LICENSE STORE over the real EF store on a throwaway SQLite file (License Switching, contract sections 2, 4, 5).
/// What these hold down: a report replaces the machine's holdings exactly; a holding that appears or leaves writes a
/// spare row (but a machine's first report writes none); machine identity ignores case; one account never sees
/// another's; a switch row is settled once and never again; a bar holds until its window resets.
/// </summary>
public sealed class LicenseStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId TenantA = new("acct-a");
    private static readonly TenantId TenantB = new("acct-b");

    private LicenseStore Open() => new(_harness.Open());

    private static LicenseReportApplied Apply(LicenseStore store, TenantId tenant, LicenseUsageReport report, long seq = 1, DateTime? at = null)
        => store.ApplyReport(tenant, "dir-1", seq, report, at ?? T0, TimeSpan.Zero);

    [Fact]
    public void ApplyReport_FirstReport_StoresMachineAccountsAndHoldings_AndLogsNoSpareRows()
    {
        var store = Open();

        var applied = Apply(store, TenantA, Report("the-windows-machine", Holding(A, true, 96), Holding(B, false, 4)));

        Assert.True(applied.FirstReport);
        Assert.Empty(applied.Added);
        var overview = new LicenseReadModel(store).Read(TenantA, T0);
        var machine = Assert.Single(overview.Machines);
        Assert.Equal((A, "dir-1"), (machine.ActiveAccountEmail, machine.DirectorId));
        Assert.Equal(new[] { B }, machine.Spares);
        Assert.Equal(new[] { A, B }, overview.Accounts.Select(a => a.AccountEmail));
        Assert.Equal(96, overview.Accounts[0].Freshest!.FiveHourUtilization);
        Assert.Empty(store.ListLog(TenantA, T0.AddDays(-1)));
    }

    [Fact]
    public void ApplyReport_SpareAddedAndRemoved_WritesOneRowEach()
    {
        var store = Open();
        Apply(store, TenantA, Report("m1", Holding(A, true), Holding(B, false)));

        var applied = Apply(store, TenantA, Report("m1", Holding(A, true), Holding(C, false)), seq: 2, at: T0.AddMinutes(2));

        Assert.Equal(new[] { C }, applied.Added);
        Assert.Equal(new[] { B }, applied.Removed);
        var log = store.ListLog(TenantA, T0);
        Assert.Equal(2, log.Count);
        var added = Assert.Single(log, r => r.Kind == LicenseLogKinds.SpareAdded);
        var removed = Assert.Single(log, r => r.Kind == LicenseLogKinds.SpareRemoved);
        Assert.Equal((C, ""), (added.ToAccountEmail, added.FromAccountEmail));
        Assert.Equal((B, ""), (removed.FromAccountEmail, removed.ToAccountEmail));
        Assert.All(log, r => Assert.Equal(LicenseLogStates.Recorded, r.State));
        Assert.Equal(new[] { C }, new LicenseReadModel(store).Read(TenantA, T0.AddMinutes(2)).Machines[0].Spares);
    }

    [Fact]
    public void ApplyReport_ActiveAndSpareTradePlaces_LogsNothing_AndMovesTheActiveAccount()
    {
        var store = Open();
        Apply(store, TenantA, Report("m1", Holding(A, true), Holding(B, false)));

        var applied = Apply(store, TenantA, Report("m1", Holding(B, true), Holding(A, false)), seq: 2);

        Assert.Empty(applied.Added);
        Assert.Empty(applied.Removed);
        var machine = new LicenseReadModel(store).Read(TenantA, T0).Machines[0];
        Assert.Equal(B, machine.ActiveAccountEmail);
        Assert.Equal(new[] { A }, machine.Spares);
    }

    [Fact]
    public void ApplyReport_MachineNameInAnotherCase_IsTheSameMachine()
    {
        var store = Open();
        Apply(store, TenantA, Report("The-Windows-Machine", Holding(A, true)));

        Apply(store, TenantA, new LicenseUsageReport
        {
            MachineName = "THE-WINDOWS-MACHINE", OsUser = "OWNER", SentAtUtc = T0, Trigger = "interval",
            Holdings = { Holding("Account-A@Example.com", true) },
        }, seq: 2);

        var overview = new LicenseReadModel(store).Read(TenantA, T0);
        Assert.Single(overview.Machines);
        Assert.Equal(A, Assert.Single(overview.Accounts).AccountEmail);
    }

    [Fact]
    public void ApplyReport_OneTenant_IsInvisibleToAnother()
    {
        var store = Open();
        Apply(store, TenantA, Report("m1", Holding(A, true)));
        Apply(store, TenantB, Report("m1", Holding(A, true, 50)));

        var a = new LicenseReadModel(store).Read(TenantA, T0);
        var b = new LicenseReadModel(store).Read(TenantB, T0);

        Assert.Equal(10, Assert.Single(a.Accounts).Freshest!.FiveHourUtilization);
        Assert.Equal(50, Assert.Single(b.Accounts).Freshest!.FiveHourUtilization);
        Assert.Single(a.Machines);
        Assert.Single(b.Machines);
    }

    [Fact]
    public void ApplyReport_ReadAtShift_IsStoredAsTheJudgedReadTime()
    {
        var store = Open();

        store.ApplyReport(TenantA, "dir-1", 1, Report("m1", Holding(A, true, readAt: T0.AddMinutes(-1))), T0, TimeSpan.FromHours(1));

        var reading = new LicenseReadModel(store).Read(TenantA, T0).Accounts[0].Freshest!;
        Assert.Equal(T0.AddMinutes(59), reading.JudgedReadAtUtc);
    }

    [Fact]
    public void ApplyReport_KeepsTheSignInTimes_AndFoldsTheExpiryVerdict()
    {
        var store = Open();
        var active = Holding(A, true);
        active.SignedInAtUtc = T0.AddDays(-28);
        active.SignInExpiresAtUtc = T0.AddDays(2);

        Apply(store, TenantA, Report("m1", active, Holding(B, false)));

        var holdings = new LicenseReadModel(store).Read(TenantA, T0).Machines[0].Holdings;
        Assert.Equal((T0.AddDays(-28), T0.AddDays(2), LicenseSignInExpiry.ExpiresSoon),
            (holdings[0].SignedInAtUtc!.Value, holdings[0].SignInExpiresAtUtc!.Value, holdings[0].ExpiryVerdict));
        Assert.Equal(LicenseSignInExpiry.NotKnown, holdings[1].ExpiryVerdict);
    }

    [Fact]
    public void PendingSwitch_IsSettledOnce_AndAFinalRowNeverChangesAgain()
    {
        var store = Open();
        var machineId = Apply(store, TenantA, Report("m1", Holding(A, true), Holding(B, false))).MachineId;

        var row = store.CreatePendingSwitch(TenantA, machineId, A, B, LicenseSwitchReasons.Threshold, T0)!;
        Assert.Equal(LicenseLogStates.Pending, row.State);
        Assert.Equal(T0 + LicenseContract.SwitchNotAfter, row.NotAfterUtc);
        Assert.Matches(LicenseContract.SwitchIdShape, row.SwitchId!);

        Assert.True(store.RecordSend(TenantA, row.SwitchId!, "dir-1", T0));
        Assert.True(store.TryMark(TenantA, row.SwitchId!, LicenseLogStates.Unknown, null, "No answer within 60 seconds.", T0.AddMinutes(1)));
        var result = new LicenseSwitchResult
        {
            SwitchId = row.SwitchId!, Outcome = LicenseSwitchOutcomes.Switched, PreviousActiveAccountEmail = A,
            ActiveAccountEmailAfter = B, VerifiedByUsageRead = true, StartedAtUtc = T0, FinishedAtUtc = T0.AddSeconds(2),
            ExchangeMilliseconds = 41, Replayed = true,
        };
        Assert.True(store.TrySettle(TenantA, result, T0.AddMinutes(2)));
        Assert.False(store.TryMark(TenantA, row.SwitchId!, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorError, "late", T0.AddMinutes(3)));

        var after = store.FindSwitch(TenantA, row.SwitchId)!;
        Assert.Equal((LicenseLogStates.Switched, B, 41, 1), (after.State, after.ActiveAccountEmailAfter, after.ExchangeMilliseconds, after.SendCount));
        Assert.True(after.Replayed);
        Assert.Empty(store.ListOpenSwitches(TenantA));
        Assert.Null(store.FindSwitch(TenantB, row.SwitchId));
    }

    // ---- the renewal date of a subscription, and a failed renewal of a sign-in ----

    [Fact]
    public void SetRenewalDate_IsStoredForTheAccount_ReadBack_AndClearedWithNull()
    {
        var store = Open();
        Apply(store, TenantA, Report("m1", Holding(A, true), Holding(B, false)));
        var date = new DateOnly(2026, 10, 17);

        Assert.True(store.SetRenewalDate(TenantA, "  Account-A@Example.com ", date));   // found whatever its spelling

        var accounts = new LicenseReadModel(store).Read(TenantA, T0).Accounts;
        Assert.Equal(date, accounts.Single(a => a.AccountEmail == A).RenewsOn);
        Assert.Null(accounts.Single(a => a.AccountEmail == B).RenewsOn);

        Assert.True(store.SetRenewalDate(TenantA, A, null));
        Assert.Null(new LicenseReadModel(store).Read(TenantA, T0).Accounts.Single(a => a.AccountEmail == A).RenewsOn);
    }

    [Fact]
    public void SetRenewalDate_ForAnAccountNoMachineOfThisDevThrottleAccountHolds_IsRefused_AndAnothersIsUntouched()
    {
        var store = Open();
        Apply(store, TenantA, Report("m1", Holding(A, true)));
        Apply(store, TenantB, Report("m2", Holding(A, true), Holding(C, false)));
        Assert.True(store.SetRenewalDate(TenantB, A, new DateOnly(2026, 11, 1)));

        Assert.False(store.SetRenewalDate(TenantA, C, new DateOnly(2026, 10, 17)));                    // the other's account: unknown here
        Assert.False(store.SetRenewalDate(TenantA, "nobody@example.com", new DateOnly(2026, 10, 17)));
        Assert.True(store.SetRenewalDate(TenantA, A, new DateOnly(2026, 10, 17)));                     // the same email, its own row

        Assert.Equal(new DateOnly(2026, 10, 17), new LicenseReadModel(store).Read(TenantA, T0).Accounts.Single().RenewsOn);
        var others = new LicenseReadModel(store).Read(TenantB, T0).Accounts;
        Assert.Equal(new DateOnly(2026, 11, 1), others.Single(a => a.AccountEmail == A).RenewsOn);
        Assert.Null(others.Single(a => a.AccountEmail == C).RenewsOn);
    }

    [Fact]
    public void SetRenewalDate_SurvivesTheNextReport_BecauseNoReportCarriesIt()
    {
        var store = Open();
        Apply(store, TenantA, Report("m1", Holding(A, true)));
        store.SetRenewalDate(TenantA, A, new DateOnly(2026, 10, 17));

        Apply(store, TenantA, Report("m1", T0.AddMinutes(2), Holding(A, true, 50, readAt: T0.AddMinutes(2))), seq: 2, at: T0.AddMinutes(2));

        Assert.Equal(new DateOnly(2026, 10, 17), new LicenseReadModel(store).Read(TenantA, T0.AddMinutes(2)).Accounts.Single().RenewsOn);
    }

    private static LicenseHolding FailedRenewal(LicenseHolding holding, DateTime? at, string? error, string? detail)
    {
        holding.RenewalFailedAtUtc = at;
        holding.RenewalError = error;
        holding.RenewalErrorDetail = detail;
        return holding;
    }

    private static LicenseHoldingView SpareOf(LicenseStore store, DateTime now)
        => new LicenseReadModel(store).Read(TenantA, now).Machines.Single().Holdings.Single(h => !h.IsActive);

    [Fact]
    public void ApplyReport_StoresAFailedRenewal_AndTheNextReportThatSaysItWorked_ClearsAllThree()
    {
        var store = Open();
        var failedAt = T0.AddHours(-3);
        Apply(store, TenantA, Report("m1", Holding(A, true),
            FailedRenewal(Holding(B, false), failedAt, LicenseRenewalErrors.WrongAccount, "The approving browser is signed in as another account.")));

        var failed = SpareOf(store, T0);
        Assert.Equal((failedAt, LicenseRenewalErrors.WrongAccount, "The approving browser is signed in as another account."),
            (failed.RenewalFailedAtUtc, failed.RenewalError, failed.RenewalErrorDetail));
        var active = new LicenseReadModel(store).Read(TenantA, T0).Machines.Single().Holdings.Single(h => h.IsActive);
        Assert.Equal((null, null, null), (active.RenewalFailedAtUtc, active.RenewalError, active.RenewalErrorDetail));

        Apply(store, TenantA, Report("m1", T0.AddMinutes(2), Holding(A, true, readAt: T0.AddMinutes(2)), Holding(B, false)), seq: 2, at: T0.AddMinutes(2));

        var renewed = SpareOf(store, T0.AddMinutes(2));
        Assert.Equal((null, null, null), (renewed.RenewalFailedAtUtc, renewed.RenewalError, renewed.RenewalErrorDetail));
    }

    [Fact]
    public void ApplyReport_AReasonWordTheContractDoesNotName_IsStoredAsNotKnown_NeverAsGiven_AndTheFailureAndItsSentenceAreKept()
    {
        var store = Open();
        Apply(store, TenantA, Report("m1", Holding(A, true),
            FailedRenewal(Holding(B, false), T0, "whatever-a-tool-printed", new string('x', 600))));

        var spare = SpareOf(store, T0);

        Assert.Equal(T0, spare.RenewalFailedAtUtc);
        // That there was a reason is kept, so nothing acting on the failure loses it; the word itself is not.
        Assert.Equal(LicenseStore.RenewalErrorNotKnown, spare.RenewalError);
        Assert.DoesNotContain(LicenseStore.RenewalErrorNotKnown, LicenseRenewalErrors.All);
        Assert.Equal(500, spare.RenewalErrorDetail!.Length);   // the sentence is capped like every other free text
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ApplyReport_AFailedRenewalWithNoReasonGiven_StoresNoReason(string? word)
    {
        var store = Open();
        Apply(store, TenantA, Report("m1", Holding(A, true), FailedRenewal(Holding(B, false), T0, word, null)));

        var spare = SpareOf(store, T0);

        Assert.Equal((T0, null), (spare.RenewalFailedAtUtc, spare.RenewalError));
    }

    [Fact]
    public void ApplyReport_AReasonWithNoFailureTime_IsNotAFailedRenewal_AndNothingOfItIsStored()
    {
        var store = Open();
        Apply(store, TenantA, Report("m1", Holding(A, true),
            FailedRenewal(Holding(B, false), null, LicenseRenewalErrors.Expired, "No approval came back.")));

        var spare = SpareOf(store, T0);

        Assert.Equal((null, null, null), (spare.RenewalFailedAtUtc, spare.RenewalError, spare.RenewalErrorDetail));
    }

    [Fact]
    public void CreatePendingSwitch_WhileTheMachineHasAnOpenSwitch_IsRefused_AndAllowedAgainOnceItEnds()
    {
        var store = Open();
        var machineId = Apply(store, TenantA, Report("m1", Holding(A, true), Holding(B, false))).MachineId;
        var first = store.CreatePendingSwitch(TenantA, machineId, A, B, LicenseSwitchReasons.Threshold, T0, "Why the first was decided.")!;
        Assert.Equal("Why the first was decided.", first.Message);

        // Contract section 5.4: no second switch identifier while one is pending, or sent with no answer.
        Assert.Null(store.CreatePendingSwitch(TenantA, machineId, A, B, LicenseSwitchReasons.Manual, T0.AddMinutes(1)));
        Assert.True(store.TryMark(TenantA, first.SwitchId!, LicenseLogStates.Unknown, null, "No answer.", T0.AddMinutes(1)));
        Assert.Null(store.CreatePendingSwitch(TenantA, machineId, A, B, LicenseSwitchReasons.Manual, T0.AddMinutes(2)));

        Assert.True(store.TryMark(TenantA, first.SwitchId!, LicenseLogStates.Switched, null, "Settled from the machine's report.", T0.AddMinutes(3)));
        var second = store.CreatePendingSwitch(TenantA, machineId, B, A, LicenseSwitchReasons.Manual, T0.AddMinutes(4));

        Assert.NotNull(second);
        Assert.NotEqual(first.SwitchId, second!.SwitchId);
        Assert.Equal(T0.AddMinutes(3), store.FindSwitch(TenantA, first.SwitchId)!.FinishedAtUtc);
    }

    [Fact]
    public void NoRoom_IsOneOpenRowPerAccount_ThatCanBeMovedAndEnded()
    {
        var store = Open();

        var opened = store.OpenNoRoom(TenantA, " Account-A@Example.com ", T0, T0.AddHours(2));
        var again = store.OpenNoRoom(TenantA, A, T0.AddMinutes(1), T0.AddHours(9));

        Assert.Equal(opened.Id, again.Id); // already open: returned as it stands
        Assert.Equal((LicenseLogKinds.NoRoom, A, LicenseLogStates.Open, T0, T0.AddHours(2)),
            (opened.Kind, opened.FromAccountEmail, opened.State, opened.CreatedAtUtc, opened.NotAfterUtc!.Value));
        Assert.Null(opened.SwitchId);
        Assert.Empty(store.ListOpenSwitches(TenantA)); // it is not a switch, and holds no machine back
        Assert.Empty(store.ListOpenNoRoom(TenantB));

        Assert.True(store.ExtendNoRoom(TenantA, A, T0.AddDays(4)));
        Assert.Equal(T0.AddDays(4), Assert.Single(store.ListOpenNoRoom(TenantA)).NotAfterUtc);

        // An open event is in the log however old it is.
        Assert.Single(store.ListLog(TenantA, T0.AddDays(30)));

        Assert.True(store.CloseNoRoom(TenantA, A, T0.AddHours(3)));
        Assert.Empty(store.ListOpenNoRoom(TenantA));
        Assert.False(store.CloseNoRoom(TenantA, A, T0.AddHours(4)));
        Assert.False(store.ExtendNoRoom(TenantA, A, T0.AddDays(9)));
        var ended = Assert.Single(store.ListLog(TenantA, T0));
        Assert.Equal((LicenseLogStates.Ended, T0.AddHours(3)), (ended.State, ended.FinishedAtUtc!.Value));

        // The account can run out again later: a new event, a new row.
        Assert.NotEqual(opened.Id, store.OpenNoRoom(TenantA, A, T0.AddHours(5), T0.AddHours(7)).Id);
    }

    [Fact]
    public void RecordNoRoomEmail_AnEventRecordedAsEmailed_IsNeverChangedAgain()
    {
        var store = Open();
        store.OpenNoRoom(TenantA, A, T0, T0.AddHours(2));

        Assert.True(store.RecordNoRoomEmail(TenantA, A, LicenseGatewayReasonCodes.OwnerEmailFailed, "Not sent yet.", null));
        var failed = Assert.Single(store.ListOpenNoRoom(TenantA));
        Assert.Equal((LicenseGatewayReasonCodes.OwnerEmailFailed, "Not sent yet.", 0), (failed.ReasonCode, failed.Message, failed.SendCount));
        Assert.Null(failed.LastSentAtUtc);

        Assert.True(store.RecordNoRoomEmail(TenantA, A, LicenseGatewayReasonCodes.OwnerEmailed, "The owner was emailed.", T0.AddMinutes(1)));
        Assert.False(store.RecordNoRoomEmail(TenantA, A, LicenseGatewayReasonCodes.OwnerEmailFailed, "A late failure.", null));
        Assert.False(store.RecordNoRoomEmail(TenantA, A, LicenseGatewayReasonCodes.OwnerEmailed, "A second send.", T0.AddMinutes(2)));

        var emailed = Assert.Single(store.ListOpenNoRoom(TenantA));
        Assert.Equal((LicenseGatewayReasonCodes.OwnerEmailed, "The owner was emailed.", 1, T0.AddMinutes(1)),
            (emailed.ReasonCode, emailed.Message, emailed.SendCount, emailed.LastSentAtUtc!.Value));
        Assert.False(store.RecordNoRoomEmail(TenantB, A, LicenseGatewayReasonCodes.OwnerEmailed, "Another tenant.", T0)); // no such event there
        Assert.Throws<ArgumentException>(() => store.RecordNoRoomEmail(TenantA, A, LicenseGatewayReasonCodes.OwnerEmailed, "No time given.", null));
        Assert.Throws<ArgumentException>(() => store.RecordNoRoomEmail(TenantA, A, "switched", "Not an email state.", null));
    }

    [Fact]
    public void HasMachines_IsTrueOnlyForATenantWhoseMachineHasReported()
    {
        var store = Open();
        Assert.False(store.HasMachines(TenantA));

        Apply(store, TenantA, Report("m1"));

        Assert.True(store.HasMachines(TenantA)); // a key holder with no sign-ins is still a machine (contract section 2.4)
        Assert.False(store.HasMachines(TenantB));
    }

    [Fact]
    public void Bar_HoldsUntilTheWindowResets_AndBarringAgainMovesIt()
    {
        var store = Open();

        store.Bar(TenantA, A, LicenseWindows.FiveHour, T0.AddHours(2), T0);
        store.Bar(TenantA, A, LicenseWindows.FiveHour, T0.AddHours(5), T0.AddHours(1));

        var bar = Assert.Single(store.ListActiveBars(TenantA, T0.AddHours(3)));
        Assert.Equal(T0.AddHours(5), bar.UntilUtc);
        Assert.Empty(store.ListActiveBars(TenantA, T0.AddHours(5)));
        Assert.Empty(store.ListActiveBars(TenantB, T0));
    }
}
