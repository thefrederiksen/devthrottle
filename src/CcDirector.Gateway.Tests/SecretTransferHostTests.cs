using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE SECRET HANDOFF ROUTES ON A REAL HOSTED HOST (issue #2943). Every request goes over HTTP through the real
/// <c>AuthMiddleware</c> and <c>SessionKeyGuard</c> with real minted keys, so a route that is mapped but not allowed for a
/// session key - the trap every new Gateway route falls into - fails here.
/// </summary>
public sealed class SecretTransferHostTests : IAsyncLifetime
{
    private const string SharedToken = "secret-transfer-host-token";
    private const string DirectorNorth = "director-secrets-north";
    private const string DirectorMac = "director-secrets-mac";
    private const string DirectorOther = "director-secrets-other";

    private readonly ITestOutputHelper _out;
    private GatewayHost _gateway = null!;
    private TenantId _tenantA;
    private TenantId _tenantB;
    private HttpClient _ownerA = null!;
    private HttpClient _machineA = null!;
    private HttpClient _sessionA = null!;
    private HttpClient _ownerB = null!;
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-secret-transfer-host-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public SecretTransferHostTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var subjectA = $"sub-secrets-a-{_runId}";
        var subjectB = $"sub-secrets-b-{_runId}";
        var a = HostedTestEnrollment.Enroll(_gateway, subjectA, $"secrets-a-{_runId}@example.com", $"dev-secrets-dir-{_runId}", "SOREN_NORTH");
        var b = HostedTestEnrollment.Enroll(_gateway, subjectB, $"secrets-b-{_runId}@example.com", $"dev-secrets-dir-b-{_runId}", "THEIRS");
        _tenantA = a.Tenant;
        _tenantB = b.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");

        var ownerA = _gateway.Devices.RegisterForTenant(_tenantA, subjectA, $"dev-secrets-owner-{_runId}", "OWNER-A", deviceType: "phone");
        var ownerB = _gateway.Devices.RegisterForTenant(_tenantB, subjectB, $"dev-secrets-owner-b-{_runId}", "OWNER-B", deviceType: "browser");
        _ownerA = Client(ownerA.DeviceKey);
        _ownerB = Client(ownerB.DeviceKey);
        _machineA = Client(a.DeviceKey);
        _sessionA = Client(SessionKey(_tenantA, DirectorNorth, _sessionId));
    }

    public async Task DisposeAsync()
    {
        foreach (var http in new[] { _ownerA, _ownerB, _machineA, _sessionA })
            http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    // ---- plumbing ----------------------------------------------------------------------------------------

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private string SessionKey(TenantId tenant, string directorId, string sessionId)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(tenant, directorId, sessionId, GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
        return key;
    }

    /// <summary>Stand in for a Director's Hello: registered, connected, and saying its key.</summary>
    private string Connect(TenantId tenant, string directorId, string machine, string? key = null)
    {
        key ??= Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _gateway.Registry.RegisterFromStream(directorId, machine, "someone", "1.0", pid: 4321, startedAt: DateTime.UtcNow, tenant: tenant);
        _gateway.PushedSessions.RegisterConnection(tenant, directorId, "conn-" + directorId);
        _gateway.SecretMachines.Record(tenant, directorId, machine, key, DateTime.UtcNow);
        return key;
    }

    private async Task<(HttpStatusCode Status, string Body)> Get(HttpClient http, string path)
    {
        using var resp = await http.GetAsync(path);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"GET {path} -> {(int)resp.StatusCode}: {(text.Length > 600 ? text[..600] + " ..." : text)}");
        return (resp.StatusCode, text);
    }

    private static List<JsonElement> Machines(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("machines").EnumerateArray().Select(e => e.Clone()).ToList();

    // ---- the machines that can receive ---------------------------------------------------------------------

    [Fact]
    public async Task Machines_ASessionKey_TheOwnersPhone_AndTheMachinesOwnCredential_AllReadTheList()
    {
        var northKey = Connect(_tenantA, DirectorNorth, "SOREN_NORTH");
        Connect(_tenantA, DirectorMac, "devthrottle-mac-mini");

        foreach (var caller in new[] { _sessionA, _ownerA, _machineA })
        {
            var (status, body) = await Get(caller, "gateway/secrets/machines");
            Assert.Equal(HttpStatusCode.OK, status);
            var rows = Machines(body);
            Assert.Equal(new[] { "devthrottle-mac-mini", "SOREN_NORTH" }, rows.Select(r => r.GetProperty("machine").GetString()));
            var north = rows.Single(r => r.GetProperty("machine").GetString() == "SOREN_NORTH");
            Assert.Equal(northKey, north.GetProperty("publicKey").GetString());
            Assert.Equal(64, north.GetProperty("fingerprint").GetString()!.Length);
        }
    }

    [Fact]
    public async Task Machines_AnotherAccountsMachine_IsNeverListed()
    {
        Connect(_tenantA, DirectorNorth, "SOREN_NORTH");
        Connect(_tenantB, DirectorOther, "THEIRS");

        var (_, mine) = await Get(_ownerA, "gateway/secrets/machines");
        var (_, theirs) = await Get(_ownerB, "gateway/secrets/machines");

        Assert.Equal(new[] { "SOREN_NORTH" }, Machines(mine).Select(r => r.GetProperty("machine").GetString()));
        Assert.Equal(new[] { "THEIRS" }, Machines(theirs).Select(r => r.GetProperty("machine").GetString()));
    }

    [Fact]
    public async Task Machines_ADirectorThatDisconnected_IsNoLongerListed()
    {
        Connect(_tenantA, DirectorMac, "devthrottle-mac-mini");
        var (_, before) = await Get(_sessionA, "gateway/secrets/machines");
        Assert.Equal(new[] { "devthrottle-mac-mini" }, Machines(before).Select(r => r.GetProperty("machine").GetString()));
        _gateway.PushedSessions.UnregisterConnection(_tenantA, DirectorMac, "conn-" + DirectorMac);

        var (status, body) = await Get(_sessionA, "gateway/secrets/machines");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(Machines(body));
    }

    // ---- approvals (phase 3) -------------------------------------------------------------------------------

    private async Task<(HttpStatusCode Status, JsonElement Body)> Post(HttpClient http, string path, object body)
    {
        using var resp = await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"POST {path} -> {(int)resp.StatusCode}: {(text.Length > 600 ? text[..600] + " ..." : text)}");
        return (resp.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private void BothMachines()
    {
        Connect(_tenantA, DirectorNorth, "SOREN_NORTH");
        Connect(_tenantA, DirectorMac, "devthrottle-mac-mini");
    }

    private static object Ask(string? reason = "sudo on the mac", string? ownerApproved = null, string? approvedHere = null,
        string? askedOn = null, string to = "devthrottle-mac-mini")
        => new { entry = "qa-handoff-host", fromMachine = "SOREN_NORTH", toMachine = to, reason, ownerApproved, approvedHere, askedOn };

    private static string State(JsonElement body) => body.GetProperty("transfer").GetProperty("state").GetString()!;
    private static string Id(JsonElement body) => body.GetProperty("transfer").GetProperty("transferId").GetString()!;
    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    [Fact]
    public async Task Transfer_ASessionAsks_ItWaits_TheOwnersPhoneAnswers_AndASecondAnswerIsRefused()
    {
        BothMachines();

        var (asked, created) = await Post(_sessionA, "gateway/secrets/transfers", Ask());
        Assert.Equal(HttpStatusCode.OK, asked);
        Assert.Equal("waiting", State(created));
        var id = Id(created);

        var (listed, _) = await Get(_sessionA, "gateway/secrets/transfers");
        Assert.Equal(HttpStatusCode.OK, listed);
        var (one, _) = await Get(_sessionA, $"gateway/secrets/transfers/{id}");
        Assert.Equal(HttpStatusCode.OK, one);

        var (answered, after) = await Post(_ownerA, $"gateway/secrets/transfers/{id}/answer", new { approve = true, where = "phone" });
        Assert.Equal(HttpStatusCode.OK, answered);
        Assert.Equal("phone", after.GetProperty("transfer").GetProperty("answeredWhere").GetString());

        var (again, refused) = await Post(_machineA, $"gateway/secrets/transfers/{id}/answer", new { deny = true, where = "window" });
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Equal("already_answered", Code(refused));
    }

    [Fact]
    public async Task Transfer_ASessionReportingTheOwnersWords_IsApprovedInTheChat_AndEmptyWordsAreRefused()
    {
        BothMachines();

        var (status, body) = await Post(_sessionA, "gateway/secrets/transfers", Ask(ownerApproved: "yes, send it"));
        Assert.Equal(HttpStatusCode.OK, status);
        var transfer = body.GetProperty("transfer");
        Assert.Equal(("chat", "yes, send it"),
            (transfer.GetProperty("answeredWhere").GetString(), transfer.GetProperty("approvalWords").GetString()));

        var (empty, refused) = await Post(_sessionA, "gateway/secrets/transfers", Ask(ownerApproved: "  "));
        Assert.Equal(HttpStatusCode.BadRequest, empty);
        Assert.Equal("owner_approved_empty", Code(refused));
    }

    [Fact]
    public async Task Transfer_ASessionCannotClaimTheWindow_NorAnswerWithoutTheOwnersWords_NorAskWithoutAReason()
    {
        BothMachines();

        var (claimed, c) = await Post(_sessionA, "gateway/secrets/transfers", Ask(approvedHere: "window", askedOn: "SOREN_NORTH"));
        Assert.Equal((HttpStatusCode.Forbidden, "session_cannot_approve_here"), (claimed, Code(c)));

        var (noReason, r) = await Post(_sessionA, "gateway/secrets/transfers", Ask(reason: ""));
        Assert.Equal((HttpStatusCode.BadRequest, "reason_required"), (noReason, Code(r)));

        var (_, waiting) = await Post(_sessionA, "gateway/secrets/transfers", Ask());
        var (answered, a) = await Post(_sessionA, $"gateway/secrets/transfers/{Id(waiting)}/answer", new { approve = true });
        Assert.Equal((HttpStatusCode.Forbidden, "owner_words_required"), (answered, Code(a)));
    }

    [Fact]
    public async Task Transfer_TheMachinesOwnCredential_ApprovesInTheWindowWhereItWasAsked()
    {
        BothMachines();

        var (status, body) = await Post(_machineA, "gateway/secrets/transfers", Ask(reason: null, approvedHere: "window", askedOn: "SOREN_NORTH"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("window", body.GetProperty("transfer").GetProperty("answeredWhere").GetString());
    }

    [Fact]
    public async Task Transfer_ToAMachineThatIsNotConnected_IsRefusedWithTheReason()
    {
        Connect(_tenantA, DirectorNorth, "SOREN_NORTH");

        var (status, body) = await Post(_sessionA, "gateway/secrets/transfers", Ask());

        Assert.Equal((HttpStatusCode.Conflict, "machine_offline"), (status, Code(body)));
    }

    [Fact]
    public async Task Transfer_AnotherAccount_NeitherSeesNorAnswersIt()
    {
        BothMachines();
        var (_, created) = await Post(_sessionA, "gateway/secrets/transfers", Ask());
        var id = Id(created);

        var (read, _) = await Get(_ownerB, $"gateway/secrets/transfers/{id}");
        var (answer, _) = await Post(_ownerB, $"gateway/secrets/transfers/{id}/answer", new { approve = true, where = "cockpit" });

        Assert.Equal(HttpStatusCode.NotFound, read);
        Assert.Equal(HttpStatusCode.NotFound, answer);
        var (_, mine) = await Get(_ownerA, $"gateway/secrets/transfers/{id}");
        Assert.Contains("\"state\":\"waiting\"", mine);
    }

    // ---- who may answer (the phase 3 review) --------------------------------------------------------------------

    private HttpClient MachineCredentialOf(string machine)
    {
        var device = _gateway.Devices.RegisterForTenant(_tenantA, $"sub-secrets-a-{_runId}", $"dev-secrets-{machine}-{_runId}", machine);
        return Client(device.DeviceKey);
    }

    [Fact]
    public async Task Answer_TheMachinesOwnCredential_WinsAWaitingTransfer_AndTheRecordNamesThatCredential()
    {
        BothMachines();
        var (_, waiting) = await Post(_sessionA, "gateway/secrets/transfers", Ask());

        var (status, body) = await Post(_machineA, $"gateway/secrets/transfers/{Id(waiting)}/answer", new { approve = true, where = "window" });

        Assert.Equal(HttpStatusCode.OK, status);
        var transfer = body.GetProperty("transfer");
        Assert.Equal("window", transfer.GetProperty("answeredWhere").GetString());
        Assert.Equal("the own credential of SOREN_NORTH", transfer.GetProperty("answeredBy").GetString());
    }

    [Fact]
    public async Task Answer_AThirdMachinesCredential_IsRefused_OnTheAskAndOnTheAnswer()
    {
        BothMachines();
        using var third = MachineCredentialOf("THIRD-MACHINE");

        var (asked, a) = await Post(third, "gateway/secrets/transfers", Ask(reason: null, approvedHere: "window", askedOn: "SOREN_NORTH"));
        Assert.Equal((HttpStatusCode.Forbidden, "not_one_of_the_two_machines"), (asked, Code(a)));
        var (askedOnly, o) = await Post(third, "gateway/secrets/transfers", Ask(reason: null));
        Assert.Equal((HttpStatusCode.Forbidden, "not_one_of_the_two_machines"), (askedOnly, Code(o)));

        var (_, waiting) = await Post(_sessionA, "gateway/secrets/transfers", Ask());
        var (answered, b) = await Post(third, $"gateway/secrets/transfers/{Id(waiting)}/answer", new { approve = true, where = "window" });
        Assert.Equal((HttpStatusCode.Forbidden, "not_one_of_the_two_machines"), (answered, Code(b)));
    }

    [Fact]
    public async Task Ask_AMachinesCredential_CannotSayTheOwnerApprovedOnTheOtherMachine()
    {
        BothMachines();

        var (status, body) = await Post(_machineA, "gateway/secrets/transfers",
            Ask(reason: null, approvedHere: "window", askedOn: "devthrottle-mac-mini"));

        Assert.Equal((HttpStatusCode.BadRequest, "asked_on_another_machine"), (status, Code(body)));
    }

    [Fact]
    public async Task Answer_ASession_CannotAnswerAnotherSessionsTransfer_ButMayWithdrawItsOwn()
    {
        BothMachines();
        using var other = Client(SessionKey(_tenantA, DirectorNorth, Guid.NewGuid().ToString()));
        var (_, mine) = await Post(_sessionA, "gateway/secrets/transfers", Ask());

        var (approve, a) = await Post(other, $"gateway/secrets/transfers/{Id(mine)}/answer", new { approve = true, ownerApproved = "yes" });
        var (deny, d) = await Post(other, $"gateway/secrets/transfers/{Id(mine)}/answer", new { deny = true });
        Assert.Equal((HttpStatusCode.Forbidden, "not_your_transfer"), (approve, Code(a)));
        Assert.Equal((HttpStatusCode.Forbidden, "not_your_transfer"), (deny, Code(d)));

        var (withdrawn, w) = await Post(_sessionA, $"gateway/secrets/transfers/{Id(mine)}/answer", new { deny = true });
        Assert.Equal(HttpStatusCode.OK, withdrawn);
        Assert.Equal("denied", State(w));
    }

    [Fact]
    public async Task Answer_ASessionNamingTheWindow_IsRecordedAsTheChat()
    {
        BothMachines();
        var (_, mine) = await Post(_sessionA, "gateway/secrets/transfers", Ask());

        var (status, body) = await Post(_sessionA, $"gateway/secrets/transfers/{Id(mine)}/answer",
            new { approve = true, where = "window", ownerApproved = "yes, send it" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("chat", body.GetProperty("transfer").GetProperty("answeredWhere").GetString());
    }

    [Fact]
    public async Task Ask_BornApproved_AnswersWithTheStateTheRowHoldsNow()
    {
        BothMachines();

        var (_, body) = await Post(_sessionA, "gateway/secrets/transfers", Ask(ownerApproved: "yes, send it"));

        // Phase 3 cannot deliver, so starting the delivery ends it at once; the answer must say so, not "approved".
        Assert.Equal("failed", State(body));
        Assert.Contains("Nothing was moved", body.GetProperty("note").GetString());
    }
}
