using System.Security.Claims;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
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
/// <c>DirectorHub.RequestLicenseApproval</c> (License Switching, contract section 8.3), driven through the real hub on a
/// fake caller context with the real relay behind it. What these hold down: refused before Hello; the call returns
/// while the approving machine still holds the command; the tenant, the asking Director and the key the approval is
/// sealed to are the CONNECTION'S - its binding and its own Hello - never anything the request says; and a Gateway
/// that relays nothing does not offer the method.
/// </summary>
public sealed class RequestLicenseApprovalHubTests : IDisposable
{
    private const string Address = "https://claude.example/authorize?challenge=abc";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cc-license-approval-hub-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _sessions = new(() => T0);
    private readonly GatewayInputStatsAggregator _inputStats;
    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _licenses;
    private readonly LicenseApproverStore _approvers;
    private readonly FakeLicenseDirectors _directors = new();
    private readonly LicenseApprovalRelay _relay;

    public RequestLicenseApprovalHubTests()
    {
        Directory.CreateDirectory(_tempDir);
        _registry = new DirectorRegistry(_tempDir);
        _inputStats = new GatewayInputStatsAggregator(Path.Combine(_tempDir, "gateway-stats.db"));
        var db = _harness.Open();
        _licenses = new LicenseStore(db);
        _approvers = new LicenseApproverStore(new TenantSettingsStore(db));
        _relay = new LicenseApprovalRelay(_approvers, new LicenseReadModel(_licenses), _directors.IsConnected, _directors.SendAsync,
            _directors.EnterScope, () => T0, CancellationToken.None);

        // The self-hosted boundary binds every connection to the Local tenant: the approving machine reports there.
        _licenses.ApplyReport(TenantId.Local, "dir-approver", 1, HoldsNothing("the-mac", T0), T0, TimeSpan.Zero);
        _directors.Connect(TenantId.Local, "dir-approver");
        _directors.Connect(TenantId.Local, "dir-target");
        Assert.True(_approvers.Set(TenantId.Local, A, "the-mac", "owner", "work", T0).Accepted);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _harness.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private DirectorHub NewHub(string connectionId, LicenseApprovalRelay? relay) => new(_sessions, _registry, InputStatsHandle.Available(_inputStats),
        new GatewayStreamRegistry(),
        new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false),
        licenseApprovals: relay) { Context = new FakeHubCallerContext(connectionId) };

    private static DirectorStreamHello Hello(string directorId = "dir-target", string? publicHalf = null)
        => new() { DirectorId = directorId, Version = "test", SecretTransferPublicKey = publicHalf ?? PublicHalf };

    private static LicenseApprovalRequest Request(string signInId = SignIn1)
        => new() { SignInId = signInId, AccountEmail = A, AuthorizeUrl = Address, NotAfterUtc = T0.AddMinutes(3) };

    private void TheApproverApproves() => _directors.AnswerWith(sent => sent.Verb == LicenseContract.ApproveVerb
        ? FakeLicenseDirectors.Ok(new LicenseApprovalResult
            { SignInId = ((LicenseApproveRequest)sent.Payload).SignInId, Outcome = LicenseApprovalOutcomes.Approved, SealedApproval = "c2VhbGVk" })
        : FakeLicenseDirectors.Ok(new { delivered = true }));

    [Fact]
    public void RequestLicenseApproval_BeforeHello_IsRefused_AndNothingIsSent()
    {
        var hub = NewHub("conn-1", _relay);

        Assert.Throws<HubException>(() => hub.RequestLicenseApproval(Request()));

        Assert.Empty(_directors.Sends);
    }

