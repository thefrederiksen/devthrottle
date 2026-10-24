using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// THE GATEWAY REPORTS ITS OWN FAILURES UNDER THE ACCOUNT THEY BELONG TO (issue #4403). Each kind is proved to land in
/// the error store as component <c>gateway</c>, under the RIGHT account and never the other one, and to be readable by
/// the administrator through <c>/gateway/admin/director-errors?email=</c> - the read the owner already uses:
///   - a scheduled run that failed, with the reason its run record carries;
///   - a session key refused because it expired, or was revoked, or was presented on a route it may not call;
///   - a device credential refused because it was revoked;
///   - a session key a Director asked to register, refused.
/// And what must NOT be reported: a key nobody holds (it names no account), and a run that went fine. No key and no
/// hash is ever written into a report.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class GatewayAccountFailuresTests : IAsyncLifetime
{
    private readonly AdminVisibilityTestHost _host = new();
    private readonly string? _priorAdmin = Environment.GetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar);

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, AdminVisibilityTestHost.AdminToken);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, _priorAdmin);
        await _host.DisposeAsync();
    }

    private IReadOnlyList<ErrorReportRecord> GatewayRows(string source)
        => _host.Stored().Where(r => r.Component == ErrorReportLimits.Gateway && r.Source == source).ToList();

    /// <summary>What the administrator reads for one account by email - the read this issue exists to complete.</summary>
    private async Task<List<JsonElement>> AdminReadAsync(string email)
    {
        _host.Sink.Flush();
        var response = await _host.SendAsync(HttpMethod.Get,
            DirectorErrorEndpoints.AdminPath + "?component=gateway&since=2d&email=" + Uri.EscapeDataString(email),
            AdminVisibilityTestHost.AdminToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray().ToList();
    }

    // ---- scheduled runs ---------------------------------------------------------------------------------------

    /// <summary>A reporter as the Gateway builds one: it reads what the store already holds to know what was told.</summary>
    private CronRunFailureReporter NewReporter(GatewayAccountFailures? failures = null)
        => new(_host.Runs, failures ?? _host.Failures,
            since => CronRunFailureReporter.StoredScheduleFailures(_host.Errors, since, _host.Now), () => _host.Now);

    private CronRunRecord FailMorningRun()
    {
        _host.AddJob(_host.AccountA, "cj_morning", "Morning report");
        return _host.AddRun(_host.AccountA, "cj_morning", _host.Now, CronRunResults.Problem, CronRunProblems.DidNotRun,
            "it did not start a session", _host.Now);
    }

    [Fact]
    public void AFailureRecordedBeforeTheReporterStarted_IsReported()
    {
        // A Gateway that restarts must not start blind: the last one may have recorded a failure it never stored.
        var run = FailMorningRun();
        _host.Now = _host.Now.AddMinutes(3);

        Assert.Equal(1, NewReporter().Scan());

        var row = Assert.Single(GatewayRows(GatewayAccountFailures.ScheduleSource));
        Assert.Equal(GatewayAccountFailures.RunCorrelation(run.RunId, run.ResultUtc!.Value), row.CorrelationId);
    }

    [Fact]
    public void AfterARestart_AFailureAlreadyStored_IsNotReportedAgain()
    {
        FailMorningRun();
        _host.Now = _host.Now.AddMinutes(1);
        Assert.Equal(1, NewReporter().Scan());
        _host.Sink.Flush();

        _host.Now = _host.Now.AddMinutes(1);
        Assert.Equal(0, NewReporter().Scan());

        Assert.Single(GatewayRows(GatewayAccountFailures.ScheduleSource));
    }

    [Fact]
    public void AfterARestart_AFailureAcceptedButLostWithTheProcess_IsReportedAgain()
    {
        FailMorningRun();
        _host.Now = _host.Now.AddMinutes(1);
        Assert.Equal(1, NewReporter().Scan());
        // The process ends before its sink wrote: that sink's table is gone. A new process has a new sink.
        _host.Failures.Detach();
        using var nextSink = new GatewayErrorSink(_host.Errors, new ErrorIntakeFloods(), () => _host.Now,
            new GatewayErrorSink.ProcessFacts("2.99.0", "linux", "test", "x64", "0123456789abcdef"));
        var nextFailures = new GatewayAccountFailures(() => _host.Now);
        nextFailures.Attach(nextSink);
        _host.Now = _host.Now.AddMinutes(1);

        Assert.Equal(1, NewReporter(nextFailures).Scan());
        nextSink.Flush();

        var row = Assert.Single(_host.Errors.Query(new ErrorReportQuery(_host.Now.AddDays(-1), _host.Now.AddDays(1), Limit: 500))
            .Records, r => r.Source == GatewayAccountFailures.ScheduleSource);
        Assert.Equal(_host.AccountA.Value, row.Account);
    }

    [Fact]
    public void AFailureTheSinkDropped_IsNotMarkedTold_AndTheNextScanReportsIt()
    {
        for (var i = 0; i < GatewayErrorSink.MaxPending; i++)
            Assert.True(_host.Sink.Report(new GatewayError("filler", "logged", $"filler error number {i} FAILED", SessionId: $"s{i}")));
        var reporter = NewReporter();
        FailMorningRun();
        _host.Now = _host.Now.AddMinutes(1);

        Assert.Equal(0, reporter.Scan());
        Assert.Empty(GatewayRows(GatewayAccountFailures.ScheduleSource));

        _host.Now = _host.Now.AddSeconds(30);
        Assert.Equal(1, reporter.Scan());
        Assert.Single(GatewayRows(GatewayAccountFailures.ScheduleSource));
    }

    [Fact]
    public async Task AFailedScheduledRun_IsReportedUnderItsAccount_WithTheReasonTheRunCarries()
    {
        var reporter = NewReporter();
        _host.AddJob(_host.AccountA, "cj_morning", "Morning report");
        _host.AddJob(_host.AccountB, "cj_nightly", "Nightly sweep");
        _host.Now = _host.Now.AddMinutes(1);
        _host.AddRun(_host.AccountA, "cj_morning", _host.Now, CronRunResults.Problem, CronRunProblems.DidNotRun,
            "it did not start a session: no Director is connected for machine workstation-a", _host.Now);
        _host.AddRun(_host.AccountB, "cj_nightly", _host.Now, CronRunResults.Ok, resultUtc: _host.Now, sessionId: "sid-b-ok");

        Assert.Equal(1, reporter.Scan());

        var row = Assert.Single(GatewayRows(GatewayAccountFailures.ScheduleSource));
        Assert.Equal(_host.AccountA.Value, row.Account);
        Assert.Equal(GatewayAccountFailures.ScheduleKind, row.Kind);
        Assert.Equal(CronRunProblems.DidNotRun, row.ErrorCode);
        Assert.Contains("'Morning report' (cj_morning)", row.Message);
        Assert.Contains("no Director is connected for machine workstation-a", row.Message);
        Assert.Equal("schedule cj_morning", row.Surface);

        var forA = await AdminReadAsync(AdminVisibilityTestHost.EmailA);
        Assert.Contains("Morning report", Assert.Single(forA).GetProperty("message").GetString());
        Assert.Empty(await AdminReadAsync(AdminVisibilityTestHost.EmailB));
    }

    [Fact]
    public void AFailedRun_IsReportedOnce_HoweverManyScansSeeIt()
    {
        var reporter = NewReporter();
        _host.AddJob(_host.AccountB, "cj_nightly", "Nightly sweep");
        _host.Now = _host.Now.AddMinutes(1);
        _host.AddRun(_host.AccountB, "cj_nightly", _host.Now, CronRunResults.Problem, CronRunProblems.DidNotReport,
            "its session ended without reporting", _host.Now, sessionId: "sid-b-1");

        Assert.Equal(1, reporter.Scan());
        _host.Now = _host.Now.AddSeconds(30);
        Assert.Equal(0, reporter.Scan());
        _host.Now = _host.Now.AddMinutes(5);
        Assert.Equal(0, reporter.Scan());

        var row = Assert.Single(GatewayRows(GatewayAccountFailures.ScheduleSource));
        Assert.Equal(_host.AccountB.Value, row.Account);
        Assert.Equal("sid-b-1", row.SessionId);
        Assert.Equal(1, row.RepeatCount);
    }

    [Fact]
    public void ARunThatFailsAfterAScan_IsReportedByTheNextOne()
    {
        var reporter = NewReporter();
        _host.AddJob(_host.AccountA, "cj_morning", "Morning report");
        Assert.Equal(0, reporter.Scan());

        _host.Now = _host.Now.AddMinutes(1);
        _host.AddRun(_host.AccountA, "cj_morning", _host.Now, CronRunResults.StartUnknown,
            reason: "whether it started a session is not known: the create timed out", resultUtc: _host.Now);

        Assert.Equal(1, reporter.Scan());
        var row = Assert.Single(GatewayRows(GatewayAccountFailures.ScheduleSource));
        Assert.Equal(_host.AccountA.Value, row.Account);
        Assert.Contains("whether it started is not known", row.Message);
    }

    [Fact]
    public void AFailureRecordedJustBeforeAScanButCommittedAfterIt_IsStillReported()
    {
        // The result time is taken before the write commits; a scan in between must not leave it behind for good.
        var reporter = NewReporter();
        _host.AddJob(_host.AccountA, "cj_morning", "Morning report");
        var stampedAt = _host.Now.AddSeconds(10);
        _host.Now = _host.Now.AddSeconds(20);
        Assert.Equal(0, reporter.Scan());

        _host.AddRun(_host.AccountA, "cj_morning", stampedAt, CronRunResults.Problem, CronRunProblems.DidNotRun,
            "it did not start a session", stampedAt);
        _host.Now = _host.Now.AddSeconds(30);

        Assert.Equal(1, reporter.Scan());
    }

    [Fact]
    public void ABurstLargerThanOnePage_IsReportedInFullOverTheNextScans()
    {
        var reporter = NewReporter();
        _host.AddJob(_host.AccountA, "cj_burst", "Burst");
        var total = CronRunFailureReporter.MaxPerScan + 5;
        for (var i = 0; i < total; i++)
        {
            var at = _host.Now.AddMilliseconds(10 * (i + 1));
            _host.AddRun(_host.AccountA, "cj_burst", at, CronRunResults.Problem, CronRunProblems.DidNotRun, $"reason {i}", at,
                sessionId: $"sid-{i}");
        }
        _host.Now = _host.Now.AddSeconds(10);

        // The sink's own timer writes between scans; here the test does.
        var first = reporter.Scan();
        _host.Sink.Flush();
        var second = reporter.Scan();
        _host.Sink.Flush();
        var third = reporter.Scan();

        Assert.Equal(CronRunFailureReporter.MaxPerScan, first);
        Assert.Equal(5, second);
        Assert.Equal(0, third);
        Assert.Equal(total, GatewayRows(GatewayAccountFailures.ScheduleSource).Count);
    }

    [Fact]
    public void ABurstThatSharesOneResultTime_IsReportedInFull()
    {
        // The runs missed while the Gateway was down are recorded in one write, every one with the same result time.
        var reporter = NewReporter();
        _host.AddJob(_host.AccountA, "cj_missed", "Missed");
        var at = _host.Now.AddSeconds(1);
        var total = CronRunFailureReporter.MaxPerScan + 3;
        for (var i = 0; i < total; i++)
            _host.AddRun(_host.AccountA, "cj_missed", at.AddMinutes(-i), CronRunResults.Problem, CronRunProblems.DidNotRun,
                "the Gateway was not running when it was due", at, sessionId: $"sid-{i}");
        _host.Now = _host.Now.AddSeconds(10);

        Assert.Equal(CronRunFailureReporter.MaxPerScan, reporter.Scan());
        Assert.Equal(3, reporter.Scan());
        Assert.Equal(0, reporter.Scan());
    }

    // ---- refused credentials -------------------------------------------------------------------------------

    [Fact]
    public async Task AnExpiredSessionKey_IsRefused_AndReportedUnderItsAccount()
    {
        var (key, sessionId) = _host.SessionKey(_host.AccountA, TimeSpan.FromMinutes(5));
        _host.Now = _host.Now.AddMinutes(10);

        var response = await _host.SendAsync(HttpMethod.Get, "/healthcheck-for-tests", key);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var row = Assert.Single(GatewayRows(GatewayAccountFailures.CredentialSource));
        Assert.Equal(_host.AccountA.Value, row.Account);
        Assert.Equal(sessionId, row.SessionId);
        Assert.Equal(401, row.HttpStatus);
        Assert.Equal("credential_revoked", row.ErrorCode);
        Assert.Contains("session key", row.Message);
        Assert.Contains("expired", row.Message);
        Assert.Equal("GET /healthcheck-for-tests", row.Surface);
        Assert.DoesNotContain(key, row.Message);
        Assert.Single(await AdminReadAsync(AdminVisibilityTestHost.EmailA));
        Assert.Empty(await AdminReadAsync(AdminVisibilityTestHost.EmailB));
    }

    [Fact]
    public async Task ARevokedSessionKey_IsReportedWithTheReasonItWasRevoked()
    {
        var (key, sessionId) = _host.SessionKey(_host.AccountB, TimeSpan.FromHours(1));
        Assert.True(_host.Sessions.Revoke(_host.AccountB, sessionId, "session-reaped"));

        var response = await _host.SendAsync(HttpMethod.Get, "/healthcheck-for-tests", key);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var row = Assert.Single(GatewayRows(GatewayAccountFailures.CredentialSource));
        Assert.Equal(_host.AccountB.Value, row.Account);
        Assert.Contains("revoked (session-reaped)", row.Message);
    }

    [Fact]
    public async Task ASessionKeyOnARouteItMayNotCall_IsReportedAsA403UnderItsAccount()
    {
        var (key, sessionId) = _host.SessionKey(_host.AccountA, TimeSpan.FromHours(1));

        var response = await _host.SendAsync(HttpMethod.Put, "/gateway/director-errors", key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var row = Assert.Single(GatewayRows(GatewayAccountFailures.CredentialSource));
        Assert.Equal(_host.AccountA.Value, row.Account);
        Assert.Equal(sessionId, row.SessionId);
        Assert.Equal(403, row.HttpStatus);
        Assert.Equal("session_key_out_of_scope", row.ErrorCode);
    }

    [Fact]
    public async Task ARevokedDeviceCredential_IsReportedUnderItsAccount()
    {
        var device = _host.Devices.Register("device-4403", "workstation-a");
        Assert.True(_host.Devices.RevokeDevice("device-4403", "replaced-by-a-new-key"));

        var response = await _host.SendAsync(HttpMethod.Get, "/healthcheck-for-tests", device.DeviceKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var row = Assert.Single(GatewayRows(GatewayAccountFailures.CredentialSource));
        // A self-hosted device belongs to the one local account.
        Assert.Equal(TenantId.Local.Value, row.Account);
        Assert.Contains("device credential", row.Message);
        Assert.Contains("replaced-by-a-new-key", row.Message);
        Assert.DoesNotContain(device.DeviceKey, row.Message);
    }

    [Fact]
    public async Task AKeyNobodyHolds_NamesNoAccount_AndIsNotReported()
    {
        var response = await _host.SendAsync(HttpMethod.Get, "/healthcheck-for-tests", CcDirector.Core.Security.GatewaySessionKey.Mint());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(GatewayRows(GatewayAccountFailures.CredentialSource));
    }

    [Fact]
    public async Task ALiveKeyOnARouteItMayCall_IsNotReported()
    {
        var (key, _) = _host.SessionKey(_host.AccountA, TimeSpan.FromHours(1));

        // The account's own error read: a route a session key may call.
        var response = await _host.SendAsync(HttpMethod.Get, DirectorErrorEndpoints.Path, key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(GatewayRows(GatewayAccountFailures.CredentialSource));
    }

    [Fact]
    public async Task ADeadKeyRetried_IsOneRowPerWindow_AndItsRepeatsAreCountedOnTheNext()
    {
        // A Director whose key is dead retries every few seconds; that must not spend the store's hourly budget.
        var (key, _) = _host.SessionKey(_host.AccountA, TimeSpan.FromMinutes(1));
        _host.Now = _host.Now.AddMinutes(2);
        for (var i = 0; i < 5; i++)
        {
            await _host.SendAsync(HttpMethod.Get, "/healthcheck-for-tests", key);
            _host.Now = _host.Now.AddSeconds(10);
        }
        Assert.Equal(1, Assert.Single(GatewayRows(GatewayAccountFailures.CredentialSource)).RepeatCount);

        _host.Now = _host.Now + GatewayAccountFailures.RepeatWindow;
        await _host.SendAsync(HttpMethod.Get, "/healthcheck-for-tests", key);

        var rows = GatewayRows(GatewayAccountFailures.CredentialSource);
        Assert.Equal(2, rows.Count);
        Assert.Equal(6, rows.Sum(r => r.RepeatCount));
    }

    [Fact]
    public void WhenTheThrottleIsFullAndNothingHasLeftItsWindow_ANewRefusalIsCountedInOneRow_AndTheTableDoesNotGrow()
    {
        var failures = new GatewayAccountFailures(() => _host.Now, maxThrottled: 2);
        failures.Attach(_host.Sink);
        CredentialRefusal Refusal(string session) => new(_host.AccountA.Value, "session key", "the session key expired", 401,
            "credential_revoked", "GET", "/x", session);

        failures.CredentialRefused(Refusal("s1"));
        failures.CredentialRefused(Refusal("s2"));
        failures.CredentialRefused(Refusal("s3"));
        failures.CredentialRefused(Refusal("s4"));
        failures.CredentialRefused(Refusal("s1"));

        var rows = GatewayRows(GatewayAccountFailures.CredentialSource);
        Assert.Equal(new[] { "s1", "s2" }, rows.Where(r => r.SessionId is not null).Select(r => r.SessionId).Order());
        var overflow = Assert.Single(rows, r => r.Message == GatewayAccountFailures.OverflowMessage);
        Assert.Equal("", overflow.Account);
        Assert.Equal(2, overflow.RepeatCount);

        // Once the remembered ones leave their window, a new refusal is kept apart again.
        _host.Now = _host.Now + GatewayAccountFailures.RepeatWindow;
        failures.CredentialRefused(Refusal("s5"));
        Assert.Contains(GatewayRows(GatewayAccountFailures.CredentialSource), r => r.SessionId == "s5");
    }

    // ---- refused registrations -----------------------------------------------------------------------------

    [Fact]
    public void ARefusedSessionKeyRegistration_IsReportedUnderTheConnectionsAccount_WithWhy()
    {
        var registry = new DirectorRegistry(Path.Combine(Path.GetTempPath(), "admin-visibility-hub-" + Guid.NewGuid().ToString("N")));
        var stats = new GatewayInputStatsAggregator(Path.Combine(Path.GetTempPath(), "admin-visibility-stats-" + Guid.NewGuid().ToString("N") + ".db"));
        try
        {
            var hub = new DirectorHub(new PushedSessionStore(() => _host.Now), registry, InputStatsHandle.Available(stats),
                new GatewayStreamRegistry(),
                new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new SingleTenantContext(), _host.Devices, hosted: false),
                sessionKeys: _host.Sessions, accountFailures: _host.Failures)
            { Context = new FakeHubCallerContext("conn-4403") };
            hub.Hello(new DirectorStreamHello { DirectorId = "dir-A", Version = "test" });
            var sessionId = Guid.NewGuid().ToString();
            var hash = CcDirector.Core.Security.GatewaySessionKey.Hash(CcDirector.Core.Security.GatewaySessionKey.Mint());
            hub.RegisterSessionKey(new SessionKeyRegistration { SessionId = sessionId, KeyHash = hash, ExpiresAtUtc = _host.Now.AddHours(1) });
            Assert.True(_host.Sessions.Revoke(TenantId.Local, sessionId, "session-reaped"));

            Assert.Throws<HubException>(() => hub.RegisterSessionKey(
                new SessionKeyRegistration { SessionId = sessionId, KeyHash = hash, ExpiresAtUtc = _host.Now.AddHours(1) }));

            var row = Assert.Single(GatewayRows(GatewayAccountFailures.RegistrationSource));
            Assert.Equal(TenantId.Local.Value, row.Account);
            Assert.Equal(sessionId, row.SessionId);
            Assert.Equal("session_key_not_registered", row.ErrorCode);
            Assert.Contains("dir-A", row.Message);
            Assert.Contains("was revoked (session-reaped)", row.Message);
            Assert.DoesNotContain(hash, row.Message);
        }
        finally
        {
            registry.Dispose();
        }
    }

    /// <summary>The same fake caller as <c>DirectorHubTests</c>: a connection identifier, and a web request context so the
    /// self-host boundary can resolve the Local tenant.</summary>
    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public FakeHubCallerContext(string connectionId)
        {
            ConnectionId = connectionId;
            Features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
                new HttpContextFeatureImpl { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() });
        }

        private sealed class HttpContextFeatureImpl : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
        {
            public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
