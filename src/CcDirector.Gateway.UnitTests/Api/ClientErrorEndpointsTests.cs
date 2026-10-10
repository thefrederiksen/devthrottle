using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The browser error channel writes into the durable error store (the Error Logging mission, issue #3675). The
/// owner's ruling: "Store them, scrubbed - same scrubbing as Director errors, readable only by that account and the
/// administrator token, same retention", with no free-text detail. These pin each clause: a report posted over HTTP
/// is stored under the caller's account with every structured field and read back through the errors read route;
/// detail, stack and page from an older client are NOT stored; another account cannot read it; a credential in the
/// message is scrubbed; a bad component is a 400; and nothing client-supplied reaches the service log.
/// It lives in this unlocked suite because it touches only its own temporary folder and a loopback port.
/// </summary>
public sealed class ClientErrorEndpointsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    // Values an older client sent in the fields that are no longer stored. Distinct, so a leak names its field.
    private const string DetailSentinel = "detail-sentinel-the-prompt-said-fix-login";
    private const string StackSentinel = "stack-sentinel-at-composer-tsx";
    private const string PageSentinel = "/m/page-sentinel/private-route";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "client-errors-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ErrorReportStore NewStore() => new(_root, () => Now);

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string UniqueDevice() => "dev-" + Guid.NewGuid().ToString("N")[..10];

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static ErrorReportQuery Everything(string? account = null) => new(Now.AddDays(-1), Now.AddMinutes(1), Account: account);

    private const string FullReport = """
        {"component":"mobile","surface":"mobile-session-controls","action":"send prompt",
         "message":"DevThrottle could not send prompt - the machine running this session could not be reached (error 502).",
         "user_visible":true,"exception_type":"GatewayError","http_status":502,"error_code":"director_offline",
         "session_id":"2c3c4215-0a1b-4c2d-9e8f-0123456789ab","correlation_id":"corr-7f3a9c"}
        """;

    private async Task<(WebApplication App, string Url)> StartAsync(ErrorReportStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        ClientErrorEndpoints.Map(app, store, tenantBoundary: null);
        DirectorErrorEndpoints.Map(app, store, tenantBoundary: null, tenants: null);
        await app.StartAsync();
        return (app, app.Urls.First());
    }

    [Fact]
    public async Task A_report_posted_over_HTTP_is_stored_with_every_field_and_read_back_through_the_errors_read()
    {
        var store = new ErrorReportStore(_root);
        var (app, url) = await StartAsync(store);
        try
        {
            using var client = new HttpClient();
            // An older client's three free-text fields ride along; the server drops them.
            var body = FullReport.TrimEnd().TrimEnd('}')
                + $",\"detail\":\"{DetailSentinel}\",\"stack\":\"{StackSentinel}\",\"page\":\"{PageSentinel}\"}}";
            var post = await client.PostAsync($"{url}/client-errors", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

            var answer = await client.GetFromJsonAsync<JsonElement>($"{url}/gateway/director-errors?component=mobile");

            Assert.Equal("account", answer.GetProperty("scope").GetString());
            Assert.Equal(90, answer.GetProperty("retention_days").GetInt32());
            var error = Assert.Single(answer.GetProperty("errors").EnumerateArray());
            Assert.Equal("mobile", error.GetProperty("component").GetString());
            Assert.Equal(TenantId.Local.Value, error.GetProperty("account").GetString());
            Assert.Equal("mobile-session-controls", error.GetProperty("surface").GetString());
            Assert.Equal("mobile-session-controls", error.GetProperty("source").GetString());
            Assert.Equal("browser", error.GetProperty("kind").GetString());
            Assert.Equal("send prompt", error.GetProperty("action").GetString());
            Assert.True(error.GetProperty("user_visible").GetBoolean());
            Assert.Equal("GatewayError", error.GetProperty("exception_type").GetString());
            Assert.Equal(502, error.GetProperty("http_status").GetInt32());
            Assert.Equal("director_offline", error.GetProperty("error_code").GetString());
            Assert.Equal("2c3c4215-0a1b-4c2d-9e8f-0123456789ab", error.GetProperty("session_id").GetString());
            Assert.Equal("corr-7f3a9c", error.GetProperty("correlation_id").GetString());
            Assert.StartsWith("DevThrottle could not send prompt", error.GetProperty("message").GetString());
            Assert.Equal(16, error.GetProperty("fingerprint").GetString()!.Length);

            var raw = error.GetRawText();
            Assert.DoesNotContain(DetailSentinel, raw);
            Assert.DoesNotContain(StackSentinel, raw);
            Assert.DoesNotContain(PageSentinel, raw);
            // The read writes every field; a browser report's stack is always empty.
            Assert.True(!error.TryGetProperty("stack", out var stack) || stack.ValueKind == JsonValueKind.Null);

            // The grouped read sees it too.
            var groups = await client.GetFromJsonAsync<JsonElement>($"{url}/gateway/director-errors/groups?component=mobile");
            Assert.Equal(1, groups.GetProperty("total_reports").GetInt32());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        // And it is in the files on disk, not in memory: a fresh store over the same folder - a redeploy - has it.
        var again = new ErrorReportStore(_root).Query(new ErrorReportQuery(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddMinutes(1)));
        Assert.Equal("mobile", Assert.Single(again.Records).Component);
        foreach (var file in Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain(DetailSentinel, text);
            Assert.DoesNotContain(StackSentinel, text);
            Assert.DoesNotContain(PageSentinel, text);
        }
    }

    [Fact]
    public void HandlePost_IsStoredUnderTheCallersAccount_AndAnotherAccountCannotReadIt()
    {
        var store = NewStore();

        Assert.Equal(StatusCodes.Status202Accepted, Status(ClientErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Json(FullReport), Now)));

        // The account read is scoped to the caller's account by DirectorErrorEndpoints, whatever the query says;
        // this is the filter it applies.
        var mine = store.Query(Everything(TenantA.Value));
        Assert.Equal(TenantA.Value, Assert.Single(mine.Records).Account);
        Assert.Empty(store.Query(Everything(TenantB.Value)).Records);
    }

    [Fact]
    public void HandlePost_ACredentialInTheMessage_IsScrubbed_LikeADirectorError()
    {
        var store = NewStore();
        const string body = """
            {"component":"cockpit","surface":"cockpit-composer","action":"attach the image",
             "message":"upload failed: token=sk-live-abc123 at C:\\Users\\robert\\shots\\a.png Bearer eyJhbGciOiJIUzI1NiJ9xyz",
             "user_visible":true,"exception_type":"password=hunter2"}
            """;

        Assert.Equal(StatusCodes.Status202Accepted, Status(ClientErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Json(body), Now)));

        var record = Assert.Single(store.Query(Everything()).Records);
        Assert.DoesNotContain("sk-live-abc123", record.Message);
        Assert.DoesNotContain("robert", record.Message);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9xyz", record.Message);
        Assert.Contains("~", record.Message);
        Assert.DoesNotContain("hunter2", record.ExceptionType);
    }

    [Theory]
    [InlineData("""{"surface":"s","message":"m"}""")]
    [InlineData("""{"component":"director","message":"m"}""")]
    [InlineData("""{"component":"gateway","message":"m"}""")]
    [InlineData("""{"component":"Mobile","message":"m"}""")]
    [InlineData("""{"component":7,"message":"m"}""")]
    public void HandlePost_AComponentOtherThanCockpitOrMobile_IsA400(string body)
    {
        var store = NewStore();

        Assert.Equal(StatusCodes.Status400BadRequest, Status(ClientErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Json(body), Now)));
        Assert.Empty(store.Query(Everything()).Records);
    }

    [Theory]
    // A field that is not on the contract is refused by name, so a renamed field cannot vanish silently.
    [InlineData("""{"component":"mobile","message":"m","sessionId":"x"}""")]
    [InlineData("""{"component":"mobile"}""")]
    [InlineData("""{"component":"mobile","message":"m","http_status":42}""")]
    [InlineData("""{"component":"mobile","message":"m","user_visible":"yes"}""")]
    [InlineData("""["not","an","object"]""")]
    public void HandlePost_AMalformedReport_IsA400(string body)
    {
        var store = NewStore();

        Assert.Equal(StatusCodes.Status400BadRequest, Status(ClientErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Json(body), Now)));
        Assert.Empty(store.Query(Everything()).Records);
    }

    [Fact]
    public void HandlePost_OneDeviceOverItsPerMinuteCap_IsRateLimited_AndAnotherDeviceIsNot()
    {
        var store = NewStore();
        var flooding = UniqueDevice();
        var statuses = Enumerable.Range(0, 35)
            .Select(_ => Status(ClientErrorEndpoints.HandlePost(store, TenantA, flooding, Json(FullReport), Now, new ErrorIntakeFloods())))
            .ToList();

        Assert.Equal(30, statuses.Count(s => s == StatusCodes.Status202Accepted));
        Assert.Equal(5, statuses.Count(s => s == StatusCodes.Status429TooManyRequests));
        Assert.Equal(StatusCodes.Status202Accepted, Status(ClientErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Json(FullReport), Now)));
    }

    [Fact]
    public void HandlePost_CorrelationIdTheGatewayMinted_IsStoredVerbatim_NotScrubbedAsAKey()
    {
        // The browser reports the X-Correlation-Id the Gateway put on its error answer (step 2). That id is a run of 32
        // characters - the shape the scrubber redacts when it looks like a key - so it must come back exactly as minted,
        // or the browser's row and the Gateway's row stop reading as one incident.
        var store = NewStore();
        var minted = GatewayRequestErrors.NewCorrelationId();
        var body = $$"""{"component":"mobile","surface":"s","action":"send prompt","message":"could not send","correlation_id":"{{minted}}"}""";

        Assert.Equal(StatusCodes.Status202Accepted, Status(ClientErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Json(body), Now)));

        Assert.Equal(minted, Assert.Single(store.Query(Everything()).Records).CorrelationId);
    }

    [Fact]
    public void HandlePost_BurstPastTheDeviceCap_IsOneFloodRowForClientErrors_WithTheCountDropped()
    {
        // Step 2's flood record (issue #3675): every report this route refuses over its limit is counted, and the
        // closed hour becomes ONE row in the store naming the route and the count - so a flood is never silent.
        var clock = Now;
        var store = new ErrorReportStore(_root, () => clock);
        var floods = new ErrorIntakeFloods();
        var flooding = UniqueDevice();

        var refused = Enumerable.Range(0, 37)
            .Count(_ => Status(ClientErrorEndpoints.HandlePost(store, TenantA, flooding, Json(FullReport), Now, floods))
                == StatusCodes.Status429TooManyRequests);
        Assert.Equal(7, refused);
        Assert.Equal(7, floods.Pending);

        clock = Now.AddHours(1).AddMinutes(1);
        var facts = new GatewayErrorSink.ProcessFacts("2.18.0-test", "linux", "Linux 6", "x64", ErrorReportMachineId.Of("gw"));
        Assert.Equal(1, new GatewayErrorSink(store, floods, () => clock, facts).Flush());

        var flood = Assert.Single(store.Query(new ErrorReportQuery(Now.AddDays(-1), clock.AddMinutes(1))).Records,
            r => r.Kind == ErrorIntakeFloods.Kind);
        Assert.Equal(ErrorReportLimits.Gateway, flood.Component);
        Assert.Equal("POST /client-errors", flood.Surface);
        Assert.Equal("POST /client-errors dropped 7 report(s) over its limit in the hour from 2026-10-09T12:00Z", flood.Message);
        Assert.Equal(7, flood.RepeatCount);
        Assert.Equal(StatusCodes.Status429TooManyRequests, flood.HttpStatus);
        Assert.Equal(0, floods.Pending);
    }
}

