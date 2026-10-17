using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Licenses;
using Lib.Net.Http.WebPush;

namespace CcDirector.Gateway.Push;

/// <summary>
/// THE LICENSE ALARM PUSH (License Switching phase 6). When a license alarm opens, every phone the account subscribed
/// gets one notification, and tapping it opens the Licenses page. It rides the same Web Push transport
/// (<see cref="IWebPushSender"/>) and the same subscription store as the "needs you" dot and the secret handoff push.
///
/// WHAT THE PUSH MAY CARRY: the machine's name and the kind of problem (<see cref="LicenseAlarmFold.PushBody"/>), never
/// an account email address. A push shows on a locked screen and is held by the browser vendor's push service; the
/// Licenses page, read over the account's own signed-in connection, shows the account. <see cref="BuildPayload"/> is the
/// one place that decides the words, and its test pins them.
///
/// THE TAP OPENS THE COCKPIT'S LICENSES PAGE (<see cref="LicensesUrl"/>): the phone app has no Licenses screen of its
/// own, and the Cockpit's page is served by the same Gateway and works at phone width.
/// </summary>
public sealed class LicenseAlarmPushNotifier
{
    /// <summary>The page a tap opens: the Licenses page of the Cockpit.</summary>
    public const string LicensesUrl = "/licenses";

    /// <summary>The <c>kind</c> the service worker reads to tell this push from the "needs you" dot.</summary>
    public const string Kind = "license-alarm";

    private static readonly JsonSerializerOptions PayloadJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly PushSubscriptionStore _store;
    private readonly IWebPushSender _sender;
    private readonly Func<TenantId, IDisposable> _enterScope;

    /// <param name="enterScope">Enters one tenant's scope, so the subscriptions read are that tenant's phones only.</param>
    public LicenseAlarmPushNotifier(PushSubscriptionStore store, IWebPushSender sender, Func<TenantId, IDisposable> enterScope)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _enterScope = enterScope ?? throw new ArgumentNullException(nameof(enterScope));
    }

    /// <summary>The whole push message for one alarm that opened, as the service worker reads it.</summary>
    public static string BuildPayload(LicenseAlarmOpened alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        return JsonSerializer.Serialize(new LicenseAlarmPayload
        {
            Kind = Kind,
            Title = LicenseAlarmFold.PushTitle,
            Body = LicenseAlarmFold.PushBody(alarm.MachineName, alarm.Kind),
            // One notification per alarm: two opening at once must not replace each other. The id is not secret.
            Tag = "devthrottle-license-alarm-" + alarm.AlarmId.ToString("N"),
            Url = LicensesUrl,
        }, PayloadJson);
    }

    /// <summary>Push every alarm in <paramref name="opened"/> to every phone of <paramref name="tenant"/>. Never throws:
    /// a push that fails must not fail the alarm it announces, so every failure is logged here.</summary>
    public async Task NotifyOpenedAsync(TenantId tenant, IReadOnlyList<LicenseAlarmOpened> opened, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opened);
        FileLog.Write($"[LicenseAlarmPushNotifier] NotifyOpenedAsync: tenant={tenant.ToLogString()}, alarms={opened.Count}");
        IReadOnlyList<StoredPushSubscription> subscriptions;
        try
        {
            using (_enterScope(tenant))
                subscriptions = _store.All();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseAlarmPushNotifier] NotifyOpenedAsync FAILED before sending: {ex.Message}");
            return;
        }
        if (subscriptions.Count == 0)
        {
            FileLog.Write($"[LicenseAlarmPushNotifier] NotifyOpenedAsync: no phone is subscribed, tenant={tenant.ToLogString()}");
            return;
        }
        foreach (var alarm in opened)
        {
            var payload = BuildPayload(alarm);
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
                    FileLog.Write($"[LicenseAlarmPushNotifier] pruned an expired subscription (status {(int)ex.StatusCode})");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One phone failing must not stop the others.
                    FileLog.Write($"[LicenseAlarmPushNotifier] push send failed: {ex.Message}");
                }
            }
            FileLog.Write($"[LicenseAlarmPushNotifier] alarm={alarm.AlarmId:N}: pushed to {sent} of {subscriptions.Count} subscription(s)");
        }
    }

    private sealed class LicenseAlarmPayload
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("tag")] public string Tag { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
    }
}