    [Fact]
    public async Task RequestLicenseApproval_ReturnsWhileTheApprovingMachineStillHoldsTheCommand()
    {
        var hub = NewHub("conn-1", _relay);
        hub.Hello(Hello());
        var approving = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _directors.Answer = sent => sent.Verb == LicenseContract.ApproveVerb
            ? approving.Task
            : Task.FromResult<DirectorCommandResult?>(FakeLicenseDirectors.Ok(new { delivered = true }));

        // The call is given a limit so that a call that waits for the Director is a red test, not a run that never ends.
        await Task.Run(() => hub.RequestLicenseApproval(Request())).WaitAsync(TimeSpan.FromSeconds(60));

        // The hub's call has returned; the approving Director has the command and no answer has gone back yet.
        await _directors.WaitFor(sent => sent.Verb == LicenseContract.ApproveVerb);
        Assert.Empty(_directors.Of(LicenseContract.ApprovalResultVerb));

        approving.SetResult(FakeLicenseDirectors.Ok(new LicenseApprovalResult
            { SignInId = SignIn1, Outcome = LicenseApprovalOutcomes.Approved, SealedApproval = "c2VhbGVk" }));
        await _relay.WhenIdleAsync();
        Assert.Single(_directors.Of(LicenseContract.ApprovalResultVerb));
    }

    [Fact]
    public async Task RequestLicenseApproval_SealsToTheKeyOfTheCallersOwnHello_AndAnswersTheCallersOwnDirectorInItsOwnTenant()
    {
        var hub = NewHub("conn-1", _relay);
        hub.Hello(Hello());
        TheApproverApproves();

        hub.RequestLicenseApproval(Request());
        await _relay.WhenIdleAsync();

        var approve = Assert.Single(_directors.Of(LicenseContract.ApproveVerb));
        Assert.Equal((TenantId.Local, "dir-approver"), (approve.Scope, approve.DirectorId));
        var asked = (LicenseApproveRequest)approve.Payload;
        Assert.Equal((SignIn1, A, Address, "work", PublicHalf), (asked.SignInId, asked.AccountEmail, asked.AuthorizeUrl, asked.BrowserProfile, asked.SealToRecipient));
        var answer = Assert.Single(_directors.Of(LicenseContract.ApprovalResultVerb));
        Assert.Equal((TenantId.Local, "dir-target"), (answer.Scope, answer.DirectorId));
        Assert.Equal(LicenseApprovalOutcomes.Approved, ((LicenseApprovalResult)answer.Payload).Outcome);
    }

    [Fact]
    public async Task RequestLicenseApproval_FromTwoDirectors_SealsEachToItsOwnKey()
    {
        var otherHalf = Convert.ToBase64String(Enumerable.Range(101, 32).Select(i => (byte)i).ToArray());
        _directors.Connect(TenantId.Local, "dir-other");
        var first = NewHub("conn-1", _relay);
        first.Hello(Hello());
        var second = NewHub("conn-2", _relay);
        second.Hello(Hello("dir-other", otherHalf));
        TheApproverApproves();

        first.RequestLicenseApproval(Request(SignIn1));
        second.RequestLicenseApproval(Request(SignIn2));
        await _relay.WhenIdleAsync();

        var keys = _directors.Of(LicenseContract.ApproveVerb).Select(s => (LicenseApproveRequest)s.Payload).ToDictionary(r => r.SignInId, r => r.SealToRecipient);
        Assert.Equal((PublicHalf, otherHalf), (keys[SignIn1], keys[SignIn2]));
        var answered = _directors.Of(LicenseContract.ApprovalResultVerb).ToDictionary(s => ((LicenseApprovalResult)s.Payload).SignInId, s => s.DirectorId);
        Assert.Equal(("dir-target", "dir-other"), (answered[SignIn1], answered[SignIn2]));
    }

    [Fact]
    public async Task RequestLicenseApproval_FromADirectorWhoseHelloCarriedNoKey_IsRefusedToItAtOnce_AndTheApproverIsAskedNothing()
    {
        var hub = NewHub("conn-1", _relay);
        hub.Hello(new DirectorStreamHello { DirectorId = "dir-target", Version = "test" });

        hub.RequestLicenseApproval(Request());
        await _relay.WhenIdleAsync();

        Assert.Empty(_directors.Of(LicenseContract.ApproveVerb));
        var answer = (LicenseApprovalResult)Assert.Single(_directors.Of(LicenseContract.ApprovalResultVerb)).Payload;
        Assert.Equal((LicenseApprovalOutcomes.Refused, LicenseRenewalErrors.ApprovalFailed), (answer.Outcome, answer.ReasonCode));
    }

