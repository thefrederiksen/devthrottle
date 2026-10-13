using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE LICENSES ROUTES ON A REAL HOSTED HOST (the License Switching mission). Every request goes over HTTP through the
/// real <c>AuthMiddleware</c> and <c>SessionKeyGuard</c> with real minted keys, so a route that is mapped but not
/// allowed for a session key - the trap every new Gateway route falls into - fails here, and so does a route that
/// answers one account with another's licenses. The usage reports go in through the host's own ingest, so the wiring
/// of the ingest to the forecast's history is exercised too.
/// </summary>
public sealed class LicenseHostTests : IAsyncLifetime
{
    private const string SharedToken = "license-host-token";
    private const string DirectorOne = "director-licenses-one";
    private const string DirectorTwo = "director-licenses-two";
    private const string AccountA = "account-a@example.com";
    private const string AccountB = "account-b@example.com";
    private const string AccountC = "account-c@example.com";
    private const string AccountD = "account-d@example.com";

    private static readonly string[] Routes = { "gateway/licenses", "gateway/licenses/forecast", "gateway/licenses/log" };

    /// <summary>The Gateway's clock for the Licenses routes in this class: fixed, so no test here reads the machine's.</summary>
    private static readonly DateTime Now = new(2026, 10, 11, 3, 12, 0, DateTimeKind.Utc);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private readonly ITestOutputHelper _out;
    private GatewayHost _gateway = null!;
    private TenantId _tenantOne;
    private TenantId _tenantTwo;
    private TenantId _tenantNothingReported;
    private HttpClient _ownerOne = null!;
    private HttpClient _machineOne = null!;
    private HttpClient _sessionOne = null!;
    private HttpClient _ownerTwo = null!;
    private HttpClient _ownerNothingReported = null!;
    private HttpClient _noCredential = null!;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-license-host-" + Guid.NewGuid().ToString("N"));

    public LicenseHostTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        _gateway.LicenseClock = new FixedClock();
        await _gateway.StartAsync();

        var one = HostedTestEnrollment.Enroll(_gateway, $"sub-lic-one-{_runId}", $"lic-one-{_runId}@example.com", $"dev-lic-dir-one-{_runId}", "machine-of-one");
        var two = HostedTestEnrollment.Enroll(_gateway, $"sub-lic-two-{_runId}", $"lic-two-{_runId}@example.com", $"dev-lic-dir-two-{_runId}", "machine-of-two");
        var none = HostedTestEnrollment.Enroll(_gateway, $"sub-lic-none-{_runId}", $"lic-none-{_runId}@example.com", $"dev-lic-dir-none-{_runId}", "machine-of-none");
        _tenantOne = one.Tenant;
        _tenantTwo = two.Tenant;
        _tenantNothingReported = none.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");

