using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// A MACHINE NEEDS A READY NEXT LICENSE ONLY WHEN IT IS RUNNING OUT (the owner's ruling of 7 November 2026). One test per
/// numbered rule, then READY (its room lasts past the reset, and it fills before), then RECOMMENDED NEXT (the most room,
/// and the tie broken by the sooner reset). The facts go in as usage reports applied to the real store on a throwaway
/// SQLite file and come back through <see cref="LicenseSnapshotSource"/>; the pace is the test's own.
/// </summary>
public sealed class LicenseNextLicenseFoldTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _source;

    private static readonly TenantId Tenant = new("acct-a");

    public LicenseNextLicenseFoldTests()
    {
        _store = new LicenseStore(_harness.Open());
        _source = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
    }

    public void Dispose() => _harness.Dispose();

    private void Apply(LicenseUsageReport report)
        => _store.ApplyReport(Tenant, "dir-" + report.MachineName, 1, report, T0, TimeSpan.Zero);

    private LicensePicture Picture() => _source.ReadPicture(Tenant, T0, Array.Empty<LicenseExhaustion>());

    private LicenseNextLicense Verdict(string machine, double? pacePerHour = null)
        => LicenseNextLicenseFold.Judge(Picture(), _ => pacePerHour).Single(v => v.MachineName == machine);

    private static LicenseHolding WeekResets(LicenseHolding holding, DateTime resetsAtUtc)
    {
        holding.SevenDay!.ResetsAtUtc = resetsAtUtc;
        return holding;
    }

    // ----- the rules -----

    // Rule 1: under 80% in both windows - no problem, whatever it does not hold.
    [Fact]
    public void Rule1_Under80_NotNeeded_WhateverItDoesNotHold()
    {
        Apply(Report("m1", Holding(A, true, fiveHour: 79, sevenDay: 79)));
        Apply(Report("m2", Holding(B, true, sevenDay: 5)));

        var verdict = Verdict("m1");

        Assert.Equal((LicenseNextLicenseStates.NotNeeded, LicenseTones.Good, false), (verdict.State, verdict.Tone, verdict.IsNeeded));
        Assert.Null(verdict.RecommendedAccountEmail);
    }

    // Rule 2: 80% or more and a ready next license held - no problem.
    [Fact]
    public void Rule2_At80_WithAReadyNextLicense_IsReady()
    {
        Apply(Report("m1", Holding(A, true, sevenDay: 80), Holding(B, false, sevenDay: 5)));

        var verdict = Verdict("m1");

        Assert.Equal((LicenseNextLicenseStates.Ready, LicenseTones.Good), (verdict.State, verdict.Tone));
        Assert.Equal(new[] { B }, verdict.ReadyAccounts);
    }

    // Rule 3: 80% or more and no ready next license - needed, yellow, naming the recommended license and its sign-in.
    [Fact]
    public void Rule3_At85_NoReadyNextLicense_NeededYellow_NamingTheRecommendedLicense()
    {
        Apply(Report("m1", Holding(A, true, sevenDay: 85)));
        Apply(Report("m2", Holding(B, true, sevenDay: 5)));

        var verdict = Verdict("m1");

        Assert.Equal((LicenseNextLicenseStates.Needed, LicenseTones.Warning, false), (verdict.State, verdict.Tone, verdict.AtThreshold));
        Assert.Equal((B, LicenseNextLicenseActions.AddSpare), (verdict.RecommendedAccountEmail, verdict.SignInAction));
        Assert.Equal($"Needs a next license: {A} is at 85% of its weekly limit, and it holds no sign-in for {B}. Sign it in to {B}.", verdict.Text);
    }

    // A refused sign-in for the recommended license is renewed, not added.
    [Fact]
    public void Rule3_TheRecommendedSignInIsRefused_TheActionIsRenew()
    {
        Apply(Report("m1", Holding(A, true, sevenDay: 85), Holding(B, false, sevenDay: 5, readError: LicenseReadErrors.Unauthorized)));
        Apply(Report("m2", Holding(B, true, sevenDay: 5)));

        var verdict = Verdict("m1");

        Assert.Equal((LicenseNextLicenseStates.Needed, B, LicenseNextLicenseActions.Renew),
            (verdict.State, verdict.RecommendedAccountEmail, verdict.SignInAction));
    }

    // Rule 4: at the threshold and still no ready next license - needed, red.
    [Fact]
    public void Rule4_At97_NoReadyNextLicense_NeededRed()
    {
        Apply(Report("m1", Holding(A, true, sevenDay: 98)));
        Apply(Report("m2", Holding(B, true, sevenDay: 5)));

        var verdict = Verdict("m1");

        Assert.Equal((LicenseNextLicenseStates.Needed, LicenseTones.Bad, true), (verdict.State, verdict.Tone, verdict.AtThreshold));
    }

    // Rule 5: no other license has room - status only, never needed.
    [Fact]
    public void Rule5_NoOtherLicenseHasRoom_StatusOnly()
    {
        Apply(Report("m1", Holding(A, true, sevenDay: 90)));
        Apply(Report("m2", Holding(B, true, sevenDay: 99)));

        var verdict = Verdict("m1");

        Assert.Equal((LicenseNextLicenseStates.NoRoom, LicenseTones.Neutral, false), (verdict.State, verdict.Tone, verdict.IsNeeded));
    }

    // ----- ready -----

    // READY lasts past the reset: B has 77 points of room; at 2 points an hour that is 38.5 hours, and A resets in 10.
    [Fact]
    public void Ready_ItsRoomLastsPastTheReset_IsReady()
    {
        Apply(Report("m1", WeekResets(Holding(A, true, sevenDay: 85), T0.AddHours(10)), Holding(B, false, sevenDay: 20)));
        Apply(Report("m2", Holding(C, true, sevenDay: 30)));

        var verdict = Verdict("m1", pacePerHour: 2);

        Assert.Equal(LicenseNextLicenseStates.Ready, verdict.State);
        Assert.Equal(T0.AddHours(10), verdict.NeededUntilUtc);
    }

    // READY fills before the reset: the same 38.5 hours, and A resets in four days. It needs one more license: C, the one
    // it does not hold.
    [Fact]
    public void Ready_ItFillsBeforeTheReset_NeedsOneMore()
    {
        Apply(Report("m1", WeekResets(Holding(A, true, sevenDay: 85), T0.AddDays(4)), Holding(B, false, sevenDay: 20)));
        Apply(Report("m2", Holding(C, true, sevenDay: 30)));

        var verdict = Verdict("m1", pacePerHour: 2);

        Assert.Equal((LicenseNextLicenseStates.Needed, C), (verdict.State, verdict.RecommendedAccountEmail));
        Assert.Equal(new[] { B }, verdict.ReadyAccounts);
        Assert.StartsWith("Needs one more license:", verdict.Text);
        Assert.Contains($"{B} would fill before {A} resets", verdict.Why);
    }

    // Two ready licenses together last past the reset: their room is added up, best first. B has 77 points and C 67; at 2
    // an hour that is 72 hours, past a reset in 60. At 3 an hour it is 48, so one more is needed: D, the one not held.
    [Theory]
    [InlineData(2.0, LicenseNextLicenseStates.Ready)]
    [InlineData(3.0, LicenseNextLicenseStates.Needed)]
    public void Ready_TwoReadyLicensesTogether_LastOrNeedOneMore(double pacePerHour, string state)
    {
        Apply(Report("m1", WeekResets(Holding(A, true, sevenDay: 85), T0.AddHours(60)), Holding(B, false, sevenDay: 20),
            Holding(C, false, sevenDay: 30)));
        Apply(Report("m2", Holding(D, true, sevenDay: 40)));

        var verdict = Verdict("m1", pacePerHour);

        Assert.Equal(state, verdict.State);
        Assert.Equal(new[] { B, C }, verdict.ReadyAccounts);
        Assert.Equal(state == LicenseNextLicenseStates.Needed ? D : null, verdict.RecommendedAccountEmail);
    }

    private const string D = "account-d@example.com";

    // ----- recommended next -----

    // The paid license with the most room left this week.
    [Fact]
    public void Recommend_TheMostRoomThisWeek()
    {
        Apply(Report("m1", Holding(A, true), Holding(B, false, sevenDay: 50), Holding(C, false, sevenDay: 10)));

        Assert.Equal(C, LicenseNextLicenseFold.Recommend(Picture().Snapshot, new[] { B, C }));
    }

    // Two close ones (within 5 points): the one that resets sooner, even with a little less room.
    [Fact]
    public void Recommend_TwoClose_TheSoonerResetWins()
    {
        Apply(Report("m1", Holding(A, true), WeekResets(Holding(B, false, sevenDay: 12), T0.AddDays(1)),
            WeekResets(Holding(C, false, sevenDay: 10), T0.AddDays(3))));

        Assert.Equal(B, LicenseNextLicenseFold.Recommend(Picture().Snapshot, new[] { B, C }));
    }

    // Not close (more than 5 points apart): the most room wins, whatever resets sooner.
    [Fact]
    public void Recommend_NotClose_TheMostRoomWins()
    {
        Apply(Report("m1", Holding(A, true), WeekResets(Holding(B, false, sevenDay: 20), T0.AddDays(1)),
            WeekResets(Holding(C, false, sevenDay: 10), T0.AddDays(3))));

        Assert.Equal(C, LicenseNextLicenseFold.Recommend(Picture().Snapshot, new[] { B, C }));
    }
}
