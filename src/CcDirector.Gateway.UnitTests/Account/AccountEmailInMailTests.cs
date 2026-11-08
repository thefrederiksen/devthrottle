using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Account;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Account;

/// <summary>
/// IN-MAIL BY DEFAULT, EMAIL ONLY WHEN CHOSEN (the owner, 8 November 2026). <c>POST /account/email</c> booted on an
/// in-memory test server over the real Your inbox store and a throwaway database, with a fake cloud standing in for the
/// email service and a stand-in for the authentication middleware putting on each request the identity the real one
/// would. These pin: a request with no choice goes to Your inbox and sends no email; <c>"deliver": "email"</c> sends the
/// email and writes nothing; what cannot be in-mail (attachments, an over-long body) is sent as an email AND noted in
/// Your inbox; an old client's request is answered in the shape its success check accepts; and the sender is named.
/// </summary>
public sealed class AccountEmailInMailTests : IAsyncLifetime
{
    private const string CallerHeader = "X-Test-Caller";
    private const string MachineToken = "machine";
    private const string Director = "director";
    private const string SessionKey = "session-key-412";
    private const string SeatKey = "session-key-301";
    private const string OtherAccountKey = "session-key-other";
    private static readonly Guid Developer = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Boss = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly TenantId A = TenantId.Local;
    private static readonly TenantId B = new("acct-b");

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase _db = null!;
    private YourInboxStore _store = null!;
    private readonly CloudStub _cloud = new();
    private WebApplication? _app;
    private HttpClient _client = null!;
    private readonly DateTime _now = new(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The email service, faked: counts calls and keeps the last request body so a test can read what was sent.</summary>
    private sealed class CloudStub : HttpMessageHandler
    {
        public int Calls;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"sent":true,"id":"resend-1"}}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    private sealed class NoRefresher : ITokenRefresher
    {
        public Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
            => Task.FromResult(TokenRefreshResult.Unavailable);
    }

