using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// SIGN-IN APPROVAL AND SIGN-IN FROM THE COCKPIT ON A REAL HOSTED GATEWAY, END TO END (License Switching, contract
/// section 8). Two accounts. Alice has a machine that asks and a machine that approves, each with a Director on the
/// real tunnel; Bob has one Director. Reports go in through <c>DirectorHub.PushLicenseUsage</c>, the request through
/// <c>DirectorHub.RequestLicenseApproval</c>, the approver and the sign-ins through the routes over real HTTP, and
/// each Director answers the commands that reach it.
///
/// What this holds down that the unit tests cannot: the host really builds the relay and the sign-in service over its
/// own <c>SendCommandAsync</c> and enters the tenant's scope on their background work, so on the HOSTED Gateway the
/// commands are delivered rather than dropped; the hub hands the relay the key of the caller's own Hello; the routes
/// are mapped, reachable by the owner and refused to a session key; and nothing of one account reaches a Director of
/// the other. Nothing here waits on a clock: background work is awaited through <c>WhenIdleAsync</c>.
/// </summary>
public sealed class LicenseSignInHostedTests : IAsyncLifetime
{
    private const string A = "account-a@example.com";
    private const string B = "account-b@example.com";
    private const string Address = "https://claude.example/authorize?challenge=abc";
    private const string SealedValue = "c2VhbGVkLXRvLXRoZS10YXJnZXQ=";
    private const string TargetMachine = "TARGET_OF_ALICE";
    private const string ApprovingMachine = "MAC_OF_ALICE";
    private const string BobMachine = "MACHINE_OF_BOB";
    private const string SignIn1 = "11111111111111111111111111111111";

    private static readonly string TargetPublicHalf = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string BobPublicHalf = Convert.ToBase64String(Enumerable.Range(101, 32).Select(i => (byte)i).ToArray());

    /// <summary>The one moment every test here happens at.</summary>
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The Gateway's license clock: <see cref="Now"/> until a test moves it.</summary>
    private sealed class TestClock : TimeProvider
    {
        public DateTime UtcNow { get; set; } = Now;

        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }

    private readonly TestClock _clock = new();
    private readonly List<(TenantId Tenant, LicenseOwnerEmail Email)> _ownerEmails = new();

    private GatewayHost _gateway = null!;
    private HostedTestDevice _alice;
    private HostedTestDevice _bob;
    private FakeTunnelDirector _target = null!;
    private FakeTunnelDirector _approver = null!;
    private FakeTunnelDirector _bobDirector = null!;
    private HttpClient _aliceOwner = null!;
    private HttpClient _bobOwner = null!;
    private HttpClient _aliceSession = null!;
    private HttpClient _aliceMachine = null!;
    private HttpClient _noCredential = null!;
    private readonly string _run = Guid.NewGuid().ToString("N")[..12];

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-license-sign-in-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        _gateway.LicenseClock = _clock;
        // Every owner email of this host is taken here: no test of this class reaches the account service.
        _gateway.LicenseOwnerMail = (tenant, email, _) =>
        {
            lock (_ownerEmails) _ownerEmails.Add((tenant, email));
            return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
        };
        await _gateway.StartAsync();

        // The Gateway database is shared by every host this test process starts, so the accounts and the Directors
        // are new ones for each test.
        _alice = HostedTestEnrollment.Enroll(_gateway, $"sub-alice-{_run}", $"alice-{_run}@example.com", $"dev-a-{_run}", "MA");
        _bob = HostedTestEnrollment.Enroll(_gateway, $"sub-bob-{_run}", $"bob-{_run}@example.com", $"dev-b-{_run}", "MB");
        _target = await FakeTunnelDirector.StartAsync(_gateway, _alice.DeviceKey, $"d-target-{_run}", TargetMachine,
            secretTransferPublicKey: TargetPublicHalf);
        _approver = await FakeTunnelDirector.StartAsync(_gateway, _alice.DeviceKey, $"d-approver-{_run}", ApprovingMachine);
        _bobDirector = await FakeTunnelDirector.StartAsync(_gateway, _bob.DeviceKey, $"d-bob-{_run}", BobMachine,
            secretTransferPublicKey: BobPublicHalf);

