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
/// IN-MAIL BY DEFAULT ON THE HOSTED GATEWAY, which is where production runs. <c>POST /account/email</c> is mapped with
/// hosted on, over a REAL hosted tenant boundary, and the in-mail path resolves the caller's account through the same
/// resolver production wires (<see cref="GatewayEndpoints.ResolveReadTenant"/>), so the tenant comes from the verified
/// key and never from the request. A fake cloud stands in for both email senders and counts calls. These pin: a hosted
/// caller that does not choose email gets a Your inbox message in ITS OWN account, nothing is sent by email, and
/// another account's inbox is untouched.
/// </summary>
public sealed class AccountEmailInMailHostedTests : IAsyncLifetime
{
    private const string CallerHeader = "X-Test-Caller";
    private const string DeviceOfA = "device-a";
    private const string SessionOfA = "session-a";
    private const string KeyOfA = "session-key-a";
    private const string KeyOfB = "session-key-b";
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private static readonly Guid SessionA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid SessionB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase _db = null!;
    private YourInboxStore _store = null!;
    private readonly CloudStub _cloud = new();
    private WebApplication? _app;
    private HttpClient _client = null!;

    /// <summary>Both email senders, faked: any call at all is counted, so "no email was sent" is a count of zero.</summary>
    private sealed class CloudStub : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"sent":true,"id":"resend-1"}}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    public async Task InitializeAsync()
    {
        _db = _h.Open();
        _store = new YourInboxStore(_db);
        var boundary = new Tenancy.HostedTenantBoundary(new AsyncLocalTenantContext(), new DeviceRegistry(), hosted: true);

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
        AccountEmailEndpoint.Map(app, hosted: true, account: null, new AccountNotifyClient(new HttpClient(_cloud)), boundary,
            byTenant: new AccountNotifyByTenantClient(new HttpClient(_cloud)),
            inMail: new AccountEmailEndpoint.InMail(
                Store: _store,
                ResolveTenant: ctx => GatewayEndpoints.ResolveReadTenant(ctx, boundary),
                ResolveSessionKey: key => key switch
                {
                    KeyOfA => new SessionCredentialIdentity(SessionA, A, "dir-a"),
                    KeyOfB => new SessionCredentialIdentity(SessionB, B, "dir-b"),
                    _ => null,
                },
                Naming: (tenant, sid) => tenant == A && sid == SessionA.ToString("D") ? new DevReportSessionNaming(120, "Booking - Developer")
                    : new DevReportSessionNaming(null, null),
                FactorySeatOf: (_, _) => null,
                DeviceOf: id => id == "dev-a-laptop" ? new DeviceDisplay("laptop-a", "windows", "director") : null,
                NowUtc: () => new DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc)));
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
        if (caller == DeviceOfA)
        {
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "device-key-a";
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("dev-a-laptop", A.Value, "director", "active");
        }
        else if (caller == SessionOfA)
        {
            // A session key authenticating the request itself. The session key guard refuses that on this route in
            // production; this proves that if it ever arrived, the hosted boundary still binds it to its own account.
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = KeyOfA;
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(SessionA, A, "dir-a");
        }
    }

    private async Task<(HttpStatusCode Status, JsonElement Json)> PostAsync(string caller, string? sessionKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/account/email")
        {
            Content = new StringContent("""{"subject":"Morning check","bodyText":"All schedules ran.","tenant_id":"acct-b"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(CallerHeader, caller);
        if (sessionKey is not null) request.Headers.Add(AccountEmailEndpoint.SessionKeyHeader, sessionKey);
        var response = await _client.SendAsync(request);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, doc.RootElement.Clone());
    }

    private IReadOnlyList<Data.Entities.YourInboxMessageEntity> InboxOf(TenantId tenant) => _store.List(tenant, YourInboxWords.Views.Inbox, 50).Messages;

    [Fact]
    public async Task Post_HostedDeviceKeyNoChoice_WritesItsOwnAccountsInboxAndSendsNoEmail()
    {
        var (status, json) = await PostAsync(DeviceOfA);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("inbox", json.GetProperty("deliveredTo").GetString());
        Assert.Equal(0, _cloud.Calls);
        var message = Assert.Single(InboxOf(A));
        Assert.Equal("Morning check", message.Subject);
        Assert.StartsWith("Sent with the key of the director \"laptop-a\" (windows)", message.Body);
        Assert.Empty(InboxOf(B));
    }

    [Fact]
    public async Task Post_HostedDeviceKeyWithItsSessionKey_IsNamedAsTheSessionInItsOwnAccount()
    {
        var (status, _) = await PostAsync(DeviceOfA, sessionKey: KeyOfA);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, _cloud.Calls);
        var message = Assert.Single(InboxOf(A));
        Assert.Equal(SessionA.ToString("D"), message.SessionId);
        Assert.Equal(120, message.SessionNumber);
        Assert.Empty(InboxOf(B));
    }

    [Fact]
    public async Task Post_HostedSessionKeyAsTheCaller_WritesItsOwnAccountsInboxAndSendsNoEmail()
    {
        var (status, _) = await PostAsync(SessionOfA);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, _cloud.Calls);
        var message = Assert.Single(InboxOf(A));
        Assert.Equal(SessionA.ToString("D"), message.SessionId);
        Assert.Empty(InboxOf(B));
    }

    [Fact]
    public async Task Post_HostedDeviceKeyHandingAnotherAccountsSessionKey_WritesOnlyToItsOwnAccount()
    {
        var (status, _) = await PostAsync(DeviceOfA, sessionKey: KeyOfB);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, _cloud.Calls);
        var message = Assert.Single(InboxOf(A));
        Assert.Null(message.SessionId);
        Assert.Contains("could not verify, so no session is named", message.Body);
        Assert.Empty(InboxOf(B));
    }

    [Fact]
    public async Task Post_HostedCallerBoundToNoAccount_IsRefusedAndNothingIsWrittenOrSent()
    {
        var (status, json) = await PostAsync("nobody");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.False(json.GetProperty("sent").GetBoolean());
        Assert.Equal(0, _cloud.Calls);
        Assert.Empty(InboxOf(A));
        Assert.Empty(InboxOf(B));
    }
}
