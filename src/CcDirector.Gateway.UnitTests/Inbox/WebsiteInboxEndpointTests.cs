using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
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

namespace CcDirector.Gateway.UnitTests.Inbox;

/// <summary>
/// THE WEBSITE WRITES TO YOUR INBOX (brief E). The routes booted on an in-memory test server over the real key store, the
/// real Your inbox store and a throwaway database, with a stand-in for the authentication middleware putting on each
/// owner request the identity the real one would. The write route is exempt from that gate in production and carries its
/// own check, so its requests carry only the website key. These pin: the key adds one message to the ONE account it was
/// made for, from "DevThrottle website - what", with no Reply; a missing, wrong or revoked key writes nothing; what Your
/// inbox cannot hold is refused so the website emails it; a retry with the same key writes once; 200 a day; and only the
/// owner's phone or browser makes, replaces or revokes the key.
/// </summary>
public sealed class WebsiteInboxEndpointTests : IAsyncLifetime
{
    private const string CallerHeader = "X-Test-Caller";
    private const string Phone = "phone";
    private const string Browser = "browser";
    private const string Director = "workstation";
    private const string Session = "session";
    private const string Machine = "machine";
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase _db = null!;
    private YourInboxStore _inbox = null!;
    private WebsiteInboxKeyStore _keys = null!;
    private WebApplication? _app;
    private HttpClient _client = null!;
    private DateTime _now = new(2026, 11, 8, 21, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        _db = _h.Open();
        _inbox = new YourInboxStore(_db);
        _keys = new WebsiteInboxKeyStore(_db);
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
        WebsiteInboxEndpoints.Map(app, new WebsiteInboxEndpoints.Parts(
            Keys: _keys,
            Inbox: _inbox,
            ResolveTenant: ctx => ctx.Request.Headers.TryGetValue("X-Test-Account", out var v) ? new TenantId(v.ToString()) : A,
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
        switch (caller)
        {
            case Phone or Browser or Director:
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "device-key";
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity($"dev-{caller}", A.Value, caller, "active");
                break;
            case Session:
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "session-key";
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Guid.NewGuid(), A, "dir-a");
                break;
            case Machine:
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "machine-token";
                break;
        }
    }

    private async Task<(HttpStatusCode Status, JsonElement Json)> SendAsync(HttpMethod method, string path, string? caller = null,
        string? bearer = null, string? json = null, string? account = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (caller is not null) request.Headers.Add(CallerHeader, caller);
        if (account is not null) request.Headers.Add("X-Test-Account", account);
        if (bearer is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, doc.RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Json)> WriteAsync(string? key, string json)
        => SendAsync(HttpMethod.Post, WebsiteInboxEndpoints.WritePath, bearer: key, json: json);

    private async Task<string> MintAsync(string caller = Phone)
    {
        var (status, json) = await SendAsync(HttpMethod.Post, WebsiteInboxEndpoints.KeyPath, caller);
        Assert.Equal(HttpStatusCode.Created, status);
        return json.GetProperty("key").GetString()!;
    }

    private IReadOnlyList<YourInboxMessageEntity> InboxOf(TenantId t) => _inbox.List(t, YourInboxWords.Views.Inbox, 500).Messages;

    private const string Ok = """{"what":"weekly-report","subject":"Weekly report","bodyText":"12 visitors, 2 accounts."}""";

    // ---------------------------------------------------------------- the website's write

    [Fact]
    public async Task Write_WithTheAccountsKey_AddsOneMessageFromTheWebsiteToThatAccountOnly()
    {
        var key = await MintAsync();

        var (status, json) = await WriteAsync(key, Ok);

        Assert.Equal(HttpStatusCode.Created, status);
        var message = Assert.Single(InboxOf(A));
        Assert.Equal(json.GetProperty("messageId").GetString(), message.Id.ToString("D"));
        Assert.Equal("Weekly report", message.Subject);
        Assert.Equal("12 visitors, 2 accounts.", message.Body);
        Assert.Equal(YourInboxWords.Senders.Website, message.FromKind);
        Assert.Equal(YourInboxWords.States.Unread, message.State);
        Assert.Empty(InboxOf(B));
        var folded = YourInboxFold.Message(message, new Dictionary<string, string>());
        Assert.Equal("DevThrottle website - Weekly report", folded.From.Label);
        Assert.Null(folded.ReplyTo);
        Assert.Empty(folded.Links);
    }

    [Fact]
    public async Task Write_KeyOfAnotherAccount_WritesOnlyToTheAccountTheKeyWasMadeFor()
    {
        var (minted, made) = await SendAsync(HttpMethod.Post, WebsiteInboxEndpoints.KeyPath, Phone, account: B.Value);
        Assert.Equal(HttpStatusCode.Created, minted);
        var keyOfB = made.GetProperty("key").GetString()!;

        await WriteAsync(keyOfB, """{"what":"booking","subject":"Booked","bodyText":"A call at 10:00.","tenant":"acct-a"}""");

        Assert.Empty(InboxOf(A));
        Assert.Single(InboxOf(B));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("dtwi_not-a-real-key")]
    [InlineData("shared-machine-token")]
    public async Task Write_WithoutALiveKey_Is401AndWritesNothing(string? bearer)
    {
        await MintAsync();

        var (status, json) = await WriteAsync(bearer, Ok);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("website_key_required", json.GetProperty("code").GetString());
        Assert.Empty(InboxOf(A));
    }

    [Fact]
    public async Task Write_RevokedKey_Is401AndSaysItWasRevoked()
    {
        var key = await MintAsync();
        await SendAsync(HttpMethod.Delete, WebsiteInboxEndpoints.KeyPath, Phone);

        var (status, json) = await WriteAsync(key, Ok);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("website_key_revoked", json.GetProperty("code").GetString());
        Assert.Empty(InboxOf(A));
    }

    [Fact]
    public async Task Write_AfterANewKeyIsMade_TheOldKeyStopsWorking()
    {
        var old = await MintAsync();
        var fresh = await MintAsync(Browser);

        Assert.Equal(HttpStatusCode.Unauthorized, (await WriteAsync(old, Ok)).Status);
        Assert.Equal(HttpStatusCode.Created, (await WriteAsync(fresh, Ok)).Status);
    }

    [Fact]
    public async Task Write_UnknownWhat_IsRefused()
    {
        var key = await MintAsync();

        var (status, json) = await WriteAsync(key, """{"what":"marketing","subject":"S","bodyText":"B"}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("unknown_what", json.GetProperty("code").GetString());
        Assert.Empty(InboxOf(A));
    }

    [Fact]
    public async Task Write_HtmlBody_BecomesTextWithItsLinksKept()
    {
        var key = await MintAsync();

        await WriteAsync(key, """{"what":"ai-cost","subject":"AI cost","bodyHtml":"<p>See <a href=\"https://devthrottle.com/admin/cost\">the cost page</a>.</p>"}""");

        Assert.Equal("See the cost page (https://devthrottle.com/admin/cost).", Assert.Single(InboxOf(A)).Body);
    }

    [Fact]
    public async Task Write_BodyOverYourInboxLimit_Is413SoTheWebsiteEmailsIt()
    {
        var key = await MintAsync();
        var json = JsonSerializer.Serialize(new { what = "weekly-report", subject = "Big", bodyText = new string('x', YourInboxStore.MaxBodyLength + 1) });

        var (status, answer) = await WriteAsync(key, json);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal("body_too_long", answer.GetProperty("code").GetString());
        Assert.Empty(InboxOf(A));
    }

    [Fact]
    public async Task Write_SubjectOverTheLimit_Is413()
    {
        var key = await MintAsync();
        var json = JsonSerializer.Serialize(new { what = "booking", subject = new string('s', YourInboxStore.MaxSubjectLength + 1), bodyText = "B" });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await WriteAsync(key, json)).Status);
    }

    [Fact]
    public async Task Write_SameRetryKeyTwice_WritesOnceAndAnswersWithTheFirst()
    {
        var key = await MintAsync();
        const string body = """{"what":"booking","subject":"Booked","bodyText":"10:00","key":"booking-123-owner"}""";

        var first = await WriteAsync(key, body);
        var second = await WriteAsync(key, body);

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.True(second.Json.GetProperty("duplicate").GetBoolean());
        Assert.Equal(first.Json.GetProperty("messageId").GetString(), second.Json.GetProperty("messageId").GetString());
        Assert.Single(InboxOf(A));
    }

    [Fact]
    public async Task Write_OverTwoHundredInADay_Is429()
    {
        var key = await MintAsync();
        for (var i = 0; i < WebsiteInboxEndpoints.DailyLimit; i++)
            _inbox.Add(A, YourInboxStore.ForWebsite(A, YourInboxWords.WebsiteSeats.Booking, $"Booked {i}", "B", null, _now.AddHours(-1)));

        var (status, json) = await WriteAsync(key, Ok);

        Assert.Equal((HttpStatusCode)429, status);
        Assert.Equal("daily_limit", json.GetProperty("code").GetString());
        Assert.Equal(WebsiteInboxEndpoints.DailyLimit, InboxOf(A).Count);
    }

    [Fact]
    public async Task Write_MessagesOlderThanADay_DoNotCountTowardTheLimit()
    {
        var key = await MintAsync();
        for (var i = 0; i < WebsiteInboxEndpoints.DailyLimit; i++)
            _inbox.Add(A, YourInboxStore.ForWebsite(A, YourInboxWords.WebsiteSeats.Booking, $"Booked {i}", "B", null, _now.AddHours(-25)));

        Assert.Equal(HttpStatusCode.Created, (await WriteAsync(key, Ok)).Status);
    }

    [Fact]
    public async Task Write_RecordsWhenTheKeyWasLastUsed()
    {
        var key = await MintAsync();
        await WriteAsync(key, Ok);

        Assert.Equal(_now, _keys.Status(A).LastUsedAtUtc);
    }

    // ---------------------------------------------------------------- the owner's key

    [Theory]
    [InlineData(Phone)]
    [InlineData(Browser)]
    public async Task Mint_FromTheOwnersPhoneOrBrowser_ShowsTheKeyOnceAndTheCardSaysActive(string caller)
    {
        var (status, json) = await SendAsync(HttpMethod.Post, WebsiteInboxEndpoints.KeyPath, caller);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.StartsWith(WebsiteInboxKeyStore.KeyPrefix, json.GetProperty("key").GetString());
        Assert.Equal("active", json.GetProperty("state").GetString());
        Assert.Contains("WEBSITE_INBOX_KEY", json.GetProperty("keyLine").GetString());

        var (_, card) = await SendAsync(HttpMethod.Get, WebsiteInboxEndpoints.KeyPath, caller);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("key").ValueKind);
        Assert.Equal("Make a new key", card.GetProperty("mintLabel").GetString());
        Assert.True(card.GetProperty("canRevoke").GetBoolean());
    }

    [Theory]
    [InlineData(Director)]
    [InlineData(Session)]
    [InlineData(Machine)]
    [InlineData(null)]
    public async Task KeyRoutes_FromAnythingButThePhoneOrBrowser_AreRefusedAndNoKeyIsMade(string? caller)
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(method, WebsiteInboxEndpoints.KeyPath, caller)).Status);
        Assert.Equal(WebsiteInboxKeyStore.States.None, _keys.Status(A).State);
    }

    [Fact]
    public async Task Status_WithNoKey_SaysTheWebsiteEmailsYou()
    {
        var (status, json) = await SendAsync(HttpMethod.Get, WebsiteInboxEndpoints.KeyPath, Phone);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("none", json.GetProperty("state").GetString());
        Assert.Contains("emails you", json.GetProperty("statusLine").GetString());
        Assert.False(json.GetProperty("canRevoke").GetBoolean());
    }

    [Fact]
    public async Task Revoke_KeepsTheRowAndTheCardSaysRevoked()
    {
        await MintAsync();

        var (status, json) = await SendAsync(HttpMethod.Delete, WebsiteInboxEndpoints.KeyPath, Phone);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("revoked", json.GetProperty("state").GetString());
        Assert.Equal(_now, _keys.Status(A).RevokedAtUtc);
    }

    [Fact]
    public void KeyStore_StoresOnlyTheHash()
    {
        var key = _keys.Mint(A, "dev-phone", _now);

        using var ctx = _db.CreateUnscopedContext();
        var row = Assert.Single(ctx.WebsiteInboxKeys.ToList());
        Assert.Equal(WebsiteInboxKeyStore.Hash(key), row.KeyHash);
        Assert.DoesNotContain(key, row.KeyHash);
    }
}
