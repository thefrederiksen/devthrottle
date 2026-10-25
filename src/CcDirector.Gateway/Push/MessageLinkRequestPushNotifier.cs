using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Lib.Net.Http.WebPush;

namespace CcDirector.Gateway.Push;

/// <summary>
/// THE MESSAGE LINK REQUEST PUSH (issue #4373). When a session asks the owner to let it message a session it may not
/// reach on its own (<c>cc-devthrottle message request</c>), every phone the account subscribed gets one notification,
/// and tapping it opens the card where the owner approves or denies. It rides the same Web Push transport
/// (<see cref="IWebPushSender"/>) and the same subscription store as the secret handoff push, so a phone that gets one
/// gets the other.
///
/// WHAT THE PUSH MAY CARRY: the two sessions' three-digit numbers, which the Directors allocate, and nothing a person or
/// an agent wrote. Never the reason and never a session's name: a push shows on a locked screen and is held by the
/// browser vendor's push service, and both are free text an agent typed. The card, read over the account's own
/// authenticated connection, shows the rest. <see cref="BuildPayload"/> is the one place that decides the words, and its
/// test pins them.
/// </summary>
public sealed class MessageLinkRequestPushNotifier : IDisposable
{
    /// <summary>The path the phone app opens when the notification is tapped: the waiting requests.</summary>
    public const string CardUrl = "/mobile/link-requests";

    /// <summary>The <c>kind</c> the service worker reads to tell this push from the "needs you" dot.</summary>
    public const string Kind = "link-request";

    /// <summary>The prefix of each notification's tag. The phone closes a notification by this tag once the request has
    /// been answered anywhere, so the prefix is shared with the app (<c>linkRequestNotificationTag</c>).</summary>
    public const string TagPrefix = "devthrottle-link-request-";

    private static readonly JsonSerializerOptions PayloadJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly PushSubscriptionStore _store;
    private readonly IWebPushSender _sender;

    public MessageLinkRequestPushNotifier(PushSubscriptionStore store, IWebPushSender sender)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    /// <summary>The sentence on the notification: the two session numbers only, or no session at all when a number is
    /// not known.</summary>
    public static string Body(int? requesterNumber, int? targetNumber)
        => requesterNumber is { } from && targetNumber is { } to
            ? $"Session {from} asks to message session {to}. Approve or deny?"
            : "A session asks to message another session. Approve or deny?";

    /// <summary>The whole push message for one new request, as the service worker reads it.</summary>
    public static string BuildPayload(string requestId, int? requesterNumber, int? targetNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return JsonSerializer.Serialize(new MessageLinkRequestPayload
        {
            Kind = Kind,
            Title = "A session asks to message another",
            Body = Body(requesterNumber, targetNumber),
            // One notification per request: two waiting at once must not replace each other. The id is not secret.
            Tag = TagPrefix + requestId,
            Url = CardUrl,
            // The alert this push announces (the Alerts mission), so a tap can open the Alerts tray on it.
            AlertId = AlertKinds.IdOf(AlertKinds.MessageRequest, requestId),
        }, PayloadJson);
    }

    /// <summary>
    /// Push one new request to every phone of the CURRENT account. The subscriptions are read before the first await, so
    /// a caller inside a request reads that request's account even when it does not wait for the sends. Never throws: a
    /// push that fails must not fail the request it announces, so every failure is logged here.
    /// </summary>
    public Task NotifyRequestedAsync(string requestId, int? requesterNumber, int? targetNumber, CancellationToken cancellationToken)
    {
        FileLog.Write($"[MessageLinkRequestPushNotifier] NotifyRequestedAsync: request={requestId}, from={requesterNumber}, to={targetNumber}");
        IReadOnlyList<StoredPushSubscription> subscriptions;
        string payload;
        try
        {
            subscriptions = _store.All();
            payload = BuildPayload(requestId, requesterNumber, targetNumber);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MessageLinkRequestPushNotifier] NotifyRequestedAsync FAILED before sending: {ex.Message}");
            return Task.CompletedTask;
        }
        if (subscriptions.Count == 0)
        {
            FileLog.Write($"[MessageLinkRequestPushNotifier] NotifyRequestedAsync: no phone is subscribed, request={requestId}");
            return Task.CompletedTask;
        }
        return SendAllAsync(requestId, subscriptions, payload, cancellationToken);
    }

    private async Task SendAllAsync(string requestId, IReadOnlyList<StoredPushSubscription> subscriptions, string payload,
        CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var subscription in subscriptions)
        {
            try
            {
                await _sender.SendAsync(subscription, payload, cancellationToken);
                sent++;
            }
            catch (PushServiceClientException ex) when (
                ex.StatusCode == HttpStatusCode.Gone || ex.StatusCode == HttpStatusCode.NotFound)
            {
                // The browser dropped this subscription; stop sending to it, as the other notifiers do.
                _store.Remove(subscription.Endpoint);
                FileLog.Write($"[MessageLinkRequestPushNotifier] pruned an expired subscription (status {(int)ex.StatusCode})");
            }
            catch (Exception ex)
            {
                // One phone failing must not stop the others.
                FileLog.Write($"[MessageLinkRequestPushNotifier] push send failed: {ex.Message}");
            }
        }
        FileLog.Write($"[MessageLinkRequestPushNotifier] request={requestId}: pushed to {sent} of {subscriptions.Count} subscription(s)");
    }

    public void Dispose() => (_sender as IDisposable)?.Dispose();

    private sealed class MessageLinkRequestPayload
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("tag")] public string Tag { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("alertId")] public string AlertId { get; set; } = "";
    }
}
