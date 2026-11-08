using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Inbox;
using Lib.Net.Http.WebPush;

namespace CcDirector.Gateway.Push;

/// <summary>
/// THE PUSH OF A SYSTEM ALERT WRITTEN INTO YOUR INBOX (the owner's ruling on the three alerts that still emailed him:
/// "Move them all to Your inbox, with a push to the phone"). When the Gateway writes the network alert, a license alert
/// or the session supervisor's escalation into the owner's Your inbox, every phone the account subscribed gets one
/// notification, and tapping it opens that message in Your inbox on the phone. It rides the same Web Push transport
/// (<see cref="IWebPushSender"/>) and the same subscription store as the "needs you" dot and the schedule failure push.
///
/// WHAT THE PUSH CARRIES (<see cref="BuildPayload"/>, the one place the words are decided): who the alert is from, as
/// Your inbox names it ("Network", "Licenses", "Session supervisor"), and the message's subject - never its body. A
/// push shows on a locked screen and is held by the browser vendor's push service; the body is read in Your inbox,
/// over the account's own signed-in connection.
/// </summary>
public sealed class YourInboxAlertPushNotifier
{
    /// <summary>The <c>kind</c> the service worker reads to tell this push from the "needs you" dot.</summary>
    public const string Kind = "inbox-alert";

    /// <summary>The page a tap opens when the push names no one message: Your inbox on the phone.</summary>
    public const string YourInboxUrl = "/mobile/inbox?tab=yours";

    /// <summary>The longest push body, in characters: a subject is at most <see cref="YourInboxStore.MaxSubjectLength"/>,
    /// and a locked screen shows far less.</summary>
    public const int MaxBodyLength = 160;

    private static readonly JsonSerializerOptions PayloadJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly PushSubscriptionStore _store;
    private readonly IWebPushSender _sender;
    private readonly Func<TenantId, IDisposable> _enterScope;

    /// <param name="enterScope">Enters one tenant's scope, so the subscriptions read are that tenant's phones only.</param>
    public YourInboxAlertPushNotifier(PushSubscriptionStore store, IWebPushSender sender, Func<TenantId, IDisposable> enterScope)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _enterScope = enterScope ?? throw new ArgumentNullException(nameof(enterScope));
    }

    /// <summary>The page a tap opens: the one message when it is known, Your inbox otherwise.</summary>
    public static string UrlOf(Guid? messageId)
        => messageId is { } id ? $"{YourInboxUrl}&message={id:D}" : YourInboxUrl;

    /// <summary>The whole push message for one alert, as the service worker reads it.</summary>
    /// <param name="seat">The Gateway seat the alert was written from (<see cref="YourInboxWords"/>).</param>
    /// <param name="subject">The Your inbox message's subject.</param>
    /// <param name="messageId">The Your inbox message, when the caller has it.</param>
    public static string BuildPayload(string seat, string subject, Guid? messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        var body = subject.Trim();
        if (body.Length > MaxBodyLength) body = body[..(MaxBodyLength - 3)].TrimEnd() + "...";
        return JsonSerializer.Serialize(new AlertPayload
        {
            Kind = Kind,
            Title = YourInboxWords.GatewayFromLabel(seat),
            Body = body,
            // One notification per message: two alerts must not replace each other. The id is not secret.
            Tag = messageId is { } id ? $"devthrottle-inbox-alert-{id:N}" : $"devthrottle-inbox-alert-{seat.Replace(' ', '-')}",
            Url = UrlOf(messageId),
        }, PayloadJson);
    }

    /// <summary>Push one alert to every phone of <paramref name="tenant"/>. Never throws: a push that fails must not undo
    /// the Your inbox message it announces, so every failure is logged here.</summary>
    public async Task NotifyAsync(TenantId tenant, string seat, string subject, Guid? messageId, CancellationToken ct)
    {
        FileLog.Write($"[YourInboxAlertPushNotifier] NotifyAsync: tenant={tenant.ToLogString()}, seat={seat}, message={messageId?.ToString("D") ?? "(none)"}");
        string payload;
        IReadOnlyList<StoredPushSubscription> subscriptions;
        try
        {
            payload = BuildPayload(seat, subject, messageId);
            using (_enterScope(tenant))
                subscriptions = _store.All();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[YourInboxAlertPushNotifier] NotifyAsync FAILED before sending: {ex.Message}");
            return;
        }
        if (subscriptions.Count == 0)
        {
            FileLog.Write($"[YourInboxAlertPushNotifier] NotifyAsync: no phone is subscribed, tenant={tenant.ToLogString()}");
            return;
        }
        var sent = 0;
        foreach (var subscription in subscriptions)
        {
            try
            {
                await _sender.SendAsync(subscription, payload, ct).ConfigureAwait(false);
                sent++;
            }
            catch (PushServiceClientException ex) when (
                ex.StatusCode == HttpStatusCode.Gone || ex.StatusCode == HttpStatusCode.NotFound)
            {
                // The browser dropped this subscription; stop sending to it, as the needs-you notifier does.
                try
                {
                    using (_enterScope(tenant))
                        _store.Remove(subscription.Endpoint);
                    FileLog.Write($"[YourInboxAlertPushNotifier] pruned an expired subscription (status {(int)ex.StatusCode})");
                }
                catch (Exception pruneEx)
                {
                    FileLog.Write($"[YourInboxAlertPushNotifier] pruning an expired subscription FAILED: {pruneEx.Message}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One phone failing must not stop the others.
                FileLog.Write($"[YourInboxAlertPushNotifier] push send failed: {ex.Message}");
            }
        }
        FileLog.Write($"[YourInboxAlertPushNotifier] seat={seat}: pushed to {sent} of {subscriptions.Count} subscription(s)");
    }

    private sealed class AlertPayload
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("tag")] public string Tag { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
    }
}
