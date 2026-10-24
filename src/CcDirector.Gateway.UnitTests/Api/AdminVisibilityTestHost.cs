using System.Net.Http.Headers;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The pieces issue #4403 touches, assembled the way the Gateway assembles them, over an in-memory test server: the real
/// authentication middleware with its session-key and device registries, the administrator's run read and error read,
/// the run store, and the error store fed by the Gateway's own sink. Two accounts, minted by the account registry with
/// made-up addresses, so the administrator read can be filtered by email. The clock is the test's.
/// </summary>
internal sealed class AdminVisibilityTestHost : IAsyncDisposable
{
    public const string AdminToken = "test-admin-service-token-4403";
    public const string MachineToken = "test-machine-token-4403";
    public const string EmailA = "alpha@example.com";
    public const string EmailB = "bravo@example.com";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "admin-visibility-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;

    public DateTime Now { get; set; } = new(2026, 10, 23, 18, 0, 0, DateTimeKind.Utc);

    public GatewayDatabase Db { get; }
    public TenantRegistry Tenants { get; }
    public TenantId AccountA { get; }
    public TenantId AccountB { get; }
    public CronRunHistoryStore Runs { get; }
    public SessionKeyRegistry Sessions { get; }
    public DeviceRegistry Devices { get; }
    public ErrorReportStore Errors { get; }
    public GatewayErrorSink Sink { get; }
    public GatewayAccountFailures Failures { get; }
    public HttpClient Http { get; private set; } = null!;

    /// <summary>Where a reporter keeps its checkpoint in this test.</summary>
    public string CheckpointPath => Path.Combine(_root, "schedule-failure-checkpoint.txt");

    public AdminVisibilityTestHost()
    {
        Db = _harness.Open();
        Tenants = new TenantRegistry(Db);
        AccountA = Tenants.MintOrLookupBySubject("subject-alpha", EmailA);
        AccountB = Tenants.MintOrLookupBySubject("subject-bravo", EmailB);
        Runs = new CronRunHistoryStore(Db, _harness.LegacyPath("cronruns-" + Guid.NewGuid().ToString("N") + ".json"));
        Sessions = new SessionKeyRegistry(Db, isHosted: false, clock: () => Now);
        Devices = _harness.OpenDevices();
        Errors = new ErrorReportStore(_root);
        // Never started: the test flushes it by hand, so nothing waits on a timer.
        Sink = new GatewayErrorSink(Errors, new ErrorIntakeFloods(), () => Now,
            new GatewayErrorSink.ProcessFacts("2.99.0", "linux", "test", "x64", "0123456789abcdef"));
        Failures = new GatewayAccountFailures(() => Now);
        Failures.Attach(Sink);
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        var config = new AuthMiddleware.RequireToken
        {
            Hosted = false, Token = MachineToken, Devices = Devices, Sessions = Sessions, AccountFailures = Failures,
        };
        app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, config, next));
        AdminCronRunEndpoints.Map(app, Runs, Tenants, () => Now);
        DirectorErrorEndpoints.Map(app, Errors,
            new HostedTenantBoundary(new AsyncLocalTenantContext(), Devices, hosted: false), Tenants, () => Now);
        // An ordinary account route a session key may not call, for the out-of-scope refusal.
        app.MapPut("/gateway/director-errors", () => "reached");
        // An ordinary route any credential reaches, for the refusals that happen before routing.
        app.MapGet("/healthcheck-for-tests", () => "reached");
        await app.StartAsync();
        _app = app;
        Http = app.GetTestClient();
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await Http.SendAsync(request);
    }

    /// <summary>A live session key for <paramref name="tenant"/>; returns the raw key and the session id.</summary>
    public (string Key, string SessionId) SessionKey(TenantId tenant, TimeSpan lifetime)
    {
        var key = CcDirector.Core.Security.GatewaySessionKey.Mint();
        var sessionId = Guid.NewGuid().ToString();
        if (!Sessions.Register(tenant, "director-" + tenant.Value, sessionId, CcDirector.Core.Security.GatewaySessionKey.Hash(key), Now + lifetime))
            throw new InvalidOperationException("the test session key was not registered");
        return (key, sessionId);
    }

    public void AddJob(TenantId tenant, string id, string name)
    {
        using var ctx = Db.CreateContext(tenant);
        ctx.CronJobs.Add(new CronJobEntity
        {
            TenantId = tenant.Value, Id = id, Name = name, ScheduleKind = "recurring", CronExpression = "0 6 * * *",
            TimeZoneId = "UTC", CreatedUtc = Now,
        });
        ctx.SaveChanges();
    }

    /// <summary>Record one run of <paramref name="jobId"/> in <paramref name="tenant"/>, as the store records any run.</summary>
    public CronRunRecord AddRun(TenantId tenant, string jobId, DateTime firedUtc, string result,
        string? problem = null, string? reason = null, DateTime? resultUtc = null, string? sessionId = null)
    {
        var record = new CronRunRecord
        {
            ScheduledUtc = firedUtc,
            FiredUtc = firedUtc,
            Machine = "workstation-" + tenant.Value[..Math.Min(4, tenant.Value.Length)],
            TargetDirectorId = "director-1",
            SessionId = sessionId,
            InfraStatus = result == CronRunResults.Problem && problem == CronRunProblems.DidNotRun ? "not-started" : "started",
            TaskStatus = "unknown",
            Result = result,
            Problem = problem,
            ResultReason = reason,
            ResultUtc = resultUtc,
        };
        using var ctx = Db.CreateContext(tenant);
        CronRunHistoryStore.AddInContext(ctx, tenant.Value, jobId, record);
        ctx.SaveChanges();
        return record;
    }

    /// <summary>Every row the error store holds, any account, any component.</summary>
    public IReadOnlyList<ErrorReportRecord> Stored()
    {
        Sink.Flush();
        return Errors.Query(new ErrorReportQuery(Now.AddDays(-2), Now.AddDays(1), Limit: 500)).Records;
    }

    public async ValueTask DisposeAsync()
    {
        Failures.Detach();
        Http?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        Sink.Dispose();
        _harness.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
