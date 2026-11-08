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
    public async Task MinuteCheck_AStreakOpenedForAFailureOlderThanTheSignIn_IsClosed_AndNoAlarmIsOpen()
    {
        // The rows the deploy of 7 November left: a streak opened at 07:07 under the 19:07 sign-in, and its alarm.
        var failedAt = new DateTime(2026, 11, 6, 14, 27, 0, DateTimeKind.Utc);
        var signedInByHandAt = new DateTime(2026, 11, 6, 19, 7, 0, DateTimeKind.Utc);
        _now = new DateTime(2026, 11, 7, 7, 7, 0, DateTimeKind.Utc);
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), RenewedByHandButStillReportingTheOldFailure(failedAt, signedInByHandAt));
        var machine = new LicenseReadModel(_store).Read(Tenant, _now).Machines.Single();
        _log.Open(Tenant, machine, B, signedInByHandAt.AddDays(30), signedInByHandAt, "Renewing failed.", _now);
        // The renewal alarm is retired (8 November 2026): a raise of it opens nothing, so only the streak is left to close.

        _now = _now.AddMinutes(1);
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), RenewedByHandButStillReportingTheOldFailure(failedAt, signedInByHandAt));
        await MinuteCheck();

        Assert.Empty(Open());
        Assert.Empty(_log.ListOpen(Tenant));
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

    // RETIRED (the owner, 8 November 2026): the only license alarm is a machine at 80% with no next license on it. A failed
    // renewal of the license a machine is ON - which used to raise at any level - opens nothing now.
    [Fact]
    public async Task RaiseAsync_AFailedRenewalOfTheLicenseInUse_OpensNothing()
    {
        var onA = Failing(A, TimeSpan.FromDays(7));
        onA.IsActive = true;
        Reports(onA, Fine(B, TimeSpan.FromDays(20)));

        var raised = await Raise();

        Assert.Equal((0, 0, 0), (raised!.Opened, raised.StillOpen, raised.Closed));
        Assert.Empty(_alarms.ListOpen(Tenant));
        Assert.Empty(_pushed);
        Assert.Empty(_mailed);
    }

    // A renewal alarm left open by an older Gateway closes on the first raise after the Gateway starts, while the renewal
    // is still failing, with no push and no email.
    [Fact]
    public async Task RaiseAsync_AnOpenRenewalAlarm_IsClosedOnTheFirstRaise()
    {
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(7)));
        var machineId = new LicenseReadModel(_store).Read(Tenant, _now).Machines.Single().MachineId;
        using (var ctx = _db.CreateContext(Tenant))
        {
            ctx.LicenseAlarms.Add(new LicenseAlarmEntity
            {
                Kind = LicenseRenewalFailureAlarm.Kind.Key, MachineId = machineId, MachineName = Mac, AccountEmail = B,
                Problem = "An older Gateway's renewal alarm.", Action = "Nothing.", OpenedAtUtc = T0.AddMinutes(-30),
                TenantId = Assert.IsType<string>(ctx.ActiveTenant),
            });
            ctx.SaveChanges();
        }

        var raised = await Raise();

        Assert.Equal((0, 0, 1), (raised!.Opened, raised.StillOpen, raised.Closed));
        Assert.Empty(_alarms.ListOpen(Tenant));
        Assert.Empty(_pushed);
        Assert.Empty(_mailed);
    }
}