        _aliceOwner = Client(_gateway.Devices.RegisterForTenant(_alice.Tenant, $"sub-alice-{_run}", $"dev-owner-a-{_run}", "OWNER-A", deviceType: "browser").DeviceKey);
        _bobOwner = Client(_gateway.Devices.RegisterForTenant(_bob.Tenant, $"sub-bob-{_run}", $"dev-owner-b-{_run}", "OWNER-B", deviceType: "browser").DeviceKey);
        var sessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_alice.Tenant, _target.DirectorId, Guid.NewGuid().ToString(), GatewaySessionKey.Hash(sessionKey),
            new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        _aliceSession = Client(sessionKey);
        _aliceMachine = Client(_alice.DeviceKey);
        _noCredential = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // The asking machine is on A; the approving machine holds no sign-in at all (contract section 2.4); Bob is on A.
        await _target.PushLicenseUsageAsync(Report(TargetMachine, Holding(A, true)));
        await _approver.PushLicenseUsageAsync(Report(ApprovingMachine));
        await _bobDirector.PushLicenseUsageAsync(Report(BobMachine, Holding(A, true)));
        Assert.Equal(new[] { ApprovingMachine, TargetMachine }, _gateway.LicenseReads.Read(_alice.Tenant, Now).Machines.Select(m => m.MachineName).Order());
    }

    public async Task DisposeAsync()
    {
        foreach (var http in new[] { _aliceOwner, _bobOwner, _aliceSession, _aliceMachine, _noCredential })
            http.Dispose();
        await _target.DisposeAsync();
        await _approver.DisposeAsync();
        await _bobDirector.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    // ---- plumbing ----

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private static LicenseHolding Holding(string email, bool active) => new()
    {
        AccountEmail = email,
        IsActive = active,
        Plan = "max",
        FiveHour = new LicenseWindow { Utilization = 10, ResetsAtUtc = Now.AddHours(2) },
        SevenDay = new LicenseWindow { Utilization = 20, ResetsAtUtc = Now.AddDays(4) },
        ReadAtUtc = Now,
    };

    private static LicenseUsageReport Report(string machine, params LicenseHolding[] holdings) => new()
    {
        MachineName = machine,
        OsUser = "test",
        SentAtUtc = Now,
        Trigger = LicenseReportTriggers.Connected,
        Holdings = holdings.ToList(),
    };

    private static LicenseApprovalRequest Request(string signInId = SignIn1, string account = A)
        => new() { SignInId = signInId, AccountEmail = account, AuthorizeUrl = Address, NotAfterUtc = Now.AddMinutes(3) };

    private static T Payload<T>(DirectorCommand command)
        => JsonSerializer.Deserialize<T>(command.PayloadJson, FakeTunnelDirector.WebJson)
           ?? throw new InvalidOperationException($"The {command.Verb} command carried no payload.");

    /// <summary>The Director approves every sign-in it is asked to and hands back a sealed value.</summary>
    private static void Approves(FakeTunnelDirector director) => director.OnCommand(command => command.Verb == LicenseContract.ApproveVerb
        ? FakeTunnelDirector.Ok(new LicenseApprovalResult
        {
            SignInId = Payload<LicenseApproveRequest>(command).SignInId,
            Outcome = LicenseApprovalOutcomes.Approved,
            Message = "Approved in the browser profile.",
            SealedApproval = SealedValue,
        })
        : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {command.Verb}"));

    /// <summary>The Director takes the answer to its request, and answers any sign-in it is sent with <paramref name="signIn"/>.</summary>
    private static void TakesAnswers(FakeTunnelDirector director, Func<LicenseSignInRequest, LicenseSignInResult>? signIn = null)
        => director.OnCommand(command => command.Verb switch
        {
            LicenseContract.ApprovalResultVerb => FakeTunnelDirector.Ok(new { delivered = true }),
            LicenseContract.SignInVerb when signIn is not null => FakeTunnelDirector.Ok(signIn(Payload<LicenseSignInRequest>(command))),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {command.Verb}"),
        });

    private static LicenseApprovalResult TheAnswerTo(FakeTunnelDirector director)
        => Payload<LicenseApprovalResult>(Assert.Single(director.AllOf(LicenseContract.ApprovalResultVerb)));

    private static Task<HttpResponseMessage> SetApprover(HttpClient http, string account, string machine, string profile = "work")
        => http.PutAsJsonAsync(LicenseSignInRoutes.Approvers.TrimStart('/'),
            new LicenseSetApproverRequest { AccountEmail = account, MachineName = machine, OsUser = "test", BrowserProfile = profile });

    private static Task<HttpResponseMessage> Post(HttpClient http, string route, string account, string machineId)
        => http.PostAsJsonAsync(route.TrimStart('/'), new LicenseStartSignInRequest { AccountEmail = account, MachineId = machineId });

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> Get(HttpClient http, string route)
    {
        using var response = await http.GetAsync(route.TrimStart('/'));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    private string MachineId(TenantId tenant, string machine)
        => _gateway.LicenseReads.Read(tenant, Now).Machines.Single(m => m.MachineName == machine).MachineId.ToString("N");

    private async Task AliceSetsHerApprover()
    {
        using var set = await SetApprover(_aliceOwner, A, ApprovingMachine);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        // Just set, it has never been checked, and an absence is never read as healthy.
        Assert.Equal("cannot-approve", (await Json(set)).GetProperty("status").GetString());
        // The approving machine's Director checks the profile and reports it over its own hub connection.
        var answer = await _approver.ReportLicenseApproverChecksAsync(CanApprove(A));
        Assert.Equal(LicenseApproverCheckOutcomes.HealthUpdated, Assert.Single(answer.Rows).Outcome);
        Assert.Equal("ready", (await Get(_aliceOwner, LicenseSignInRoutes.Approvers)).GetProperty("approvers")[0].GetProperty("status").GetString());
    }

    private static LicenseApproverCheckReport CanApprove(string account, string profile = "work") => new()
    {
        OffersApprovers = true,
        Rows = [new LicenseApproverCheckRow { BrowserProfile = profile, AccountEmail = account, ShownAccount = account, State = LicenseApproverStates.CanApprove, CheckedAtUtc = Now }],
    };

    // ---- the relay ----

    [Fact]
    public void Hello_OffersRequestLicenseApproval()
        => Assert.Contains(LicenseContract.RequestApprovalHubMethod, _target.Capabilities!.HubMethods);

    [Fact]
    public async Task ARequest_IsApprovedOnTheApprovingMachine_SealedToTheAskingDirectorsOwnKey_AndAnsweredToIt()
    {
        await AliceSetsHerApprover();
        Approves(_approver);
        TakesAnswers(_target);

        await _target.RequestLicenseApprovalAsync(Request());
        await _gateway.LicenseApprovals.WhenIdleAsync();

        var asked = Assert.Single(_approver.AllOf(LicenseContract.ApproveVerb));
        Assert.Equal("", asked.SessionId);
        var approve = Payload<LicenseApproveRequest>(asked);
        Assert.Equal((SignIn1, A, Address, "work", TargetPublicHalf),
            (approve.SignInId, approve.AccountEmail, approve.AuthorizeUrl, approve.BrowserProfile, approve.SealToRecipient));
        Assert.Equal(Now.AddMinutes(3), approve.NotAfterUtc);

        var answer = TheAnswerTo(_target);
        Assert.Equal((SignIn1, LicenseApprovalOutcomes.Approved, SealedValue), (answer.SignInId, answer.Outcome, answer.SealedApproval));
        Assert.Null(answer.ReasonCode);

        // Nothing of it reached the other account, or went back to the machine that approved.
        Assert.Empty(_bobDirector.AllOf(LicenseContract.ApproveVerb));
        Assert.Empty(_bobDirector.AllOf(LicenseContract.ApprovalResultVerb));
        Assert.Empty(_approver.AllOf(LicenseContract.ApprovalResultVerb));
    }

    [Fact]
    public async Task ARequest_ForAnAccountWithNoApproverSet_IsAnsweredAtOnce_RefusedNoApprover()
    {
        Approves(_approver);
        TakesAnswers(_target);

        await _target.RequestLicenseApprovalAsync(Request());
        await _gateway.LicenseApprovals.WhenIdleAsync();

        var answer = TheAnswerTo(_target);
        Assert.Equal((SignIn1, LicenseApprovalOutcomes.Refused, LicenseRenewalErrors.NoApprover), (answer.SignInId, answer.Outcome, answer.ReasonCode));
        Assert.Null(answer.SealedApproval);
        Assert.Empty(_approver.AllOf(LicenseContract.ApproveVerb));
    }

    [Fact]
    public async Task ARequest_WhoseApprovingMachineHasNoDirectorConnected_IsAnsweredAtOnce_RefusedApproverOffline()
    {
        // A machine that reported and whose Director is not on the tunnel: its report goes in the way the hub puts it in.
        var offline = $"d-offline-{_run}";
        _gateway.LicenseUsage.RegisterConnection(_alice.Tenant, offline, "conn-" + offline);
        Assert.Equal(LicenseIngestOutcome.Accepted,
            _gateway.LicenseUsage.Ingest(_alice.Tenant, offline, "conn-" + offline, 1, Report("OFFLINE_MAC_OF_ALICE"), Now));
        using var set = await SetApprover(_aliceOwner, A, "OFFLINE_MAC_OF_ALICE");
        Assert.Equal("not-connected", (await Json(set)).GetProperty("status").GetString());
        Approves(_approver);
        TakesAnswers(_target);

        await _target.RequestLicenseApprovalAsync(Request());
        await _gateway.LicenseApprovals.WhenIdleAsync();

        var answer = TheAnswerTo(_target);
        Assert.Equal((LicenseApprovalOutcomes.Refused, LicenseRenewalErrors.ApproverOffline), (answer.Outcome, answer.ReasonCode));
        Assert.Empty(_approver.AllOf(LicenseContract.ApproveVerb));
    }

    [Fact]
    public async Task OneAccount_NeverReachesAnApproverOrADirectorOfAnother()
    {
        // Alice's approver for A is her own machine. Bob sets, for the same account, a machine of that same NAME.
        await AliceSetsHerApprover();
        using var bobSet = await SetApprover(_bobOwner, A, ApprovingMachine);
        Assert.Equal("never-reported", (await Json(bobSet)).GetProperty("status").GetString());
        Approves(_approver);
        TakesAnswers(_bobDirector);

        await _bobDirector.RequestLicenseApprovalAsync(Request());
        await _gateway.LicenseApprovals.WhenIdleAsync();

        // Bob is refused: in HIS account no Director is connected for that machine. Alice's machine was asked nothing.
        var answer = TheAnswerTo(_bobDirector);
        Assert.Equal((LicenseApprovalOutcomes.Refused, LicenseRenewalErrors.ApproverOffline), (answer.Outcome, answer.ReasonCode));
        Assert.Empty(_approver.AllOf(LicenseContract.ApproveVerb));
        Assert.Empty(_target.AllOf(LicenseContract.ApprovalResultVerb));

        // And each owner reads only their own approver.
        var ofAlice = (await Get(_aliceOwner, LicenseSignInRoutes.Approvers)).GetProperty("approvers");
        var ofBob = (await Get(_bobOwner, LicenseSignInRoutes.Approvers)).GetProperty("approvers");
        Assert.Equal(("ready", 1), (ofAlice[0].GetProperty("status").GetString(), ofAlice.GetArrayLength()));
        Assert.Equal(("never-reported", 1), (ofBob[0].GetProperty("status").GetString(), ofBob.GetArrayLength()));
    }

    [Fact]
    public async Task AnAccountWithNoApprover_IsNotServedByTheApproverOfAnother()
    {
        await AliceSetsHerApprover();
        Approves(_approver);
        TakesAnswers(_bobDirector);

        await _bobDirector.RequestLicenseApprovalAsync(Request());
        await _gateway.LicenseApprovals.WhenIdleAsync();

        Assert.Equal(LicenseRenewalErrors.NoApprover, TheAnswerTo(_bobDirector).ReasonCode);
        Assert.Empty(_approver.AllOf(LicenseContract.ApproveVerb));
    }

    // ---- the approver routes ----

    [Fact]
    public async Task TheApproverRoutes_AreTheOwnersToWrite_AndASessionKeyMayOnlyRead()
    {
        await AliceSetsHerApprover();

        // A session key reads who approves, in its own account.
        var read = await Get(_aliceSession, LicenseSignInRoutes.Approvers);
        Assert.Equal(A, read.GetProperty("approvers")[0].GetProperty("accountEmail").GetString());

        // It may not set one, remove one, or start a sign-in.
        using var set = await SetApprover(_aliceSession, B, ApprovingMachine);
        using var remove = await _aliceSession.DeleteAsync($"gateway/licenses/approvers?accountEmail={Uri.EscapeDataString(A)}");
        using var add = await Post(_aliceSession, LicenseSignInRoutes.AddSpare, B, MachineId(_alice.Tenant, TargetMachine));
        using var renew = await Post(_aliceSession, LicenseSignInRoutes.Renew, A, MachineId(_alice.Tenant, TargetMachine));
        Assert.All(new[] { set, remove, add, renew }, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        Assert.Equal(1, (await Get(_aliceOwner, LicenseSignInRoutes.Approvers)).GetProperty("approvers").GetArrayLength());
        Assert.Empty(_target.AllOf(LicenseContract.SignInVerb));

        // A Director's own credential reads, and is refused the same four writes: they are for a signed-in person only.
        Assert.Equal(1, (await Get(_aliceMachine, LicenseSignInRoutes.Approvers)).GetProperty("approvers").GetArrayLength());
        using var machineSet = await SetApprover(_aliceMachine, B, ApprovingMachine);
        using var machineRemove = await _aliceMachine.DeleteAsync($"gateway/licenses/approvers?accountEmail={Uri.EscapeDataString(A)}");
        using var machineAdd = await Post(_aliceMachine, LicenseSignInRoutes.AddSpare, B, MachineId(_alice.Tenant, TargetMachine));
        using var machineRenew = await Post(_aliceMachine, LicenseSignInRoutes.Renew, A, MachineId(_alice.Tenant, TargetMachine));
        foreach (var response in new[] { machineSet, machineRemove, machineAdd, machineRenew })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("owner_only", (await Json(response)).GetProperty("code").GetString());
        }
        Assert.Equal(1, (await Get(_aliceOwner, LicenseSignInRoutes.Approvers)).GetProperty("approvers").GetArrayLength());
        Assert.Empty(_target.AllOf(LicenseContract.SignInVerb));

        // With no credential nothing answers at all.
        using var anonymous = await _noCredential.GetAsync("gateway/licenses/approvers");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // The owner removes it.
        using var removed = await _aliceOwner.DeleteAsync($"gateway/licenses/approvers?accountEmail={Uri.EscapeDataString(A)}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.True((await Json(removed)).GetProperty("removed").GetBoolean());
        Assert.True((await Get(_aliceOwner, LicenseSignInRoutes.Approvers)).GetProperty("isEmpty").GetBoolean());
    }

    [Fact]
    public async Task TheOverview_SaysWhoApprovesEachAccount_AndOffersTheSignInsItsRoutesWouldStart()
    {
        await AliceSetsHerApprover();

        var overview = await Get(_aliceOwner, LicenseRoutes.Overview);

        var account = overview.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("accountEmail").GetString() == A);
        Assert.Equal(("ready", "work"), (account.GetProperty("approver").GetProperty("status").GetString(),
            account.GetProperty("approver").GetProperty("browserProfile").GetString()));
        Assert.Equal($"Sign-ins for {A} are approved on {ApprovingMachine}, in the browser profile work.", account.GetProperty("approverText").GetString());
        var target = overview.GetProperty("machines").EnumerateArray().Single(m => m.GetProperty("machineName").GetString() == TargetMachine);
        Assert.True(target.GetProperty("canAddSpare").GetBoolean());
        Assert.True(target.GetProperty("active").GetProperty("canRenewNow").GetBoolean());

        // Bob's overview says nothing of Alice's approver.
        var ofBob = await Get(_bobOwner, LicenseRoutes.Overview);
        Assert.Equal(JsonValueKind.Null, ofBob.GetProperty("accounts")[0].GetProperty("approver").ValueKind);
    }

    /// <summary>A session of Alice's that she has raised, through the real raise route.</summary>
    private async Task<HttpClient> ARaisedSessionOfAlice()
    {
        var sessionId = Guid.NewGuid().ToString();
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_alice.Tenant, _target.DirectorId, sessionId, GatewaySessionKey.Hash(key),
            new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await _target.PushSnapshotAsync(new SessionDto
            { SessionId = sessionId, Name = "A session the owner raised", ActivityState = "WaitingForInput", CreatedAt = Now, LastActivityAt = Now });
        using var raise = await _aliceOwner.PostAsync($"sessions/{sessionId}/raise", null);
        Assert.Equal(HttpStatusCode.OK, raise.StatusCode);
        Assert.True(_gateway.RaisedSessions.IsRaised(_alice.Tenant, sessionId));
        return Client(key);
    }

    [Fact]
    public async Task ARegisteredApprover_ComesInOverTheDirectorsHub_ASessionKeyStillCannotRemoveIt_AndTheOwnersRemovalSticks()
    {
        // The approving machine's Director registers its profile for account A, which Alice's machines hold.
        var registered = await _approver.ReportLicenseApproverChecksAsync(CanApprove(A));
        Assert.Equal(LicenseApproverCheckOutcomes.Registered, Assert.Single(registered.Rows).Outcome);
        var row = (await Get(_aliceOwner, LicenseSignInRoutes.Approvers)).GetProperty("approvers")[0];
        Assert.Equal((ApprovingMachine, "registered", "ready"),
            (row.GetProperty("machineName").GetString(), row.GetProperty("source").GetString(), row.GetProperty("status").GetString()));
        var assigned = Assert.Single((await _approver.GetLicenseApproverAssignmentsAsync()).Rows);
        Assert.Equal((A, "work"), (assigned.AccountEmail, assigned.BrowserProfile));

        // Neither a session key nor a raised one may remove it or replace it: the guard is unchanged.
        using var raised = await ARaisedSessionOfAlice();
        using var remove = await _aliceSession.DeleteAsync($"gateway/licenses/approvers?accountEmail={Uri.EscapeDataString(A)}");
        using var raisedRemove = await raised.DeleteAsync($"gateway/licenses/approvers?accountEmail={Uri.EscapeDataString(A)}");
        using var put = await SetApprover(_aliceSession, A, TargetMachine);
        using var raisedPut = await SetApprover(raised, A, TargetMachine);
        foreach (var response in new[] { remove, raisedRemove, put, raisedPut })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("session_key_out_of_scope", (await Json(response)).GetProperty("code").GetString());
        }
        Assert.Equal(LicenseApproverSources.Registered, _gateway.LicenseApprovers.Find(_alice.Tenant, A)!.Source);
        Assert.Empty(_gateway.LicenseApprovers.ListRemovals(_alice.Tenant));

        // The owner removes it, and the next check does not bring it back.
        using var removed = await _aliceOwner.DeleteAsync($"gateway/licenses/approvers?accountEmail={Uri.EscapeDataString(A)}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var again = await _approver.ReportLicenseApproverChecksAsync(CanApprove(A));
        Assert.Equal(LicenseApproverRefusals.OwnerRemoved, Assert.Single(again.Rows).Reason);
        Assert.True((await Get(_aliceOwner, LicenseSignInRoutes.Approvers)).GetProperty("isEmpty").GetBoolean());
    }

    [Fact]
    public async Task ASignedOutCheck_OverTheDirectorsHub_OpensTheApproverAlarm_AndACanApproveCheckClosesIt()
    {
        // Bob has a healthy approver for the SAME account, so a raise that read or wrote across accounts would show.
        using var bobSet = await SetApprover(_bobOwner, A, BobMachine);
        Assert.Equal(HttpStatusCode.OK, bobSet.StatusCode);
        Assert.Equal(LicenseApproverCheckOutcomes.HealthUpdated, Assert.Single((await _bobDirector.ReportLicenseApproverChecksAsync(CanApprove(A))).Rows).Outcome);
        await _gateway.LicenseApproverAlarm.WhenIdleAsync();
        using var set = await SetApprover(_aliceOwner, A, ApprovingMachine);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        // The approving machine's Director finds the profile signed out and says so over its own hub connection. The
        // raise runs on the host's own task, inside the tenant's scope the host enters for it.
        var signedOut = await _approver.ReportLicenseApproverChecksAsync(new LicenseApproverCheckReport
        {
            OffersApprovers = true,
            Rows = [new LicenseApproverCheckRow { BrowserProfile = "work", AccountEmail = A, State = LicenseApproverStates.SignedOut, CheckedAtUtc = Now }],
        });
        Assert.Equal(LicenseApproverCheckOutcomes.HealthUpdated, Assert.Single(signedOut.Rows).Outcome);
        await _gateway.LicenseApproverAlarm.WhenIdleAsync();

        var open = Assert.Single(_gateway.LicenseAlarms.ListOpen(_alice.Tenant));
        Assert.Equal((LicenseApproverAlarm.Kind.Key, A), (open.Kind, open.AccountEmail));
        Assert.Equal($"The approver for {A} (browser profile \"work\" on {ApprovingMachine}) is not signed in to claude.ai.", open.Problem);
        Assert.Empty(_gateway.LicenseAlarms.ListOpen(_bob.Tenant));
        Assert.Equal("ready", (await Get(_bobOwner, LicenseSignInRoutes.Approvers)).GetProperty("approvers")[0].GetProperty("status").GetString());
        lock (_ownerEmails) Assert.Equal(_alice.Tenant, Assert.Single(_ownerEmails).Tenant);

        // It signs back in, the next check says so, and the alarm closes by itself.
        var healthy = await _approver.ReportLicenseApproverChecksAsync(CanApprove(A));
        Assert.Equal(LicenseApproverCheckOutcomes.HealthUpdated, Assert.Single(healthy.Rows).Outcome);
        await _gateway.LicenseApproverAlarm.WhenIdleAsync();

        Assert.Empty(_gateway.LicenseAlarms.ListOpen(_alice.Tenant));
    }

    [Fact]
    public async Task TheFourWrites_AskedWithASessionKeyTheOwnerRaised_AreStillRefused_AndNothingIsStoredOrSent()
    {
        await AliceSetsHerApprover();
        using var raised = await ARaisedSessionOfAlice();
        var machineId = MachineId(_alice.Tenant, TargetMachine);

        using var set = await SetApprover(raised, B, ApprovingMachine);
        using var remove = await raised.DeleteAsync($"gateway/licenses/approvers?accountEmail={Uri.EscapeDataString(A)}");
        using var add = await Post(raised, LicenseSignInRoutes.AddSpare, B, machineId);
        using var renew = await Post(raised, LicenseSignInRoutes.Renew, A, machineId);

        // Raising a session widens what its key may do, and never to these four: the session key guard refuses them
        // before the route is reached, as it does for a session that was not raised.
        foreach (var response in new[] { set, remove, add, renew })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("session_key_out_of_scope", (await Json(response)).GetProperty("code").GetString());
        }
        // A raised session still reads; nothing was changed and no Director was sent a sign-in.
        var approvers = (await Get(raised, LicenseSignInRoutes.Approvers)).GetProperty("approvers");
        Assert.Equal((1, A), (approvers.GetArrayLength(), approvers[0].GetProperty("accountEmail").GetString()));
        Assert.Empty(_target.AllOf(LicenseContract.SignInVerb));
        Assert.DoesNotContain(_gateway.LicenseStore.ListLog(_alice.Tenant, Now.AddDays(-1)), r => r.Kind == LicenseSignInLog.Kind);
    }

    // ---- nothing the relay is given reaches the host's log ----

    [Fact]
    public async Task NoLineOfTheHostsLog_CarriesTheAddressOrTheApproval_AcrossAnApprovedRelayAndADroppedTunnel()
    {
        await AliceSetsHerApprover();
        TakesAnswers(_target);
        using var capture = new CapturedLog();

        // 1. An approved relay, through the real hub, the real router and the real tunnel.
        Approves(_approver);
        await _target.RequestLicenseApprovalAsync(Request());
        await _gateway.LicenseApprovals.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(LicenseApprovalOutcomes.Approved, Payload<LicenseApprovalResult>(_target.LastOf(LicenseContract.ApprovalResultVerb)!).Outcome);

        // 2. A relay whose approving Director loses its tunnel while it holds the command.
        using var holding = new ManualResetEventSlim();
        using var arrived = new SemaphoreSlim(0);
        _approver.OnCommand(command =>
        {
            arrived.Release();
            Assert.True(holding.Wait(TimeSpan.FromMinutes(2)));
            return FakeTunnelDirector.Ok(new LicenseApprovalResult
                { SignInId = Payload<LicenseApproveRequest>(command).SignInId, Outcome = LicenseApprovalOutcomes.Approved, SealedApproval = SealedValue });
        });
        const string second = "22222222222222222222222222222222";
        await _target.RequestLicenseApprovalAsync(Request(second));
        Assert.True(await arrived.WaitAsync(TimeSpan.FromMinutes(2)));
        var stopping = _approver.StopTunnelAsync();
        await _gateway.LicenseApprovals.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));
        holding.Set();
        await stopping.WaitAsync(TimeSpan.FromSeconds(60));
        var dropped = Payload<LicenseApprovalResult>(_target.LastOf(LicenseContract.ApprovalResultVerb)!);
        Assert.Equal((second, LicenseApprovalOutcomes.Refused, LicenseRenewalErrors.ApproverOffline), (dropped.SignInId, dropped.Outcome, dropped.ReasonCode));

        var lines = capture.Lines();

        // The host's own lines for these sends are in the capture: the router's line for an answered command, its line
        // for the dropped tunnel - the one line on the path that prints text this code does not write - and the relay's.
        Assert.Contains(lines, l => l.Contains("[DirectorCommandRouter] " + LicenseContract.ApproveVerb) && l.Contains(_approver.DirectorId));
        Assert.Contains(lines, l => l.Contains("[DirectorCommandRouter] " + LicenseContract.ApproveVerb) && l.Contains("TUNNEL DROPPED mid-command"));
        Assert.Contains(lines, l => l.Contains("[DirectorCommandRouter] " + LicenseContract.ApprovalResultVerb) && l.Contains(_target.DirectorId));
        Assert.Contains(lines, l => l.Contains("[LicenseApprovalRelay]") && l.Contains(SignIn1) && l.Contains(LicenseApprovalOutcomes.Approved));
        Assert.Contains(lines, l => l.Contains("[LicenseApprovalRelay]") && l.Contains(second) && l.Contains(LicenseRenewalErrors.ApproverOffline));

        Assert.DoesNotContain(lines, l => l.Contains(Address, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("claude.example", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("challenge=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains(SealedValue, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(TargetPublicHalf, StringComparison.Ordinal));
    }

    // ---- a sign-in with no answer is settled by the host's minute check ----

    /// <summary>A sign-in row as a Gateway that stopped while it waited leaves it: written, sent, and never answered.</summary>
    private string AnUnansweredSignIn(string reason, string account)
    {
        var machine = _gateway.LicenseReads.Read(_alice.Tenant, Now).Machines.Single(m => m.MachineName == TargetMachine);
        var row = _gateway.LicenseSignInLog.CreatePending(_alice.Tenant, machine, account, reason, "Asked from the Licenses page.", Now);
        Assert.True(_gateway.LicenseSignInLog.RecordSend(_alice.Tenant, row.SwitchId!, machine.DirectorId, Now));
        Assert.True(_gateway.LicenseSignInLog.TryMarkUnknown(_alice.Tenant, row.SwitchId!, "No answer came back."));
        return row.SwitchId!;
    }

    [Fact]
    public async Task AnUnansweredSignIn_IsAskedAgainByTheHostsMinuteCheck_OverTheTunnel_AndEndsWithTheDirectorsStoredAnswer()
    {
        var signInId = AnUnansweredSignIn(LicenseSignInReasons.Manual, B);
        TakesAnswers(_target, request => new LicenseSignInResult
        {
            SignInId = request.SignInId,
            Outcome = LicenseSignInOutcomes.Added,
            Message = "Signed in and stored as a spare.",
            FinishedAtUtc = Now.AddMinutes(2),
            Replayed = true,
        });

        // Still inside the time a Director could be running it: the check asks nobody.
        _clock.UtcNow = Now + LicenseSignInService.UnansweredAfter;
        await _gateway.RunLicenseSwitchCheckAsync();
        Assert.Empty(_target.AllOf(LicenseContract.SignInVerb));

        _clock.UtcNow = Now + LicenseSignInService.UnansweredAfter + TimeSpan.FromSeconds(1);
        await _gateway.RunLicenseSwitchCheckAsync();

        var again = Payload<LicenseSignInRequest>(Assert.Single(_target.AllOf(LicenseContract.SignInVerb)));
        Assert.Equal((signInId, B, LicenseSignInReasons.Manual, Now + LicenseContract.SignInNotAfter),
            (again.SignInId, again.AccountEmail, again.Reason, again.NotAfterUtc));
        var row = _gateway.LicenseSignInLog.Find(_alice.Tenant, signInId)!;
        Assert.Equal((LicenseSignInOutcomes.Added, "Signed in and stored as a spare.", true, 2), (row.State, row.Message, row.Replayed, row.SendCount));
        // The other account's Director was asked nothing.
        Assert.Empty(_bobDirector.AllOf(LicenseContract.SignInVerb));

        // And a later check asks nobody again: the row has ended.
        await _gateway.RunLicenseSwitchCheckAsync();
        Assert.Single(_target.AllOf(LicenseContract.SignInVerb));
    }

    [Fact]
    public async Task AnUnansweredSignIn_WhoseDirectorLostItsTunnelAndHasNotReportedSince_StaysOpen()
    {
        var signInId = AnUnansweredSignIn(LicenseSignInReasons.Renewal, A);
        await _target.StopTunnelAsync();

        _clock.UtcNow = Now + LicenseSignInService.UnansweredAfter + TimeSpan.FromHours(2);
        await _gateway.RunLicenseSwitchCheckAsync();
        await _gateway.RunLicenseSwitchCheckAsync();

        // The only report is from before the sign-in was asked for: nothing is known, so nothing is claimed.
        var row = _gateway.LicenseSignInLog.Find(_alice.Tenant, signInId)!;
        Assert.Equal((LicenseLogStates.Unknown, (DateTime?)null), (row.State, row.FinishedAtUtc));
        var shown = (await Get(_aliceOwner, LicenseRoutes.Log)).GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "sign-in");
        Assert.True(shown.GetProperty("isOpen").GetBoolean());
    }

    // ---- a sign-in from the Cockpit ----

    [Fact]
    public async Task AddASpare_IsAcceptedWhileTheDirectorStillHoldsTheCommand_AndItsOutcomeLandsInTheLog()
    {
        using var release = new ManualResetEventSlim();
        using var arrived = new SemaphoreSlim(0);
        TakesAnswers(_target, request =>
        {
            arrived.Release();
            // The Director is in the middle of the sign-in until the test lets it finish; the limit only ends a broken run.
            Assert.True(release.Wait(TimeSpan.FromMinutes(2)));
            return new LicenseSignInResult
            {
                SignInId = request.SignInId,
                Outcome = LicenseSignInOutcomes.Added,
                Message = "Signed in and stored as a spare.",
                SignedInAtUtc = Now,
                SignInExpiresAtUtc = Now.AddDays(30),
                FinishedAtUtc = Now.AddSeconds(50),
            };
        });
        var machineId = MachineId(_alice.Tenant, TargetMachine);

        using var accepted = await Post(_aliceOwner, LicenseSignInRoutes.AddSpare, B, machineId);

        // Answered, and the Director has the command and has not finished it.
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var started = await Json(accepted);
        var signInId = started.GetProperty("signInId").GetString()!;
        Assert.Matches(LicenseContract.SignInIdShape, signInId);
        Assert.True(await arrived.WaitAsync(TimeSpan.FromMinutes(2)));
        var sent = Payload<LicenseSignInRequest>(Assert.Single(_target.AllOf(LicenseContract.SignInVerb)));
        Assert.Equal((signInId, B, LicenseSignInReasons.Manual, Now + LicenseContract.SignInNotAfter),
            (sent.SignInId, sent.AccountEmail, sent.Reason, sent.NotAfterUtc));

        // While it runs: the log shows it open, the overview shows it on the machine, and a second one is refused.
        var open = (await Get(_aliceOwner, LicenseRoutes.Log)).GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "sign-in");
        Assert.Equal(("pending", "Signing in", true), (open.GetProperty("state").GetString(), open.GetProperty("stateText").GetString(), open.GetProperty("isOpen").GetBoolean()));
        var machine = (await Get(_aliceOwner, LicenseRoutes.Overview)).GetProperty("machines").EnumerateArray().Single(m => m.GetProperty("machineId").GetString() == machineId);
        Assert.Equal(signInId, machine.GetProperty("signInsInProgress")[0].GetProperty("signInId").GetString());
        using var second = await Post(_aliceOwner, LicenseSignInRoutes.AddSpare, B, machineId);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var refusal = await Json(second);
        Assert.Equal(("sign_in_running", $"A sign-in for {B} on {TargetMachine} is already running."),
            (refusal.GetProperty("code").GetString(), refusal.GetProperty("error").GetString()));

        release.Set();
        await _gateway.LicenseSignIns.WhenIdleAsync();

        var done = (await Get(_aliceOwner, LicenseRoutes.Log)).GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "sign-in");
        Assert.Equal(("added", "Signed in", "good", false, "Signed in and stored as a spare.", "Add a spare"),
            (done.GetProperty("state").GetString(), done.GetProperty("stateText").GetString(), done.GetProperty("stateTone").GetString(),
             done.GetProperty("isOpen").GetBoolean(), done.GetProperty("message").GetString(), done.GetProperty("whyText").GetString()));
        Assert.Single(_target.AllOf(LicenseContract.SignInVerb));
        // The other account's Director was sent nothing, and its log has no such row.
        Assert.Empty(_bobDirector.AllOf(LicenseContract.SignInVerb));
        Assert.DoesNotContain((await Get(_bobOwner, LicenseRoutes.Log)).GetProperty("entries").EnumerateArray(), e => e.GetProperty("kind").GetString() == "sign-in");
    }

    [Fact]
    public async Task RenewNow_SendsARenewal_AndTheDirectorsRefusalLandsInTheLogInWords()
    {
        TakesAnswers(_target, request => new LicenseSignInResult
        {
            SignInId = request.SignInId,
            Outcome = LicenseSignInOutcomes.Failed,
            ReasonCode = LicenseRenewalErrors.NoApprover,
            Message = "No browser profile is set to approve this account.",
            FinishedAtUtc = Now,
        });

        using var accepted = await Post(_aliceOwner, LicenseSignInRoutes.Renew, A, MachineId(_alice.Tenant, TargetMachine));
        await _gateway.LicenseSignIns.WhenIdleAsync();

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(LicenseSignInReasons.Renewal, Payload<LicenseSignInRequest>(Assert.Single(_target.AllOf(LicenseContract.SignInVerb))).Reason);
        var row = (await Get(_aliceOwner, LicenseRoutes.Log)).GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "sign-in");
        Assert.Equal(("failed", "Failed", "bad", "no-approver", "Renew now", $"The sign-in for {A} on {TargetMachine} was not renewed."),
            (row.GetProperty("state").GetString(), row.GetProperty("stateText").GetString(), row.GetProperty("stateTone").GetString(),
             row.GetProperty("reasonCode").GetString(), row.GetProperty("whyText").GetString(), row.GetProperty("summary").GetString()));
    }

    [Fact]
    public async Task ASignIn_OnAMachineWithNoDirectorConnected_IsRefusedWithAFinishedSentence_AndNothingIsWritten()
    {
        var offline = $"d-offline-{_run}";
        _gateway.LicenseUsage.RegisterConnection(_alice.Tenant, offline, "conn-" + offline);
        Assert.Equal(LicenseIngestOutcome.Accepted,
            _gateway.LicenseUsage.Ingest(_alice.Tenant, offline, "conn-" + offline, 1, Report("OFFLINE_MAC_OF_ALICE", Holding(A, true)), Now));

        using var refused = await Post(_aliceOwner, LicenseSignInRoutes.AddSpare, B, MachineId(_alice.Tenant, "OFFLINE_MAC_OF_ALICE"));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var refusal = await Json(refused);
        Assert.Equal(("director_offline", "The Director on OFFLINE_MAC_OF_ALICE is not connected, so a sign-in cannot be started there."),
            (refusal.GetProperty("code").GetString(), refusal.GetProperty("error").GetString()));
        Assert.DoesNotContain((await Get(_aliceOwner, LicenseRoutes.Log)).GetProperty("entries").EnumerateArray(), e => e.GetProperty("kind").GetString() == "sign-in");
    }

    [Fact]
    public async Task ASignIn_OnAMachineOfAnotherAccount_IsRefusedAsUnknown_AndThatMachinesDirectorIsSentNothing()
    {
        TakesAnswers(_bobDirector, request => new LicenseSignInResult { SignInId = request.SignInId, Outcome = LicenseSignInOutcomes.Added, FinishedAtUtc = Now });

        using var add = await Post(_aliceOwner, LicenseSignInRoutes.AddSpare, B, MachineId(_bob.Tenant, BobMachine));
        using var renew = await Post(_aliceOwner, LicenseSignInRoutes.Renew, A, MachineId(_bob.Tenant, BobMachine));
        await _gateway.LicenseSignIns.WhenIdleAsync();

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (add.StatusCode, renew.StatusCode));
        Assert.Equal("unknown_machine", (await Json(add)).GetProperty("code").GetString());
        Assert.Empty(_bobDirector.AllOf(LicenseContract.SignInVerb));
        Assert.DoesNotContain((await Get(_bobOwner, LicenseRoutes.Log)).GetProperty("entries").EnumerateArray(), e => e.GetProperty("kind").GetString() == "sign-in");
    }
}