    public async Task InitializeAsync()
    {
        _db = _h.Open();
        _store = new YourInboxStore(_db);
        var account = new DevThrottleAccountService(new InMemoryTokenStore(), new JwtAccessTokenValidator(GatewayTestJwt.SigningSecret), new NoRefresher());
        account.StoreTokens(new DevThrottleTokens(GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddDays(1), "owner@example.com", "github"), "refresh-1"));
        var boundary = new Tenancy.HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            StandInForAuthentication(ctx);
            await next();
        });
        AccountEmailEndpoint.Map(app, false, account, new AccountNotifyClient(new HttpClient(_cloud)), boundary,
            inMail: new AccountEmailEndpoint.InMail(
                Store: _store,
                ResolveTenant: _ => A,
                ResolveSessionKey: key => key switch
                {
                    SessionKey => new SessionCredentialIdentity(Developer, A, "dir-a"),
                    SeatKey => new SessionCredentialIdentity(Boss, A, "dir-a"),
                    OtherAccountKey => new SessionCredentialIdentity(Developer, B, "dir-b"),
                    _ => null,
                },
                Naming: (_, sid) => sid == Developer.ToString("D") ? new DevReportSessionNaming(412, "Release - Developer")
                    : sid == Boss.ToString("D") ? new DevReportSessionNaming(301, "Booking - Boss")
                    : new DevReportSessionNaming(null, null),
                FactorySeatOf: (_, sid) => sid == Boss.ToString("D") ? new DevReportFactorySeatIdentity("booking", "boss") : null,
                DeviceOf: id => id == "dev-devlinux" ? new DeviceDisplay("devlinux", "linux", "director") : null,
                NowUtc: () => _now));
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _h.Dispose();
    }

    private static void StandInForAuthentication(HttpContext ctx)
    {
        var caller = ctx.Request.Headers[CallerHeader].ToString();
        if (caller == Director)
        {
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "device-key";
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("dev-devlinux", A.Value, "director", "active");
        }
        else if (caller == MachineToken)
        {
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "machine-token";
        }
    }

    private async Task<(HttpStatusCode Status, JsonElement Json)> PostAsync(string json, string caller = MachineToken, string? sessionKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/account/email")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(CallerHeader, caller);
        if (sessionKey is not null) request.Headers.Add(AccountEmailEndpoint.SessionKeyHeader, sessionKey);
        var response = await _client.SendAsync(request);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, doc.RootElement.Clone());
    }

    private IReadOnlyList<Data.Entities.YourInboxMessageEntity> Inbox() => _store.List(A, YourInboxWords.Views.Inbox, 50).Messages;

    private static string Attachment(string name) =>
        $$"""{"filename":"{{name}}","content":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes("pdf bytes"))}}","contentType":"application/pdf"}""";

    // ---------------------------------------------------------------- the default: Your inbox

    [Fact]
    public async Task Post_NoDeliverField_WritesYourInboxAndSendsNoEmail()
    {
        var (status, json) = await PostAsync("""{"subject":"Nightly drift","bodyText":"2 aging pull requests"}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, _cloud.Calls);
        var message = Assert.Single(Inbox());
        Assert.Equal("Nightly drift", message.Subject);
        Assert.EndsWith("2 aging pull requests", message.Body);
        Assert.Equal("inbox", json.GetProperty("deliveredTo").GetString());
        Assert.Equal(message.Id.ToString("D"), json.GetProperty("inboxMessageId").GetString());
        Assert.Contains("Your inbox", json.GetProperty("message").GetString());
        Assert.Contains("--email", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Post_OldClientRequest_AnsweredInTheShapeItsSuccessCheckAccepts()
    {
        // The checks callers make today: a 2xx with JSON (cc-devthrottle email owner), "sent" true and a non-empty
        // "providerId" (the mentor send_report.py check). Nothing they read was removed or changed type.
        var (status, json) = await PostAsync("""{"subject":"Weekly report","bodyText":"Done."}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(json.GetProperty("sent").GetBoolean());
        var providerId = json.GetProperty("providerId").GetString();
        Assert.False(string.IsNullOrEmpty(providerId));
        Assert.StartsWith(AccountEmailEndpoint.YourInboxProviderPrefix, providerId);
        Assert.False(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Post_ExplicitDeliverInbox_IsTheSameAsTheDefault()
    {
        var (status, json) = await PostAsync("""{"subject":"S","bodyText":"B","deliver":"inbox"}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("inbox", json.GetProperty("deliveredTo").GetString());
        Assert.Equal(0, _cloud.Calls);
        Assert.Single(Inbox());
    }

    [Fact]
    public async Task Post_HtmlBody_BecomesPlainTextWithEveryLinkAddressKept()
    {
        var html = """{"subject":"Run report","bodyHtml":"<h1>Run 5</h1><p>See <a href=\"https://example.com/run/5\">the run</a> and https://example.com/raw.</p><ul><li>one</li><li>two</li></ul>"}""";
        var (status, _) = await PostAsync(html);

        Assert.Equal(HttpStatusCode.OK, status);
        var body = Assert.Single(Inbox()).Body;
        Assert.Contains("the run (https://example.com/run/5)", body);
        Assert.Contains("https://example.com/raw", body);
        Assert.Contains("- one", body);
        Assert.DoesNotContain("<", body);
    }

    // ---------------------------------------------------------------- who sent it

    [Fact]
    public async Task Post_MachineTokenCaller_IsShownFromOwnerEmailAndTheFirstLineNamesTheKey()
    {
        await PostAsync("""{"subject":"S","bodyText":"B"}""");

        var message = Assert.Single(Inbox());
        Assert.Equal(YourInboxWords.Senders.Gateway, message.FromKind);
        Assert.Equal(YourInboxWords.OwnerEmailSeat, message.Seat);
        Assert.StartsWith("Sent with this Gateway's machine token", message.Body);
        Assert.Equal("Owner email - no session named", YourInboxFold.FromLabel(message, new Dictionary<string, string>()));
    }

    [Fact]
    public async Task Post_DirectorKeyCaller_NamesTheMachine()
    {
        await PostAsync("""{"subject":"S","bodyText":"B"}""", caller: Director);

        var message = Assert.Single(Inbox());
        Assert.StartsWith("Sent with the key of the director \"devlinux\" (linux)", message.Body);
    }

    [Fact]
    public async Task Post_WithItsOwnSessionKey_IsNamedAsThatSessionLikeInboxSend()
    {
        await PostAsync("""{"subject":"S","bodyText":"Just the body"}""", sessionKey: SessionKey);

        var message = Assert.Single(Inbox());
        Assert.Equal(YourInboxWords.Senders.Session, message.FromKind);
        Assert.Equal(Developer.ToString("D"), message.SessionId);
        Assert.Equal(412, message.SessionNumber);
        Assert.Equal("Release - Developer", message.SessionName);
        Assert.Equal("Just the body", message.Body);
    }

    [Fact]
    public async Task Post_FromAFactorySeat_NamesTheFactoryAndSeat()
    {
        await PostAsync("""{"subject":"S","bodyText":"B"}""", sessionKey: SeatKey);

        var message = Assert.Single(Inbox());
        Assert.Equal(301, message.SessionNumber);
        Assert.Equal("booking", message.Factory);
        Assert.Equal("boss", message.Seat);
    }

    [Fact]
    public async Task Post_SessionKeyOfAnotherAccount_NamesNoSessionAndSaysSo()
    {
        await PostAsync("""{"subject":"S","bodyText":"B"}""", sessionKey: OtherAccountKey);

        var message = Assert.Single(Inbox());
        Assert.Equal(YourInboxWords.Senders.Gateway, message.FromKind);
        Assert.Null(message.SessionId);
        Assert.Contains("could not verify, so no session is named", message.Body);
    }

    // ---------------------------------------------------------------- email when chosen

    [Fact]
    public async Task Post_DeliverEmail_SendsTheEmailAndWritesNothing()
    {
        var (status, json) = await PostAsync("""{"subject":"S","bodyText":"B","deliver":"email"}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, _cloud.Calls);
        Assert.Empty(Inbox());
        Assert.True(json.GetProperty("sent").GetBoolean());
        Assert.Equal("resend-1", json.GetProperty("providerId").GetString());
        Assert.Equal("email", json.GetProperty("deliveredTo").GetString());
        Assert.False(json.TryGetProperty("inboxMessageId", out _));
    }

    [Fact]
    public async Task Post_DeliverEmailWithAttachments_SendsTheEmailAndWritesNoNote()
    {
        var (status, _) = await PostAsync($$"""{"subject":"S","bodyText":"B","deliver":"email","attachments":[{{Attachment("week.pdf")}}]}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, _cloud.Calls);
        Assert.Contains("week.pdf", _cloud.LastBody);
        Assert.Empty(Inbox());
    }

    [Fact]
    public async Task Post_UnknownDeliverValue_IsRefusedAndNothingIsSent()
    {
        var (status, json) = await PostAsync("""{"subject":"S","bodyText":"B","deliver":"sms"}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(json.GetProperty("sent").GetBoolean());
        Assert.Equal(0, _cloud.Calls);
        Assert.Empty(Inbox());
    }

    // ---------------------------------------------------------------- what cannot be in-mail

    [Fact]
    public async Task Post_AttachmentsWithoutTheChoice_SendsTheEmailAndWritesANote()
    {
        var (status, json) = await PostAsync($$"""{"subject":"Weekly report","bodyText":"See attached.","attachments":[{{Attachment("week.pdf")}}]}""",
            sessionKey: SessionKey);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, _cloud.Calls);
        Assert.Contains("week.pdf", _cloud.LastBody);
        var note = Assert.Single(Inbox());
        Assert.Equal("Sent to you as an email: Weekly report", note.Subject);
        Assert.Contains("week.pdf", note.Body);
        Assert.Contains("attachment", note.Body);
        Assert.Equal(412, note.SessionNumber);
        Assert.Equal("email", json.GetProperty("deliveredTo").GetString());
        Assert.Equal("resend-1", json.GetProperty("providerId").GetString());
        Assert.Equal(note.Id.ToString("D"), json.GetProperty("inboxMessageId").GetString());
        Assert.Contains("A short note in Your inbox says so", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Post_BodyOverTheYourInboxLimit_SendsTheEmailAndWritesANote()
    {
        var longBody = new string('x', YourInboxStore.MaxBodyLength + 1);
        var (status, json) = await PostAsync(JsonSerializer.Serialize(new { subject = "Big log", bodyText = longBody }));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, _cloud.Calls);
        var note = Assert.Single(Inbox());
        Assert.Contains($"holds at most {YourInboxStore.MaxBodyLength}", note.Body);
        Assert.StartsWith("Sent with this Gateway's machine token", note.Body);
        Assert.Equal("email", json.GetProperty("deliveredTo").GetString());
    }

    [Fact]
    public async Task Post_EmptyBodyAndNoAttachments_IsRefusedAndNothingIsSent()
    {
        var (status, json) = await PostAsync("""{"subject":"Only a subject"}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(json.GetProperty("sent").GetBoolean());
        Assert.Equal(0, _cloud.Calls);
        Assert.Empty(Inbox());
    }
}
