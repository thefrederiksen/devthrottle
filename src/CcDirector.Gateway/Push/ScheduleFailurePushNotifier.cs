using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Lib.Net.Http.WebPush;

namespace CcDirector.Gateway.Push;

/// <summary>
/// THE SCHEDULE FAILURE PUSH (issue #4436). When a schedule's owner is told that it failed
/// (<see cref="Running.ScheduleFailureNotifier"/>), every phone the account subscribed gets one notification, and tapping
/// it opens the Schedule page. It rides the same Web Push transport (<see cref="IWebPushSender"/>) and the same
/// subscription store as the "needs you" dot and the license alarm push.
///
/// WHAT THE PUSH CARRIES: the schedule's name and the plain sentence (<see cref="Running.ScheduleFailureNotifier.PushWords"/>),
/// never the run's raw reason - a push shows on a locked screen, and the detail is on the Schedule page.
/// </summary>
public sealed class ScheduleFailurePushNotifier
{
    /// <summary>The page a tap opens: the Schedule page of the Cockpit.</summary>
    public const string ScheduleUrl = "/schedule";

    /// <summary>The <c>kind</c> the service worker reads to tell this push from the "needs you" dot.</summary>
    public const string Kind = "schedule-failed";

    private static readonly JsonSerializerOptions PayloadJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly PushSubscriptionStore _store;
    private readonly IWebPushSender _sender;
    private readonly Func<TenantId, IDisposable> _enterScope;

    /// <param name="enterScope">Enters one tenant's scope, so the subscriptions read are that tenant's phones only.</param>
    public ScheduleFailurePushNotifier(PushSubscriptionStore store, IWebPushSender sender, Func<TenantId, IDisposable> enterScope)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _enterScope = enterScope ?? throw new ArgumentNullException(nameof(enterScope));
    }

    /// <summary>The whole push message for one failure notice, as the service worker reads it.</summary>
    public static string BuildPayload(CronRunCompletedPayload notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var (title, body) = Running.ScheduleFailureNotifier.PushWords(notice);
        return JsonSerializer.Serialize(new SchedulePayload
        {
            Kind = Kind,
            Title = title,
            Body = body,
            // One notification per schedule and cause: a newer notice of the same failure replaces the older one.
            Tag = $"devthrottle-schedule-failed-{notice.JobId}-{notice.Cause}",
            Url = ScheduleUrl,
        }, PayloadJson);
    }

    /// <summary>Push <paramref name="notice"/> to every phone of <paramref name="tenant"/>. Never throws: a push that fails
    /// must not stop the other channels, so every failure is logged here.</summary>
    public async Task NotifyAsync(TenantId tenant, CronRunCompletedPayload notice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notice);
        IReadOnlyList<StoredPushSubscription> subscriptions;
        try
        {
            using (_enterScope(tenant))
                subscriptions = _store.All();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ScheduleFailurePushNotifier] NotifyAsync FAILED before sending: {ex.Message}");
            return;
        }
        if (subscriptions.Count == 0)
        {
            FileLog.Write($"[ScheduleFailurePushNotifier] NotifyAsync: no phone is subscribed, tenant={tenant.ToLogString()}");
            return;
        }
        var payload = BuildPayload(notice);
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
                using (_enterScope(tenant))
                    _store.Remove(subscription.Endpoint);
                FileLog.Write($"[ScheduleFailurePushNotifier] pruned an expired subscription (status {(int)ex.StatusCode})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One phone failing must not stop the others.
                FileLog.Write($"[ScheduleFailurePushNotifier] push send failed: {ex.Message}");
            }
        }
        FileLog.Write($"[ScheduleFailurePushNotifier] job={notice.JobId}, cause={notice.Cause}: pushed to {sent} of {subscriptions.Count} subscription(s)");
    }

    private sealed class SchedulePayload
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("tag")] public string Tag { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
    }
}
