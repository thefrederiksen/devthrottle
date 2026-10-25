using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Push;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Lib.Net.Http.WebPush;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The message link request push and the phone's card (issue #4373): a NEW request reaches every subscribed phone with
/// the two session numbers only - never the reason, never a name, never an id - and the list names each session by its
/// number and name in words the phone renders verbatim.
/// </summary>
public sealed class MessageLinkRequestPushNotifierTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly TenantId Tenant = new("acct-push");
    private const string Requester = "aaaaaaaa-0000-0000-0000-000000000121";
    private const string Target = "bbbbbbbb-0000-0000-0000-000000000125";
    private static readonly DateTime T0 = new(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);

    private PushSubscriptionStore NewStore()
        => new(_h.Open(), _h.LegacyPath("push-subscriptions-" + Guid.NewGuid().ToString("N") + ".json"));

    [Fact]
    public void BuildPayload_Body_NamesOnlyTheTwoSessionNumbers()
    {
        var payload = JsonDocument.Parse(MessageLinkRequestPushNotifier.BuildPayload("req-1", 121, 125)).RootElement;

        Assert.Equal("Session 121 asks to message session 125. Approve or deny?", payload.GetProperty("body").GetString());
    }

    [Fact]
    public void BuildPayload_ANumberUnknown_NamesNoSession()
    {
        var payload = JsonDocument.Parse(MessageLinkRequestPushNotifier.BuildPayload("req-1", 121, null)).RootElement;

        Assert.Equal("A session asks to message another session. Approve or deny?", payload.GetProperty("body").GetString());
    }

    [Fact]
    public void BuildPayload_HasExactlyTheFieldsTheServiceWorkerReads()
    {
        var payload = JsonDocument.Parse(MessageLinkRequestPushNotifier.BuildPayload("req-1", 121, 125)).RootElement;

        var names = payload.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "alertId", "body", "kind", "tag", "title", "url" }, names);
        Assert.Equal("link-request", payload.GetProperty("kind").GetString());
        // A tap opens the phone on the alert: the Alerts tray, on this request (the Alerts mission).
        Assert.Equal("/mobile/?alert=message-request%3Areq-1", payload.GetProperty("url").GetString());
        Assert.Equal("devthrottle-link-request-req-1", payload.GetProperty("tag").GetString());
        // The Alerts mission: the push names the alert it announces, so a tap opens the tray on it.
        Assert.Equal("message-request:req-1", payload.GetProperty("alertId").GetString());
    }

    [Fact]
    public async Task NotifyRequestedAsync_PushesTheOnePayloadToEverySubscription()
    {
        var store = NewStore();
        store.Add("https://push.example/aaa", "p", "a");
        store.Add("https://push.example/bbb", "p", "a");
        var sender = new FakeSender();
        var notifier = new MessageLinkRequestPushNotifier(store, sender);

        await notifier.NotifyRequestedAsync("req-1", 121, 125, CancellationToken.None);

        Assert.Equal(new[] { "https://push.example/aaa", "https://push.example/bbb" }, sender.Sent.Select(s => s.endpoint).OrderBy(e => e).ToArray());
        Assert.All(sender.Sent, s => Assert.Equal(MessageLinkRequestPushNotifier.BuildPayload("req-1", 121, 125), s.payload));
    }

    [Fact]
    public async Task NotifyRequestedAsync_GoneSubscription_IsPrunedAndTheOthersStillGetIt()
    {
        var store = NewStore();
        store.Add("https://push.example/gone", "p", "a");
        store.Add("https://push.example/live", "p", "a");
        var sender = new FakeSender();
        sender.GoneEndpoints.Add("https://push.example/gone");
        var notifier = new MessageLinkRequestPushNotifier(store, sender);

        await notifier.NotifyRequestedAsync("req-1", 121, 125, CancellationToken.None);

        Assert.Equal(new[] { "https://push.example/live" }, sender.Sent.Select(s => s.endpoint).ToArray());
        Assert.Equal(new[] { "https://push.example/live" }, store.All().Select(s => s.Endpoint).ToArray());
    }

    [Fact]
    public async Task NotifyRequestedAsync_OneSendThrows_DoesNotThrowAndTheOthersStillGetIt()
    {
        var store = NewStore();
        store.Add("https://push.example/broken", "p", "a");
        store.Add("https://push.example/live", "p", "a");
        var sender = new FakeSender();
        sender.BrokenEndpoints.Add("https://push.example/broken");
        var notifier = new MessageLinkRequestPushNotifier(store, sender);

        await notifier.NotifyRequestedAsync("req-1", 121, 125, CancellationToken.None);

        Assert.Equal(new[] { "https://push.example/live" }, sender.Sent.Select(s => s.endpoint).ToArray());
    }

    [Fact]
    public async Task NotifyRequestedAsync_ReachesOnlyTheAskingAccountsPhones()
    {
        var tenants = new AsyncLocalTenantContext();
        var accountA = new TenantId("acct-a");
        var accountB = new TenantId("acct-b");
        PushSubscriptionStore store;
        using (tenants.Enter(accountA))
        {
            store = new PushSubscriptionStore(_h.Open(tenants), _h.LegacyPath("push-" + Guid.NewGuid().ToString("N") + ".json"));
            store.Add("https://push.example/phone-a", "p", "a");
        }
        using (tenants.Enter(accountB))
            store.Add("https://push.example/phone-b", "p", "a");
        var sender = new FakeSender();
        var notifier = new MessageLinkRequestPushNotifier(store, sender);

        using (tenants.Enter(accountA))
            await notifier.NotifyRequestedAsync("req-1", 121, 125, CancellationToken.None);

        Assert.Equal(new[] { "https://push.example/phone-a" }, sender.Sent.Select(s => s.endpoint).ToArray());
    }

    [Fact]
    public void AnswerNotes_SayWhatTheAnswerDid_InTheGatewaysWords()
    {
        Assert.Equal("Approved. 121 Manager may now message 125 Release, and has been told in its inbox.",
            FleetMessageLinkRequestEndpoints.ApprovedNote("121 Manager", "125 Release"));
        Assert.Equal("Denied. 121 Manager has been told in its inbox.", FleetMessageLinkRequestEndpoints.DeniedNote("121 Manager", told: true));
        Assert.Equal("Denied. 121 Manager has ended, so nobody was told.", FleetMessageLinkRequestEndpoints.DeniedNote("121 Manager", told: false));
    }

    [Theory]
    [InlineData(121, "Fix the Release - Manager", "121 Fix the Release - Manager")]
    [InlineData(121, "  ", "Session 121")]
    [InlineData(null, "Fix the Release - Manager", "Fix the Release - Manager")]
    [InlineData(null, null, "A session this Gateway no longer knows")]
    public void SessionLabel_NumberAndName_NeverTheId(int? number, string? name, string expected)
    {
        var session = new SessionDto { SessionId = Requester, Number = number, Name = name };

        var label = FleetMessageLinkRequestEndpoints.SessionLabel(session);

        Assert.Equal(expected, label);
        Assert.DoesNotContain("aaaaaaaa", label);
    }

    [Fact]
    public void SessionLabel_UnknownSession_SaysSoWithoutAnId()
    {
        Assert.Equal("A session this Gateway no longer knows", FleetMessageLinkRequestEndpoints.SessionLabel(null));
    }

    [Fact]
    public async Task AskAsync_ANewRequest_IsAnnouncedOnce_AndAskingAgainDoesNotAnnounceIt()
    {
        var requests = new FleetMessageLinkRequestStore(_h.Open());
        var links = new FleetMessageLinkStore(_h.Open());
        var record = new FleetMessageLinkRecord(new GovernanceAuditLog(_h.Open(new FixedTenantContext(Tenant))), _ => new NoScope());
        var announced = new List<string>();

        async Task<int> Ask()
        {
            var ctx = AskingContext("I need to hand the merge to the release manager");
            var result = await FleetMessageLinkRequestEndpoints.AskAsync(ctx, _ => Tenant, requests, links, record, FindSession,
                (_, request) => announced.Add(request.RequestId), () => T0);
            await result.ExecuteAsync(ctx);
            return ctx.Response.StatusCode;
        }

        Assert.Equal(StatusCodes.Status201Created, await Ask());
        Assert.Equal(StatusCodes.Status200OK, await Ask());

        var waiting = Assert.Single(requests.List(Tenant, T0));
        Assert.Equal(new[] { waiting.RequestId }, announced);
    }

    private static SessionDto? FindSession(TenantId _, string id) => id switch
    {
        Requester => new SessionDto { SessionId = Requester, Number = 121, Name = "Requester" },
        Target => new SessionDto { SessionId = Target, Number = 125, Name = "Target" },
        _ => null,
    };

    private static DefaultHttpContext AskingContext(string reason)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Guid.Parse(Requester), Tenant, "dir-1");
        var body = JsonSerializer.Serialize(new { targetSessionId = Target, reason, amount = "once-with-reply" });
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentType = "application/json";
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private sealed class NoScope : IDisposable { public void Dispose() { } }

    private sealed class FakeSender : IWebPushSender
    {
        public List<(string endpoint, string payload)> Sent { get; } = new();
        public HashSet<string> GoneEndpoints { get; } = new();
        public HashSet<string> BrokenEndpoints { get; } = new();

        public Task SendAsync(StoredPushSubscription subscription, string payloadJson, CancellationToken cancellationToken)
        {
            if (GoneEndpoints.Contains(subscription.Endpoint))
                throw new PushServiceClientException("gone", HttpStatusCode.Gone);
            if (BrokenEndpoints.Contains(subscription.Endpoint))
                throw new HttpRequestException("network down");
            Sent.Add((subscription.Endpoint, payloadJson));
            return Task.CompletedTask;
        }
    }
}