        _ownerOne = Client(_gateway.Devices.RegisterForTenant(_tenantOne, $"sub-lic-one-{_runId}", $"dev-lic-owner-one-{_runId}", "OWNER-ONE", deviceType: "browser").DeviceKey);
        _ownerTwo = Client(_gateway.Devices.RegisterForTenant(_tenantTwo, $"sub-lic-two-{_runId}", $"dev-lic-owner-two-{_runId}", "OWNER-TWO", deviceType: "browser").DeviceKey);
        _ownerNothingReported = Client(_gateway.Devices.RegisterForTenant(_tenantNothingReported, $"sub-lic-none-{_runId}", $"dev-lic-owner-none-{_runId}", "OWNER-NONE", deviceType: "phone").DeviceKey);
        _machineOne = Client(one.DeviceKey);
        _sessionOne = Client(SessionKey(_tenantOne, DirectorOne, Guid.NewGuid().ToString()));
        _noCredential = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        Report(_tenantOne, DirectorOne, "machine-of-one", AccountA, AccountB);
        Report(_tenantTwo, DirectorTwo, "machine-of-two", AccountC, AccountD);
    }

    public async Task DisposeAsync()
    {
        foreach (var http in new[] { _ownerOne, _ownerTwo, _ownerNothingReported, _machineOne, _sessionOne, _noCredential })
            http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    // ---- plumbing ----

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private string SessionKey(TenantId tenant, string directorId, string sessionId)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(tenant, directorId, sessionId, GatewaySessionKey.Hash(key), new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        return key;
    }

    /// <summary>
    /// Two usage reports from one machine through the host's own ingest, twenty minutes of readings apart: the active
    /// account rises from 50% to 60% of its 5-hour window, and a spare sits at 4%. Every time is the fixed clock's.
    /// </summary>
    private void Report(TenantId tenant, string directorId, string machine, string active, string spare)
    {
        var now = Now;
        _gateway.LicenseUsage.RegisterConnection(tenant, directorId, "conn-" + directorId);
        Assert.Equal(LicenseIngestOutcome.Accepted, _gateway.LicenseUsage.Ingest(tenant, directorId, "conn-" + directorId, 1,
            UsageReport(machine, now, Holding(active, true, 50, now.AddMinutes(-20), now), Holding(spare, false, 4, now.AddMinutes(-20), now)), now));
        Assert.Equal(LicenseIngestOutcome.Accepted, _gateway.LicenseUsage.Ingest(tenant, directorId, "conn-" + directorId, 2,
            UsageReport(machine, now, Holding(active, true, 60, now, now), Holding(spare, false, 4, now, now)), now));
        var machineId = _gateway.LicenseReads.Read(tenant, now).Machines.Single().MachineId;
        Assert.NotNull(_gateway.LicenseStore.CreatePendingSwitch(tenant, machineId, active, spare, LicenseSwitchReasons.Manual, now.AddSeconds(-1)));
    }

    private static LicenseUsageReport UsageReport(string machine, DateTime sentAt, params LicenseHolding[] holdings) => new()
    {
        MachineName = machine,
        OsUser = "owner",
        SentAtUtc = sentAt,
        Trigger = LicenseReportTriggers.Interval,
        Holdings = holdings.ToList(),
    };

    private static LicenseHolding Holding(string email, bool active, double fiveHour, DateTime readAt, DateTime now) => new()
    {
        AccountEmail = email,
        IsActive = active,
        Plan = "max",
        FiveHour = new LicenseWindow { Utilization = fiveHour, ResetsAtUtc = now.AddHours(4) },
        SevenDay = new LicenseWindow { Utilization = 20, ResetsAtUtc = now.AddDays(4) },
        ReadAtUtc = readAt,
        SignInExpiresAtUtc = now.AddDays(20),
    };

    private async Task<(HttpStatusCode Status, string Text)> Get(HttpClient http, string path)
    {
        using var resp = await http.GetAsync(path);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"GET {path} -> {(int)resp.StatusCode}: {(text.Length > 900 ? text[..900] + " ..." : text)}");
        return (resp.StatusCode, text);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // ---- who may read ----

    [Fact]
    public async Task EveryRoute_IsReadByASessionKey_TheOwnersBrowser_AndTheMachinesOwnCredential()
    {
        foreach (var route in Routes)
        foreach (var caller in new[] { _sessionOne, _ownerOne, _machineOne })
        {
            var (status, text) = await Get(caller, route);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains(AccountA, text);
            Assert.False(Json(text).GetProperty("isEmpty").GetBoolean());
        }
    }

    [Fact]
    public async Task EveryRoute_WithNoCredential_IsRefused()
    {
        foreach (var route in Routes)
        {
            var (status, text) = await Get(_noCredential, route);

            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.DoesNotContain("example.com", text);
        }
    }

    [Fact]
    public async Task SwitchNow_IsRefusedToASessionKey()
    {
        using var resp = await _sessionOne.PostAsync("gateway/licenses/switch-now",
            new StringContent($"{{\"accountEmail\":\"{AccountA}\"}}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        // Nothing was sent: the one switch row of the account is the one the fixture wrote.
        Assert.Single(_gateway.LicenseStore.ListOpenSwitches(_tenantOne));
    }

    // ---- the renewal date: the one thing a person sets ----

    private async Task<(HttpStatusCode Status, string Text)> PutRenewal(HttpClient http, string body)
    {
        using var resp = await http.PutAsync("gateway/licenses/accounts/renewal", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"PUT gateway/licenses/accounts/renewal {body} -> {(int)resp.StatusCode}: {text}");
        return (resp.StatusCode, text);
    }

    private async Task<string?> RenewsOn(HttpClient http, string account)
    {
        var (_, text) = await Get(http, "gateway/licenses");
        var row = Json(text).GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("accountEmail").GetString() == account);
        return row.GetProperty("renewsOn").GetString();
    }

    [Fact]
    public async Task RenewalDate_SetByTheOwnersBrowser_IsStored_AndTheOverviewSaysIt()
    {
        var (status, text) = await PutRenewal(_ownerOne, $"{{\"accountEmail\":\"{AccountA}\",\"renewsOn\":\"2026-10-17\"}}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("2026-10-17", "Renews 17 Oct"), (Json(text).GetProperty("renewsOn").GetString(), Json(text).GetProperty("renewsText").GetString()));
        Assert.Equal("2026-10-17", await RenewsOn(_ownerOne, AccountA));
        Assert.Null(await RenewsOn(_ownerOne, AccountB));

        var cleared = await PutRenewal(_ownerOne, $"{{\"accountEmail\":\"{AccountA}\",\"renewsOn\":null}}");
        Assert.Equal(HttpStatusCode.OK, cleared.Status);
        Assert.Null(await RenewsOn(_ownerOne, AccountA));
    }

    [Fact]
    public async Task RenewalDate_IsRefusedToASessionKey_AndToNoCredential_AndNothingIsStored()
    {
        var body = $"{{\"accountEmail\":\"{AccountA}\",\"renewsOn\":\"2026-10-17\"}}";

        Assert.Equal(HttpStatusCode.Forbidden, (await PutRenewal(_sessionOne, body)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutRenewal(_noCredential, body)).Status);

        Assert.Null(await RenewsOn(_ownerOne, AccountA));
    }

    [Fact]
    public async Task RenewalDate_ABodyThatIsNotTheRequest_IsRefusedWithAReason_NotAServerError()
    {
        var notJson = await PutRenewal(_ownerOne, "renews on the seventeenth");
        var badDate = await PutRenewal(_ownerOne, $"{{\"accountEmail\":\"{AccountA}\",\"renewsOn\":\"17 Oct\"}}");

        Assert.Equal((HttpStatusCode.BadRequest, "invalid_body"), (notJson.Status, Json(notJson.Text).GetProperty("code").GetString()));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_date"), (badDate.Status, Json(badDate.Text).GetProperty("code").GetString()));
        Assert.Null(await RenewsOn(_ownerOne, AccountA));
    }

    // ---- one account never sees another's ----

    [Fact]
    public async Task EveryRoute_AnswersOnlyTheCallersOwnAccount()
    {
        var ofOne = new[] { AccountA, AccountB, "machine-of-one" };
        var ofTwo = new[] { AccountC, AccountD, "machine-of-two" };

        foreach (var route in Routes)
        {
            var (_, one) = await Get(_ownerOne, route);
            var (_, two) = await Get(_ownerTwo, route);
            var (_, session) = await Get(_sessionOne, route);

            Assert.All(ofOne, word => Assert.Contains(word, one));
            Assert.All(ofTwo, word => Assert.DoesNotContain(word, one));
            Assert.All(ofTwo, word => Assert.Contains(word, two));
            Assert.All(ofOne, word => Assert.DoesNotContain(word, two));
            Assert.All(ofTwo, word => Assert.DoesNotContain(word, session));
        }
    }

    [Fact]
    public async Task AnAccountNoDirectorHasReportedFor_GetsEmptyAnswersWithASentence_NotAnError()
    {
        foreach (var route in Routes)
        {
            var (status, text) = await Get(_ownerNothingReported, route);
            var json = Json(text);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(json.GetProperty("isEmpty").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("emptyText").GetString()));
            Assert.DoesNotContain("example.com", text);
        }
    }

    // ---- what the routes say, end to end ----

    [Fact]
    public async Task Overview_SaysWhatTheMachineReported_WithItsVerdictsFolded()
    {
        var json = Json((await Get(_ownerOne, "gateway/licenses")).Text);

        var accounts = json.GetProperty("accounts").EnumerateArray().ToList();
        Assert.Equal(new[] { AccountA, AccountB }, accounts.Select(a => a.GetProperty("accountEmail").GetString()));
        Assert.Equal(("in-use", "In use", 60d), (accounts[0].GetProperty("status").GetString(), accounts[0].GetProperty("statusText").GetString(),
            accounts[0].GetProperty("fiveHour").GetProperty("utilization").GetDouble()));
        Assert.Equal("has-room", accounts[1].GetProperty("status").GetString());
        var machine = json.GetProperty("machines")[0];
        Assert.Equal(("machine-of-one", AccountA, "switching"),
            (machine.GetProperty("machineName").GetString(), machine.GetProperty("activeAccountEmail").GetString(), machine.GetProperty("status").GetString()));
        Assert.Equal(AccountB, machine.GetProperty("switchInProgress").GetProperty("toAccountEmail").GetString());
        // The machine is mid-switch, so Switch now is not offered, and the answer says why.
        Assert.False(accounts[0].GetProperty("canSwitchNow").GetBoolean());
        Assert.Contains("already mid-switch", accounts[0].GetProperty("switchNowText").GetString());
    }

    [Fact]
    public async Task Forecast_HasARateFromTheReportsTheIngestAccepted()
    {
        var json = Json((await Get(_sessionOne, "gateway/licenses/forecast")).Text);

        var a = json.GetProperty("accounts").EnumerateArray().Single(x => x.GetProperty("accountEmail").GetString() == AccountA);
        // Ten percent in twenty minutes is thirty percent an hour: the two reports reached the history.
        Assert.Equal(30d, a.GetProperty("fiveHour").GetProperty("percentPerHour").GetDouble(), 3);
        Assert.Equal("30% an hour", a.GetProperty("fiveHour").GetProperty("rateText").GetString());
        Assert.Equal(AccountA, json.GetProperty("nextSwitch").GetProperty("fromAccountEmail").GetString());
    }

    [Fact]
    public async Task Log_ListsTheAccountsSwitchRow()
    {
        var json = Json((await Get(_machineOne, "gateway/licenses/log?limit=10")).Text);

        var entry = Assert.Single(json.GetProperty("entries").EnumerateArray().ToList());
        Assert.Equal(("switch", "machine-of-one", AccountA, AccountB, "By hand", true),
            (entry.GetProperty("kind").GetString(), entry.GetProperty("machineName").GetString(), entry.GetProperty("fromAccountEmail").GetString(),
             entry.GetProperty("toAccountEmail").GetString(), entry.GetProperty("whyText").GetString(), entry.GetProperty("isOpen").GetBoolean()));
        Assert.False(json.GetProperty("hasMore").GetBoolean());
    }
}
