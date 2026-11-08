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
/// THE APPROVER ALARM (License Switching phase 6, Track C), over the real license alarm service, the real approver
/// store and the real Licenses store on a throwaway SQLite file. What these hold down: an approver that cannot approve
/// opens exactly one alarm, with the readiness sentences, and pushes and emails once; it stays open quietly while that
/// stays true and closes by itself when it stops being true or the approver is removed; an approver nobody checks
/// alarms once its 26 hours pass; an account with no approver raises nothing; a check report raises the alarm without
/// the hub call waiting for it; and one tenant never raises or closes another's.
/// </summary>
public sealed class LicenseApproverAlarmTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");
    private const string MachineA = "machine-a";
    private const string DirectorA = "dir-a";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _licenses;
    private readonly LicenseApproverStore _approvers;
    private readonly LicenseAlarmService _alarms;
    private readonly LicenseApproverAlarm _alarm;
    private readonly List<(TenantId Tenant, LicenseOwnerEmail Email)> _emails = new();
    private readonly List<(TenantId Tenant, LicenseAlarmOpened Alarm)> _pushes = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logged = new();
    private DateTime _now = T0;
    private long _sequence;
    // The Directors connected now, by identifier; none unless a test connects one.
    private readonly HashSet<string> _connected = new();

    public LicenseApproverAlarmTests()
    {
        _db = _harness.Open();
        _licenses = new LicenseStore(_db);
        _approvers = new LicenseApproverStore(new TenantSettingsStore(_db));
        _alarms = new LicenseAlarmService(_db, Mail, Push, () => _now, NeverNight);
        _alarm = new LicenseApproverAlarm(_approvers, new LicenseReadModel(_licenses), _alarms, (_, directorId) => _connected.Contains(directorId), _ => new NoScope(), () => _now,
            CancellationToken.None, _logged.Enqueue);
        _licenses.ApplyReport(One, DirectorA, ++_sequence, Report(MachineA, _now, Holding(A, true)), _now, TimeSpan.Zero);
    }

    public void Dispose() => _harness.Dispose();

    private sealed class NoScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private Task<LicenseOwnerEmailResult> Mail(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
    {
        _emails.Add((tenant, email));
        return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
    }

    private Task Push(TenantId tenant, IReadOnlyList<LicenseAlarmOpened> opened, CancellationToken ct)
    {
        foreach (var alarm in opened)
            _pushes.Add((tenant, alarm));
        return Task.CompletedTask;
    }

    /// <summary>Account A's approver on machine A, profile "work", as its last check left it.</summary>
    private void ApproverOfA(string? state, DateTime? checkedAt, string machine = MachineA, TenantId? tenant = null)
        => ApproverOf(A, state, checkedAt, machine, tenant);

    private void ApproverOf(string account, string? state, DateTime? checkedAt, string machine = MachineA, TenantId? tenant = null,
        string? detail = null)
        => _approvers.Change(tenant ?? One, records =>
        {
            var next = records.Approvers.Where(a => a.AccountEmail != account).ToList();
            next.Add(new LicenseApprover(account, machine, "owner", "work", LicenseApproverSources.Registered, state, checkedAt,
                state == LicenseApproverStates.CanApprove ? account : null, detail, "Work Chrome"));
            return ((IReadOnlyList<LicenseApprover>?)next, 0);
        }, _now);

    private Task<LicenseAlarmRaised> Raise(TenantId? tenant = null) => _alarm.RaiseAsync(tenant ?? One, CancellationToken.None);

    // ----- step 4: an account a machine needs has no approver at all (mission "Licenses - Loud When Stranded") -----
    //
    // On 26 October 2026 this alarm handed "no approver at all" to readiness, readiness started no sign-in and raised
    // nothing, and nobody was told. These run the real readiness check over the real store: machine A is on account A,
    // machine B on account B, both well under any limit - a fault, never usage - and neither holds a sign-in for the
    // other's account.

    private const string MachineB = "machine-b";
    private const string DirectorB = "dir-b";

    private LicenseApproverAlarm WithReadiness(Func<LicenseApproverNeeds, LicenseApproverNeeds>? change = null)
    {
        _licenses.ApplyReport(One, DirectorB, ++_sequence, Report(MachineB, _now, Holding(B, true, readAt: _now)), _now, TimeSpan.Zero);
        _licenses.ApplyReport(One, DirectorA, ++_sequence, Report(MachineA, _now, Holding(A, true, readAt: _now)), _now, TimeSpan.Zero);
        _connected.Add(DirectorA);
        _connected.Add(DirectorB);
        var readiness = LicenseReadinessTestKit.ForReading(_db, _licenses, (_, directorId) => _connected.Contains(directorId), () => _now);
        return new LicenseApproverAlarm(_approvers, new LicenseReadModel(_licenses), _alarms, (_, directorId) => _connected.Contains(directorId),
            _ => new NoScope(), () => _now, CancellationToken.None, _logged.Enqueue,
            judge: tenant => change is null ? readiness.JudgeNow(tenant) : change(readiness.JudgeNow(tenant)));
    }

    [Fact]
    public void TheLicensesPage_AMachineWhoseApproverCheckFailed_ItsReadinessLineSaysWhy_AndNoOtherMachinesLineDoes()
    {
        // The page leg of "the reason is never discarded": the overview route's folds, over the real store and the real
        // readiness check, with a failed check on machine A's own browser profile and none on machine B.
        WithReadiness();
        _approvers.RecordChecks(One, MachineA, "owner", new[]
        {
            new LicenseApproverCheckRow
            {
                BrowserProfile = "mindzie", BrowserProfileName = "mindzie", State = LicenseApproverStates.CheckFailed, CheckedAtUtc = _now,
                Detail = "The check could not run: the browser profile could not be started: CC_DIRECTOR_ID is not set.",
            },
        }, _now);
        var readiness = LicenseReadinessTestKit.ForReading(_db, _licenses, (_, directorId) => _connected.Contains(directorId), () => _now);
        var picture = new LicenseSnapshotSource(_licenses, new LicenseReadModel(_licenses)).ReadPicture(One, _now, Array.Empty<LicenseExhaustion>());
        var overview = LicenseTestData.OverviewOf(picture);

        LicenseReadinessFold.Apply(overview, readiness.Judge(One, picture, _now), _approvers.ListFailedChecksQuietly(One), _now);

        var a = overview.Machines.Single(m => m.MachineName == MachineA);
        var b = overview.Machines.Single(m => m.MachineName == MachineB);
        Assert.EndsWith(" The approver check of browser profile \"mindzie\" on machine-a could not run at 2026-10-11 03:12 UTC: The check could not run: "
                        + "the browser profile could not be started: CC_DIRECTOR_ID is not set.", a.ReadinessText);
        Assert.NotNull(b.ReadinessText);
        Assert.DoesNotContain("approver check", b.ReadinessText);
    }

    [Fact]
    public void FailedChecksText_ForOneMachine_CountsOnlyThatMachineAndUsersChecks_AndNamesTheLatest()
    {
        var failed = new[]
        {
            new LicenseApproverFailedCheck(MachineA, "owner", "work", "Work Chrome", _now.AddHours(-2), "Older."),
            new LicenseApproverFailedCheck("MACHINE-A", "OWNER", "mindzie", null, _now.AddHours(-1), "Latest on machine-a."),
            new LicenseApproverFailedCheck(MachineA, "someone-else", "home", null, _now, "Another user."),
            new LicenseApproverFailedCheck(MachineB, "owner", "work", null, _now, "Another machine."),
            new LicenseApproverFailedCheck(MachineA, "owner", "stale", null, _now - LicenseApproverContract.NotCheckedAfter - TimeSpan.FromMinutes(1), "Too old."),
        };

        var text = LicenseApproverReadiness.FailedChecksText(failed, _now, MachineA, "owner");

        Assert.Equal("2 approver checks could not run, the latest of browser profile \"mindzie\" on MACHINE-A at 2026-10-11 02:12 UTC: Latest on machine-a.",
            text);
        Assert.Null(LicenseApproverReadiness.FailedChecksText(failed, _now, "machine-z", "owner"));
    }

    [Fact]
    public async Task NoApproverAtAll_NoMachineNeedsTheAccount_RaisesNothing_AtAnyUse()
    {
        // Every machine holds a sign-in for the other's account, so neither needs an approver: use alone, even at 100%,
        // is never an alarm here (issue #4249).
        _licenses.ApplyReport(One, DirectorB, ++_sequence,
            Report(MachineB, _now, Holding(B, true, fiveHour: 100, sevenDay: 100, readAt: _now), Holding(A, false, readAt: _now)), _now, TimeSpan.Zero);
        _licenses.ApplyReport(One, DirectorA, ++_sequence,
            Report(MachineA, _now, Holding(A, true, fiveHour: 99, sevenDay: 99, readAt: _now), Holding(B, false, readAt: _now)), _now, TimeSpan.Zero);
        var readiness = LicenseReadinessTestKit.ForReading(_db, _licenses, (_, _) => true, () => _now);
        var alarm = new LicenseApproverAlarm(_approvers, new LicenseReadModel(_licenses), _alarms, (_, _) => true, _ => new NoScope(), () => _now,
            CancellationToken.None, _logged.Enqueue, judge: readiness.JudgeNow);

        var raised = await alarm.RaiseAsync(One, CancellationToken.None);

        Assert.Equal(0, raised.Opened);
        Assert.Empty(_pushes);
    }

    [Fact]
    public async Task ACheckReport_KeepsAFailedCheckOfAProfileNoRecordNames_AndAWorkingCheckClearsIt()
    {
        // From 19 to 26 October every one of SOREN_NORTH's checks was answered "nothing to store" and lost.
        var registration = new LicenseApproverRegistration(_approvers, new LicenseReadModel(_licenses), () => _now);
        var failed = new LicenseApproverCheckReport
        {
            OffersApprovers = true,
            Rows = [new LicenseApproverCheckRow { BrowserProfile = "mindzie", State = LicenseApproverStates.CheckFailed, CheckedAtUtc = _now, Detail = "Why it failed." }],
        };

        var answer = registration.Report(One, DirectorA, failed);

        Assert.Equal(LicenseApproverCheckOutcomes.NothingToStore, Assert.Single(answer.Rows).Outcome);
        var kept = Assert.Single(_approvers.ListFailedChecksQuietly(One));
        Assert.Equal((MachineA, "mindzie", "Why it failed."), (kept.MachineName, kept.BrowserProfile, kept.Detail));

        registration.Report(One, DirectorA, new LicenseApproverCheckReport
        {
            OffersApprovers = true,
            Rows = [new LicenseApproverCheckRow { BrowserProfile = "mindzie", State = LicenseApproverStates.SignedOut, CheckedAtUtc = _now }],
        });
        Assert.Empty(_approvers.ListFailedChecksQuietly(One));
    }

    [Fact]
    public async Task AnApproverTheOwnerRemoves_ClosesItsAlarm()
    {
        ApproverOfA(LicenseApproverStates.WrongAccount, _now);
        await Raise();

        Assert.True(_approvers.Remove(One, A, _now));
        await Raise();

        Assert.Empty(_alarms.ListOpen(One));
    }

    [Fact]
    public async Task AMinuteRaiseWithNothingToSay_WritesNoLogLine()
    {
        await Raise();
        ApproverOfA(LicenseApproverStates.CanApprove, _now);
        await Raise();

        Assert.Empty(_logged);
    }

    [Fact]
    public async Task AnAccountWithNoApprover_RaisesNothing()
    {
        var raised = await Raise();

        Assert.Equal((0, 0, 0), (raised.Opened, raised.StillOpen, raised.Closed));
        Assert.Empty(_alarms.ListOpen(One));
        Assert.Empty(_pushes);
    }

    // RETIRED (the owner, 8 November 2026): the only license alarm is a machine at 80% with no next license on it. A check
    // report still writes the record and still starts a raise, and the raise opens nothing.
    [Fact]
    public async Task ACheckReport_WritesTheRecord_AndTheRetiredApproverAlarmOpensNothing()
    {
        var registration = new LicenseApproverRegistration(_approvers, new LicenseReadModel(_licenses), () => _now, _alarm.RaiseSoon);
        Assert.True(_approvers.Set(One, A, MachineA, "owner", "work", _now).Accepted);

        var answer = registration.Report(One, DirectorA, new LicenseApproverCheckReport
        {
            Rows = [new LicenseApproverCheckRow { BrowserProfile = "work", AccountEmail = A, State = LicenseApproverStates.SignedOut, CheckedAtUtc = _now }],
        });
        await _alarm.WhenIdleAsync();

        Assert.Equal(LicenseApproverCheckOutcomes.HealthUpdated, Assert.Single(answer.Rows).Outcome);
        Assert.Empty(_alarms.ListOpen(One));
        Assert.Empty(_pushes);
        Assert.Empty(_emails);
    }

    // An approver alarm left open by an older Gateway closes on the first raise after the Gateway starts, with no push and
    // no email, while the approver still cannot approve.
    [Fact]
    public async Task AnOpenApproverAlarm_IsClosedOnTheFirstRaise_AndNoneOpens()
    {
        ApproverOfA(LicenseApproverStates.SignedOut, _now);
        using (var ctx = _db.CreateContext(One))
        {
            ctx.LicenseAlarms.Add(new LicenseAlarmEntity
            {
                Kind = LicenseApproverAlarm.Kind.Key, MachineId = Guid.Empty, MachineName = "", AccountEmail = A,
                Problem = "An older Gateway's approver alarm.", Action = "Nothing.", OpenedAtUtc = T0.AddMinutes(-30),
                TenantId = Assert.IsType<string>(ctx.ActiveTenant),
            });
            ctx.SaveChanges();
        }

        var raised = await Raise();

        Assert.Equal((0, 0, 1), (raised.Opened, raised.StillOpen, raised.Closed));
        Assert.Empty(_alarms.ListOpen(One));
        Assert.Empty(_pushes);
        Assert.Empty(_emails);
    }

    [Fact]
    public void AReportRefusedWhole_RaisesNothing_AndADecidedReportRaisesOnce()
    {
        var raises = new List<TenantId>();
        var registration = new LicenseApproverRegistration(_approvers, new LicenseReadModel(_licenses), () => _now, raises.Add);

        Assert.Throws<ArgumentException>(() => registration.Report(One, DirectorA, null));
        Assert.Empty(raises);

        registration.Report(One, DirectorA, new LicenseApproverCheckReport());
        Assert.Equal(new[] { One }, raises);
    }
}
