using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// A RENEWAL THAT FAILED IS A LICENSE ALARM FROM THE FIRST FAILURE UNTIL A RENEWAL WORKS (issue 4697). Over the real
/// license store, the real failure-streak log and the real alarm service on a throwaway SQLite file, with a push that
/// records what it is given. The clock is the test's own.
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
    private DateTime _now = T0;
    private long _sequence;

    public LicenseRenewalFailureAlarmTests()
    {
        _db = _harness.Open();
        _store = new LicenseStore(_db);
        _log = new LicenseRenewalFailureLog(_db);
        _alarms = new LicenseAlarmService(_db,
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            (_, opened, _) => { lock (_pushed) _pushed.AddRange(opened); return Task.CompletedTask; },
            () => _now);
        _alarm = new LicenseRenewalFailureAlarm(_store, _log, new LicenseReadModel(_store), _alarms, () => _now);
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
        var holding = Holding(account, active, readAt: _now);
        holding.SignInExpiresAtUtc = _now + endsIn;
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

    [Fact]
    public async Task RaiseAsync_ADirectorRestartForgetsTheFailure_AnOpenStreakKeepsTheAlarmOpen()
    {
        // A Director keeps its failed renewals in memory; its first report after a restart carries no failure time. The
        // failure streak in the Licenses log is still open, so the alarm does not read as fixed.
        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Failing(B, TimeSpan.FromDays(2)));
        var machine = new LicenseReadModel(_store).Read(Tenant, _now).Machines.Single();
        _log.Open(Tenant, machine, B, _now + TimeSpan.FromDays(2), null, "a failure streak", _now);
        await Raise();

        Reports(Fine(A, TimeSpan.FromDays(20), active: true), Fine(B, TimeSpan.FromDays(2)));
        await Raise();

        Assert.Single(Open());
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
        var machine = new LicenseMachineView(Guid.NewGuid(), new string('m', 128), new string('u', 128), "dir-1", null,
            Array.Empty<string>(), new[] { holding }, T0, false, null, 0);

        var condition = Assert.Single(LicenseRenewalFailureAlarm.Conditions(new[] { machine }, Array.Empty<LicenseSwitchLogEntity>(), T0));

        Assert.True(condition.Problem.Length <= LicenseAlarmService.MaxSentenceLength);
        Assert.True(condition.Action.Length <= LicenseAlarmService.MaxSentenceLength);
    }
}
