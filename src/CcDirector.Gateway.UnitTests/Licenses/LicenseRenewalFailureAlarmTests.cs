using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// A RENEWAL THAT FAILED IS A LICENSE ALARM FROM THE FIRST FAILURE UNTIL A RENEWAL WORKS (issue 4697). Over the real
/// license store, the real failure-streak log and the real alarm service on a throwaway SQLite file, with a push that
/// records what it is given. The clock is the test's own. Whether a failing SPARE matters is the real readiness
/// service's next-license verdict, on the same store: account A, the license the machine is on, sits at 85% of its weekly
/// limit unless a test says otherwise, so a spare that is the machine's only ready next license is one it relies on.
/// </summary>
public sealed class LicenseRenewalFailureAlarmTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-a");
    private const string Mac = "the-mac";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _store;
    private readonly LicenseRenewalFailureLog _log;
    private readonly LicenseAlarmService _alarms;
    private readonly LicenseRenewalFailureAlarm _alarm;
    private readonly List<LicenseAlarmOpened> _pushed = new();
    private readonly List<LicenseOwnerEmail> _mailed = new();
    private readonly LicenseRenewalEmailService _emails;
    private readonly LicenseReadinessService _readiness;
    private DateTime _now = T0;
    private long _sequence;
    // The weekly use of account A, the license the machine is on.
    private double _aWeekly = 85;

    public LicenseRenewalFailureAlarmTests()
    {
        _db = _harness.Open();
        _store = new LicenseStore(_db);
        _log = new LicenseRenewalFailureLog(_db);
        _alarms = new LicenseAlarmService(_db,
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            (_, opened, _) => { lock (_pushed) _pushed.AddRange(opened); return Task.CompletedTask; },
            () => _now, NeverNight);
        var reads = new LicenseReadModel(_store);
        var directors = new FakeLicenseDirectors();
        var signInLog = new LicenseSignInLog(_db);
        var signIns = new LicenseSignInService(signInLog, reads, directors.IsConnected, directors.SendAsync, directors.EnterScope,
            () => _now, CancellationToken.None);
        var approvers = new LicenseApproverReadiness(new LicenseApproverStore(new TenantSettingsStore(_db)), reads,
            directors.IsConnected, () => _now);
        _readiness = new LicenseReadinessService(_store, new LicenseSnapshotSource(_store, reads), signInLog, signIns,
            approvers.WhyNotReady, directors.IsConnected, _alarms, new LicenseUsageHistory(), () => _now);
        _alarm = new LicenseRenewalFailureAlarm(_store, _log, _alarms, _readiness.NextLicensesNow);
        _emails = new LicenseRenewalEmailService(_store, _log, new LicenseReadModel(_store),
            (_, email, _) => { lock (_mailed) _mailed.Add(email); return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)); },
            () => _now);
    }

    public void Dispose() => _harness.Dispose();

    private void Reports(params LicenseHolding[] holdings)
        => _store.ApplyReport(Tenant, "dir-" + Mac, ++_sequence, Report(Mac, _now, holdings), _now, TimeSpan.Zero);

    private LicenseHolding Failing(string account, TimeSpan endsIn, string? error = LicenseRenewalErrors.NeedsFreshSignIn)
    {
        var holding = Holding(account, false, readAt: _now);
        holding.SignInExpiresAtUtc = _now + endsIn;
        holding.RenewalFailedAtUtc = _now.AddMinutes(-5);
        holding.RenewalError = error;
        return holding;
    }

    private LicenseHolding Fine(string account, TimeSpan endsIn, bool active = false)
    {
        var holding = active ? Holding(account, true, sevenDay: _aWeekly, readAt: _now) : Holding(account, false, readAt: _now);
        holding.SignInExpiresAtUtc = _now + endsIn;
        // The windows reset ahead of the test's own clock, so a test that moves the clock to November still reads them.
        holding.FiveHour!.ResetsAtUtc = _now.AddHours(2);
        holding.SevenDay!.ResetsAtUtc = _now.AddDays(1);
        return holding;
    }

    private Task<LicenseAlarmRaised?> Raise() => _alarm.RaiseAsync(Tenant, CancellationToken.None);

    private List<LicenseAlarmEntity> Open() => _alarms.ListOpen(Tenant).Where(a => a.Kind == LicenseRenewalFailureAlarm.Kind.Key).ToList();

    [Fact]
    public async Task RaiseAsync_TheFirstFailedRenewal_OpensTheAlarmAtOnce_WithTheReason_AWeekBeforeTheEnd()
    {
        // Seven days left: far outside the three-day owner email, and the alarm opens anyway.
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(7)));

        await Raise();

        var alarm = Assert.Single(Open());
        Assert.Equal((Mac, B), (alarm.MachineName, alarm.AccountEmail));
        Assert.Equal($"Renewing the sign-in for {B} on {Mac} failed: the approving browser needs a fresh claude.ai sign-in on the approval page. "
                     + $"The sign-in ends {(_now + TimeSpan.FromDays(7)):yyyy-MM-dd HH:mm} UTC.", alarm.Problem);
        Assert.Equal($"Put right what the reason says. {Mac} tries again every day, and the alarm closes by itself when a renewal works.", alarm.Action);
        Assert.Single(_pushed);
    }

    [Fact]
    public async Task RaiseAsync_TheDailyRetryFailsAgain_TheAlarmStaysOpen_AndNothingIsSentAgain()
    {
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(7)));
        await Raise();

        _now = _now.AddDays(1);
        Reports(Fine(A, TimeSpan.FromDays(19), active: true), Failing(B, TimeSpan.FromDays(6), LicenseRenewalErrors.ApproverOffline));
        var raised = await Raise();

        var alarm = Assert.Single(Open());
        Assert.Contains("the machine that approves this account was not connected", alarm.Problem);
        Assert.Equal((0, 1, 0), (raised!.Opened, raised.StillOpen, raised.Closed));
        Assert.Single(_pushed);
    }

    [Fact]
    public async Task RaiseAsync_ARenewalThatWorks_ClosesTheAlarm()
    {
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(7)));
        await Raise();

        _now = _now.AddDays(1);
        Reports(Fine(A, TimeSpan.FromDays(19), active: true), Fine(B, TimeSpan.FromDays(30)));
        await Raise();

        Assert.Empty(Open());
    }

    /// <summary>The Gateway's minute checks as they run in production: the renewal email check (which opens a failure
    /// streak's row) and the alarm raise.</summary>
    private async Task MinuteCheck()
    {
        await _emails.TickAsync(Tenant, CancellationToken.None);
        await Raise();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(2)]
    public async Task RaiseAsync_ADirectorRestartForgetsTheFailure_TheAlarmStaysOpen_WithNoCameBack_AtAnyTimeLeft(int daysLeft)
    {
        // Review of pull request 4717: a Director keeps its failed renewals in memory, and its first report after a restart
        // carries no failure time. At seven days out no streak row used to exist, so that report closed the alarm and the
        // next failure reopened it as "it has come and gone". Nothing is hand-built here: the real email check opens the
        // streak at the first failure, and the alarm must survive the restart's report.
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(daysLeft)));
        await MinuteCheck();
        var opened = Assert.Single(Open());
        Assert.Single(_log.ListOpen(Tenant));

        _now = _now.AddMinutes(1);
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Fine(B, TimeSpan.FromDays(daysLeft) - TimeSpan.FromMinutes(1)));
        await MinuteCheck();
        _now = _now.AddMinutes(3);
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(daysLeft) - TimeSpan.FromMinutes(4)));
        await MinuteCheck();

        var still = Assert.Single(Open());
        Assert.Equal(opened.Id, still.Id);
        Assert.DoesNotContain("come and gone", LicenseAlarmFold.Banner(_alarms.ListOpen(Tenant), _now).Alarms.Single().Text);
        Assert.DoesNotContain(_store.ListLog(Tenant, T0.AddDays(-1)), r => r.Kind == LicenseLogKinds.AlarmClosed);
        Assert.Single(_pushed);
    }

    [Fact]
    public async Task EmailCheck_AFailureAWeekOut_OpensTheStreakAtOnce_ButEmailsOnlyWhenThreeDaysAreLeft()
    {
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(7)));

        Assert.Equal(0, await _emails.TickAsync(Tenant, CancellationToken.None));
        var row = Assert.Single(_log.ListOpen(Tenant));
        Assert.EndsWith("the owner is told in Your inbox if it is still failing when 3 days of the sign-in are left.", row.Message);
        Assert.DoesNotContain(_mailed, m => !m.Subject.StartsWith("DevThrottle license alarm", StringComparison.Ordinal));

        _now = _now.AddDays(4).AddMinutes(1);
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(3) - TimeSpan.FromMinutes(1)));
        Assert.Equal(1, await _emails.TickAsync(Tenant, CancellationToken.None));
        Assert.Single(_log.ListOpen(Tenant));
    }

    /// <summary>The live sequence of 6 and 7 November 2026: the Director's renewal of B failed at 14:27, the sign-in was
    /// made again by hand at 19:07, and the Director went on reporting the 14:27 failure under the new sign-in.</summary>
    private LicenseHolding RenewedByHandButStillReportingTheOldFailure(DateTime failedAt, DateTime signedInByHandAt)
    {
        var holding = Holding(B, false, readAt: _now);
        holding.SignedInAtUtc = signedInByHandAt;
        holding.SignInExpiresAtUtc = signedInByHandAt.AddDays(30);
        holding.RenewalFailedAtUtc = failedAt;
        holding.RenewalError = LicenseRenewalErrors.NeedsFreshSignIn;
        return holding;
    }

    [Fact]
    public async Task MinuteCheck_AFailureOlderThanTheSignInMadeByHandSince_OpensNoAlarmAndNoStreak()
    {
        var failedAt = new DateTime(2026, 11, 6, 14, 27, 0, DateTimeKind.Utc);
        var signedInByHandAt = new DateTime(2026, 11, 6, 19, 7, 0, DateTimeKind.Utc);
        _now = new DateTime(2026, 11, 7, 7, 7, 0, DateTimeKind.Utc);
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), RenewedByHandButStillReportingTheOldFailure(failedAt, signedInByHandAt));

        await MinuteCheck();

        Assert.Empty(Open());
        Assert.Empty(_log.ListOpen(Tenant));
        Assert.Empty(_pushed);
        var holding = new LicenseReadModel(_store).Read(Tenant, _now).Machines.Single().Holdings.Single(h => h.AccountEmail == B);
        Assert.Equal(((DateTime?)null, (string?)null, true), (holding.RenewalFailedAtUtc, holding.RenewalError, holding.RenewalFailureOver));
    }

    [Fact]
    public async Task MinuteCheck_AnAlarmAndStreakOpenedForAFailureOlderThanTheSignIn_AreClosed()
    {
        // The rows the deploy of 7 November left: a streak opened at 07:07 under the 19:07 sign-in, and its alarm.
        var failedAt = new DateTime(2026, 11, 6, 14, 27, 0, DateTimeKind.Utc);
        var signedInByHandAt = new DateTime(2026, 11, 6, 19, 7, 0, DateTimeKind.Utc);
        _now = new DateTime(2026, 11, 7, 7, 7, 0, DateTimeKind.Utc);
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), RenewedByHandButStillReportingTheOldFailure(failedAt, signedInByHandAt));
        var machine = new LicenseReadModel(_store).Read(Tenant, _now).Machines.Single();
        _log.Open(Tenant, machine, B, signedInByHandAt.AddDays(30), signedInByHandAt, "Renewing failed.", _now);
        await _alarms.RaiseAsync(Tenant, LicenseRenewalFailureAlarm.Kind,
            new[] { new LicenseAlarmCondition(machine.MachineId, Mac, B, "Renewing the sign-in failed.", "Put it right.") }, CancellationToken.None);
        Assert.Single(Open());

        _now = _now.AddMinutes(1);
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), RenewedByHandButStillReportingTheOldFailure(failedAt, signedInByHandAt));
        await MinuteCheck();

        Assert.Empty(Open());
        Assert.Empty(_log.ListOpen(Tenant));
    }

    [Fact]
    public async Task MinuteCheck_AFailureNewerThanTheSignIn_StillOpensTheAlarm()
    {
        var signedInAt = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);
        _now = new DateTime(2026, 11, 7, 7, 7, 0, DateTimeKind.Utc);
        var holding = RenewedByHandButStillReportingTheOldFailure(_now.AddHours(-1), signedInAt);
        holding.SignInExpiresAtUtc = _now.AddDays(5);
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), holding);

        await MinuteCheck();

        Assert.Single(Open());
        Assert.Single(_log.ListOpen(Tenant));
    }

    [Fact]
    public async Task RaiseAsync_ARenewalReportedBeforeTheEmailCheckEndsTheStreak_ClosesTheAlarmAtOnce()
    {
        // The full gate of pull request 4721: the email check and the alarm raise of one minute run side by side. When the
        // raise ran first it still found the streak row open and kept the alarm open a minute after the renewal worked.
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(5)));
        await MinuteCheck();
        Assert.Single(Open());

        _now = _now.AddMinutes(1);
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Fine(B, TimeSpan.FromDays(30)));
        await Raise();                               // the raise first, the streak row still open

        Assert.Empty(Open());
        Assert.Single(_log.ListOpen(Tenant));         // the email check ends the row on its own turn
    }

    [Fact]
    public async Task RaiseAsync_NoMachineHasReported_ReadsAndWritesNothing()
    {
        Assert.Null(await Raise());
        Assert.Empty(Open());
    }

    [Fact]
    public void Conditions_NamesTooLongForAnAlarm_SendTheOwnerToThePage_AndNeverBreakTheRaise()
    {
        var holding = new LicenseHoldingView(new string('a', 300) + "@example.com", false, null, T0.AddDays(7), LicenseSignInExpiry.Fine, false, null,
            RenewalFailedAtUtc: T0, RenewalError: LicenseRenewalErrors.NeedsFreshSignIn);
        // The license the machine is on: its failure always raises, whatever the next-license verdict.
        var machine = new LicenseMachineView(Guid.NewGuid(), new string('m', 128), new string('u', 128), "dir-1", holding.AccountEmail,
            Array.Empty<string>(), new[] { holding }, T0, false, null, 0);

        var condition = Assert.Single(LicenseRenewalFailureAlarm.Conditions(new[] { machine }, Array.Empty<LicenseSwitchLogEntity>(),
            Array.Empty<LicenseNextLicense>(), T0));

        Assert.True(condition.Problem.Length <= LicenseAlarmService.MaxSentenceLength);
        Assert.True(condition.Action.Length <= LicenseAlarmService.MaxSentenceLength);
    }

    // A SPARE'S FAILURE REACHES THE BAR ONLY WHEN THE MACHINE WOULD BE STUCK (owner ruling of 7 November 2026). Each of
    // these goes through the real minute check and the real readiness service's verdict.

    [Fact]
    public async Task MinuteCheck_ASpareFailsUnderTheWarningLevel_RaisesNothing_AndThePageStillSaysIt()
    {
        _aWeekly = 20;
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(5)));

        await MinuteCheck();

        Assert.Empty(Open());
        Assert.Empty(_pushed);
        var page = System.Text.Json.JsonSerializer.Serialize(OverviewOf(new LicenseSnapshotSource(_store, new LicenseReadModel(_store))
            .ReadPicture(Tenant, _now, Array.Empty<LicenseExhaustion>())));
        Assert.Contains("Renewing this sign-in failed", page);
    }

    [Fact]
    public async Task MinuteCheck_ASpareFailsWhileAnotherReadySpareIsHeld_RaisesNothing()
    {
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(5)), Fine(C, TimeSpan.FromDays(30)));

        await MinuteCheck();

        Assert.Empty(Open());
        Assert.Empty(_pushed);
    }

    [Fact]
    public async Task MinuteCheck_TheLicenseTheMachineIsOnFails_RaisesAtAnyLevel()
    {
        _aWeekly = 20;
        var active = Failing(A, TimeSpan.FromDays(5));
        active.IsActive = true;
        Reports(active, Fine(B, TimeSpan.FromDays(30)));

        await MinuteCheck();

        var alarm = Assert.Single(Open());
        Assert.Equal(A, alarm.AccountEmail);
    }

    [Fact]
    public async Task MinuteCheck_AQuietSpareFailure_Raises_OnceTheMachineReachesTheWarningLevelAndReliesOnIt()
    {
        _aWeekly = 20;
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(5)));
        await MinuteCheck();
        Assert.Empty(Open());

        _now = _now.AddMinutes(1);
        _aWeekly = 85;
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(5) - TimeSpan.FromMinutes(1)));
        await MinuteCheck();

        var alarm = Assert.Single(Open());
        Assert.Equal(B, alarm.AccountEmail);
        Assert.Single(_pushed);
    }

    [Fact]
    public async Task MinuteCheck_AMachineOnNoLicense_RaisesForAFailingSignIn()
    {
        // No license it is on: the sign-in it holds is the one it would have to use, so its failure is loud.
        Reports(Failing(B, TimeSpan.FromDays(5)));

        await MinuteCheck();

        var alarm = Assert.Single(Open());
        Assert.Equal(B, alarm.AccountEmail);
    }

    [Fact]
    public async Task MinuteCheck_ASpareFails_WhileTheMachineCannotBeJudged_StillRaises()
    {
        // The reading of the license it is on is too old to judge: a state the verdict cannot vouch for never silences it,
        // even with another ready spare held.
        var active = Fine(A, TimeSpan.FromDays(20), active: true);
        active.ReadAtUtc = _now - LicenseContract.ActiveHoldingStaleAfter - TimeSpan.FromMinutes(5);
        Reports(active, Failing(B, TimeSpan.FromDays(5)), Fine(C, TimeSpan.FromDays(30)));

        await MinuteCheck();

        var alarm = Assert.Single(Open());
        Assert.Equal(B, alarm.AccountEmail);
    }

    private static LicenseNextLicense Verdict(string state, params string[] ready)
        => new(Guid.NewGuid(), Mac, "u", Mac, A, state, null, false, null, ready, null, null, null, "", LicenseTones.Neutral);

    [Theory]
    [InlineData(LicenseNextLicenseStates.NotNeeded, false)]
    [InlineData(LicenseNextLicenseStates.NoRoom, false)]
    [InlineData(LicenseNextLicenseStates.Needed, true)]
    [InlineData(LicenseNextLicenseStates.SwitchFailing, true)]
    [InlineData(LicenseNextLicenseStates.NotJudged, true)]
    public void SpareMatters_ByVerdictState(string state, bool matters)
        => Assert.Equal(matters, LicenseRenewalFailureAlarm.SpareMatters(Verdict(state), B));

    [Fact]
    public void SpareMatters_NoVerdict_AMachineOnNoLicense_Matters()
        => Assert.True(LicenseRenewalFailureAlarm.SpareMatters(null, B));

    [Fact]
    public void SpareMatters_ReadyOnlyThroughThisSpare_Matters_InAnyCase()
        => Assert.True(LicenseRenewalFailureAlarm.SpareMatters(Verdict(LicenseNextLicenseStates.Ready, B.ToUpperInvariant()), B));

    [Fact]
    public void SpareMatters_ReadyThroughAnotherLicenseToo_IsQuiet()
        => Assert.False(LicenseRenewalFailureAlarm.SpareMatters(Verdict(LicenseNextLicenseStates.Ready, B, C), B));

    [Fact]
    public void SpareMatters_ReadyWithNoAccountNamed_Matters()
        => Assert.True(LicenseRenewalFailureAlarm.SpareMatters(Verdict(LicenseNextLicenseStates.Ready), B));
}
