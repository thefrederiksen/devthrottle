using System.Net;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Push;
using CcDirector.Gateway.Tests.Data;
using Lib.Net.Http.WebPush;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The push of a system alert written into Your inbox (the owner's ruling: "Move them all to Your inbox, with a push to
/// the phone"): the words are decided once on the Gateway - who it is from and the subject, never the body - a tap opens
/// that message in Your inbox on the phone, and it reaches every subscribed phone. The service worker shows it because
/// it knows the kind (<c>apps/mobile/src/push/pushSw.test.ts</c>).
/// </summary>
public sealed class YourInboxAlertPushNotifierTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly Guid MessageId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void BuildPayload_HasExactlyTheFieldsTheServiceWorkerReads_AndOpensTheMessage()
    {
        var payload = JsonDocument.Parse(YourInboxAlertPushNotifier.BuildPayload(
            YourInboxWords.NetworkSeat, "Your home network is slow (Phone)", MessageId)).RootElement;

        var names = payload.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "body", "kind", "tag", "title", "url" }, names);
        Assert.Equal("inbox-alert", payload.GetProperty("kind").GetString());
        Assert.Equal("Network", payload.GetProperty("title").GetString());
        Assert.Equal("Your home network is slow (Phone)", payload.GetProperty("body").GetString());
        Assert.Equal("/mobile/inbox?tab=yours&message=11111111-2222-3333-4444-555555555555", payload.GetProperty("url").GetString());
        Assert.Equal("devthrottle-inbox-alert-11111111222233334444555555555555", payload.GetProperty("tag").GetString());
    }

    [Theory]
    [InlineData(YourInboxWords.LicensesSeat, "Licenses")]
    [InlineData(YourInboxWords.SessionSupervisorSeat, "Session supervisor")]
    public void BuildPayload_NoMessageId_OpensYourInbox(string seat, string title)
    {
        var payload = JsonDocument.Parse(YourInboxAlertPushNotifier.BuildPayload(seat, "Subject", null)).RootElement;

        Assert.Equal(title, payload.GetProperty("title").GetString());
        Assert.Equal("/mobile/inbox?tab=yours", payload.GetProperty("url").GetString());
    }

    [Fact]
    public void BuildPayload_ALongSubject_IsCut()
    {
        var payload = JsonDocument.Parse(YourInboxAlertPushNotifier.BuildPayload(
            YourInboxWords.LicensesSeat, new string('x', 200), MessageId)).RootElement;

        Assert.Equal(YourInboxAlertPushNotifier.MaxBodyLength, payload.GetProperty("body").GetString()!.Length);
    }

    [Fact]
    public async Task NotifyAsync_PushesToEveryPhone_AndPrunesAGoneOne()
    {
        var store = new PushSubscriptionStore(_h.Open(), _h.LegacyPath("push-subscriptions-" + Guid.NewGuid().ToString("N") + ".json"));
        store.Add("https://push.example/live", "p", "a");
        store.Add("https://push.example/gone", "p", "a");
        var sender = new FakeSender();
        sender.GoneEndpoints.Add("https://push.example/gone");
        var notifier = new YourInboxAlertPushNotifier(store, sender, _ => new NoScope());

        await notifier.NotifyAsync(Tenant, YourInboxWords.NetworkSeat, "Your home network is slow (Phone)", MessageId, CancellationToken.None);

        var (endpoint, json) = Assert.Single(sender.Sent);
        Assert.Equal("https://push.example/live", endpoint);
        Assert.Equal("inbox-alert", JsonDocument.Parse(json).RootElement.GetProperty("kind").GetString());
        Assert.Equal(new[] { "https://push.example/live" }, store.All().Select(s => s.Endpoint).ToArray());
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class FakeSender : IWebPushSender
    {
        public List<(string endpoint, string payload)> Sent { get; } = new();
        public HashSet<string> GoneEndpoints { get; } = new();

        public Task SendAsync(StoredPushSubscription subscription, string payloadJson, CancellationToken cancellationToken)
        {
            if (GoneEndpoints.Contains(subscription.Endpoint))
                throw new PushServiceClientException("gone", HttpStatusCode.Gone);
            Sent.Add((subscription.Endpoint, payloadJson));
            return Task.CompletedTask;
        }
    }
}
