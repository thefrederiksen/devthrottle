using System.Net;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Push;
using CcDirector.Gateway.Tests.Data;
using Lib.Net.Http.WebPush;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Secret Handoff push (issue #2943, phase 5): a transfer waiting for the owner reaches every subscribed phone, and
/// the notification carries the entry name and the two machine names - never a value, never the reason, never who asked.
/// </summary>
public sealed class SecretTransferPushNotifierTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private PushSubscriptionStore NewStore()
        => new(_h.Open(), _h.LegacyPath("push-subscriptions-" + Guid.NewGuid().ToString("N") + ".json"));

    // Every free-text field is filled with a marker, so a test can prove none of it reaches the push.
    private static SecretTransferDto Waiting() => new()
    {
        TransferId = "tr-0001",
        Entry = "vercel-bypass",
        TargetName = "renamed-target-marker",
        FromMachine = "SOREN_NORTH",
        ToMachine = "devthrottle-mac-mini",
        AskedBySessionId = "11111111-2222-3333-4444-555555555555",
        AskedBy = "Session 042 \"asked-by-marker\"",
        Reason = "reason-marker sk-live-VALUE-MARKER",
        State = "waiting",
        ApprovalWords = "approval-words-marker",
        Summary = "summary-marker",
        StatusText = "status-marker",
        ReplaceNote = "replace-note-marker",
        CanAnswer = true,
    };

    [Fact]
    public void BuildPayload_Body_NamesOnlyTheEntryAndTheTwoMachines()
    {
        var payload = JsonDocument.Parse(SecretTransferPushNotifier.BuildPayload(Waiting())).RootElement;

        Assert.Equal("Approve sending vercel-bypass from SOREN_NORTH to devthrottle-mac-mini?", payload.GetProperty("body").GetString());
    }

    [Fact]
    public void BuildPayload_CarriesNoFreeTextFieldAndNoValue()
    {
        var json = SecretTransferPushNotifier.BuildPayload(Waiting());

        foreach (var marker in new[] { "reason-marker", "VALUE-MARKER", "asked-by-marker", "approval-words-marker",
                     "renamed-target-marker", "summary-marker", "status-marker", "replace-note-marker", "11111111-2222" })
            Assert.DoesNotContain(marker, json);
    }

    [Fact]
    public void BuildPayload_HasExactlyTheFieldsTheServiceWorkerReads()
    {
        var payload = JsonDocument.Parse(SecretTransferPushNotifier.BuildPayload(Waiting())).RootElement;

        var names = payload.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "body", "kind", "tag", "title", "url" }, names);
        Assert.Equal("secret-transfer", payload.GetProperty("kind").GetString());
        Assert.Equal("/mobile/secret-transfers", payload.GetProperty("url").GetString());
        Assert.Equal("devthrottle-secret-transfer-tr-0001", payload.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task NotifyWaitingAsync_PushesTheOnePayloadToEverySubscription()
    {
        var store = NewStore();
        store.Add("https://push.example/aaa", "p", "a");
        store.Add("https://push.example/bbb", "p", "a");
        var sender = new FakeSender();
        var notifier = new SecretTransferPushNotifier(store, sender);

        await notifier.NotifyWaitingAsync(Waiting(), CancellationToken.None);

        Assert.Equal(new[] { "https://push.example/aaa", "https://push.example/bbb" }, sender.Sent.Select(s => s.endpoint).OrderBy(e => e).ToArray());
        Assert.All(sender.Sent, s => Assert.Equal(SecretTransferPushNotifier.BuildPayload(Waiting()), s.payload));
    }

    [Fact]
    public async Task NotifyWaitingAsync_NoSubscription_SendsNothing()
    {
        var sender = new FakeSender();
        var notifier = new SecretTransferPushNotifier(NewStore(), sender);

        await notifier.NotifyWaitingAsync(Waiting(), CancellationToken.None);

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task NotifyWaitingAsync_GoneSubscription_IsPrunedAndTheOthersStillGetIt()
    {
        var store = NewStore();
        store.Add("https://push.example/gone", "p", "a");
        store.Add("https://push.example/live", "p", "a");
        var sender = new FakeSender();
        sender.GoneEndpoints.Add("https://push.example/gone");
        var notifier = new SecretTransferPushNotifier(store, sender);

        await notifier.NotifyWaitingAsync(Waiting(), CancellationToken.None);

        Assert.Equal(new[] { "https://push.example/live" }, sender.Sent.Select(s => s.endpoint).ToArray());
        Assert.Equal(new[] { "https://push.example/live" }, store.All().Select(s => s.Endpoint).ToArray());
    }

    [Fact]
    public async Task NotifyWaitingAsync_OneSendThrows_DoesNotThrowAndTheOthersStillGetIt()
    {
        var store = NewStore();
        store.Add("https://push.example/broken", "p", "a");
        store.Add("https://push.example/live", "p", "a");
        var sender = new FakeSender();
        sender.BrokenEndpoints.Add("https://push.example/broken");
        var notifier = new SecretTransferPushNotifier(store, sender);

        await notifier.NotifyWaitingAsync(Waiting(), CancellationToken.None);

        Assert.Equal(new[] { "https://push.example/live" }, sender.Sent.Select(s => s.endpoint).ToArray());
        Assert.Equal(2, store.All().Count);
    }

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
