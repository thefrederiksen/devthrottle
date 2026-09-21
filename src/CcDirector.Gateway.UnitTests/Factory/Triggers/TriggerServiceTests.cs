using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
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
    private Exception? _startThrows;
    private string? _startUnknown;
    private readonly List<CancellationToken> _startTokens = new();
    private readonly CancellationTokenSource _lifetime = new();

    public void Dispose() => _harness.Dispose();

    private TriggerService Service()
    {
        var store = new TriggerStore(_harness.Open());
        // The record is opened on the harness's default (Local) tenant on purpose: the service must write each row
        // into the TRIGGER's account by naming it, never into whatever tenant happens to be ambient.
        return new TriggerService(store, new FactoryActivityRecord(_harness.Open()),
            async (machine, request, ct) =>
            {
                lock (_starts) { _starts.Add((machine, request)); _startTokens.Add(ct); }
                if (_holdStart is not null) await _holdStart.Task;
                // Like the real create command: a cancelled token cuts the start off mid-create.
                ct.ThrowIfCancellationRequested();
                if (_startThrows is not null) throw _startThrows;
                if (_startUnknown is not null) return TriggerStartAttempt.Unknown(_startUnknown);
                if (_startError is not null) return TriggerStartAttempt.Failed(_startError);
                var id = $"aaaaaaaa-0000-4000-8000-{++_nextSession:D12}";
                _sessions[id] = new SessionDto { SessionId = id, ActivityState = "Working", Name = request.Name };
                return TriggerStartAttempt.Started(id);
            },
            findSession: (_, sid) => _sessions.TryGetValue(sid, out var s) ? s : null,
            findSessionByName: (_, name) => _sessions.Values.FirstOrDefault(s => s.Name == name),
            timeZone: _ => TimeZoneInfo.Utc,
            nowUtc: () => _now,
            startLifetime: _lifetime.Token);
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
        // A check that begins a start has no row until the start returns; wait for it, as the history would.
        var run = result.Starting is { } starting ? await starting : result.Run;
        return TriggerService.ToDto(run!);
    }

    /// <summary>The factory activity rows the account holds, oldest first, read as that account.</summary>
    private List<FactoryActivityDto> Activity(TenantId? tenant = null)
        => new FactoryActivityRecord(_harness.Open(new FixedTenantContext(tenant ?? Tenant)))
            .Query(oldestFirst: true, limit: FactoryActivityRecord.MaxPageSize).Rows;

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
    public async Task ADirectorThatDiedLeavesItsSessionWorking_TheLockHolds_ButTheOwnerSeesItRed()
    {
        // Review finding 1: the Director that ran the session is killed without unregistering, so the Gateway's last
        // row for the session says Working for good. The lock still never starts a second session - that lean is
        // deliberate - but it must not hold with an OK face: past the horizon the trigger turns red and names it.
        var service = Service();
        var t = Add(service);
        var first = await Report(service, t, Counted(2));
        Assert.Equal("Working", _sessions[first.SessionId!].ActivityState);

        _now = _now.AddHours(1);
        Assert.Equal(TriggerRunOutcome.SkippedRunning, (await Report(service, t, Counted(3))).Outcome);
        Assert.Equal("OK", service.ToDto(service.Store.Find(Tenant, t.Id)!).StatusText);

        _now = _now.Add(TriggerStatusFold.LongRunningAfter);
        Assert.Equal(TriggerRunOutcome.SkippedRunning, (await Report(service, t, Counted(5))).Outcome);
        var dto = service.ToDto(service.Store.Find(Tenant, t.Id)!);

        Assert.Equal(TriggerStatusKind.Red, dto.Status);
        Assert.Equal($"session {first.SessionId} has not ended after 7 hours; no new session starts until it does - pause and resume the trigger to release it", dto.StatusText);
        Assert.Single(_starts);
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

        var results = await Task.WhenAll(
            service.ReportCheckAsync(Tenant, Director, t.Id, Counted(2), CancellationToken.None),
            service.ReportCheckAsync(Tenant, Director, t.Id, Counted(2), CancellationToken.None));
        _holdStart.SetResult();

        var starting = Assert.Single(results, r => r.Starting is not null);
        var skipped = Assert.Single(results, r => r.Starting is null);
        Assert.Equal(TriggerRunOutcome.SkippedRunning, skipped.Run!.Outcome);
        Assert.Equal(TriggerRunOutcome.Started, (await starting.Starting!)!.Outcome);
        Assert.Single(_starts);
    }

    // ---------------------------------------------------------------------------------------------------
    // A start outlives the report (the live check, 2026-09-21): a real session start takes 10 to 16 seconds and the
    // Director waits 10 for its report's answer. The report must answer at once, the lock must hold from before the
    // start, and the start must run on the Gateway's lifetime, never the request's.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASlowStartThatOutlivesTheReportRequest_EndsAsOneStartedRow_WithTheLockHeld()
    {
        var service = Service();
        var t = Add(service);
        _holdStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var request = new CancellationTokenSource();

        // The report comes back while the start is still running: no row yet, a start under way.
        var result = await service.ReportCheckAsync(Tenant, Director, t.Id, Counted(1), request.Token);
        Assert.Equal(TriggerReportRefusal.None, result.Refusal);
        Assert.Null(result.Run);
        Assert.NotNull(result.Starting);
        Assert.False(result.Starting!.IsCompleted);
        Assert.Equal(1, result.StartingCount);
        Assert.Empty(service.Store.ListRuns(Tenant, Guid.Parse(t.Id), 10));

        // The Director gives up on its request - what cut every live start off. The start must not care: the fake
        // starter throws on a cancelled token exactly as the real create command does.
        request.Cancel();
        _holdStart.SetResult();
        var run = await result.Starting;

        Assert.NotNull(run);
        Assert.Equal(TriggerRunOutcome.Started, run!.Outcome);
        Assert.Equal(1, run.Count);
        Assert.NotNull(run.SessionId);
        Assert.Equal(_lifetime.Token, Assert.Single(_startTokens));
        Assert.Equal(run.Id, Assert.Single(service.Store.ListRuns(Tenant, Guid.Parse(t.Id), 10)).Id);
        var started = Assert.Single(Activity());
        Assert.Equal(FactoryActivityOutcome.Started, started.Outcome);
        Assert.Equal(run.SessionId, started.SessionId);

        // The lock is that session: the next interval is skipped, and nothing else started.
        Assert.Equal(run.SessionId, service.Store.Find(Tenant, t.Id)!.LastSessionId);
        _now = _now.AddMinutes(1);
        var next = await Report(service, t, Counted(1));
        Assert.Equal(TriggerRunOutcome.SkippedRunning, next.Outcome);
        Assert.Equal(run.SessionId, next.SessionId);
        Assert.Single(_starts);
    }

    [Fact]
    public async Task ACheckWhileAStartIsStillRunning_IsSkipped_AndStartsNothing()
    {
        var service = Service();
        var t = Add(service);
        _holdStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await service.ReportCheckAsync(Tenant, Director, t.Id, Counted(1), CancellationToken.None);
        Assert.NotNull(first.Starting);

        _now = _now.AddMinutes(1);
        var second = await Report(service, t, Counted(1));

        Assert.Equal(TriggerRunOutcome.SkippedRunning, second.Outcome);
        Assert.Null(second.SessionId);
        Assert.Single(_starts);
        var skipped = Assert.Single(Activity());
        Assert.Equal(FactoryActivityOutcome.Skipped, skipped.Outcome);
        Assert.Equal("Checked website-new-mail - counted 1, but its session is still being started, so nothing started", skipped.What);
        Assert.Equal("OK", service.ToDto(service.Store.Find(Tenant, t.Id)!).StatusText);

        _holdStart.SetResult();
        Assert.Equal(TriggerRunOutcome.Started, (await first.Starting!)!.Outcome);
        Assert.Single(_starts);
    }

    [Fact]
    public async Task AFailedSlowStart_ReleasesTheLock_TurnsTheTriggerRed_AndTheNextCheckStartsAgain()
    {
        var service = Service();
        var t = Add(service);
        _holdStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _startError = "machine SOREN_NORTH is off";
        var first = await service.ReportCheckAsync(Tenant, Director, t.Id, Counted(2), CancellationToken.None);
        Assert.NotNull(service.Store.Find(Tenant, t.Id)!.LastStartedUtc); // the lock is held while it runs

        _holdStart.SetResult();
        var failed = await first.Starting!;

        Assert.Equal(TriggerRunOutcome.Failed, failed!.Outcome);
        Assert.Equal(2, failed.Count);
        Assert.Null(failed.SessionId);
        var trigger = service.Store.Find(Tenant, t.Id)!;
        Assert.Null(trigger.LastSessionId);
        Assert.Null(trigger.LastStartedUtc);
        var dto = service.ToDto(trigger);
        Assert.Equal(TriggerStatusKind.Red, dto.Status);
        Assert.Equal("start failed: machine SOREN_NORTH is off", dto.StatusText);

        _holdStart = null;
        _startError = null;
        _now = _now.AddMinutes(1);
        Assert.Equal(TriggerRunOutcome.Started, (await Report(service, t, Counted(2))).Outcome);
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task AStartThatThrows_IsAFailedRow_AndReleasesTheLock()
    {
        var service = Service();
        var t = Add(service);
        _startThrows = new InvalidOperationException("the tunnel to SOREN_NORTH dropped");

        var run = await Report(service, t, Counted(2));

        Assert.Equal(TriggerRunOutcome.Failed, run.Outcome);
        Assert.Equal(TriggerStatusFold.StartFailedPrefix + "the tunnel to SOREN_NORTH dropped", run.Reason);
        Assert.Null(service.Store.Find(Tenant, t.Id)!.LastStartedUtc);
    }

    [Fact]
    public async Task AStartCutOffByTheGatewayStopping_IsAnUnknownOutcome_AndKeepsTheLock()
    {
        // The create may already be on its way to the Director when the Gateway stops, so this is not a definite
        // failure: the lock stays, and the next Gateway finds the session by name or lets the lock lapse.
        var service = Service();
        var t = Add(service);
        _holdStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await service.ReportCheckAsync(Tenant, Director, t.Id, Counted(2), CancellationToken.None);

        _lifetime.Cancel();
        _holdStart.SetResult();
        var run = await result.Starting!;

        Assert.Equal(TriggerRunOutcome.Failed, run!.Outcome);
        Assert.StartsWith(TriggerStatusFold.StartUnknownPrefix + "the Gateway stopped while the session was being started.", run.Reason);
        Assert.NotNull(service.Store.Find(Tenant, t.Id)!.LastStartedUtc);
    }

    // ---------------------------------------------------------------------------------------------------
    // A start whose outcome is UNKNOWN (live, 2026-09-21: the first start after a Gateway restart spent 27 seconds
    // installing skills, the Gateway's 30-second wait ran out, the lock was released, and the Director had created the
    // session anyway - so the next interval started a second one). The lock is held; the session is adopted by name
    // when it shows up; without one inside the start grace the lock lapses. A definite failure still releases at once
    // (AFailedSlowStart_ReleasesTheLock_TurnsTheTriggerRed_AndTheNextCheckStartsAgain).
    // ---------------------------------------------------------------------------------------------------

    private const string DidNotAnswer =
        "The Director on SOREN_NORTH did not answer within 30 seconds. It is not known whether the command was carried out.";

    [Fact]
    public async Task AnUnknownOutcome_KeepsTheLock_IsRed_AndAFailedRowSaysTheLockIsHeld()
    {
        var service = Service();
        var t = Add(service);
        _startUnknown = DidNotAnswer;

        var run = await Report(service, t, Counted(1));

        Assert.Equal(TriggerRunOutcome.Failed, run.Outcome);
        Assert.Equal(1, run.Count);
        Assert.Null(run.SessionId);
        Assert.Equal(TriggerStatusFold.StartUnknownPrefix + DidNotAnswer +
                     " The lock is held until a session named 'Front Desk - website-new-mail - 2026-09-21 12:00' shows up, or for 5 minutes.",
            run.Reason);
        var row = Assert.Single(Activity());
        Assert.Equal(FactoryActivityOutcome.Failed, row.Outcome);
        Assert.StartsWith("Checked website-new-mail - counted 1, but the session start outcome is not known: ", row.What);
        var trigger = service.Store.Find(Tenant, t.Id)!;
        Assert.NotNull(trigger.LastStartedUtc);
        var dto = service.ToDto(trigger);
        Assert.Equal(TriggerStatusKind.Red, dto.Status);
        Assert.Equal(TriggerStatusFold.StartUnknownStatus, dto.StatusText);

        // The next interval, inside the grace, no session reported yet: skipped, nothing started, still red.
        _startUnknown = null;
        _now = _now.AddMinutes(1);
        var next = await Report(service, t, Counted(1));
        Assert.Equal(TriggerRunOutcome.SkippedRunning, next.Outcome);
        Assert.Single(_starts);
        Assert.Equal(TriggerStatusFold.StartUnknownStatus, service.ToDto(service.Store.Find(Tenant, t.Id)!).StatusText);
    }

    [Fact]
    public async Task AnUnknownOutcome_WhoseSessionShowsUp_IsAdopted_AsANewStartedRow_WithTheLockOnIt()
    {
        var service = Service();
        var t = Add(service);
        _startUnknown = DidNotAnswer;
        var unknown = await Report(service, t, Counted(1));
        _startUnknown = null;

        // The Director did create it, and now reports it under the start's name.
        const string created = "bbbbbbbb-0000-4000-8000-000000000001";
        _sessions[created] = new SessionDto
        {
            SessionId = created, ActivityState = "Working", Name = "Front Desk - website-new-mail - 2026-09-21 12:00",
        };
        _now = _now.AddMinutes(1);
        var check = await Report(service, t, Counted(1));

        // Three rows: the unknown start, the adoption, and this check. (The last two share a recorded time here.)
        var runs = service.Store.ListRuns(Tenant, Guid.Parse(t.Id), 10);
        Assert.Equal(3, runs.Count);
        Assert.Equal(unknown.Id, Assert.Single(runs, r => r.Outcome == TriggerRunOutcome.Failed).Id.ToString("D"));
        Assert.Equal(created, Assert.Single(runs, r => r.Outcome == TriggerRunOutcome.Started).SessionId);
        Assert.Equal(TriggerRunOutcome.SkippedRunning, check.Outcome);
        Assert.Equal(created, check.SessionId);
        Assert.Single(_starts);
        var trigger = service.Store.Find(Tenant, t.Id)!;
        Assert.Equal(created, trigger.LastSessionId);
        Assert.Equal("OK", service.ToDto(trigger).StatusText);
        var adopted = Activity().Single(r => r.Outcome == FactoryActivityOutcome.Started);
        Assert.Equal(created, adopted.SessionId);
        Assert.Equal($"Found session {created}, named Front Desk - website-new-mail - 2026-09-21 12:00, from the start of website-new-mail whose outcome was not known, and took it as the lock", adopted.What);

        // The lock is that session: when it ends, the next check starts a new one.
        _sessions[created].ActivityState = "Exited";
        _now = _now.AddMinutes(1);
        Assert.Equal(TriggerRunOutcome.Started, (await Report(service, t, Counted(1))).Outcome);
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task AnUnknownOutcome_WithNoSessionInsideTheGrace_Lapses_WithARowSayingSo_AndTheNextCheckStarts()
    {
        var service = Service();
        var t = Add(service);
        _startUnknown = DidNotAnswer;
        await Report(service, t, Counted(1));
        _startUnknown = null;

        _now = _now.Add(TriggerService.StartGrace);
        var check = await Report(service, t, Counted(1));

        // Three rows: the unknown start, the lapse, and this check's own start.
        var runs = service.Store.ListRuns(Tenant, Guid.Parse(t.Id), 10);
        Assert.Equal(3, runs.Count);
        Assert.Single(runs, r => r.Outcome == TriggerRunOutcome.Started);
        Assert.Single(runs, r => r.Outcome == TriggerRunOutcome.Failed && r.Reason == TriggerStatusFold.StartFailedPrefix +
            "no session named 'Front Desk - website-new-mail - 2026-09-21 12:00' showed up within 5 minutes of a start whose outcome was not known; the lock is released");
        Assert.Single(runs, r => r.Outcome == TriggerRunOutcome.Failed
                                 && r.Reason!.StartsWith(TriggerStatusFold.StartUnknownPrefix, StringComparison.Ordinal));
        Assert.Equal(TriggerRunOutcome.Started, check.Outcome);
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task AStartStillRunning_IsNotTakenForAnUnknownOutcome()
    {
        // In flight, the lock also has no session - but its start is running here, so nothing is looked up or lapsed
        // and the trigger does not read red.
        var service = Service();
        var t = Add(service);
        _holdStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await service.ReportCheckAsync(Tenant, Director, t.Id, Counted(1), CancellationToken.None);

        _now = _now.Add(TriggerService.StartGrace);
        Assert.Equal("OK", service.ToDto(service.Store.Find(Tenant, t.Id)!).StatusText);

        _holdStart.SetResult();
        Assert.Equal(TriggerRunOutcome.Started, (await first.Starting!)!.Outcome);
        Assert.Single(service.Store.ListRuns(Tenant, Guid.Parse(t.Id), 10));
    }

    [Fact]
    public async Task APendingStartLeftByAGatewayThatStopped_LapsesAfterTheGrace()
    {
        // BeginStart took the lock, and the Gateway stopped before the start could write its row. The lock must not
        // hold for good: inside the grace it holds, after it the next check starts.
        var service = Service();
        var t = Add(service);
        Assert.True(service.Store.BeginStart(Tenant, Guid.Parse(t.Id), Director, _now));

        _now = _now.AddMinutes(1);
        Assert.Equal(TriggerRunOutcome.SkippedRunning, (await Report(service, t, Counted(2))).Outcome);
        Assert.Empty(_starts);

        _now = _now.Add(TriggerService.StartGrace);
        Assert.Equal(TriggerRunOutcome.Started, (await Report(service, t, Counted(2))).Outcome);
        Assert.Single(_starts);
    }

    [Fact]
    public async Task ABrokenCheckWhileAStartIsRunning_DoesNotReleaseTheLock()
    {
        var service = Service();
        var t = Add(service);
        _holdStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await service.ReportCheckAsync(Tenant, Director, t.Id, Counted(2), CancellationToken.None);

        _now = _now.AddMinutes(1);
        await Report(service, t, new TriggerCheckReport { CheckedAtUtc = _now, ExitCode = 1, ErrorOutput = "not signed in" });
        _now = _now.AddMinutes(1);
        Assert.Equal(TriggerRunOutcome.SkippedRunning, (await Report(service, t, Counted(2))).Outcome);

        _holdStart.SetResult();
        await first.Starting!;
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

    // ---------------------------------------------------------------------------------------------------
    // Every check is also one factory activity row (the record, pull request 3272).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANothingToDoCheck_WritesOneActivityRow_NamingTheTriggerAsActor()
    {
        var service = Service();
        var t = Add(service);

        var run = await Report(service, t, Counted(0));

        var row = Assert.Single(Activity());
        Assert.Equal(FactoryActivityOutcome.NothingToDo, row.Outcome);
        Assert.Equal("website-factory", row.Factory);
        Assert.Equal("Front Desk", row.FactoryAgent);
        Assert.Equal("trigger:" + t.Id, row.Actor);
        Assert.Equal("website-new-mail", row.Subject);
        Assert.Equal("Checked website-new-mail - nothing to do", row.What);
        Assert.Null(row.SessionId);
        Assert.Equal(run.CheckedUtc, row.OccurredUtc);
    }

    [Fact]
    public async Task AStartedCheck_WritesStarted_WithTheSessionId()
    {
        var service = Service();
        var t = Add(service);

        var run = await Report(service, t, Counted(2));

        var row = Assert.Single(Activity());
        Assert.Equal(FactoryActivityOutcome.Started, row.Outcome);
        Assert.Equal(run.SessionId, row.SessionId);
        Assert.Equal($"Checked website-new-mail - counted 2, started session {run.SessionId}", row.What);
    }

    [Fact]
    public async Task APausedCheck_WritesPaused()
    {
        var service = Service();
        var t = Add(service, paused: true);

        await Report(service, t, Counted(4));

        var row = Assert.Single(Activity());
        Assert.Equal(FactoryActivityOutcome.Paused, row.Outcome);
        Assert.Equal("Checked website-new-mail - counted 4, but the trigger is paused, so nothing started", row.What);
    }

    [Fact]
    public async Task ASkippedRunningCheck_WritesTheRecordsSkipped_WithTheRunningSession()
    {
        var service = Service();
        var t = Add(service);
        var first = await Report(service, t, Counted(2));

        _now = _now.AddMinutes(5);
        await Report(service, t, Counted(3));

        var rows = Activity();
        Assert.Equal(2, rows.Count);
        Assert.Equal(FactoryActivityOutcome.Skipped, rows[1].Outcome);
        Assert.Equal(first.SessionId, rows[1].SessionId);
        Assert.Equal($"Checked website-new-mail - counted 3, but its last session {first.SessionId} is still running, so nothing started", rows[1].What);
    }

    [Fact]
    public async Task ABrokenCheck_WritesFailed_WithTheReason()
    {
        var service = Service();
        var t = Add(service);

        await Report(service, t, new TriggerCheckReport { CheckedAtUtc = _now, ExitCode = 1, ErrorOutput = "not signed in" });

        var row = Assert.Single(Activity());
        Assert.Equal(FactoryActivityOutcome.Failed, row.Outcome);
        Assert.Equal("Checked website-new-mail - the check failed: exit code 1: not signed in", row.What);
    }

    [Fact]
    public async Task AFailedStart_WritesFailed_SayingTheSessionCouldNotBeStarted()
    {
        var service = Service();
        var t = Add(service);
        _startError = "machine SOREN_NORTH is off";

        await Report(service, t, Counted(2));

        var row = Assert.Single(Activity());
        Assert.Equal(FactoryActivityOutcome.Failed, row.Outcome);
        Assert.Null(row.SessionId);
        Assert.Equal("Checked website-new-mail - counted 2, but the session could not be started: machine SOREN_NORTH is off", row.What);
    }

    [Fact]
    public async Task EveryCheck_WritesExactlyOneRow_InTheTriggersOwnAccount()
    {
        var service = Service();
        var t = Add(service);
        await Report(service, t, Counted(0));
        _now = _now.AddMinutes(5);
        await Report(service, t, Counted(2));
        _now = _now.AddMinutes(5);
        await Report(service, t, Counted(2));

        Assert.Equal(new[] { FactoryActivityOutcome.NothingToDo, FactoryActivityOutcome.Started, FactoryActivityOutcome.Skipped },
            Activity().Select(r => r.Outcome).ToArray());
        Assert.Equal(3, service.Store.ListRuns(Tenant, Guid.Parse(t.Id), 10).Count); // the trigger's own history stays too
        Assert.Empty(Activity(TenantId.Local));
        Assert.Empty(Activity(OtherTenant));
    }

    [Fact]
    public async Task AFailureReasonLongerThanTheRecordTakes_IsCut_AndTheRunKeepsItWhole()
    {
        var service = Service();
        var t = Add(service);
        // A check's own output is already cut to a short quote; a machine's refusal to start a session is not.
        var longError = new string('x', 900);
        _startError = longError;

        var run = await Report(service, t, Counted(2));

        var row = Assert.Single(Activity());
        Assert.Equal(FactoryActivityRecord.MaxWhatChars, row.What.Length);
        Assert.EndsWith("...", row.What);
        Assert.EndsWith(longError, run.Reason);
    }

    // ---------------------------------------------------------------------------------------------------
    // The way out of a lock that never lets go: pause, then resume (the Tech Lead's addition to finding 1).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PauseThenResume_ReleasesAStuckLock_RecordsWhoAndWhichSession_AndTheNextCheckStarts()
    {
        var service = Service();
        var t = Add(service);
        var first = await Report(service, t, Counted(2)); // its Director then dies: the row says Working for good
        _now = _now.AddHours(7);
        await Report(service, t, Counted(5));
        Assert.Equal(TriggerStatusKind.Red, service.ToDto(service.Store.Find(Tenant, t.Id)!).Status);

        service.Store.SetPaused(Tenant, t.Id, true);
        var resumed = await service.ResumeAsync(Tenant, t.Id, "device phone p-1", CancellationToken.None);

        Assert.NotNull(resumed);
        Assert.False(resumed!.Paused);
        Assert.Null(resumed.LastSessionId);
        var release = Activity().Last();
        Assert.Equal(FactoryActivityOutcome.Allowed, release.Outcome);
        Assert.Equal("device phone p-1", release.Actor);
        Assert.Equal(first.SessionId, release.SessionId);
        Assert.Equal("website-new-mail", release.Subject);
        Assert.Equal($"device phone p-1 resumed website-new-mail and released its lock, which had been waiting on session {first.SessionId}; the next check that counts work starts a new session", release.What);

        _now = _now.AddMinutes(5);
        var next = await Report(service, t, Counted(5));
        Assert.Equal(TriggerRunOutcome.Started, next.Outcome);
        Assert.Equal(2, _starts.Count);
        Assert.Equal("OK", service.ToDto(service.Store.Find(Tenant, t.Id)!).StatusText);
    }

    [Fact]
    public async Task ResumeOfATriggerThatWasNotPaused_ReleasesNothing_AndRecordsNothing()
    {
        var service = Service();
        var t = Add(service);
        var first = await Report(service, t, Counted(2));

        var resumed = await service.ResumeAsync(Tenant, t.Id, "session s-1", CancellationToken.None);

        Assert.Equal(first.SessionId, resumed!.LastSessionId);
        Assert.DoesNotContain(Activity(), r => r.Outcome == FactoryActivityOutcome.Allowed);
        _now = _now.AddMinutes(5);
        Assert.Equal(TriggerRunOutcome.SkippedRunning, (await Report(service, t, Counted(2))).Outcome);
        Assert.Single(_starts);
    }

    [Fact]
    public async Task ResumeOfAPausedTriggerWithNoSession_RecordsNothing()
    {
        var service = Service();
        var t = Add(service, paused: true);

        var resumed = await service.ResumeAsync(Tenant, t.Id, "session s-1", CancellationToken.None);

        Assert.False(resumed!.Paused);
        Assert.Empty(Activity());
    }

    [Fact]
    public async Task ResumeOfATriggerInAnotherAccount_IsNotFound_AndReleasesNothing()
    {
        var service = Service();
        var t = Add(service);
        var first = await Report(service, t, Counted(2));
        service.Store.SetPaused(Tenant, t.Id, true);

        Assert.Null(await service.ResumeAsync(OtherTenant, t.Id, "session s-9", CancellationToken.None));
        Assert.Equal(first.SessionId, service.Store.Find(Tenant, t.Id)!.LastSessionId);
    }
}
