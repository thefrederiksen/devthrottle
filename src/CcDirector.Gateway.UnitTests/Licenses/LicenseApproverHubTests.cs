using System.Security.Claims;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseSignInTestData;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// <c>DirectorHub.GetLicenseApproverAssignments</c> and <c>DirectorHub.ReportLicenseApproverChecks</c> (License Switching
/// phase 6, Track C), driven through the real hub on a fake caller context with the real registration behind it. What
/// these hold down: both are refused before Hello; the tenant and the Director are the connection's binding, so a
/// connection bound as one Director cannot act as another; a report that is not one fails the call with the
/// registration's own sentence; and a Gateway that keeps no registrations does not offer either method.
/// </summary>
public sealed class LicenseApproverHubTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cc-license-approver-hub-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _sessions = new(() => T0);
    private readonly GatewayInputStatsAggregator _inputStats;
    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseApproverStore _approvers;
    private readonly LicenseApproverRegistration _registration;
    private readonly CcDirector.Gateway.Pairing.DeviceRegistry _devices;

    public LicenseApproverHubTests()
    {
        Directory.CreateDirectory(_tempDir);
        _registry = new DirectorRegistry(_tempDir);
        _inputStats = new GatewayInputStatsAggregator(Path.Combine(_tempDir, "gateway-stats.db"));
        var db = _harness.Open();
        var licenses = new LicenseStore(db);
        _devices = _harness.OpenDevices();
        _approvers = new LicenseApproverStore(new TenantSettingsStore(db));
        _registration = new LicenseApproverRegistration(_approvers, new LicenseReadModel(licenses), () => T0);

        // The self-hosted boundary binds every connection to the Local tenant: machine A reports there, holding account A.
        licenses.ApplyReport(TenantId.Local, "dir-a", 1, Report("machine-a", T0, Holding(A, active: true)), T0, TimeSpan.Zero);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _harness.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private DirectorHub NewHub(string connectionId, LicenseApproverRegistration? registration) => new(_sessions, _registry,
        InputStatsHandle.Available(_inputStats), new GatewayStreamRegistry(),
        new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new SingleTenantContext(), _devices, hosted: false),
        licenseApproverRegistration: registration) { Context = new FakeHubCallerContext(connectionId) };

    private static DirectorStreamHello Hello(string directorId) => new() { DirectorId = directorId, Version = "test", SecretTransferPublicKey = PublicHalf };

    private static LicenseApproverCheckReport CanApproveA(int rows = 1) => new()
    {
        OffersApprovers = true,
        Rows = Enumerable.Range(0, rows).Select(_ => new LicenseApproverCheckRow
        {
            BrowserProfile = "work", AccountEmail = A, ShownAccount = A, State = LicenseApproverStates.CanApprove, CheckedAtUtc = T0,
        }).ToList(),
    };

    [Fact]
    public void BothMethods_BeforeHello_AreRefused_AndNothingIsStored()
    {
        var hub = NewHub("conn-1", _registration);

        Assert.Throws<HubException>(() => hub.GetLicenseApproverAssignments());
        Assert.Throws<HubException>(() => hub.ReportLicenseApproverChecks(CanApproveA()));

        Assert.Empty(_approvers.List(TenantId.Local));
    }

    [Fact]
    public void ReportLicenseApproverChecks_FromTheDirectorThatReportsForTheMachine_Registers_AndItsAssignmentsListIt()
    {
        var hub = NewHub("conn-1", _registration);
        hub.Hello(Hello("dir-a"));

        var answer = hub.ReportLicenseApproverChecks(CanApproveA());

        Assert.Equal(LicenseApproverCheckOutcomes.Registered, Assert.Single(answer.Rows).Outcome);
        Assert.Equal("machine-a", _approvers.Find(TenantId.Local, A)!.MachineName);
        var assigned = Assert.Single(hub.GetLicenseApproverAssignments().Rows);
        Assert.Equal((A, "work", LicenseApproverSources.Registered), (assigned.AccountEmail, assigned.BrowserProfile, assigned.Source));
    }

    [Fact]
    public void ReportLicenseApproverChecks_OnAConnectionBoundAsAnotherDirector_RegistersNothing()
    {
        var hub = NewHub("conn-2", _registration);
        hub.Hello(Hello("dir-other"));

        var answer = hub.ReportLicenseApproverChecks(CanApproveA());

        Assert.Equal(LicenseApproverRefusals.NotTheReportingDirector, Assert.Single(answer.Rows).Reason);
        Assert.Empty(_approvers.List(TenantId.Local));
        Assert.Empty(hub.GetLicenseApproverAssignments().Rows);
    }

    [Fact]
    public void ReportLicenseApproverChecks_WithTooManyRows_FailsTheCall_WithTheRegistrationsSentence()
    {
        var hub = NewHub("conn-1", _registration);
        hub.Hello(Hello("dir-a"));

        var refused = Assert.Throws<HubException>(() => hub.ReportLicenseApproverChecks(CanApproveA(LicenseApproverContract.MaxCheckRows + 1)));

        Assert.StartsWith($"A report carries at most {LicenseApproverContract.MaxCheckRows} rows.", refused.Message);
        Assert.Empty(_approvers.List(TenantId.Local));
    }

    [Fact]
    public void Hello_OffersBothMethods_WhenTheGatewayKeepsRegistrations()
    {
        var methods = NewHub("conn-1", _registration).Hello(Hello("dir-a"))!.HubMethods;

        Assert.Contains(LicenseApproverContract.GetAssignmentsHubMethod, methods);
        Assert.Contains(LicenseApproverContract.ReportChecksHubMethod, methods);
    }

    [Fact]
    public void AGatewayThatKeepsNoRegistrations_OffersNeitherMethod_AndRefusesBoth()
    {
        var hub = NewHub("conn-1", null);
        var methods = hub.Hello(Hello("dir-a"))!.HubMethods;

        Assert.DoesNotContain(LicenseApproverContract.GetAssignmentsHubMethod, methods);
        Assert.DoesNotContain(LicenseApproverContract.ReportChecksHubMethod, methods);
        Assert.Throws<HubException>(() => hub.GetLicenseApproverAssignments());
        Assert.Throws<HubException>(() => hub.ReportLicenseApproverChecks(CanApproveA()));
    }

    [Fact]
    public void TheHubMethodNames_AreTheContractsNames()
    {
        Assert.Equal(LicenseApproverContract.GetAssignmentsHubMethod, nameof(DirectorHub.GetLicenseApproverAssignments));
        Assert.Equal(LicenseApproverContract.ReportChecksHubMethod, nameof(DirectorHub.ReportLicenseApproverChecks));
    }

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
