using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// THE GATEWAY DECIDES AND STARTS (the Website Business Factory mission, product track), against a real database file.
/// The session starter is a fake that records every call, so "no session was started" is proven by a count of zero
/// calls - a presence of evidence, not an absence of an error. The session list is a dictionary the test moves, read
/// through the same delegate production hands the service.
/// </summary>
public sealed class TriggerServiceTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-trigger-a");
    private static readonly TenantId OtherTenant = new("acct-trigger-b");
    private const string Director = "dir-north-1";
    private const string OtherDirector = "dir-north-2";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly List<(string machine, NewSessionRequest request)> _starts = new();
    private readonly Dictionary<string, SessionDto> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
    private string? _startError;
    private int _nextSession;
    private TaskCompletionSource? _holdStart;

    public void Dispose() => _harness.Dispose();

    private TriggerService Service()
    {
        var store = new TriggerStore(_harness.Open());
        return new TriggerService(store,
            async (machine, request, ct) =>
            {
                lock (_starts) _starts.Add((machine, request));
                if (_holdStart is not null) await _holdStart.Task;
                if (_startError is not null) return (null, _startError);
                var id = $"aaaaaaaa-0000-4000-8000-{++_nextSession:D12}";
                _sessions[id] = new SessionDto { SessionId = id, ActivityState = "Working" };
                return (id, null);
            },
            findSession: (_, sid) => _sessions.TryGetValue(sid, out var s) ? s : null,
            timeZone: _ => TimeZoneInfo.Utc,
            nowUtc: () => _now);
    }

    private static TriggerEntityRef Add(TriggerService service, bool paused = false, TenantId? tenant = null)
    {
        var req = TriggerDefinitionTests.Valid();
        req.Paused = paused;
        var (created, error) = service.Store.Create(tenant ?? Tenant, req, "session test", new DateTime(2026, 9, 21, 11, 59, 0, DateTimeKind.Utc));
        Assert.Null(error);
        return new TriggerEntityRef(created!.Id.ToString("D"));
    }

    private sealed record TriggerEntityRef(string Id);

    private TriggerCheckReport Counted(int count) => new() { CheckedAtUtc = _now, ExitCode = 0, Output = $"{{\"count\": {count}}}" };

    private async Task<TriggerRunDto> Report(TriggerService service, TriggerEntityRef t, TriggerCheckReport report,
        string director = Director, TenantId? tenant = null)
    {
        var result = await service.ReportCheckAsync(tenant ?? Tenant, director, t.Id, report, CancellationToken.None);
        Assert.Equal(TriggerReportRefusal.None, result.Refusal);
        return TriggerService.ToDto(result.Run!);
    }

    [Fact]
    public async Task EmptyCheck_WritesNothingToDo_AndStartsNoSession()
    {
        var service = Service();
        var t = Add(service);

        var run = await Report(service, t, Counted(0));

        Assert.Equal(TriggerRunOutcome.NothingToDo, run.Outcome);
        Assert.Equal(0, run.Count);
        Assert.Empty(_starts);
        var stored = Assert.Single(service.Store.ListRuns(Tenant, Guid.Parse(t.Id), 10));
        Assert.Equal(TriggerRunOutcome.NothingToDo, stored.Outcome);
        Assert.Equal("OK", service.ToDto(service.Store.Find(Tenant, t.Id)!).StatusText);
    }

    [Fact]
    public async Task CountTwo_StartsExactlyOneSession_WhosePromptSaysTwo()
    {
        var service = Service();
        var t = Add(service);

        var run = await Report(service, t, Counted(2));

        Assert.Equal(TriggerRunOutcome.Started, run.Outcome);
        var (machine, request) = Assert.Single(_starts);
        Assert.Equal("SOREN_NORTH", machine);
        Assert.Equal("New mail: 2 threads. Handle them.", request.PrePrompt);
        Assert.Equal("Front Desk - website-new-mail - 2026-09-21 12:00", request.Name);
        Assert.Equal(@"D:\ReposFred\cc-consult", request.RepoPath);
        Assert.Equal(SessionOriginKinds.Schedule, request.Origin);
        Assert.Equal(SessionOriginSurfaces.Trigger, request.OriginSurface);
        Assert.Equal(Director, request.Director);
        Assert.True(request.AutoDismiss);
        Assert.Equal(run.SessionId, service.Store.Find(Tenant, t.Id)!.LastSessionId);
    }

    [Fact]
    public async Task ASecondCheckWhileThatSessionLives_IsSkippedRunning_AndAfterItEnds_ANewOneStarts()
    {
        var service = Service();
        var t = Add(service);
        var first = await Report(service, t, Counted(2));

        _now = _now.AddMinutes(5);
        var second = await Report(service, t, Counted(3));

        Assert.Equal(TriggerRunOutcome.SkippedRunning, second.Outcome);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Single(_starts);

        // The session ends. The lock is THAT session, so the next check that counts work starts a new one.
        _sessions[first.SessionId!].ActivityState = "Exited";
        _now = _now.AddMinutes(5);
        var third = await Report(service, t, Counted(1));

        Assert.Equal(TriggerRunOutcome.Started, third.Outcome);
        Assert.Equal(2, _starts.Count);
        Assert.NotEqual(first.SessionId, third.SessionId);
        Assert.Equal("New mail: 1 threads. Handle them.", _starts[1].request.PrePrompt);
    }

    [Fact]
    public async Task ACrashedSession_HasEnded()
    {
        var service = Service();
        var t = Add(service);
        var first = await Report(service, t, Counted(2));
        _sessions[first.SessionId!].Crashed = true;

        _now = _now.AddMinutes(5);
        Assert.Equal(TriggerRunOutcome.Started, (await Report(service, t, Counted(2))).Outcome);
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task ASessionNotYetInTheList_CountsAsAliveInsideTheGrace_AndEndedAfterIt()
    {
        var service = Service();
        var t = Add(service);
        var first = await Report(service, t, Counted(2));
        _sessions.Remove(first.SessionId!); // no Director has reported it yet, or it has since gone

        _now = _now.AddMinutes(1);
        Assert.Equal(TriggerRunOutcome.SkippedRunning, (await Report(service, t, Counted(2))).Outcome);

        _now = _now.Add(TriggerService.StartGrace);
        Assert.Equal(TriggerRunOutcome.Started, (await Report(service, t, Counted(2))).Outcome);
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task Paused_WritesPaused_AndStartsNothing()
    {
        var service = Service();
        var t = Add(service, paused: true);

        var run = await Report(service, t, Counted(4));

        Assert.Equal(TriggerRunOutcome.Paused, run.Outcome);
        Assert.Equal(4, run.Count);
        Assert.Empty(_starts);

        service.Store.SetPaused(Tenant, "website-new-mail", false);
        Assert.Equal(TriggerRunOutcome.Started, (await Report(service, t, Counted(4))).Outcome);
        Assert.Single(_starts);
    }

    public static TheoryData<string, TriggerCheckReport, string> BrokenChecks => new()
    {
        { "exit 1", new TriggerCheckReport { ExitCode = 1, ErrorOutput = "not signed in" }, "exit code 1: not signed in" },
        { "not JSON", new TriggerCheckReport { ExitCode = 0, Output = "three" }, "the output is not JSON: three" },
        { "no count", new TriggerCheckReport { ExitCode = 0, Output = "{\"n\":3}" }, "the output has no count: {\"n\":3}" },
        { "timeout", new TriggerCheckReport { TimedOut = true }, "the check timed out" },
    };

    [Theory]
    [MemberData(nameof(BrokenChecks))]
    public async Task ABrokenCheck_WritesFailedWithTheReason_TurnsTheTriggerRed_AndStartsNothing(
        string _, TriggerCheckReport report, string reason)
    {
        var service = Service();
        var t = Add(service);
        report.CheckedAtUtc = _now;

        var run = await Report(service, t, report);

        Assert.Equal(TriggerRunOutcome.Failed, run.Outcome);
        Assert.Equal(reason, run.Reason);
        Assert.Null(run.Count);
        Assert.Empty(_starts);
        var dto = service.ToDto(service.Store.Find(Tenant, t.Id)!);
        Assert.Equal(TriggerStatusKind.Red, dto.Status);
        Assert.Equal("check failed: " + reason, dto.StatusText);
    }

    [Fact]
    public async Task AStartThatFails_IsAFailedRow_AndRedStartFailed()
    {
        var service = Service();
        var t = Add(service);
        _startError = "machine SOREN_NORTH is off";

        var run = await Report(service, t, Counted(2));

        Assert.Equal(TriggerRunOutcome.Failed, run.Outcome);
        Assert.Equal(2, run.Count);
        Assert.Single(_starts);
        Assert.Equal("start failed: machine SOREN_NORTH is off", service.ToDto(service.Store.Find(Tenant, t.Id)!).StatusText);
    }

    [Fact]
    public async Task NoReportForTwoIntervals_IsRedNoChecksRan()
    {
        var service = Service();
        var t = Add(service);
        await Report(service, t, Counted(0));

        _now = _now.AddMinutes(10).AddSeconds(1);

        var dto = service.ToDto(service.Store.Find(Tenant, t.Id)!);
        Assert.Equal(TriggerStatusKind.Red, dto.Status);
        Assert.Equal("no checks ran", dto.StatusText);
    }

    [Fact]
    public async Task TwoReportsAtOnce_StartOneSession()
    {
        var service = Service();
        var t = Add(service);
        _holdStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = service.ReportCheckAsync(Tenant, Director, t.Id, Counted(2), CancellationToken.None);
        var second = service.ReportCheckAsync(Tenant, Director, t.Id, Counted(2), CancellationToken.None);
        await Task.Delay(100);
        _holdStart.SetResult();
        var outcomes = (await Task.WhenAll(first, second)).Select(r => r.Run!.Outcome).OrderBy(o => o).ToList();

        Assert.Equal(new[] { TriggerRunOutcome.SkippedRunning, TriggerRunOutcome.Started }, outcomes);
        Assert.Single(_starts);
    }

    [Fact]
    public async Task AReportFromADirectorThatDoesNotHoldTheTrigger_IsRefused_AndRecordsNothing()
    {
        var service = Service();
        var t = Add(service);
        Assert.Single(service.AssignmentsFor(Tenant, Director, "soren_north"));

        var result = await service.ReportCheckAsync(Tenant, OtherDirector, t.Id, Counted(2), CancellationToken.None);

        Assert.Equal(TriggerReportRefusal.HeldByAnotherDirector, result.Refusal);
        Assert.Empty(service.Store.ListRuns(Tenant, Guid.Parse(t.Id), 10));
        Assert.Empty(_starts);
    }

    [Fact]
    public void Assignments_GoToOneDirectorPerMachine_UntilItsClaimLapses()
    {
        var service = Service();
        Add(service);

        Assert.Single(service.AssignmentsFor(Tenant, Director, "SOREN_NORTH"));
        Assert.Empty(service.AssignmentsFor(Tenant, OtherDirector, "SOREN_NORTH"));
        Assert.Empty(service.AssignmentsFor(Tenant, "dir-elsewhere", "OTHER_MACHINE"));

        // The holder stops asking. Two intervals and a minute later the other Director on the machine takes it.
        _now = _now.Add(TriggerStore.ClaimLapse(300)).AddSeconds(1);
        Assert.Single(service.AssignmentsFor(Tenant, OtherDirector, "SOREN_NORTH"));
        Assert.Empty(service.AssignmentsFor(Tenant, Director, "SOREN_NORTH"));
    }

    [Fact]
    public async Task ATriggerIsTheAccountsOwn()
    {
        var service = Service();
        var t = Add(service);

        Assert.Null(service.Store.Find(OtherTenant, t.Id));
        Assert.Null(service.Store.Find(OtherTenant, "website-new-mail"));
        var result = await service.ReportCheckAsync(OtherTenant, Director, t.Id, Counted(2), CancellationToken.None);
        Assert.Equal(TriggerReportRefusal.NoSuchTrigger, result.Refusal);
        Assert.Empty(_starts);
    }

    [Fact]
    public void ANameTaken_IgnoringCase_IsRefused()
    {
        var service = Service();
        Add(service);
        var req = TriggerDefinitionTests.Valid();
        req.Name = "Website-New-Mail";

        var (created, error) = service.Store.Create(Tenant, req, "session test", _now);

        Assert.Null(created);
        Assert.Equal("a trigger named 'Website-New-Mail' already exists in this account", error);
    }

    [Fact]
    public async Task TheHistory_KeepsAtLeastTheNewest500()
    {
        var service = Service();
        var t = Add(service);
        for (var i = 0; i < 560; i++)
        {
            _now = _now.AddSeconds(1);
            await Report(service, t, Counted(0));
        }

        var runs = service.Store.ListRuns(Tenant, Guid.Parse(t.Id), 1000);
        Assert.InRange(runs.Count, TriggerStore.RunsKept, 560);
        // The newest check is always there.
        Assert.Equal(_now, runs[0].RecordedUtc);
    }
}
