using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Lib.Net.Http.WebPush;

namespace CcDirector.Gateway.Push;

/// <summary>
/// THE SECRET HANDOFF PUSH (issue #2943, phase 5). When a secret transfer starts waiting for the owner's answer, every
/// phone the account subscribed gets one notification that names the entry and the two machines, and tapping it opens
/// the approval card. It rides the same Web Push transport (<see cref="IWebPushSender"/>) and the same subscription
/// store as the "needs you" dot, so a phone that gets the dot gets this too.
///
/// WHAT THE PUSH MAY CARRY: the entry's name and the two machine names, and nothing else a person or an agent wrote.
/// Never a value (the Gateway never has one), and also never the reason or the asking session's name: a push
/// notification shows on a locked screen and is held by the browser vendor's push service, and the reason is free
/// text an agent typed. The card, read over the account's own authenticated connection, shows the rest.
/// <see cref="BuildPayload"/> is the one place that decides the words, and its test pins them.
/// </summary>
public sealed class SecretTransferPushNotifier : IDisposable
{
    /// <summary>The path the phone app opens when the notification is tapped: the waiting approvals.</summary>
    public const string CardUrl = "/mobile/secret-transfers";

    /// <summary>The <c>kind</c> the service worker reads to tell this push from the "needs you" dot.</summary>
    public const string Kind = "secret-transfer";

    private static readonly JsonSerializerOptions PayloadJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly PushSubscriptionStore _store;
    private readonly IWebPushSender _sender;

    public SecretTransferPushNotifier(PushSubscriptionStore store, IWebPushSender sender)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    /// <summary>The sentence on the notification: the entry and the two machines only.</summary>
    public static string Body(string entry, string fromMachine, string toMachine)
        => $"Approve sending {entry} from {fromMachine} to {toMachine}?";

    /// <summary>The whole push message for one waiting transfer, as the service worker reads it.</summary>
    public static string BuildPayload(SecretTransferDto transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        return JsonSerializer.Serialize(new SecretTransferPayload
        {
            Kind = Kind,
            Title = "Secret transfer waiting",
            Body = Body(transfer.Entry, transfer.FromMachine, transfer.ToMachine),
            // One notification per transfer: two waiting at once must not replace each other. The id is not secret.
            Tag = "devthrottle-secret-transfer-" + transfer.TransferId,
            Url = CardUrl,
        }, PayloadJson);
    }

    /// <summary>
    /// Push one waiting transfer to every phone of the CURRENT account. The subscriptions are read before the first
    /// await, so a caller inside a request reads that request's account even when it does not wait for the sends.
    /// Never throws: a push that fails must not fail the transfer it announces, so every failure is logged here.
    /// </summary>
    public Task NotifyWaitingAsync(SecretTransferDto transfer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        FileLog.Write($"[SecretTransferPushNotifier] NotifyWaitingAsync: transfer={transfer.TransferId}");
        IReadOnlyList<StoredPushSubscription> subscriptions;
        string payload;
        try
        {
            subscriptions = _store.All();
            payload = BuildPayload(transfer);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SecretTransferPushNotifier] NotifyWaitingAsync FAILED before sending: {ex.Message}");
            return Task.CompletedTask;
        }
        if (subscriptions.Count == 0)
        {
            FileLog.Write($"[SecretTransferPushNotifier] NotifyWaitingAsync: no phone is subscribed, transfer={transfer.TransferId}");
            return Task.CompletedTask;
        }
        return SendAllAsync(transfer.TransferId, subscriptions, payload, cancellationToken);
    }

    private async Task SendAllAsync(string transferId, IReadOnlyList<StoredPushSubscription> subscriptions, string payload,
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
                // The browser dropped this subscription; stop sending to it, as the needs-you notifier does.
                _store.Remove(subscription.Endpoint);
                FileLog.Write($"[SecretTransferPushNotifier] pruned an expired subscription (status {(int)ex.StatusCode})");
            }
            catch (Exception ex)
            {
                // One phone failing must not stop the others.
                FileLog.Write($"[SecretTransferPushNotifier] push send failed: {ex.Message}");
            }
        }
        FileLog.Write($"[SecretTransferPushNotifier] transfer={transferId}: pushed to {sent} of {subscriptions.Count} subscription(s)");
    }

    public void Dispose() => (_sender as IDisposable)?.Dispose();

    private sealed class SecretTransferPayload
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("tag")] public string Tag { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
    }
}