    [Fact]
    public void RequestLicenseApproval_WithAnIdentifierThatIsNotOne_IsRefusedOnTheCall()
    {
        var hub = NewHub("conn-1", _relay);
        hub.Hello(Hello());

        Assert.Throws<HubException>(() => hub.RequestLicenseApproval(Request("not-an-identifier")));
        Assert.Throws<HubException>(() => hub.RequestLicenseApproval(null!));

        Assert.Empty(_directors.Sends);
    }

    [Fact]
    public void RequestLicenseApproval_WithAnAccountOrAnAddressThatIsNotOne_FailsTheCall_WithASentenceThatQuotesNothing()
    {
        var hub = NewHub("conn-1", _relay);
        hub.Hello(Hello());
        var noAccount = Request();
        noAccount.AccountEmail = "account-a@example.com\nINJECTED";
        var noAddress = Request();
        noAddress.AuthorizeUrl = "https://claude.example/" + new string('a', 100_000);

        var first = Assert.Throws<HubException>(() => hub.RequestLicenseApproval(noAccount));
        var second = Assert.Throws<HubException>(() => hub.RequestLicenseApproval(noAddress));

        Assert.DoesNotContain("INJECTED", first.Message);
        Assert.DoesNotContain("claude.example", second.Message);
        Assert.Empty(_directors.Sends);
        Assert.Equal(0, _relay.InFlightCount());
    }

    [Fact]
    public async Task RequestLicenseApproval_PastTheAccountsLimit_FailsTheCall_AndTheGatewayHoldsNothingForIt()
    {
        var approving = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _directors.Answer = sent => sent.Verb == LicenseContract.ApproveVerb
            ? approving.Task
            : Task.FromResult<DirectorCommandResult?>(FakeLicenseDirectors.Ok(new { delivered = true }));
        var hub = NewHub("conn-1", _relay);
        hub.Hello(Hello());
        for (var i = 0; i < LicenseApprovalRelay.MaxInFlightPerTenant; i++)
            hub.RequestLicenseApproval(Request(i.ToString("x").PadLeft(32, 'a')));

        for (var i = 0; i < 100; i++)
        {
            var busy = Assert.Throws<HubException>(() => hub.RequestLicenseApproval(Request(i.ToString("x").PadLeft(32, 'b'))));
            Assert.StartsWith("The Gateway is already relaying 8 approvals for this account.", busy.Message);
        }

        Assert.Equal(LicenseApprovalRelay.MaxInFlightPerTenant, _relay.InFlightCount());
        Assert.Empty(_directors.Of(LicenseContract.ApprovalResultVerb));
        approving.SetResult(DirectorCommandResult.Fail(DirectorCommandStatus.TunnelDropped, "dropped"));
        await _relay.WhenIdleAsync();
        Assert.Equal(0, _relay.InFlightCount());
    }

    [Fact]
    public void GatewayCapabilities_ListRequestLicenseApproval()
        => Assert.Contains(LicenseContract.RequestApprovalHubMethod, NewHub("conn-1", _relay).Hello(Hello())!.HubMethods);

    [Fact]
    public void AGatewayThatRelaysNoApprovals_DoesNotOfferTheMethod_AndRefusesACallToIt()
    {
        var hub = NewHub("conn-1", relay: null);

        var methods = hub.Hello(Hello())!.HubMethods;

        Assert.DoesNotContain(LicenseContract.RequestApprovalHubMethod, methods);
        Assert.Contains("PushRepoSnapshot", methods);
        Assert.Throws<HubException>(() => hub.RequestLicenseApproval(Request()));
        Assert.Empty(_directors.Sends);
    }

    /// <summary>The same fake caller as <c>PushLicenseUsageHubTests</c>: a connection identifier, its own items, and a
    /// web request context so the self-host boundary can resolve the Local tenant.</summary>
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
