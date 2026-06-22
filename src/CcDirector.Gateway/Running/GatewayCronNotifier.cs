using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Events;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Production <see cref="ICronNotifier"/> (issue #622). Delivers a cron run-complete notification
/// over the EXISTING fleet notification channel - the per-Director doorbell event ring (#330),
/// observable at <c>GET /directors/{id}/events</c> and consumed by the same desktop/phone surfaces
/// that already see needs-you / session-created / session-exited - rather than inventing a new
/// mechanism. When the job carries a webhook URL it ALSO POSTs the same
/// <see cref="CronRunCompletedPayload"/> there for external consumers.
///
/// The deep link to the resulting session is built the same way the Gateway's /sessions aggregation
/// builds <c>ViewUrl</c>: <c>{directorEndpoint}/sessions/{sessionId}/view?gw={gatewayBaseUrl}</c>.
/// The resolved Director endpoint is supplied by <see cref="_resolveDirectorEndpoint"/> so this
/// class stays decoupled from the registry and is unit-testable.
///
/// Delivery is best-effort by design: both legs swallow their own failures (logged, never thrown)
/// so a notification problem can never break a fire - the firing engine's outcome is already
/// recorded in run history before this runs.
/// </summary>
public sealed class GatewayCronNotifier : ICronNotifier
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly DirectorEventLog _events;
    private readonly Func<string, string?> _resolveDirectorEndpoint;
    private readonly string _gatewayBaseUrl;
    private readonly HttpClient _webhookHttp;

    /// <param name="events">The per-Director event ring this notification rides (the existing channel).</param>
    /// <param name="resolveDirectorEndpoint">
    /// Resolves a directorId to its reachable base endpoint (tailnet/control), or null when unknown.
    /// Used to build the session deep link; the in-fleet event is still recorded when it returns null.
    /// </param>
    /// <param name="gatewayBaseUrl">The Gateway's own base URL, stamped on the deep link as the <c>gw</c> query.</param>
    /// <param name="webhookHttp">
    /// HttpClient for the optional outbound webhook POST. A dedicated short-timeout client is
    /// supplied by the host; tests inject a fake handler to capture the POST.
    /// </param>
    public GatewayCronNotifier(
        DirectorEventLog events,
        Func<string, string?> resolveDirectorEndpoint,
        string gatewayBaseUrl,
        HttpClient webhookHttp)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _resolveDirectorEndpoint = resolveDirectorEndpoint ?? throw new ArgumentNullException(nameof(resolveDirectorEndpoint));
        _gatewayBaseUrl = (gatewayBaseUrl ?? "").TrimEnd('/');
        _webhookHttp = webhookHttp ?? throw new ArgumentNullException(nameof(webhookHttp));
    }

    /// <summary>
    /// Build a session deep link the same way the /sessions aggregation does, or empty when there
    /// is no session or no reachable Director endpoint to root it on.
    /// </summary>
    public string BuildSessionLink(string? directorId, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return "";
        var endpoint = string.IsNullOrEmpty(directorId) ? null : _resolveDirectorEndpoint(directorId);
        if (string.IsNullOrEmpty(endpoint)) return "";
        var baseUrl = endpoint.TrimEnd('/');
        var gw = string.IsNullOrEmpty(_gatewayBaseUrl) ? "" : $"?gw={Uri.EscapeDataString(_gatewayBaseUrl)}";
        return $"{baseUrl}/sessions/{sessionId}/view{gw}";
    }

    public async Task NotifyRunCompletedAsync(CronJobDto job, string directorId, CronRunCompletedPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(payload);

        FileLog.Write($"[GatewayCronNotifier] NotifyRunCompleted: job={job.Id}, succeeded={payload.Succeeded}, infra={payload.InfraStatus}, sid={payload.SessionId}, webhook={(string.IsNullOrWhiteSpace(job.NotifyWebhookUrl) ? "none" : "set")}");

        // Leg 1: ride the existing per-Director event ring (the fleet notification channel). When the
        // fire resolved a Director the event files under that Director, alongside its needs-you /
        // session-exited events; when it did not (a not-started failure, the worst case the feature
        // exists to surface, AC2) it files under the job id so the failure is still observable. The
        // event's state carries the infra-status so a reader sees the outcome at a glance.
        var ringKey = string.IsNullOrEmpty(directorId) ? job.Id : directorId;
        _events.Record(ringKey, payload.SessionId ?? "", DoorbellEvents.CronRunCompleted, payload.InfraStatus);

        // Leg 2: optional outbound webhook with the SAME payload (AC5).
        var webhookUrl = job.NotifyWebhookUrl;
        if (!string.IsNullOrWhiteSpace(webhookUrl))
            await PostWebhookAsync(webhookUrl, payload, ct);
    }

    private async Task PostWebhookAsync(string url, CronRunCompletedPayload payload, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            FileLog.Write($"[GatewayCronNotifier] webhook SKIPPED: job={payload.JobId}, not an http(s) url: {url}");
            return;
        }

        try
        {
            using var resp = await _webhookHttp.PostAsJsonAsync(uri, payload, JsonOpts, ct);
            if (resp.IsSuccessStatusCode)
                FileLog.Write($"[GatewayCronNotifier] webhook delivered: job={payload.JobId}, url={uri}, status={(int)resp.StatusCode}");
            else
                FileLog.Write($"[GatewayCronNotifier] webhook non-success (dropped): job={payload.JobId}, url={uri}, status={(int)resp.StatusCode}");
        }
        catch (OperationCanceledException)
        {
            // Shutdown / timeout: best-effort delivery, nothing to reconcile.
            FileLog.Write($"[GatewayCronNotifier] webhook canceled/timed out (dropped): job={payload.JobId}, url={uri}");
        }
        catch (HttpRequestException ex)
        {
            FileLog.Write($"[GatewayCronNotifier] webhook FAILED (dropped): job={payload.JobId}, url={uri}: {ex.Message}");
        }
    }
}