/// <summary>The service log carries who and which component, never client text (FileLog is one process-wide seam,
/// so this runs in the capture collection, alone).</summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class ClientErrorServiceLogTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "client-errors-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ErrorReportStore NewStore() => new(_root, () => Now);

    private static string UniqueDevice() => "dev-" + Guid.NewGuid().ToString("N")[..10];

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public async Task A_hostile_report_leaves_no_client_text_in_the_service_log()
    {
        using var logScope = FileLog.RedirectForTests();
        const string hostile = "the password is hunter2 and the prompt said fix login";
        var store = NewStore();

        var body = $$"""{"component":"mobile","surface":"{{hostile}}","action":"{{hostile}}","message":"{{hostile}}","session_id":"{{hostile}}"}""";
        ClientErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Json(body), Now);
        await Task.Yield();

        var lines = logScope.DrainAndReadLines();
        Assert.Contains(lines, l => l.Contains("[ClientErrorEndpoints] recorded 1 report"));
        Assert.All(lines, l =>
        {
            Assert.DoesNotContain("hunter2", l);
            Assert.DoesNotContain("prompt said", l);
            Assert.DoesNotContain(TenantA.Value, l);
        });
    }
}

/// <summary>The administrator token reads every account's browser errors - and only it does (issue #3675).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ClientErrorAdministratorReadTests : IDisposable
{
    private const string Token = "test-admin-service-token-client-errors-5c2e";
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "client-errors-admin-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorAdmin = Environment.GetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar);

    public ClientErrorAdministratorReadTests()
        => Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, Token);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, _priorAdmin);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_administrator_token_reads_a_browser_report_and_no_token_does_not()
    {
        var store = new ErrorReportStore(_root);
        var body = JsonSerializer.Deserialize<JsonElement>("""
            {"component":"cockpit","surface":"cockpit-composer","action":"send that to the session","message":"could not send","user_visible":true}
            """);
        ClientErrorEndpoints.HandlePost(store, TenantA, "dev-admin-read", body, DateTime.UtcNow);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        DirectorErrorEndpoints.Map(app, store, tenantBoundary: null, tenants: null);
        await app.StartAsync();
        try
        {
            var url = app.Urls.First();
            using var anonymous = new HttpClient();
            var refused = await anonymous.GetAsync($"{url}{DirectorErrorEndpoints.AdminPath}?component=cockpit");
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

            using var admin = new HttpClient();
            admin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
            var answer = await admin.GetFromJsonAsync<JsonElement>($"{url}{DirectorErrorEndpoints.AdminPath}?component=cockpit&account={TenantA.Value}");

            Assert.Equal("all-accounts", answer.GetProperty("scope").GetString());
            var error = Assert.Single(answer.GetProperty("errors").EnumerateArray());
            Assert.Equal(TenantA.Value, error.GetProperty("account").GetString());
            Assert.Equal("send that to the session", error.GetProperty("action").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