/// <summary>
/// THE FOUR SIGN-IN WRITES ON A SELF-HOSTED GATEWAY: the shared machine token is a credential there - every Director
/// on the machine has it - and it reads who approves each account, but it is not a signed-in person, so setting or
/// removing an approver and starting a sign-in are refused to it (the review of pull request 3943, finding 6).
/// </summary>
public sealed class LicenseSignInWritesSelfHostedTests : IAsyncLifetime
{
    private const string SharedToken = "license-sign-in-self-hosted-token";
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-license-sign-in-self-hosted-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!;
    private HttpClient _sharedToken = null!;

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _sharedToken = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _sharedToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SharedToken);
    }

    public async Task DisposeAsync()
    {
        _sharedToken.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task TheSharedMachineToken_ReadsWhoApproves_AndIsRefusedAllFourWrites()
    {
        const string body = "{\"accountEmail\":\"account-a@example.com\",\"machineId\":\"0123456789abcdef0123456789abcdef\"}";
        StringContent Sent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

        using var read = await _sharedToken.GetAsync("gateway/licenses/approvers");
        using var set = await _sharedToken.PutAsync("gateway/licenses/approvers",
            Sent("{\"accountEmail\":\"account-a@example.com\",\"machineName\":\"the-mac\",\"osUser\":\"owner\",\"browserProfile\":\"work\"}"));
        using var remove = await _sharedToken.DeleteAsync("gateway/licenses/approvers?accountEmail=account-a%40example.com");
        using var add = await _sharedToken.PostAsync(LicenseSignInRoutes.AddSpare.TrimStart('/'), Sent(body));
        using var renew = await _sharedToken.PostAsync(LicenseSignInRoutes.Renew.TrimStart('/'), Sent(body));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        foreach (var refused in new[] { set, remove, add, renew })
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var json = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("owner_only", json.GetProperty("code").GetString());
        }
        using var after = await _sharedToken.GetAsync("gateway/licenses/approvers");
        Assert.True(JsonDocument.Parse(await after.Content.ReadAsStringAsync()).RootElement.GetProperty("isEmpty").GetBoolean());
    }
}
