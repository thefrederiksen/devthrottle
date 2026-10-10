using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The run-result routes (Factory Control, step 1), driven through their handlers with a real run history. These pin
/// what only the route knows: the run is read off the CALLING session's key, and resolving a factory's problem is
/// limited to that factory's own sessions and the owner.
/// </summary>
public sealed class CronRunResultEndpointsTests : IDisposable
{
    private const string RunSession = "71000000-0000-4000-8000-000000000001";
    private const string MailSession = "71000000-0000-4000-8000-000000000002";
    private const string OutsideSession = "71000000-0000-4000-8000-000000000003";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly CronRunHistoryStore _runs;
    private readonly CronRunResultService _service;
    private readonly Dictionary<string, CronJobDto> _jobs = new(StringComparer.Ordinal);

    public CronRunResultEndpointsTests()
    {
        var db = _harness.Open();
        _runs = new CronRunHistoryStore(db, _harness.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        var activity = new FactoryActivityRecord(db);
        _service = new CronRunResultService(_runs,
            jobById: id => _jobs.TryGetValue(id, out var j) ? j : null,
            allJobs: () => _jobs.Values.ToList(),
            endingsOf: _ => new Dictionary<string, SessionEndingFact>(StringComparer.Ordinal),
            zoneOf: _ => TimeZoneInfo.FindSystemTimeZoneById("America/Toronto"),
            appendActivity: (tenant, row) => activity.Append(tenant, row, callingActor: null).Id,
            nowUtc: () => new DateTime(2026, 10, 10, 14, 0, 0, DateTimeKind.Utc));
        _jobs["cj_mail"] = new CronJobDto { Id = "cj_mail", Name = "Mail Desk", Factory = "mail", Seat = "mail-desk" };
        _runs.Append("cj_mail", new CronRunRecord
        {
            ScheduledUtc = new DateTime(2026, 10, 10, 13, 0, 0, DateTimeKind.Utc),
            FiredUtc = new DateTime(2026, 10, 10, 13, 0, 0, DateTimeKind.Utc),
            Machine = "SOREN_NORTH",
            SessionId = RunSession,
            InfraStatus = "started",
            TaskStatus = CronRunHistoryStore.TaskStatusUnknown,
            Result = CronRunResults.Pending,
        });
    }

    public void Dispose() => _harness.Dispose();

    private static TenantId? Local(HttpContext _) => TenantId.Local;

    private static DefaultHttpContext Request(string? session = null)
    {
        var ctx = new DefaultHttpContext();
        if (session is not null)
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                new SessionCredentialIdentity(Guid.Parse(session), TenantId.Local, "dir-a");
        return ctx;
    }

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static SessionFactoryLookup FactoryOf(string session) => session switch
    {
        RunSession or MailSession => SessionFactoryLookup.In("mail"),
        _ => SessionFactoryLookup.InNoFactory,
    };

    private IResult Resolve(HttpContext ctx, string runId, string? reason) =>
        CronRunResultEndpoints.Resolve(ctx, runId, new CronRunResolveRequest { Reason = reason }, _service, Local,
            id => _jobs.TryGetValue(id, out var j) ? j : null, FactoryOf, (t, id) => _runs.Find(t, id));

    private string ReportProblem()
    {
        Assert.Equal(200, Status(CronRunResultEndpoints.Result(Request(RunSession),
            new CronRunResultRequest { Result = "problem", Reason = "the inbox would not load" }, _service, Local)));
        return _runs.List("cj_mail").Single().RunId;
    }

    [Fact]
    public void Result_FromTheRunsOwnSession_IsRecordedOnThatRun()
    {
        var result = CronRunResultEndpoints.Result(Request(RunSession), new CronRunResultRequest { Result = "ok" }, _service, Local);

        var answer = Assert.IsType<JsonHttpResult<CronRunResultResponse>>(result).Value!;
        Assert.True(answer.CloseSession);
        Assert.Equal("cj_mail", answer.JobId);
        Assert.Equal(CronRunResults.Ok, _runs.List("cj_mail").Single().Result);
    }

    [Fact]
    public void Result_FromASessionNoScheduleStarted_IsRefused()
    {
        var result = CronRunResultEndpoints.Result(Request(OutsideSession), new CronRunResultRequest { Result = "ok" }, _service, Local);

        Assert.Equal(404, Status(result));
        Assert.Equal(CronRunResults.Pending, _runs.List("cj_mail").Single().Result);
    }

    [Fact]
    public void Result_WithNoSessionKey_IsRefused()
    {
        var result = CronRunResultEndpoints.Result(Request(), new CronRunResultRequest { Result = "ok" }, _service, Local);

        Assert.Equal(403, Status(result));
    }

    [Fact]
    public void Resolve_BySessionOfTheFactory_IsAllowed_AndRecordsWhichSession()
    {
        var runId = ReportProblem();

        var result = Resolve(Request(MailSession), runId, "fixed the login");

        var problem = Assert.IsType<JsonHttpResult<CronRunProblemDto>>(result).Value!;
        Assert.Equal(CronRunProblemStates.Resolved, problem.State);
        Assert.Equal($"session {MailSession}", problem.ResolvedBy);
    }

    [Fact]
    public void Resolve_ByASessionOutsideTheFactory_IsRefused()
    {
        var runId = ReportProblem();

        var result = Resolve(Request(OutsideSession), runId, "not mine to fix");

        Assert.Equal(403, Status(result));
        Assert.Null(_runs.List("cj_mail").Single().ResolvedUtc);
    }

    [Fact]
    public void Resolve_ByAPerson_IsAllowed_AndRecordedAsYou()
    {
        var runId = ReportProblem();

        var problem = Assert.IsType<JsonHttpResult<CronRunProblemDto>>(Resolve(Request(), runId, "known outage")).Value!;

        Assert.Equal(CronRunResultService.ResolvedByYou, problem.ResolvedBy);
    }

    [Fact]
    public void Resolve_WithAnEmptyReason_IsRefused()
    {
        var runId = ReportProblem();

        Assert.Equal(400, Status(Resolve(Request(), runId, "")));
    }

    [Fact]
    public void Problems_ListsOpenByDefault_AndRefusesAStateItDoesNotKnow()
    {
        ReportProblem();

        var open = Assert.IsType<JsonHttpResult<CronRunProblemsDto>>(
            CronRunResultEndpoints.ListProblems(Request(), null, null, _service, Local)).Value!;
        Assert.Equal(1, open.Count);
        Assert.Equal(CronRunProblemStates.Open, open.State);

        Assert.Equal(400, Status(CronRunResultEndpoints.ListProblems(Request(), null, "closed", _service, Local)));
    }
}
