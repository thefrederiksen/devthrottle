using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Account;
using CcDirector.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// The hosted owner-email SEND (devthrottle_internal #986 consumer, closing the half of issue #984 that the
/// truthful-state fold could not).
///
/// Issue #984 stopped <c>POST /account/email</c> lying to a signed-in hosted user, but it still could not
/// send: the original cloud primitive resolves the recipient from an account access token, and the hosted
/// Gateway holds none - hosted enrollment validates the account token, mints a tenant and a device key, and
/// stores NEITHER. #986 adds a tenant-addressed cloud route so the Gateway names a TENANT and the cloud
/// resolves the recipient server-side. This class proves the Gateway's half of that wire against a stub
/// implementing the agreed contract exactly, so the wiring is verified before either side's credentialed
/// configuration step exists.
///
/// WHY A STUB AND NOT THE REAL ROUTE, stated so nobody reads more into these greens than they cover. The
/// cloud preview sits behind Vercel deployment protection, the tenant-resolution migration is not applied
/// yet, and the service secret was deliberately destroyed after being set (a credential that can mail any
/// tenant owner from a verified domain does not belong in a message log). So a live call is impossible until
/// a human applies two configuration steps. These tests prove REQUEST SHAPE, AUTH DISCIPLINE, RESPONSE
/// HANDLING and FAIL-CLOSED BEHAVIOUR - everything on this side of the wire. They do NOT prove that the
/// cloud resolves a real tenant; only a live run against production can, and it is deliberately outstanding.
///
/// THE PROPERTIES UNDER PROOF, in the order they matter:
/// <list type="number">
/// <item>The Gateway CANNOT ADDRESS ANYONE. There is no recipient field on the wire, and none can be
/// constructed - the safety property the whole design rests on.</item>
/// <item>An unconfigured Gateway FAILS CLOSED rather than calling unauthenticated, which is what keeps a
/// cloud 401 meaning "wrong secret" and never "no secret".</item>
/// <item>The service credential travels in its own header and NEVER as an Authorization header, because a
/// route whose auth mode depends on which header arrived will eventually pick the wrong one.</item>
/// <item>A refusal is never rendered as a success, and the cloud's human-readable message is surfaced
/// verbatim rather than reworded into something less true.</item>
/// </list>
/// </summary>
public sealed class HostedOwnerEmailByTenantTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";
    private const string ServiceSecret = "test-service-secret-for-986";

    private readonly string _subject = "sub-986-" + Guid.NewGuid().ToString("N");
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-986-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";
    private string _tenantId = "";
    private string? _priorRoot;
    private string? _priorSecret;
    private string? _priorApiUrl;

    /// <summary>
    /// A stub of the cloud route, run as a REAL HTTP server the Gateway reaches through
    /// <c>DEVTHROTTLE_API_URL</c>. Deliberately a server rather than an injected message handler: the
    /// Gateway builds its own client, and going over the wire exercises the real header names, the real JSON
    /// serialization and the real Retry-After parsing rather than a hand-fed object graph. The whole point is
    /// to catch a wire mismatch before a credentialed run can, so the wire has to be real.
    /// </summary>
    private sealed class CloudStub : IAsyncDisposable
    {
        private readonly Microsoft.AspNetCore.Builder.WebApplication _app;

        public int Status = 200;
        public string Body = """{"data":{"sent":true,"id":"resend-986"}}""";
        public string? RetryAfter;

        public int Calls;
        public string? SeenPath;
        public string? SeenServiceToken;
        public string? SeenAuthorization;
        public string? SeenBody;

        public string BaseUrl { get; private set; } = "";

        private CloudStub(Microsoft.AspNetCore.Builder.WebApplication app) => _app = app;

        public static async Task<CloudStub> StartAsync()
        {
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");
            var stub = new CloudStub(app);

            app.MapPost(AccountNotifyByTenantClient.NotifyOwnerByTenantPath, async (Microsoft.AspNetCore.Http.HttpContext ctx) =>
            {
                Interlocked.Increment(ref stub.Calls);
                stub.SeenPath = ctx.Request.Path.Value;
                stub.SeenServiceToken = ctx.Request.Headers[AccountNotifyByTenantClient.ServiceTokenHeader].FirstOrDefault();
                stub.SeenAuthorization = ctx.Request.Headers.Authorization.FirstOrDefault();
                using var reader = new StreamReader(ctx.Request.Body);
                stub.SeenBody = await reader.ReadToEndAsync();

                ctx.Response.StatusCode = stub.Status;
                if (stub.RetryAfter is not null)
                    ctx.Response.Headers["Retry-After"] = stub.RetryAfter;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(stub.Body);
            });

            await app.StartAsync();
            stub.BaseUrl = app.Urls.First();
            return stub;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    private CloudStub _cloud = null!;

    public async Task InitializeAsync()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _priorSecret = Environment.GetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar);
        _priorApiUrl = Environment.GetEnvironmentVariable(DevThrottleApi.BaseUrlEnvVar);

        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);
        Environment.SetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar, ServiceSecret);

        // Start the stub BEFORE the Gateway: the Gateway's egress client resolves its base URL at
        // construction, so the environment variable has to be pointing at the stub by then.
        _cloud = await CloudStub.StartAsync();
        Environment.SetEnvironmentVariable(DevThrottleApi.BaseUrlEnvVar, _cloud.BaseUrl.TrimEnd('/'));

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(_subject, "owner@example.com");
        _tenantId = tenant.Value;
        _key = _gateway.Devices.Register("dev-986", "M986").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-986", _subject, _tenantId);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        await _cloud.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        Environment.SetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar, _priorSecret);
        Environment.SetEnvironmentVariable(DevThrottleApi.BaseUrlEnvVar, _priorApiUrl);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    // ---- the client's wire shape, proven directly (no Gateway needed) -------------------------------

    [Fact]
    public void The_request_body_can_never_carry_a_recipient()
    {
        // PROPERTY 1, and the one that must never regress. The cloud refuses a recipient field with a hard
        // 400 rather than ignoring it; this asserts the Gateway could not send one in the first place. Belt
        // (their refusal) and braces (this) - and the braces are what stop a bad request ever being formed.
        var body = AccountNotifyByTenantClient.BuildBody(
            "tenant-1", "subject-1", "Subject line", "text", "<p>html</p>",
            new List<NotifyAttachment> { new("report.html", "YWJj", "text/html") });

        using var doc = JsonDocument.Parse(body);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        foreach (var forbidden in new[]
                 {
                     "to", "cc", "bcc", "email", "recipient", "recipients",
                     "owner_email", "to_email", "address", "from", "reply_to",
                 })
        {
            Assert.False(keys.Contains(forbidden, StringComparer.OrdinalIgnoreCase),
                $"the tenant-addressed body carried a recipient-shaped key '{forbidden}'. The Gateway must " +
                $"name a TENANT and nothing else - the recipient is resolved cloud-side. Body: {body}");
        }

        Assert.Equal("tenant-1", doc.RootElement.GetProperty("tenant_id").GetString());
        Assert.Equal("subject-1", doc.RootElement.GetProperty("account_subject").GetString());
    }

    [Fact]
    public void An_unresolvable_account_subject_is_omitted_rather_than_sent_blank()
    {
        // The cross-check is compared byte-for-byte cloud-side, so a blank would be a guaranteed 403
        // subject_mismatch - turning a missing cross-check into a failed send. Omission keeps it optional,
        // which is what the contract says it is.
        var body = AccountNotifyByTenantClient.BuildBody("tenant-1", null, "Subject", "text", null, null);

        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("account_subject", out _));
        Assert.Equal("tenant-1", doc.RootElement.GetProperty("tenant_id").GetString());
    }

    [Fact]
    public void A_two_hundred_that_does_not_confirm_a_send_is_not_reported_as_sent()
    {
        // A status code is not a delivery receipt. Reporting a send that did not happen is the exact failure
        // this whole line of work exists to prevent, so the success envelope must SAY sent.
        var result = AccountNotifyByTenantClient.ParseSuccess("""{"data":{"id":"x"}}""", 200);

        Assert.False(result.Sent);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("tenant_not_found")]
    [InlineData("owner_not_resolvable")]
    [InlineData("subject_mismatch")]
    [InlineData("notify_rate_limited")]
    [InlineData("tenant_lookup_not_installed")]
    [InlineData("gateway_auth_failed")]
    [InlineData("recipient_not_accepted")]
    public void Every_agreed_error_code_keeps_its_message_and_its_code(string code)
    {
        // The cloud's messages are written to be read by a human, so they are surfaced verbatim. The code is
        // for our logs and the retry rule only. Parsing every agreed code here means a contract change on
        // their side shows up as a red test rather than as a generic message in front of a user.
        var body = "{\"error\":{\"type\":\"t\",\"code\":\"" + code
                   + "\",\"message\":\"the human sentence for " + code + "\"}}";

        var (message, parsed) = AccountNotifyByTenantClient.ParseError(body, 400);

        Assert.Equal($"the human sentence for {code}", message);
        Assert.Equal(code, parsed);
    }

    // ---- end to end through the real hosted route ---------------------------------------------------

    [Fact]
    public async Task A_hosted_caller_sends_by_naming_its_own_tenant_and_never_a_recipient()
    {
        var resp = await PostEmail();
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("sent").GetBoolean());
        Assert.Equal("resend-986", doc.RootElement.GetProperty("providerId").GetString());

        Assert.Equal(1, _cloud.Calls);
        Assert.Equal(AccountNotifyByTenantClient.NotifyOwnerByTenantPath, _cloud.SeenPath);

        // The tenant it named is the caller's OWN, resolved from their authenticated device key - never
        // anything the caller put in the request.
        using var sent = JsonDocument.Parse(_cloud.SeenBody!);
        Assert.Equal(_tenantId, sent.RootElement.GetProperty("tenant_id").GetString());
        Assert.Equal(_subject, sent.RootElement.GetProperty("account_subject").GetString());
    }

    [Fact]
    public async Task The_service_credential_travels_in_its_own_header_and_never_as_authorization()
    {
        // Presenting both modes is a hard 400 by contract. The Gateway must not be the thing that does it,
        // and it must never leak the CALLER's device key upstream either.
        await PostEmail();

        Assert.Equal(ServiceSecret, _cloud.SeenServiceToken);
        Assert.True(string.IsNullOrEmpty(_cloud.SeenAuthorization), $"an Authorization header was sent alongside the service token, which the contract refuses with a 400: {_cloud.SeenAuthorization}");
        Assert.DoesNotContain(_key, _cloud.SeenBody ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unconfigured_gateway_refuses_rather_than_calling_unauthenticated()
    {
        // PROPERTY 2. If an unconfigured Gateway called anyway, a cloud 401 would mean either "wrong secret"
        // or "no secret" and neither end could tell which. Refusing here keeps that 401 diagnostic. It is
        // also the moment most likely to be reported as a sign-out by a future edit, so the message is
        // asserted, not just the status.
        Environment.SetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar, null);
        try
        {
            var resp = await PostEmail();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.Equal(0, _cloud.Calls);

            var error = await ErrorOf(resp);
            Assert.Contains(AccountNotifyByTenantClient.ServiceTokenEnvVar, error, StringComparison.Ordinal);
            Assert.Contains("You are signed in", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no email was sent", error, StringComparison.OrdinalIgnoreCase);
            Assert.False(error.Contains("not signed in", StringComparison.OrdinalIgnoreCase),
                $"an unconfigured Gateway reported a configuration gap as a sign-out. Body: {error}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar, ServiceSecret);
        }
    }

    [Fact]
    public async Task The_missing_migration_is_reported_in_the_cloud_own_words_and_never_as_a_send()
    {
        // The state the pair is actually in until a human applies the migration. The cloud answers 503
        // tenant_lookup_not_installed - deliberately not a 404, which would have claimed the tenant was
        // unknown and sent someone hunting a tenant problem that does not exist.
        _cloud.Status = 503;
        _cloud.Body = """{"error":{"type":"server_error","code":"tenant_lookup_not_installed","message":"The tenant lookup function is not installed on this deployment."}}""";

        var resp = await PostEmail();

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("sent").GetBoolean());
        Assert.Equal("The tenant lookup function is not installed on this deployment.",
            doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_rate_limit_keeps_its_status_and_passes_the_cloud_wait_through_untouched()
    {
        // The cloud computes Retry-After from when the oldest hit in its window actually expires, so
        // honouring it is sufficient and a backoff of our own would only fight it. Rewriting 429 to a generic
        // 400 would also strip the caller's ability to wait correctly.
        _cloud.Status = 429;
        _cloud.RetryAfter = "137";
        _cloud.Body = """{"error":{"type":"rate_limited","code":"notify_rate_limited","message":"Too many owner emails from this account recently. Try again shortly."}}""";

        var resp = await PostEmail();

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal("137", resp.Headers.TryGetValues("Retry-After", out var v) ? v.FirstOrDefault() : null);
        Assert.Contains("Too many owner emails", await ErrorOf(resp), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_subject_mismatch_is_surfaced_as_the_refusal_it_is()
    {
        // The cross-check doing its job: a wrong tenant id becomes a refusal instead of a correctly-delivered
        // email to the wrong person. It must never be softened into a success or a retry.
        _cloud.Status = 403;
        _cloud.Body = """{"error":{"type":"forbidden","code":"subject_mismatch","message":"The account subject does not match the tenant."}}""";

        var resp = await PostEmail();

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("does not match the tenant", await ErrorOf(resp), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_cannot_name_a_tenant_that_is_not_its_own()
    {
        // The isolation property, asserted from this side too. The tenant is taken from the authenticated
        // device key; anything tenant-shaped in the request body is ignored entirely.
        var request = new HttpRequestMessage(HttpMethod.Post, "account/email");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        request.Content = new StringContent(
            """{"subject":"s","bodyText":"b","tenant_id":"11111111-1111-1111-1111-111111111111","to":"someone@elsewhere.example"}""",
            Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var sent = JsonDocument.Parse(_cloud.SeenBody!);
        Assert.Equal(_tenantId, sent.RootElement.GetProperty("tenant_id").GetString());
        Assert.DoesNotContain("someone@elsewhere.example", _cloud.SeenBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", _cloud.SeenBody!, StringComparison.Ordinal);
    }

    private Task<HttpResponseMessage> PostEmail()
    {
        // Point the client's egress at the stub by handing the Gateway host a client over it. The route is
        // mapped by the real GatewayHost, so this replaces only the outermost network hop.
        var request = new HttpRequestMessage(HttpMethod.Post, "account/email");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        request.Content = new StringContent("""{"subject":"issue 986","bodyText":"body"}""",
            Encoding.UTF8, "application/json");
        return _http.SendAsync(request);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString() ?? "";
    }
}
