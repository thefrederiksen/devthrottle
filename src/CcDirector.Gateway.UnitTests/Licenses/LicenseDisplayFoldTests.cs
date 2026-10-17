using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE LICENSES PAGE'S VERDICTS (License Switching; CLAUDE.md rule 7). Every verdict the page shows is folded on the
/// Gateway, so each one is pinned here AT ITS BOUNDARY: the last value that does not earn it and the first that does.
/// The facts go in the way they do in production - usage reports applied to the real store on a throwaway SQLite
/// file, read back through <see cref="LicenseSnapshotSource"/> - so these also watch the read model and the snapshot
/// the fold is given, not a picture built by hand.
/// </summary>
public sealed class LicenseDisplayFoldTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _source;

    private static readonly TenantId Tenant = new("acct-a");
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    public LicenseDisplayFoldTests()
    {
        _store = new LicenseStore(_harness.Open());
        _source = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
    }

    public void Dispose() => _harness.Dispose();

    private Guid Apply(LicenseUsageReport report, long seq = 1, DateTime? at = null)
        => _store.ApplyReport(Tenant, "dir-1", seq, report, at ?? T0, TimeSpan.Zero).MachineId;

    private LicensePicture Picture(DateTime now) => _source.ReadPicture(Tenant, now, Array.Empty<LicenseExhaustion>());

    private LicenseOverviewResponse Overview(DateTime now) => LicenseDisplayFold.Overview(Picture(now));

    private LicenseAccountDto Account(string email, DateTime now) => Overview(now).Accounts.Single(a => a.AccountEmail == email);

    private static LicenseHolding Expiring(LicenseHolding holding, DateTime expiresAtUtc)
    {
        holding.SignInExpiresAtUtc = expiresAtUtc;
        return holding;
    }

    // ---- the status of an account, each verdict at its boundary ----

    [Theory]
    [InlineData(96.99, 20, LicenseAccountStatuses.InUse)]
    [InlineData(97.0, 20, LicenseAccountStatuses.AtLimit)]
    [InlineData(10, 96.99, LicenseAccountStatuses.InUse)]
    [InlineData(10, 97.0, LicenseAccountStatuses.AtLimit)]
    public void Account_AtLimit_StartsAtExactlyTheThreshold_OnEitherWindow(double fiveHour, double sevenDay, string expected)
    {
        Apply(Report("m1", Holding(A, true, fiveHour, sevenDay), Holding(B, false)));

        var account = Account(A, T0);

        Assert.Equal(expected, account.Status);
        Assert.Equal(expected == LicenseAccountStatuses.AtLimit ? "At limit" : "In use", account.StatusText);
        Assert.Equal(expected == LicenseAccountStatuses.AtLimit ? LicenseTones.Bad : LicenseTones.Good, account.StatusTone);
    }

    [Fact]
    public void Account_AtLimit_SaysWhichWindowAndWhenItResets()
    {
        Apply(Report("m1", Holding(A, true, 98.5, 61, readAt: T0.AddMinutes(10)), Holding(B, false)));

        var account = Account(A, T0.AddMinutes(12));

        Assert.Equal("At 98.5% of its 5-hour window, which resets in 1 hour 48 minutes.", account.StatusDetail);
        Assert.Equal(LicenseWindowLevels.AtLimit, account.FiveHour.Level);
        Assert.Equal(LicenseWindowLevels.Normal, account.SevenDay.Level);
    }

    [Theory]
    [InlineData(true, 6 * 60, false)]         // an active reading is stale after 6 minutes
    [InlineData(true, 6 * 60 + 1, true)]
    [InlineData(false, 30 * 60, false)]       // a spare's after 30
    [InlineData(false, 30 * 60 + 1, true)]
    public void Account_Stale_StartsOneSecondPastItsLimit(bool active, int secondsOld, bool stale)
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));
        var email = active ? A : B;

        var account = Account(email, T0.AddSeconds(secondsOld));

        Assert.Equal(stale, account.IsStale);
        Assert.Equal(stale ? LicenseAccountStatuses.Stale : active ? LicenseAccountStatuses.InUse : LicenseAccountStatuses.HasRoom, account.Status);
        if (stale)
        {
            Assert.Equal(("Stale", LicenseTones.Warning), (account.StatusText, account.StatusTone));
            Assert.EndsWith("old, so its use now is not known.", account.StatusDetail);
        }
    }

    [Fact]
    public void Account_Stale_OutranksAtLimit_BecauseOldNumbersCannotSayItIsAtItsLimit()
    {
        Apply(Report("m1", Holding(A, true, 99, 99), Holding(B, false)));

        Assert.Equal(LicenseAccountStatuses.AtLimit, Account(A, T0.AddMinutes(6)).Status);
        Assert.Equal(LicenseAccountStatuses.Stale, Account(A, T0.AddMinutes(6) + OneSecond).Status);
    }

    [Fact]
    public void Account_Stale_ItsWindowsAreStaleToo_NeverAtLimitOrNormal_SoTheLevelAloneIsEnough()
    {
        Apply(Report("m1", Holding(A, true, 99, 20), Holding(B, false)));

        var fresh = Account(A, T0.AddMinutes(6));
        var stale = Account(A, T0.AddMinutes(6) + OneSecond);

        Assert.Equal((LicenseWindowLevels.AtLimit, LicenseWindowLevels.Normal), (fresh.FiveHour.Level, fresh.SevenDay.Level));
        Assert.Equal((LicenseWindowLevels.Stale, LicenseWindowLevels.Stale), (stale.FiveHour.Level, stale.SevenDay.Level));
        Assert.Equal(("99%", "20%"), (stale.FiveHour.UtilizationText, stale.SevenDay.UtilizationText)); // the old numbers are still shown
    }

    [Fact]
    public void Window_OfAStaleReading_IsStale_WhateverItsNumber_AndAMissingOneIsStillNotKnown()
    {
        var atLimit = LicenseDisplayFold.Window(new LicenseWindowReading(99, T0.AddHours(1)), T0, true);
        var resetSince = LicenseDisplayFold.Window(new LicenseWindowReading(99, T0.AddHours(-1)), T0, true);
        var missing = LicenseDisplayFold.Window(null, T0, true);

        Assert.Equal(LicenseWindowLevels.Stale, atLimit.Level);
        Assert.Equal((LicenseWindowLevels.Stale, "Has reset since this reading"), (resetSince.Level, resetSince.ResetsText));
        Assert.Equal(LicenseWindowLevels.NotKnown, missing.Level);
    }

    // ---- the sign-in expiry on the account row ----

    [Fact]
    public void Account_SignInExpiry_IsTheSoonestAcrossItsMachines_WithTheMachineTheVerdictAndTheSentence()
    {
        Apply(Report("m1", Holding(A, true), Expiring(Holding(B, false), T0.AddDays(9))));
        var second = Apply(Report("m2", Holding(A, true), Expiring(Holding(B, false), T0.AddDays(2))));
        Apply(Report("m3", Holding(A, true), Holding(B, false))); // gave no expiry: it cannot be the soonest

        var expiry = Account(B, T0).SignInExpiry;

        Assert.Equal((T0.AddDays(2), LicenseSignInExpiry.ExpiresSoon, "Sign-in on m2 expires in 2 days", LicenseTones.Warning),
            (expiry.ExpiresAtUtc, expiry.Verdict, expiry.Text, expiry.Tone));
        Assert.Equal((second.ToString("N"), "m2", "owner", "m2"), (expiry.Machine!.MachineId, expiry.Machine.MachineName, expiry.Machine.OsUser, expiry.Machine.Label));
    }

    [Theory]
    [InlineData(0, LicenseSignInExpiry.Expired, "Sign-in on m1 expired less than a minute ago", LicenseTones.Bad)]
    [InlineData(1, LicenseSignInExpiry.ExpiresSoon, "Sign-in on m1 expires in less than a minute", LicenseTones.Warning)]
    [InlineData(3 * 24 * 3600, LicenseSignInExpiry.ExpiresSoon, "Sign-in on m1 expires in 3 days", LicenseTones.Warning)]
    [InlineData(3 * 24 * 3600 + 1, LicenseSignInExpiry.Fine, "Sign-in on m1 expires in 3 days", LicenseTones.Good)]
    public void Account_SignInExpiry_CarriesTheSameVerdictItsHoldingDoes_AtEachBoundary(int secondsFromNow, string verdict, string text, string tone)
    {
        Apply(Report("m1", Holding(A, true), Expiring(Holding(B, false), T0.AddSeconds(secondsFromNow))));

        var overview = Overview(T0);
        var expiry = overview.Accounts.Single(a => a.AccountEmail == B).SignInExpiry;

        Assert.Equal((verdict, text, tone), (expiry.Verdict, expiry.Text, expiry.Tone));
        Assert.Equal(overview.Machines.Single().Spares.Single().ExpiryVerdict, expiry.Verdict);
    }

    [Fact]
    public void Account_SignInExpiry_WhenNoMachineGaveOne_IsNotKnown_AndWhenNoMachineHoldsIt_SaysSo()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));
        var notKnown = Account(B, T0).SignInExpiry;
        Apply(Report("m1", T0.AddMinutes(2), Holding(A, true, readAt: T0.AddMinutes(2))), seq: 2, at: T0.AddMinutes(2));
        var notHeld = Account(B, T0.AddMinutes(2)).SignInExpiry;

        Assert.Equal((null, null, LicenseSignInExpiry.NotKnown, "Sign-in expiry not known", LicenseTones.Neutral),
            (notKnown.ExpiresAtUtc, notKnown.Machine, notKnown.Verdict, notKnown.Text, notKnown.Tone));
        Assert.Equal((null, null, LicenseSignInExpiry.NotKnown, "Not signed in on any machine", LicenseTones.Neutral),
            (notHeld.ExpiresAtUtc, notHeld.Machine, notHeld.Verdict, notHeld.Text, notHeld.Tone));
    }

    // ---- the renewal date of the subscription ----

    [Theory]
    [InlineData(null, "Renewal date not set")]
    [InlineData("2026-10-11", "Renews today, 11 Oct")]               // T0 is 11 October 2026; the date is always given
    [InlineData("2026-10-12", "Renews 12 Oct")]
    [InlineData("2026-10-17", "Renews 17 Oct")]
    [InlineData("2026-12-31", "Renews 31 Dec")]
    [InlineData("2027-01-01", "Renews 1 Jan 2027")]                  // another year is said
    [InlineData("2026-10-10", "Renewal date 10 Oct has passed")]     // never shown as if it were still ahead
    [InlineData("2025-12-31", "Renewal date 31 Dec 2025 has passed")]
    public void RenewsText_SaysTheDateAsAPersonWould_AndNeverGuessesOne(string? renewsOn, string expected)
    {
        DateOnly? date = renewsOn is null ? null : DateOnly.ParseExact(renewsOn, "yyyy-MM-dd");

        Assert.Equal(expected, LicenseDisplayFold.RenewsText(date, T0));
    }

    [Fact]
    public void RenewsText_TodayIsTheGatewaysDateInCoordinatedUniversalTime_UntilTheLastSecondOfIt()
    {
        var date = new DateOnly(2026, 10, 11);

        Assert.Equal("Renews today, 11 Oct", LicenseDisplayFold.RenewsText(date, new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("Renews today, 11 Oct", LicenseDisplayFold.RenewsText(date, new DateTime(2026, 10, 11, 23, 59, 59, DateTimeKind.Utc)));
        Assert.Equal("Renews 11 Oct", LicenseDisplayFold.RenewsText(date, new DateTime(2026, 10, 10, 23, 59, 59, DateTimeKind.Utc)));
        Assert.Equal("Renewal date 11 Oct has passed", LicenseDisplayFold.RenewsText(date, new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Account_CarriesTheRenewalDateAPersonSet_AndSaysNotSetForAnAccountWithNone()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));
        _store.SetRenewalDate(Tenant, A, new DateOnly(2026, 10, 17));

        var overview = Overview(T0);
        var a = overview.Accounts.Single(x => x.AccountEmail == A);
        var b = overview.Accounts.Single(x => x.AccountEmail == B);

        Assert.Equal(("2026-10-17", "Renews 17 Oct"), (a.RenewsOn, a.RenewsText));
        Assert.Equal((null, "Renewal date not set"), (b.RenewsOn, b.RenewsText));
        // It is not the sign-in expiry, and setting it does not touch that.
        Assert.Equal("Sign-in expiry not known", a.SignInExpiry.Text);
    }

    // ---- an account barred from the automatic choice ----

    [Fact]
    public void Account_BarredFromTheAutomaticChoice_SaysSoInOneSentence_AndCanStillBeChosenByHand()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false), Holding(C, false)));
        _store.Bar(Tenant, B, LicenseWindowNames.FiveHour, T0.AddHours(2), T0);

        var overview = Overview(T0);
        var barred = overview.Accounts.Single(a => a.AccountEmail == B);
        var free = overview.Accounts.Single(a => a.AccountEmail == C);

        Assert.Equal(T0.AddHours(2), barred.BarredUntilUtc);
        Assert.Equal("Left out of the automatic choice for 2 hours, until its 5-hour window resets: it triggered a switch. It can still be chosen by hand.", barred.BarredText);
        Assert.Equal((null, null), (free.BarredUntilUtc, free.BarredText));
        // The bar stops the automatic choice only: by hand it is still a target.
        Assert.True(overview.Accounts.Single(a => a.AccountEmail == A).SwitchTargets.Single(t => t.AccountEmail == B).CanChoose);
    }

    [Fact]
    public void Account_WhoseBarHasEnded_IsNotBarred_AtTheVeryMomentItEnds()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));
        _store.Bar(Tenant, B, LicenseWindowNames.SevenDay, T0.AddMinutes(1), T0);

        var before = Account(B, T0.AddSeconds(59));
        var atTheEnd = Account(B, T0.AddMinutes(1));

        Assert.StartsWith("Left out of the automatic choice for less than a minute, until its weekly limit resets: ", before.BarredText);
        Assert.Equal((null, null), (atTheEnd.BarredUntilUtc, atTheEnd.BarredText));
    }

    // ---- a failed renewal of a sign-in ----

    private LicenseMachineHoldingDto SpareWithFailedRenewal(DateTime? failedAt, string? error, string? detail, DateTime? expires, DateTime now)
    {
        var spare = Holding(B, false);
        spare.RenewalFailedAtUtc = failedAt;
        spare.RenewalError = error;
        spare.RenewalErrorDetail = detail;
        spare.SignInExpiresAtUtc = expires;
        Apply(Report("m1", Holding(A, true), spare));
        return Overview(now).Machines.Single().Spares.Single();
    }

    [Fact]
    public void Holding_WhoseLastRenewalWorkedOrIsNotKnown_HasNoFailedRenewal()
    {
        var spare = SpareWithFailedRenewal(null, null, null, T0.AddDays(20), T0);

        Assert.Equal((null, null, null), (spare.RenewalFailedAtUtc, spare.RenewalFailedText, spare.RenewalFailedTone));
    }

    [Fact]
    public void Holding_WhoseRenewalFailed_SaysWhenAndWhy()
    {
        var spare = SpareWithFailedRenewal(T0.AddHours(-3), LicenseRenewalErrors.WrongAccount, "ignored when the word is known", T0.AddDays(20), T0);

        Assert.Equal(T0.AddHours(-3), spare.RenewalFailedAtUtc);
        Assert.Equal("Renewing this sign-in failed 3 hours ago: the approving browser is signed in as another account.", spare.RenewalFailedText);
        Assert.Equal(LicenseTones.Warning, spare.RenewalFailedTone);
    }

    [Theory]
    [InlineData(0, LicenseTones.Bad)]                       // the sign-in has expired and could not be renewed
    [InlineData(1, LicenseTones.Bad)]
    [InlineData(3 * 24 * 3600, LicenseTones.Bad)]           // expires soon, up to exactly three days
    [InlineData(3 * 24 * 3600 + 1, LicenseTones.Warning)]   // fine: there is still time
    public void Holding_FailedRenewal_IsBadOnceTheSignInIsExpiredOrExpiresSoon_AndAWarningWhileThereIsTime(int expiresInSeconds, string tone)
    {
        var spare = SpareWithFailedRenewal(T0.AddHours(-1), LicenseRenewalErrors.Expired, null, T0.AddSeconds(expiresInSeconds), T0);

        Assert.Equal(tone, spare.RenewalFailedTone);
    }

    [Fact]
    public void Holding_FailedRenewal_WithNoExpiryKnown_IsAWarning()
    {
        Assert.Equal(LicenseTones.Warning, SpareWithFailedRenewal(T0, LicenseRenewalErrors.Expired, null, null, T0).RenewalFailedTone);
    }

    [Fact]
    public void Holding_FailedRenewal_EveryReasonTheContractNames_HasItsOwnWords()
    {
        var sentences = new List<string>();
        var seq = 0;
        foreach (var word in LicenseRenewalErrors.All.OrderBy(w => w, StringComparer.Ordinal))
        {
            var spare = Holding(B, false);
            spare.RenewalFailedAtUtc = T0.AddMinutes(-5);
            spare.RenewalError = word;
            Apply(Report("m1", Holding(A, true), spare), seq: ++seq);
            sentences.Add(Overview(T0).Machines.Single().Spares.Single().RenewalFailedText!);
        }

        Assert.Equal(9, sentences.Count);                                // a word added to the contract shows up here
        Assert.All(sentences, s => Assert.StartsWith("Renewing this sign-in failed 5 minutes ago: ", s));
        Assert.All(sentences, s => Assert.EndsWith(".", s));
        Assert.Equal(sentences.Count, sentences.Distinct().Count());
    }

    [Fact]
    public void Holding_FailedRenewal_WithNoReasonThisGatewayKnows_GivesTheDirectorsSentence_OrOnlyTheFact()
    {
        var withSentence = SpareWithFailedRenewal(T0.AddMinutes(-5), "whatever-a-tool-printed", "The approval page did not load.", null, T0);
        Assert.Equal("Renewing this sign-in failed 5 minutes ago. The approval page did not load.", withSentence.RenewalFailedText);

        var spare = Holding(B, false);
        spare.RenewalFailedAtUtc = T0.AddMinutes(-5);
        Apply(Report("m1", Holding(A, true), spare), seq: 2);
        Assert.Equal("Renewing this sign-in failed 5 minutes ago.", Overview(T0).Machines.Single().Spares.Single().RenewalFailedText);
    }

    [Fact]
    public void Holding_FailedRenewal_StampedAheadOfTheGatewaysClock_SaysItFailed_WithoutAnAgeThatWouldBeWrong()
    {
        var spare = SpareWithFailedRenewal(T0.AddMinutes(1), LicenseRenewalErrors.Expired, null, null, T0);

        Assert.Equal("Renewing this sign-in failed: no approval came back in time.", spare.RenewalFailedText);
    }

    // ---- a machine is a name AND a user ----

    private static LicenseUsageReport ReportAs(string machine, string osUser, params LicenseHolding[] holdings)
    {
        var report = Report(machine, holdings);
        report.OsUser = osUser;
        return report;
    }

    [Fact]
    public void TwoUsersOfOneMachine_AreToldApartInEveryListAndEverySentence_AndAMachineWithItsOwnNameIsNot()
    {
        var first = Apply(ReportAs("m1", "anna", Holding(A, true, 60.25), Holding(B, false)));
        var second = Apply(ReportAs("m1", "ben", Holding(A, true, 60.25), Holding(B, false)));
        var other = Apply(ReportAs("m2", "anna", Holding(A, true, 60.25), Holding(B, false)));
        var history = new LicenseUsageHistory();
        history.Record(Tenant, Report("m1", Holding(A, true, 50.25, readAt: T0.AddMinutes(-20))), TimeSpan.Zero, T0);
        history.Record(Tenant, Report("m1", Holding(A, true, 60.25)), TimeSpan.Zero, T0);

        var overview = Overview(T0);
        var a = overview.Accounts.Single(x => x.AccountEmail == A);
        var b = overview.Accounts.Single(x => x.AccountEmail == B);
        var forecast = Forecast(T0, history);

        var labels = new[] { "m1 (anna)", "m1 (ben)", "m2" };
        var ids = new[] { first.ToString("N"), second.ToString("N"), other.ToString("N") };
        Assert.Equal(labels, overview.Machines.Select(m => m.Label));
        Assert.Equal(labels, a.ActiveOnMachines.Select(m => m.Label));
        Assert.Equal(ids, a.ActiveOnMachines.Select(m => m.MachineId));
        Assert.Equal(ids, overview.Machines.Select(m => m.MachineId));
        Assert.Equal(labels, b.SpareOnMachines.Select(m => m.Label));
        Assert.Equal("In use on m1 (anna), m1 (ben) and m2.", a.StatusDetail);
        Assert.Equal("Not in use. Held as a spare on m1 (anna), m1 (ben) and m2.", b.StatusDetail);
        Assert.Equal($"Moves m1 (anna), m1 (ben) and m2 to {B}.", a.SwitchNowText);
        Assert.Equal(labels, forecast.NextSwitch!.Machines.Select(m => m.Label));
        Assert.Equal(ids, forecast.NextSwitch.Machines.Select(m => m.MachineId));
        Assert.Equal(labels, forecast.Machines.Select(m => m.Label));
        Assert.Equal(ids, forecast.Machines.Select(m => m.MachineId));
    }

    [Fact]
    public void TwoUsersOfOneMachine_TheOneLeftOutOfSwitchNow_AndTheOneWhoseSignInExpires_AreNamedWithTheirUser()
    {
        Apply(ReportAs("m1", "anna", Holding(A, true), Holding(B, false)));
        var ben = Apply(ReportAs("m1", "ben", Holding(A, true), Expiring(Holding(B, false), T0.AddDays(1))));
        _store.CreatePendingSwitch(Tenant, ben, A, B, LicenseSwitchReasons.Threshold, T0);

        var overview = Overview(T0);

        Assert.Contains("m1 (ben) is not switched: ", overview.Accounts.Single(x => x.AccountEmail == A).SwitchNowText);
        Assert.Equal("The sign-in on m1 (ben) expires in 1 day.", overview.Accounts.Single(x => x.AccountEmail == B).StatusDetail);
        Assert.Equal("Sign-in on m1 (ben) expires in 1 day", overview.Accounts.Single(x => x.AccountEmail == B).SignInExpiry.Text);
        Assert.StartsWith("Read less than a minute ago on m1 (", overview.Accounts.Single(x => x.AccountEmail == A).ReadAgeText);
    }

    [Fact]
    public void Account_NeverRead_IsStale_AndSaysNoReadingHasArrived()
    {
        var never = Holding(B, false);
        never.FiveHour = null;
        never.SevenDay = null;
        never.ReadAtUtc = null;
        Apply(Report("m1", Holding(A, true), never));

        var account = Account(B, T0);

        Assert.Equal(LicenseAccountStatuses.Stale, account.Status);
        Assert.Equal("No usage reading has arrived for this account yet.", account.StatusDetail);
        Assert.Equal("Never read on any machine", account.ReadAgeText);
        Assert.Null(account.ReadAtUtc);
        Assert.Equal((LicenseWindowLevels.NotKnown, "Not known"), (account.FiveHour.Level, account.FiveHour.UtilizationText));
        Assert.Equal((LicenseExtraUsageStates.NotKnown, "Paid extra usage not known"), (account.ExtraUsage.State, account.ExtraUsage.Text));
    }

    // The whole-phase review's finding 4: a holding's read error spoke of "the numbers shown" on a card that showed
    // none. It does so only when some machine has read the account.
    [Fact]
    public void Holding_ReadError_PointsAtTheNumbersShown_OnlyWhenTheAccountWasEverRead()
    {
        var never = Holding(B, false, readError: LicenseReadErrors.Network);
        never.FiveHour = null;
        never.SevenDay = null;
        never.ReadAtUtc = null;
        Apply(Report("m1", Holding(A, true, readError: LicenseReadErrors.Network), never));

        var machine = Overview(T0).Machines.Single();

        Assert.Equal("The machine could not reach the provider. The numbers shown are the last ones read.", machine.Active!.ReadErrorText);
        Assert.Equal("The machine could not reach the provider. No usage has been read for this account yet.", machine.Spares.Single().ReadErrorText);
    }

    // The whole-phase review's finding 5: the role word of a sign-in is the Gateway's, not the page's.
    [Fact]
    public void Holding_RoleText_IsActiveOrSpare()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));

        var machine = Overview(T0).Machines.Single();

        Assert.Equal((true, "Active"), (machine.Active!.IsActive, machine.Active.RoleText));
        Assert.Equal((false, "Spare"), (machine.Spares.Single().IsActive, machine.Spares.Single().RoleText));
    }

    [Theory]
    [InlineData(0, LicenseAccountStatuses.SignInNeeded)]          // expired at or before now
    [InlineData(1, LicenseAccountStatuses.SignInExpiresSoon)]
    [InlineData(3 * 24 * 3600, LicenseAccountStatuses.SignInExpiresSoon)]   // within the three-day warning
    [InlineData(3 * 24 * 3600 + 1, LicenseAccountStatuses.HasRoom)]
    public void Account_SignInExpiry_ExpiredAtNow_ExpiresSoonWithinThreeDays_FineAfter(int secondsFromNow, string expected)
    {
        Apply(Report("m1", Holding(A, true), Expiring(Holding(B, false), T0.AddSeconds(secondsFromNow))));

        var account = Account(B, T0);

        Assert.Equal(expected, account.Status);
        Assert.Equal(expected switch
        {
            LicenseAccountStatuses.SignInNeeded => LicenseTones.Bad,
            LicenseAccountStatuses.SignInExpiresSoon => LicenseTones.Warning,
            _ => LicenseTones.Good,
        }, account.StatusTone);
    }

    [Fact]
    public void Account_SignInNeeded_NamesTheMachine_AndSaysWhy()
    {
        Apply(Report("m1", Holding(A, true), Expiring(Holding(B, false), T0.AddHours(-2))));

        var account = Account(B, T0);

        Assert.Equal("Sign-in needed", account.StatusText);
        Assert.Equal("The sign-in on m1 has expired. This account needs a new sign-in there.", account.StatusDetail);
    }

    [Theory]
    [InlineData(LicenseReadErrors.Unauthorized, LicenseAccountStatuses.SignInNeeded)]
    [InlineData(LicenseReadErrors.RefreshFailed, LicenseAccountStatuses.SignInNeeded)]
    [InlineData(LicenseReadErrors.RateLimited, LicenseAccountStatuses.HasRoom)]
    [InlineData(LicenseReadErrors.Network, LicenseAccountStatuses.HasRoom)]
    public void Account_OnlyAReadErrorThatMeansTheSignInIsDead_NeedsASignIn(string readError, string expected)
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false, readError: readError)));

        var account = Account(B, T0);

        Assert.Equal(expected, account.Status);
        Assert.Equal(readError, account.ReadError);
        Assert.False(string.IsNullOrWhiteSpace(account.ReadErrorText));
        if (expected == LicenseAccountStatuses.SignInNeeded)
            Assert.Equal("The sign-in on m1 was refused by the provider. This account needs a new sign-in there.", account.StatusDetail);
    }

    [Fact]
    public void Account_SignInNeeded_OutranksStaleAndAtLimit_BecauseOnlyTheOwnerCanFixIt()
    {
        Apply(Report("m1", Holding(A, true), Expiring(Holding(B, false, 99, 99), T0)));

        Assert.Equal(LicenseAccountStatuses.SignInNeeded, Account(B, T0).Status);                 // and at its limit
        Assert.Equal(LicenseAccountStatuses.SignInNeeded, Account(B, T0.AddHours(1)).Status);     // and stale
    }

    [Fact]
    public void Account_AtLimit_OutranksSignInExpiresSoon()
    {
        Apply(Report("m1", Holding(A, true), Expiring(Holding(B, false, 97), T0.AddDays(1))));

        Assert.Equal(LicenseAccountStatuses.AtLimit, Account(B, T0).Status);
    }

    [Fact]
    public void Account_SignInExpiresSoon_SaysWhereAndInHowLong()
    {
        Apply(Report("m1", Expiring(Holding(A, true), T0.AddDays(2).AddHours(3)), Holding(B, false)));

        var account = Account(A, T0);

        Assert.Equal(LicenseAccountStatuses.SignInExpiresSoon, account.Status);
        Assert.Equal("The sign-in on m1 expires in 2 days 3 hours.", account.StatusDetail);
        Assert.True(account.IsInUse);
    }

    [Fact]
    public void Account_NoMachineHoldsItAnyMore_IsNotHeld()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));
        Apply(Report("m1", T0.AddMinutes(2), Holding(A, true, readAt: T0.AddMinutes(2))), seq: 2, at: T0.AddMinutes(2));

        var account = Account(B, T0.AddMinutes(2));

        Assert.Equal((LicenseAccountStatuses.NotHeld, "Not on any machine", LicenseTones.Neutral), (account.Status, account.StatusText, account.StatusTone));
        Assert.Empty(account.SpareOnMachines);
        Assert.False(account.CanSwitchNow);
    }

    [Fact]
    public void Account_InUse_NamesItsMachines_AndHasRoom_NamesWhereItIsHeld()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));
        Apply(Report("m2", Holding(A, true), Holding(B, false)));

        var overview = Overview(T0);
        var a = overview.Accounts.Single(x => x.AccountEmail == A);
        var b = overview.Accounts.Single(x => x.AccountEmail == B);

        Assert.Equal((LicenseAccountStatuses.InUse, "In use on m1 and m2."), (a.Status, a.StatusDetail));
        Assert.Equal(new[] { "m1", "m2" }, a.ActiveOnMachines.Select(m => m.Label));
        Assert.Equal((LicenseAccountStatuses.HasRoom, "Has room", "Not in use. Held as a spare on m1 and m2."), (b.Status, b.StatusText, b.StatusDetail));
        Assert.Equal(new[] { "m1", "m2" }, b.SpareOnMachines.Select(m => m.Label));
        Assert.False(b.IsInUse);
    }

    [Fact]
    public void Account_AFreshReadingWithAWindowMissing_IsUseNotKnown_NeverHasRoom()
    {
        var noWeek = Holding(B, false, 4);
        noWeek.SevenDay = null;
        Apply(Report("m1", Holding(A, true), noWeek));

        var account = Account(B, T0);

        Assert.Equal((LicenseAccountStatuses.UseNotKnown, "Use not known", LicenseTones.Warning), (account.Status, account.StatusText, account.StatusTone));
        Assert.Equal("Its use now is not known: the latest reading carried no number for a window.", account.StatusDetail);
    }

    [Fact]
    public void Account_AReadingTakenBeforeItsWindowReset_IsNotAtLimit_AndItsWindowSaysItHasReset()
    {
        // 99% of a 5-hour window that reset two hours after the reading: read at T0, judged just after the reset.
        Apply(Report("m1", Holding(A, true), Holding(B, false, 99)));
        var afterReset = T0.AddHours(2);

        var atTheReset = _source.ReadPicture(Tenant, afterReset, Array.Empty<LicenseExhaustion>());
        var window = LicenseDisplayFold.Window(atTheReset.Snapshot.Accounts.Single(r => r.AccountEmail == B).FiveHour, afterReset, false);
        var justBefore = LicenseDisplayFold.Window(atTheReset.Snapshot.Accounts.Single(r => r.AccountEmail == B).FiveHour, afterReset - OneSecond, false);

        Assert.Equal((LicenseWindowLevels.ResetSince, "Has reset since this reading"), (window.Level, window.ResetsText));
        Assert.Equal((LicenseWindowLevels.AtLimit, "Resets in less than a minute"), (justBefore.Level, justBefore.ResetsText));
    }

    // ---- a usage window ----

    [Theory]
    [InlineData(96.99, LicenseWindowLevels.Normal, "96.9%")]   // rounded down: never shown as 97% while under it
    [InlineData(97.0, LicenseWindowLevels.AtLimit, "97%")]
    [InlineData(100, LicenseWindowLevels.AtLimit, "100%")]
    [InlineData(0, LicenseWindowLevels.Normal, "0%")]
    public void Window_Level_IsAtLimitFromExactlyTheThreshold(double utilization, string level, string text)
    {
        var window = LicenseDisplayFold.Window(new LicenseWindowReading(utilization, T0.AddHours(1).AddMinutes(48)), T0, false);

        Assert.Equal((level, text, "Resets in 1 hour 48 minutes"), (window.Level, window.UtilizationText, window.ResetsText));
        Assert.Equal(utilization, window.Utilization);
    }

    [Fact]
    public void Window_WithNoResetTime_SaysSo_AndAMissingWindowIsNotKnown()
    {
        var noReset = LicenseDisplayFold.Window(new LicenseWindowReading(98, null), T0, false);
        var missing = LicenseDisplayFold.Window(null, T0, false);

        Assert.Equal((LicenseWindowLevels.AtLimit, "Reset time not known"), (noReset.Level, noReset.ResetsText));
        Assert.Equal((LicenseWindowLevels.NotKnown, "Not known", (double?)null), (missing.Level, missing.UtilizationText, missing.Utilization));
    }

    [Theory]
    [InlineData(-5, "less than a minute")]
    [InlineData(59, "less than a minute")]
    [InlineData(60, "1 minute")]
    [InlineData(119, "1 minute")]
    [InlineData(120, "2 minutes")]
    [InlineData(3599, "59 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(3660, "1 hour 1 minute")]
    [InlineData(6480, "1 hour 48 minutes")]
    [InlineData(86399, "23 hours 59 minutes")]
    [InlineData(86400, "1 day")]
    [InlineData(90000, "1 day 1 hour")]
    [InlineData(183600, "2 days 3 hours")]
    public void Span_SaysALengthOfTimeInWords_RoundedDown(int seconds, string expected)
    {
        Assert.Equal(expected, LicenseDisplayFold.Span(TimeSpan.FromSeconds(seconds)));
    }

    // ---- reading age, extra usage, plan ----

    [Fact]
    public void Account_SaysHowOldItsNumbersAre_AndWhereTheyWereRead()
    {
        var holding = Holding(A, true);
        holding.ExtraUsage = new LicenseExtraUsage { IsEnabled = true, Utilization = 12 };
        holding.Plan = "max";
        Apply(Report("the-windows-machine", holding, Holding(B, false)));

        var a = Account(A, T0.AddMinutes(2));
        var b = Account(B, T0.AddMinutes(2));

        Assert.Equal("Read 2 minutes ago on the-windows-machine", a.ReadAgeText);
        Assert.Equal(T0, a.ReadAtUtc);
        Assert.Equal(DateTimeKind.Utc, a.ReadAtUtc!.Value.Kind);
        Assert.Equal(("max", "Max", "default_claude_max_20x"), (a.Plan, a.PlanText, a.RateLimitTier));
        Assert.Equal((LicenseExtraUsageStates.On, "Paid extra usage is on (12% used)"), (a.ExtraUsage.State, a.ExtraUsage.Text));
        Assert.Equal((LicenseExtraUsageStates.Off, "Paid extra usage is off"), (b.ExtraUsage.State, b.ExtraUsage.Text));
        Assert.Null(a.ReadErrorText);
    }

    // ---- Switch now ----

    [Theory]
    [InlineData(96.99, true)]
    [InlineData(97.0, false)]
    public void SwitchNow_IsOfferedOnlyWhileATargetHasRoom_AndTheSentenceSaysWhyNot(double targetFiveHour, bool offered)
    {
        Apply(Report("m1", Holding(A, true, 98), Holding(B, false, targetFiveHour)));

        var account = Account(A, T0);
        var target = Assert.Single(account.SwitchTargets);

        Assert.Equal(offered, account.CanSwitchNow);
        Assert.Equal((B, offered, offered), (target.AccountEmail, target.CanChoose, target.IsAutomaticChoice));
        if (offered)
        {
            Assert.Equal(B, account.SwitchNowTargetAccountEmail);
            Assert.Equal($"Moves m1 to {B}.", account.SwitchNowText);
            Assert.Null(target.UnavailableText);
        }
        else
        {
            Assert.Null(account.SwitchNowTargetAccountEmail);
            Assert.Equal($"No other account has room for the machines on {A}.", account.SwitchNowText);
            Assert.Equal($"{B} has no room: it is at 97% of its 5-hour window.", target.UnavailableText);
        }
    }

    [Fact]
    public void SwitchNow_ForAnAccountNoMachineIsOn_IsNotOffered_AndSaysSo()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));

        var account = Account(B, T0);

        Assert.False(account.CanSwitchNow);
        Assert.Equal($"No machine on {B} can be switched now: none is on it.", account.SwitchNowText);
        Assert.Empty(account.SwitchTargets);
    }

    [Theory]
    [InlineData(6 * 60, true)]
    [InlineData(6 * 60 + 1, false)]   // the machine's last report is too old: it cannot be sent a switch
    public void SwitchNow_IsNotOfferedOnceTheOnlyMachineIsStale(int secondsSinceItsReport, bool offered)
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false, readAt: T0.AddMinutes(5))));

        var account = Account(A, T0.AddSeconds(secondsSinceItsReport));

        Assert.Equal(offered, account.CanSwitchNow);
        if (!offered)
            // Which of the two it is, is said for each machine - the same sentence the route refuses with.
            Assert.Equal($"No machine on {A} can be switched now: every machine on it is stale or already mid-switch. m1 is not switched: its last report is too old.", account.SwitchNowText);
    }

    [Fact]
    public void SwitchNow_ATargetBarredFromTheAutomaticChoice_CanStillBeChosenByHand()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));
        _store.Bar(Tenant, B, LicenseWindows.FiveHour, T0.AddHours(1), T0);

        var account = Account(A, T0);

        Assert.True(account.CanSwitchNow);
        Assert.Null(account.SwitchNowTargetAccountEmail);
        Assert.Equal((true, false), (account.SwitchTargets.Single().CanChoose, account.SwitchTargets.Single().IsAutomaticChoice));
        Assert.Equal($"No other account has room for the machines on {A}. A target can still be chosen by hand.", account.SwitchNowText);
    }

    [Fact]
    public void SwitchNow_SaysWhichMachineIsLeftOut_WhenOneIsMidSwitch()
    {
        var first = Apply(Report("m1", Holding(A, true), Holding(B, false)));
        Apply(Report("m2", Holding(A, true), Holding(B, false)));
        _store.CreatePendingSwitch(Tenant, first, A, B, LicenseSwitchReasons.Threshold, T0);

        var account = Account(A, T0);

        Assert.True(account.CanSwitchNow);
        Assert.Equal($"Moves m2 to {B}. m1 is not switched: a switch sent to it has not finished.", account.SwitchNowText);
    }

    // ---- a machine ----

    [Theory]
    [InlineData(6 * 60, LicenseMachineStatuses.Reporting, "Reporting", false)]
    [InlineData(6 * 60 + 1, LicenseMachineStatuses.NotReporting, "Not reporting", true)]
    public void Machine_IsNotReporting_OneSecondPastSixMinutesWithoutAReport(int seconds, string status, string text, bool stale)
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false)));

        var machine = Overview(T0.AddSeconds(seconds)).Machines.Single();

        Assert.Equal((status, text, stale), (machine.Status, machine.StatusText, machine.IsStale));
        Assert.Equal(stale ? LicenseTones.Warning : LicenseTones.Good, machine.StatusTone);
        Assert.Equal("Last report 6 minutes ago", machine.LastReportText);
        Assert.Equal(T0, machine.LastReportAtUtc);
    }

    [Fact]
    public void Machine_ListsItsActiveSignInAndItsSpares_EachWithItsExpiryVerdictInWords()
    {
        var active = Expiring(Holding(A, true), T0.AddDays(20));
        active.SignedInAtUtc = T0.AddDays(-10);
        Apply(Report("the-windows-machine",
            active,
            Expiring(Holding(B, false), T0.AddDays(3)),
            Expiring(Holding(C, false, readError: LicenseReadErrors.Unauthorized), T0.AddMinutes(-90)),
            Holding("account-d@example.com", false)));

        var machine = Overview(T0).Machines.Single();

        Assert.Equal(("the-windows-machine", "owner", A), (machine.MachineName, machine.OsUser, machine.ActiveAccountEmail));
        Assert.Equal((A, true, "fine", "Sign-in expires in 20 days", LicenseTones.Good, "Signed in 10 days ago"),
            (machine.Active!.AccountEmail, machine.Active.IsActive, machine.Active.ExpiryVerdict, machine.Active.ExpiryText, machine.Active.ExpiryTone, machine.Active.SignedInText));
        Assert.Equal(T0.AddDays(20), machine.Active.SignInExpiresAtUtc);
        Assert.Equal(new[] { B, C, "account-d@example.com" }, machine.Spares.Select(s => s.AccountEmail));
        Assert.Equal(("expires soon", "Sign-in expires in 3 days", LicenseTones.Warning), (machine.Spares[0].ExpiryVerdict, machine.Spares[0].ExpiryText, machine.Spares[0].ExpiryTone));
        Assert.Equal(("expired", "Sign-in expired 1 hour 30 minutes ago", LicenseTones.Bad), (machine.Spares[1].ExpiryVerdict, machine.Spares[1].ExpiryText, machine.Spares[1].ExpiryTone));
        Assert.Equal(LicenseReadErrors.Unauthorized, machine.Spares[1].ReadError);
        Assert.Equal("The provider refused this sign-in. It needs a new sign-in.", machine.Spares[1].ReadErrorText);
        Assert.Equal(("not known", "Sign-in expiry not known", LicenseTones.Neutral, "Sign-in date not known"),
            (machine.Spares[2].ExpiryVerdict, machine.Spares[2].ExpiryText, machine.Spares[2].ExpiryTone, machine.Spares[2].SignedInText));
        Assert.All(machine.Spares, s => Assert.False(s.IsActive));
    }

    [Fact]
    public void Machine_WithAnOpenSwitchRow_IsSwitching_AndSaysWhereItStands()
    {
        var id = Apply(Report("m1", Holding(A, true), Holding(B, false)));
        var row = _store.CreatePendingSwitch(Tenant, id, A, B, LicenseSwitchReasons.Threshold, T0)!;

        var sending = Overview(T0).Machines.Single();
        _store.TryMark(Tenant, row.SwitchId!, LicenseLogStates.Pending, LicenseGatewayReasonCodes.DirectorOffline, null, T0);
        var offline = Overview(T0).Machines.Single();
        _store.TryMark(Tenant, row.SwitchId!, LicenseLogStates.Unknown, null, null, T0);
        var noAnswer = Overview(T0).Machines.Single();
        _store.TryMark(Tenant, row.SwitchId!, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorError, "It failed.", T0);
        var settled = Overview(T0).Machines.Single();

        Assert.Equal((LicenseMachineStatuses.Switching, "Switching"), (sending.Status, sending.StatusText));
        Assert.Equal((row.SwitchId, A, B), (sending.SwitchInProgress!.SwitchId, sending.SwitchInProgress.FromAccountEmail, sending.SwitchInProgress.ToAccountEmail));
        Assert.Equal($"Switching from {A} to {B}: the command is being sent.", sending.SwitchInProgress.Text);
        Assert.Equal($"Waiting to switch from {A} to {B}: the machine's Director is not connected.", offline.SwitchInProgress!.Text);
        Assert.Equal($"Switching from {A} to {B}: the command was sent and no answer has come back yet.", noAnswer.SwitchInProgress!.Text);
        Assert.Null(settled.SwitchInProgress);
        Assert.Equal(LicenseMachineStatuses.Reporting, settled.Status);
    }

    [Fact]
    public void Machine_ThatReportsASwitchInProgress_IsSwitching_AndOneSignedInToNothingIsSignedOut()
    {
        var busy = Report("m1", Holding(A, true), Holding(B, false));
        busy.SwitchInProgressId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40";
        Apply(busy);
        Apply(Report("m2"));

        var machines = Overview(T0).Machines;

        Assert.Equal(LicenseMachineStatuses.Switching, machines[0].Status);
        Assert.Equal(("6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40", "The machine reports a switch in progress."), (machines[0].SwitchInProgress!.SwitchId, machines[0].SwitchInProgress!.Text));
        Assert.Equal((LicenseMachineStatuses.SignedOut, "Signed out", (string?)null), (machines[1].Status, machines[1].StatusText, machines[1].ActiveAccountEmail));
        Assert.Null(machines[1].Active);
        Assert.Empty(machines[1].Spares);
    }

    // ---- the empty account ----

    [Fact]
    public void Overview_BeforeAnyMachineHasReported_IsEmpty_WithTheSentenceSayingSo()
    {
        var overview = Overview(T0);

        Assert.True(overview.IsEmpty);
        Assert.Equal(LicenseDisplayFold.NothingReportedText, overview.EmptyText);
        Assert.Empty(overview.Accounts);
        Assert.Empty(overview.Machines);
        Assert.Equal(T0, overview.AsOfUtc);
    }

    [Fact]
    public void Overview_OnceAMachineHasReported_IsNotEmpty_EvenWhenItHoldsNoSignIn()
    {
        Apply(Report("m1"));

        var overview = Overview(T0);

        Assert.False(overview.IsEmpty);
        Assert.Null(overview.EmptyText);
        Assert.Single(overview.Machines);
    }

    // ---- the forecast ----

    private LicenseForecastResponse Forecast(DateTime now, LicenseUsageHistory history)
    {
        var picture = Picture(now);
        return LicenseDisplayFold.Forecast(picture, history.Read(Tenant, now));
    }

    // The whole-phase review's finding 1: right after a Switch now with every Director offline, the card offered
    // Switch now while the schedule said no machine could be switched and that a switch "sent to it" had not
    // finished - two routes folding one machine from two pictures. A row the Gateway knows it never sent must read
    // the same on both: the machine can be switched, and the schedule says where it goes next.
    [Fact]
    public void ForecastAndOverview_ASwitchKnownNeverSent_AreFoldedFromOnePicture_SoTheThreeSentencesAgree()
    {
        var history = new LicenseUsageHistory();
        history.Record(Tenant, Report("m1", Holding(A, true, 50.25, readAt: T0.AddMinutes(-20))), TimeSpan.Zero, T0);
        history.Record(Tenant, Report("m1", Holding(A, true, 60.25)), TimeSpan.Zero, T0);
        var mac = Apply(Report("m1", Holding(A, true, 60.25), Holding(B, false, 4, 22)));
        var windows = Apply(Report("m2", Holding(A, true, 60.25), Holding(B, false, 4, 22)), seq: 2);
        foreach (var machine in new[] { mac, windows })
        {
            var row = _store.CreatePendingSwitch(Tenant, machine, A, B, LicenseSwitchReasons.Manual, T0)!;
            Assert.True(_store.TryMark(Tenant, row.SwitchId!, LicenseLogStates.Pending, LicenseGatewayReasonCodes.DirectorOffline, null, T0));
        }

        var overview = Overview(T0);
        var forecast = Forecast(T0, history);

        // The card: Switch now is offered, and it moves both machines to B.
        var card = overview.Accounts.Single(a => a.AccountEmail == A);
        Assert.True(card.CanSwitchNow);
        Assert.Equal(B, card.SwitchNowTargetAccountEmail);
        Assert.Equal($"Moves m1 and m2 to {B}.", card.SwitchNowText);
        // The schedule's page sentence: the same target, and never "nothing would be switched".
        Assert.Equal($"{A} is expected to reach 97% of its 5-hour window in about 1 hour 13 minutes; its machines then move to {B}, the account the Gateway would choose today.",
            forecast.NextSwitchText);
        Assert.Equal(new[] { "m1", "m2" }, forecast.NextSwitch!.Machines.Select(m => m.Label));
        Assert.Equal($"Next: {B}, in about 1 hour 13 minutes.", forecast.Accounts.Single(a => a.AccountEmail == A).NextSwitchText);
        // Each schedule row: where it goes next, beside the overview's true "waiting" sentence - never "sent to it".
        Assert.All(forecast.Machines, m => Assert.Equal((B, $"Next: {B}, in about 1 hour 13 minutes."), (m.NextAccountEmail, m.Text)));
        Assert.All(overview.Machines, m => Assert.Equal($"Waiting to switch from {A} to {B}: the machine's Director is not connected.", m.SwitchInProgress!.Text));
    }

    // The other side of the same picture: a switch that WAS sent and has no answer holds its machine back on both
    // routes, so the card offers nothing and the schedule says why.
    [Fact]
    public void ForecastAndOverview_ASwitchSentWithNoAnswer_HoldsTheMachineBackOnBoth()
    {
        var id = Apply(Report("m1", Holding(A, true, 60.25), Holding(B, false, 4, 22)));
        var row = _store.CreatePendingSwitch(Tenant, id, A, B, LicenseSwitchReasons.Manual, T0)!;
        Assert.True(_store.TryMark(Tenant, row.SwitchId!, LicenseLogStates.Unknown, null, null, T0));

        var card = Account(A, T0);
        var machine = Forecast(T0, new LicenseUsageHistory()).Machines.Single();

        Assert.False(card.CanSwitchNow);
        Assert.Equal("Would not be switched now: a switch sent to it has not finished.", machine.Text);
        Assert.Null(machine.NextAccountEmail);
    }

    [Fact]
    public void Forecast_AnAccountInUseThatIsRising_SaysWhenItReachesTheThreshold_AndWhereItsMachinesGoNext()
    {
        // 50.25% twenty minutes ago and 60.25% now is 30% an hour: 36.75% to go is 73.5 minutes.
        var history = new LicenseUsageHistory();
        history.Record(Tenant, Report("m1", Holding(A, true, 50.25, readAt: T0.AddMinutes(-20))), TimeSpan.Zero, T0);
        history.Record(Tenant, Report("m1", Holding(A, true, 60.25)), TimeSpan.Zero, T0);
        Apply(Report("m1", Holding(A, true, 60.25), Holding(B, false, 4, 22)));

        var forecast = Forecast(T0, history);
        var a = forecast.Accounts.Single(x => x.AccountEmail == A);
        var b = forecast.Accounts.Single(x => x.AccountEmail == B);

        Assert.False(forecast.IsEmpty);
        Assert.Equal((A, B, LicenseWindowNames.FiveHour, "5-hour window"),
            (forecast.NextSwitch!.FromAccountEmail, forecast.NextSwitch.TargetAccountEmail, forecast.NextSwitch.Window, forecast.NextSwitch.WindowText));
        Assert.Equal(new[] { "m1" }, forecast.NextSwitch.Machines.Select(m => m.Label));
        Assert.Equal(T0.AddMinutes(73.5), forecast.NextSwitch.ExpectedAtUtc, TimeSpan.FromSeconds(1));
        Assert.Equal($"{A} is expected to reach 97% of its 5-hour window in about 1 hour 13 minutes; its machines then move to {B}, the account the Gateway would choose today.",
            forecast.NextSwitchText);

        Assert.True(a.IsInUse);
        Assert.Equal("Expected to reach 97% of its 5-hour window in about 1 hour 13 minutes.", a.ExpectedText);
        Assert.Equal((B, $"Next: {B}, in about 1 hour 13 minutes."), (a.NextSwitchTargetAccountEmail, a.NextSwitchText));
        Assert.Equal(("30% an hour", "Expected to reach 97% in about 1 hour 13 minutes."), (a.FiveHour.RateText, a.FiveHour.ExpectedText));
        // The window's own words, the same ones the overview gives, so the schedule compares nothing itself.
        var overviewWindow = Account(A, T0).FiveHour;
        Assert.Equal(("60.2%", LicenseWindowLevels.Normal, "Resets in 2 hours", T0.AddHours(2)),
            (a.FiveHour.UtilizationText, a.FiveHour.Level, a.FiveHour.ResetsText, a.FiveHour.ResetsAtUtc));
        Assert.Equal((overviewWindow.UtilizationText, overviewWindow.Level, overviewWindow.ResetsText, overviewWindow.ResetsAtUtc, overviewWindow.Utilization),
            (a.FiveHour.UtilizationText, a.FiveHour.Level, a.FiveHour.ResetsText, a.FiveHour.ResetsAtUtc, a.FiveHour.Utilization));
        Assert.Equal(("Not rising", "Not rising, so not expected to reach 97%."), (a.SevenDay.RateText, a.SevenDay.ExpectedText));
        Assert.Equal(("No rate yet", "No forecast yet: there are not enough readings to show a rate."), (b.FiveHour.RateText, b.FiveHour.ExpectedText));

        Assert.False(b.IsInUse);
        Assert.Equal(("Not expected to reach its limit.", "Not in use, so there is nothing to switch."), (b.ExpectedText, b.NextSwitchText));

        var machine = forecast.Machines.Single();
        Assert.Equal(("m1", A, B, $"Next: {B}, in about 1 hour 13 minutes."), (machine.MachineName, machine.ActiveAccountEmail, machine.NextAccountEmail, machine.Text));
        Assert.Equal(forecast.NextSwitch.ExpectedAtUtc, machine.ExpectedSwitchAtUtc);
    }

    [Fact]
    public void Forecast_WithNoRate_ExpectsNoSwitch_ButStillSaysWhichAccountIsNext()
    {
        Apply(Report("m1", Holding(A, true, 60), Holding(B, false)));

        var forecast = Forecast(T0, new LicenseUsageHistory());

        Assert.Null(forecast.NextSwitch);
        Assert.Equal(LicenseDisplayFold.NoSwitchExpectedText, forecast.NextSwitchText);
        var a = forecast.Accounts.Single(x => x.AccountEmail == A);
        Assert.Equal("Not expected to reach its limit.", a.ExpectedText);
        Assert.Equal($"Next: {B}, when this account reaches its limit. No switch is expected yet.", a.NextSwitchText);
        Assert.Equal($"Next: {B}, when {A} reaches its limit. No switch is expected yet.", forecast.Machines.Single().Text);
        Assert.Null(forecast.Machines.Single().ExpectedSwitchAtUtc);
    }

    [Fact]
    public void Forecast_AWindowAtItsLimit_OrOfAStaleReading_SaysSoInItsLevel()
    {
        Apply(Report("m1", Holding(A, true, 98, 20), Holding(B, false)));

        var fresh = Forecast(T0, new LicenseUsageHistory()).Accounts.Single(x => x.AccountEmail == A);
        var stale = Forecast(T0.AddMinutes(6) + OneSecond, new LicenseUsageHistory()).Accounts.Single(x => x.AccountEmail == A);

        Assert.Equal((LicenseWindowLevels.AtLimit, LicenseWindowLevels.Normal), (fresh.FiveHour.Level, fresh.SevenDay.Level));
        Assert.Equal((LicenseWindowLevels.Stale, LicenseWindowLevels.Stale), (stale.FiveHour.Level, stale.SevenDay.Level));
    }

    [Fact]
    public void Forecast_AWindowRisingSoSlowlyItWouldTakeLongerThanAnyWindowLasts_IsNotExpectedToReachItsLimit_AndNothingThrows()
    {
        // The reviewer's case: two readings 1.9 hours apart that differ by a millionth of a percent. The time to the
        // threshold at that rate is past the end of the calendar.
        var history = new LicenseUsageHistory();
        history.Record(Tenant, Report("m1", Holding(A, true, 10, readAt: T0.AddHours(-1.9))), TimeSpan.Zero, T0);
        history.Record(Tenant, Report("m1", Holding(A, true, 10.000001)), TimeSpan.Zero, T0);
        Apply(Report("m1", Holding(A, true, 10.000001), Holding(B, false)));

        var forecast = Forecast(T0, history);
        var a = forecast.Accounts.Single(x => x.AccountEmail == A);

        Assert.Null(forecast.NextSwitch);
        Assert.Null(a.FiveHour.ExpectedAtLimitUtc);
        Assert.Equal("Not expected to reach 97% before it resets.", a.FiveHour.ExpectedText);
        Assert.Equal("Not expected to reach its limit.", a.ExpectedText);
    }

    [Fact]
    public void Forecast_AnAccountAtItsLimitWithNoTarget_SaysNoAccountHasRoom()
    {
        Apply(Report("m1", Holding(A, true, 98), Holding(B, false, 99)));

        var forecast = Forecast(T0, new LicenseUsageHistory());
        var a = forecast.Accounts.Single(x => x.AccountEmail == A);

        Assert.Null(forecast.NextSwitch!.TargetAccountEmail);
        Assert.Equal($"{A} has reached 97% of its 5-hour window. No other account has room for the machines on {A}. Nothing would be switched.", forecast.NextSwitchText);
        Assert.Equal("Has reached 97% of its 5-hour window.", a.ExpectedText);
        Assert.Equal($"No other account has room for the machines on {A}. It is at its limit now.", a.NextSwitchText);
        Assert.Equal("Has reached 97%.", a.FiveHour.ExpectedText);
        Assert.Equal($"No other account has room for the machines on {A}.", forecast.Machines.Single().Text);
        Assert.Null(forecast.Machines.Single().NextAccountEmail);
    }

    [Fact]
    public void Forecast_WhenTheOnlyMachineIsMidSwitch_TheNextSwitchSaysThat_NotThatNoAccountHasRoom()
    {
        var machine = Apply(Report("m1", Holding(A, true, 98), Holding(B, false)));
        _store.CreatePendingSwitch(Tenant, machine, A, B, LicenseSwitchReasons.Threshold, T0);

        var forecast = Forecast(T0, new LicenseUsageHistory());

        Assert.Null(forecast.NextSwitch!.TargetAccountEmail);
        Assert.Empty(forecast.NextSwitch.Machines);
        Assert.Equal($"{A} has reached 97% of its 5-hour window. No machine on {A} can be switched now: every machine on it is stale or already mid-switch. Nothing would be switched.",
            forecast.NextSwitchText);
    }

    [Theory]
    [InlineData(6 * 60, false)]
    [InlineData(6 * 60 + 1, true)]
    public void Forecast_FromAStaleReading_ForecastsNothing_AndSaysTheReadingIsTooOld(int secondsOld, bool stale)
    {
        Apply(Report("m1", Holding(A, true, 98), Holding(B, false, readAt: T0.AddMinutes(5))));

        var a = Forecast(T0.AddSeconds(secondsOld), new LicenseUsageHistory()).Accounts.Single(x => x.AccountEmail == A);

        Assert.Equal(stale, a.IsStale);
        Assert.Equal(stale ? "No forecast: the latest reading is too old." : "Has reached 97% of its 5-hour window.", a.ExpectedText);
        Assert.Equal(stale ? "No forecast: the latest reading is too old." : "Has reached 97%.", a.FiveHour.ExpectedText);
    }

    [Fact]
    public void Forecast_AMachineThatCannotBeSwitched_SaysWhy_AndAnEmptyAccountSaysNothingHasReported()
    {
        Assert.True(Forecast(T0, new LicenseUsageHistory()).IsEmpty);
        Assert.Equal(LicenseDisplayFold.NothingReportedText, Forecast(T0, new LicenseUsageHistory()).EmptyText);

        var first = Apply(Report("m1", Holding(A, true), Holding(B, false)));
        Apply(Report("m2", Holding(A, true), Holding(B, false)));
        Apply(Report("m3"));
        _store.CreatePendingSwitch(Tenant, first, A, B, LicenseSwitchReasons.Threshold, T0);

        var machines = Forecast(T0, new LicenseUsageHistory()).Machines;

        Assert.Equal("Would not be switched now: a switch sent to it has not finished.", machines[0].Text);
        Assert.Equal(B, machines[1].NextAccountEmail);
        Assert.Equal("Signed in to no account, so there is nothing to switch.", machines[2].Text);
    }

    // ---- a log row ----

    private static LicenseSwitchLogEntity Row(string kind, string state, string? reasonCode = null, string? message = null, string? reason = LicenseSwitchReasons.Threshold)
        => new()
        {
            Kind = kind,
            SwitchId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40",
            MachineName = "m1",
            OsUser = "owner",
            FromAccountEmail = A,
            ToAccountEmail = B,
            Reason = reason,
            State = state,
            ReasonCode = reasonCode,
            Message = message,
            CreatedAtUtc = DateTime.SpecifyKind(T0, DateTimeKind.Unspecified),
        };

    [Theory]
    [InlineData(LicenseLogStates.Pending, null, "Waiting to be sent", LicenseTones.Neutral, true, "m1 is switching from account-a@example.com to account-b@example.com.")]
    [InlineData(LicenseLogStates.Pending, LicenseGatewayReasonCodes.DirectorOffline, "Waiting for the machine", LicenseTones.Warning, true, "m1 is switching from account-a@example.com to account-b@example.com.")]
    [InlineData(LicenseLogStates.Unknown, null, "Sent, no answer yet", LicenseTones.Warning, true, "m1 is switching from account-a@example.com to account-b@example.com.")]
    [InlineData(LicenseLogStates.Switched, null, "Switched", LicenseTones.Good, false, "m1 switched from account-a@example.com to account-b@example.com.")]
    [InlineData(LicenseLogStates.AlreadyActive, null, "Already on the target", LicenseTones.Good, false, "m1 was already on account-b@example.com.")]
    [InlineData(LicenseLogStates.Failed, LicenseSwitchReasonCodes.SpareNotHeld, "Failed", LicenseTones.Bad, false, "m1 was not switched from account-a@example.com to account-b@example.com.")]
    public void LogEntry_ASwitchRow_SaysItsStateInWords(string state, string? reasonCode, string stateText, string tone, bool open, string summary)
    {
        var entry = LicenseDisplayFold.LogEntry(Row(LicenseLogKinds.Switch, state, reasonCode));

        Assert.Equal(("Switch", state, stateText, tone, open, summary), (entry.KindText, entry.State, entry.StateText, entry.StateTone, entry.IsOpen, entry.Summary));
        Assert.Equal(("threshold", "Threshold reached"), (entry.Why, entry.WhyText));
        Assert.Equal(reasonCode, entry.ReasonCode);
        Assert.Equal(reasonCode is null, entry.ReasonText is null);
        Assert.Equal(DateTimeKind.Utc, entry.AtUtc.Kind);
        Assert.Equal(T0, entry.AtUtc);
    }

    [Fact]
    public void LogEntry_EveryReasonWordTheContractNames_HasASentence_AndOneItDoesNotKnowIsShownAsItIs()
    {
        var known = new[]
        {
            LicenseSwitchReasonCodes.NotKeyHolder, LicenseSwitchReasonCodes.SwitchInProgress, LicenseSwitchReasonCodes.Expired,
            LicenseSwitchReasonCodes.ActiveAccountChanged, LicenseSwitchReasonCodes.SpareNotHeld, LicenseSwitchReasonCodes.SpareNotUsable,
            LicenseSwitchReasonCodes.ExchangeFailedRestored, LicenseSwitchReasonCodes.ExchangeFailedUnknown, LicenseSwitchReasonCodes.VerifyFailed,
            LicenseGatewayReasonCodes.DirectorOffline, LicenseGatewayReasonCodes.DirectorError,
        };

        Assert.All(known, word =>
        {
            var text = LicenseDisplayFold.ReasonText(word)!;
            Assert.NotEqual(word, text);
            Assert.EndsWith(".", text);
        });
        Assert.Equal(known.Length, known.Select(LicenseDisplayFold.ReasonText).Distinct().Count());
        Assert.Equal("a-later-reason", LicenseDisplayFold.ReasonText("a-later-reason"));
        Assert.Null(LicenseDisplayFold.ReasonText(null));
    }

    [Fact]
    public void LogEntry_ASwitchRowInAStateThisBuildDoesNotKnow_NeverSaysItIsSwitching()
    {
        var withMessage = LicenseDisplayFold.LogEntry(Row(LicenseLogKinds.Switch, "cancelled", message: "The switch was cancelled."));
        var bare = LicenseDisplayFold.LogEntry(Row(LicenseLogKinds.Switch, "cancelled"));

        Assert.Equal(("cancelled", false, "The switch was cancelled."), (withMessage.StateText, withMessage.IsOpen, withMessage.Summary));
        Assert.Equal("m1: a switch from account-a@example.com to account-b@example.com, in a state this Gateway does not know (cancelled).", bare.Summary);
        Assert.DoesNotContain("is switching", withMessage.Summary + bare.Summary);
    }

    [Theory]
    [InlineData(LicenseLogKinds.Switch, LicenseLogStates.Switched, true, "The new sign-in answered a usage read.")]
    [InlineData(LicenseLogKinds.Switch, LicenseLogStates.Switched, false, "The new sign-in did not answer a usage read.")]
    [InlineData(LicenseLogKinds.Switch, LicenseLogStates.AlreadyActive, true, "The new sign-in answered a usage read.")]
    [InlineData(LicenseLogKinds.Switch, LicenseLogStates.Switched, null, null)]
    [InlineData(LicenseLogKinds.Switch, LicenseLogStates.Failed, false, null)]    // refused or failed: nothing to confirm
    [InlineData(LicenseLogKinds.Switch, LicenseLogStates.Pending, null, null)]
    [InlineData(LicenseLogKinds.Switch, LicenseLogStates.Unknown, false, null)]
    [InlineData(LicenseLogKinds.SpareAdded, LicenseLogStates.Recorded, false, null)]
    [InlineData("a-kind-from-a-later-build", LicenseLogStates.Switched, true, null)]
    public void LogEntry_SaysWhetherTheSwitchWasConfirmed_OnlyOnARowWhoseSwitchWasMade(string kind, string state, bool? verified, string? text)
    {
        var row = Row(kind, state);
        row.VerifiedByUsageRead = verified;

        Assert.Equal(text, LicenseDisplayFold.LogEntry(row).VerifiedText);
    }

    [Fact]
    public void LogEntries_CallAMachineWhatTheOverviewCallsIt_AndARowAboutNoMachineHasNoIdentifierAndNoLabel()
    {
        var id = Guid.NewGuid();
        var alone = Row(LicenseLogKinds.Switch, LicenseLogStates.Switched);
        alone.MachineId = id;
        var shared = Row(LicenseLogKinds.Switch, LicenseLogStates.Failed);
        shared.MachineName = "m2";
        var noMachine = Row(LicenseDisplayFold.NoRoomKind, "open", message: "No account had room.", reason: null);
        noMachine.MachineName = "";
        noMachine.OsUser = "";
        // m2 is shared by two users - one of them known only from the account's machines, not from any row here.
        var machines = new[] { ("m1", "owner"), ("m2", "owner"), ("M2", "second") };

        var entries = LicenseDisplayFold.LogEntries([alone, shared, noMachine], machines);

        Assert.Equal((id.ToString("N"), "m1", $"m1 switched from {A} to {B}."), (entries[0].MachineId, entries[0].Label, entries[0].Summary));
        Assert.Equal(("m2 (owner)", $"m2 (owner) was not switched from {A} to {B}."), (entries[1].Label, entries[1].Summary));
        Assert.Equal((null, "", "", "No account had room."), (entries[2].MachineId, entries[2].Label, entries[2].MachineName, entries[2].Summary));
    }

    [Fact]
    public void LogEntries_TwoUsersOfAMachineNoLongerListed_AreStillToldApart_FromTheRowsThemselves()
    {
        var first = Row(LicenseLogKinds.Switch, LicenseLogStates.Switched);
        var second = Row(LicenseLogKinds.Switch, LicenseLogStates.Switched);
        second.OsUser = "second";

        var entries = LicenseDisplayFold.LogEntries([first, second], []);

        Assert.Equal(new[] { "m1 (owner)", "m1 (second)" }, entries.Select(e => e.Label));
    }

    [Fact]
    public void LogEntry_ASwitchRowWithNoStateWord_SaysNoStateWasRecorded_NotThatRecordedIsAnUnknownState()
    {
        var entry = LicenseDisplayFold.LogEntry(Row(LicenseLogKinds.Switch, ""));

        Assert.Equal($"m1: a switch from {A} to {B}, with no state recorded.", entry.Summary);
    }

    [Fact]
    public void LogEntry_ASwitchByHand_SaysSo_AndCarriesItsTimings()
    {
        var row = Row(LicenseLogKinds.Switch, LicenseLogStates.Switched, message: "Switched from account-a@example.com to account-b@example.com.", reason: LicenseSwitchReasons.Manual);
        row.SendCount = 2;
        row.LastSentAtUtc = T0.AddSeconds(1);
        row.DirectorStartedAtUtc = T0.AddSeconds(1);
        row.DirectorFinishedAtUtc = T0.AddSeconds(3);
        row.ExchangeMilliseconds = 41;
        row.FinishedAtUtc = T0.AddSeconds(4);
        row.VerifiedByUsageRead = true;

        var entry = LicenseDisplayFold.LogEntry(row);

        Assert.Equal(("manual", "By hand"), (entry.Why, entry.WhyText));
        Assert.Equal("Took 2.0 seconds on the machine (the exchange itself 41 milliseconds)", entry.TimingText);
        Assert.Equal((2, T0.AddSeconds(1), T0.AddSeconds(1), T0.AddSeconds(3), 41, T0.AddSeconds(4), true),
            (entry.SendCount, entry.LastSentAtUtc, entry.StartedOnMachineAtUtc, entry.FinishedOnMachineAtUtc, entry.ExchangeMilliseconds, entry.FinishedAtUtc, entry.VerifiedByUsageRead));
        Assert.Equal("Switched from account-a@example.com to account-b@example.com.", entry.Message);
        Assert.Equal(32, entry.EntryId.Length);
    }

    [Fact]
    public void LogEntry_ARowThatNeverReachedTheMachine_OrWasRefusedAtOnce_HasNoTimingText()
    {
        var refusedAtOnce = Row(LicenseLogKinds.Switch, LicenseLogStates.Failed, LicenseSwitchReasonCodes.SpareNotHeld);
        refusedAtOnce.DirectorStartedAtUtc = T0;
        refusedAtOnce.DirectorFinishedAtUtc = T0;
        var quick = Row(LicenseLogKinds.Switch, LicenseLogStates.Switched);
        quick.DirectorStartedAtUtc = T0;
        quick.DirectorFinishedAtUtc = T0.AddMilliseconds(250);

        Assert.Null(LicenseDisplayFold.LogEntry(Row(LicenseLogKinds.Switch, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorOffline)).TimingText);
        Assert.Null(LicenseDisplayFold.LogEntry(refusedAtOnce).TimingText);
        Assert.Equal("Took 250 milliseconds on the machine", LicenseDisplayFold.LogEntry(quick).TimingText);
    }

    [Theory]
    [InlineData(LicenseLogKinds.SpareAdded, "Spare added")]
    [InlineData(LicenseLogKinds.SpareRemoved, "Spare removed")]
    public void LogEntry_ASpareRow_IsItsMessage(string kind, string kindText)
    {
        var entry = LicenseDisplayFold.LogEntry(Row(kind, LicenseLogStates.Recorded, message: "The machine's report shows a new spare for account-b@example.com.", reason: null));

        Assert.Equal((kindText, "Recorded", LicenseTones.Neutral, false), (entry.KindText, entry.StateText, entry.StateTone, entry.IsOpen));
        Assert.Equal("The machine's report shows a new spare for account-b@example.com.", entry.Summary);
        Assert.Equal((null, ""), (entry.Why, entry.WhyText));
    }

    [Fact]
    public void LogEntry_ANoRoomRow_IsItsMessage_AndIsOpenUntilItIsFinished()
    {
        var row = Row(LicenseDisplayFold.NoRoomKind, "open", message: "No account had room for the machines on account-a@example.com. You were emailed.", reason: null);
        row.MachineName = "";
        row.ToAccountEmail = "";

        var open = LicenseDisplayFold.LogEntry(row);
        row.FinishedAtUtc = T0.AddHours(2);
        var ended = LicenseDisplayFold.LogEntry(row);

        Assert.Equal(("no-room", "Nothing was switched", true), (open.Kind, open.KindText, open.IsOpen));
        Assert.Equal("No account had room for the machines on account-a@example.com. You were emailed.", open.Summary);
        Assert.Equal("open", open.StateText);   // a state word this build does not know is shown as it is
        Assert.False(ended.IsOpen);
    }

    [Fact]
    public void LogEntry_AKindThisBuildDoesNotKnow_IsRenderedAsItsMessage_AndNeverFails()
    {
        var withMessage = LicenseDisplayFold.LogEntry(Row("a-kind-from-a-later-build", "a-later-state", "a-later-reason", "Something a later build wrote.", reason: "a-later-why"));
        var bare = LicenseDisplayFold.LogEntry(new LicenseSwitchLogEntity { Kind = "a-kind-from-a-later-build" });

        Assert.Equal(("a-kind-from-a-later-build", "Note", "Something a later build wrote."), (withMessage.Kind, withMessage.KindText, withMessage.Summary));
        Assert.Equal(("a-later-state", LicenseTones.Neutral, false), (withMessage.StateText, withMessage.StateTone, withMessage.IsOpen));
        Assert.Equal(("a-later-reason", "a-later-why"), (withMessage.ReasonText, withMessage.WhyText));
        Assert.Equal(("Note", "Note", "Recorded", ""), (bare.KindText, bare.Summary, bare.StateText, bare.MachineName));
        Assert.Null(bare.Message);
    }
}
