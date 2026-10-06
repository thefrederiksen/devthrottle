using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>Connection states of this Director's link to its Gateway.</summary>
public enum GatewayConnectionStatus
{
    /// <summary>No gateway.url configured - a legitimate local-only Director (gray, not an error).</summary>
    NotConfigured,

    /// <summary>Gateway configured; the tunnel is dialing or reconnecting (yellow).</summary>
    Connecting,

    /// <summary>
    /// The outbound tunnel to the Gateway is UP (green). Gateway Cleanup mission (tunnel-only): this was
    /// <c>Verified</c>, the verdict of the two-way nonce handshake (issues #223/#224). That handshake is
    /// gone - the Gateway deleted its half at the cut because it no longer dials the Director back - so the
    /// state is named for what now proves it: a live tunnel. Still earned, never assumed; the connection
    /// itself IS the proof, which is why the handshake was redundant as well as broken.
    /// </summary>
    Connected,

    /// <summary>The connection could not be established; <see cref="GatewayConnectionMonitor.FailureSummary"/> says why (red).</summary>
    Failed,

    /// <summary>
    /// This machine has no resolvable tailnet identity to advertise (issue #324): the
    /// Tailscale LocalAPI and CLI both failed and no usable config override is set. An
    /// explicit state - not a generic <see cref="Failed"/> - so the indicator and the
    /// troubleshooter can name the exact fix (start Tailscale / set gateway.tailnetEndpoint).
    /// Self-healing: the Director re-resolves every heartbeat cycle, so this clears within
    /// ~15s of Tailscale coming up - no restart.
    /// </summary>
    NoTailnetIdentity,

    /// <summary>
    /// The hosted subscription (or device enrollment) for this Gateway has lapsed or been revoked, so the
    /// Gateway refused the tunnel with a TERMINAL 401/402. The Director STOPPED reconnecting - there is no point
    /// hammering a locked door - and this state names the fix on screen (renew the subscription / re-enroll)
    /// with a link to Billing. Distinct from <see cref="Failed"/> (which keeps retrying) and from
    /// <see cref="Connecting"/>: a red, non-retrying "your subscription lapsed" state.
    /// </summary>
    SubscriptionRequired,

    /// <summary>
    /// The Gateway refused this Director's key with a TERMINAL 401 - removed from its team, revoked, or not known -
    /// and the Director STOPPED reconnecting (devthrottle_internal#2311, live proof F3).
    /// <see cref="GatewayConnectionMonitor.KeyRefusal"/> says which, and its words are what the status box and the
    /// panel show, so the person reads what happened instead of a forever-yellow "Connecting...".
    /// </summary>
    KeyRefused,
}

/// <summary>
/// The one home of this Director's Gateway-connection truth.
///
/// Owned by ControlApiHost - NOT by GatewayClient - so it survives the client being
/// replaced on a settings change (ReapplyGatewayAsync). Two writers, one reader model:
///   - The Gateway tunnel (GatewayStreamClient) marks itself connected/reconnecting here.
///   - The desktop indicator and the connection panel read state and subscribe
///     to <see cref="Changed"/>.
///
/// Green is EARNED: only a LIVE tunnel sets <see cref="GatewayConnectionStatus.Connected"/>.
/// The original lesson stands - the EXAMPLE-PC outage hid for days behind an indicator that
/// called succeeding heartbeats "connected" while the Gateway could not actually reach the
/// Director - but the proof is now the connection itself. The Gateway reaches this Director
/// ONLY down this stream, so a live stream is not evidence OF reachability, it IS reachability.
///
/// Gateway Cleanup mission (tunnel-only): the two-way nonce handshake that used to earn green
/// (issues #223/#224) is GONE. The Gateway deleted its half at the cut - it no longer dials the
/// Director back - so this side's half could never pass again, and the "handshake" writers here
/// (BeginHandshake / CompleteHandshake / RecordCallback) went with it.
/// </summary>
public sealed class GatewayConnectionMonitor
{
    private readonly object _lock = new();

    public GatewayConnectionStatus Status { get; private set; } = GatewayConnectionStatus.NotConfigured;

    /// <summary>UTC time the tunnel last came up. Survives a later drop so the UI can show
    /// "connected until HH:mm" next to a red X.</summary>
    public DateTime? LastVerifiedAt { get; private set; }

    /// <summary>One human-readable line saying why while <see cref="Status"/> is
    /// <see cref="GatewayConnectionStatus.Failed"/>; null otherwise.</summary>
    public string? FailureSummary { get; private set; }

    /// <summary>Why the Gateway refused this Director's key, while <see cref="Status"/> is
    /// <see cref="GatewayConnectionStatus.KeyRefused"/>; null otherwise.</summary>
    public GatewayKeyRefusal? KeyRefusal { get; private set; }

    /// <summary>Raised after every state change. May fire on any thread - UI subscribers dispatch.</summary>
    public event Action? Changed;

    /// <summary>
    /// (Re)initialize for the current config: Connecting when a Gateway is configured,
    /// NotConfigured otherwise. Clears all per-gateway state including LastVerifiedAt -
    /// a verification earned against the OLD gateway URL says nothing about the new one.
    /// </summary>
    public void Reset(bool gatewayConfigured)
    {
        lock (_lock)
        {
            Status = gatewayConfigured ? GatewayConnectionStatus.Connecting : GatewayConnectionStatus.NotConfigured;
            LastVerifiedAt = null;
            FailureSummary = null;
            KeyRefusal = null;
        }
        FileLog.Write($"[GatewayConnectionMonitor] Reset: status={Status}");
        Changed?.Invoke();
    }

    /// <summary>
    /// Gateway Cleanup mission (tunnel-only): the Director's outbound tunnel to the Gateway is UP
    /// (the SignalR connection established and the Hello/PushSnapshot reseed ran). This is the ONE
    /// truth of connectivity now - the Gateway reaches this Director only over this stream, so a live
    /// stream IS a proven two-way connection. Green. Sticky NotConfigured (a local-only Director with
    /// no gateway.url never goes green).
    /// </summary>
    public void MarkTunnelConnected()
    {
        lock (_lock)
        {
            if (Status == GatewayConnectionStatus.NotConfigured) return;
            if (Status == GatewayConnectionStatus.Connected) return; // no churn
            Status = GatewayConnectionStatus.Connected;
            LastVerifiedAt = DateTime.UtcNow;
            FailureSummary = null;
            KeyRefusal = null;
        }
        FileLog.Write("[GatewayConnectionMonitor] tunnel connected (two-way stream up)");
        Changed?.Invoke();
    }

    /// <summary>
    /// Gateway Cleanup mission (tunnel-only): the tunnel dropped and is reconnecting (or dialing for the
    /// first time). Yellow. Sticky NotConfigured. LastVerifiedAt survives so the UI can show "connected
    /// until HH:mm" beside the reconnecting state.
    /// </summary>
    public void MarkTunnelConnecting()
    {
        lock (_lock)
        {
            if (Status == GatewayConnectionStatus.NotConfigured) return;
            if (Status == GatewayConnectionStatus.Connecting) return; // no churn
            Status = GatewayConnectionStatus.Connecting;
            KeyRefusal = null;
        }
        FileLog.Write("[GatewayConnectionMonitor] tunnel connecting/reconnecting");
        Changed?.Invoke();
    }

    /// <summary>
    /// A registration attempt failed before any handshake could run (Gateway unreachable,
    /// own front door refused verification, no tailnet endpoint to advertise). Surfaces as
    /// Failed: an indicator stuck on yellow "connecting" while registration loops forever
    /// would hide the problem just as effectively as a lying green check.
    /// </summary>
    public void ReportRegistrationFailure(string summary)
    {
        lock (_lock)
        {
            // NotConfigured is sticky until Reset(true): a local-only Director never goes red.
            if (Status == GatewayConnectionStatus.NotConfigured) return;
            if (Status == GatewayConnectionStatus.Failed && FailureSummary == summary) return; // no churn
            Status = GatewayConnectionStatus.Failed;
            FailureSummary = summary;
            KeyRefusal = null;
        }
        FileLog.Write($"[GatewayConnectionMonitor] Registration failure: {summary}");
        Changed?.Invoke();
    }

    /// <summary>
    /// No tailnet identity resolved on this machine (issue #324): LocalAPI and CLI both
    /// failed and no usable override exists. Distinct from <see cref="ReportRegistrationFailure"/>
    /// because the remediation is LOCAL (start Tailscale / set the override), not a Gateway
    /// problem - the indicator paints it as its own state so the fix is named on screen.
    /// </summary>
    public void ReportTailnetIdentityFailure(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            throw new ArgumentException("A failure summary naming the fix is required", nameof(summary));
        lock (_lock)
        {
            // NotConfigured is sticky until Reset(true): a local-only Director never goes red.
            if (Status == GatewayConnectionStatus.NotConfigured) return;
            if (Status == GatewayConnectionStatus.NoTailnetIdentity && FailureSummary == summary) return; // no churn
            Status = GatewayConnectionStatus.NoTailnetIdentity;
            FailureSummary = summary;
            KeyRefusal = null;
        }
        FileLog.Write($"[GatewayConnectionMonitor] Tailnet identity failure: {summary}");
        Changed?.Invoke();
    }

    /// <summary>
    /// The Gateway refused the tunnel with a TERMINAL 401/402 - the hosted subscription lapsed or the device
    /// was revoked (e.g. the customer stopped paying). The Director has STOPPED its reconnect loop, so this
    /// surfaces the reason and the fix (renew / re-enroll) instead of a forever-yellow "connecting" or a
    /// retrying red. <see cref="LastVerifiedAt"/> survives so the UI can show "connected until HH:mm". Sticky
    /// NotConfigured (a local-only Director never hits this). Cleared by <see cref="Reset"/> on a settings
    /// change or a re-enrollment, which restarts the tunnel.
    /// </summary>
    public void MarkSubscriptionRequired(string summary)
    {
        lock (_lock)
        {
            // NotConfigured is sticky until Reset(true): a local-only Director never goes here.
            if (Status == GatewayConnectionStatus.NotConfigured) return;
            if (Status == GatewayConnectionStatus.SubscriptionRequired && FailureSummary == summary) return; // no churn
            Status = GatewayConnectionStatus.SubscriptionRequired;
            FailureSummary = summary;
            KeyRefusal = null;
        }
        FileLog.Write("[GatewayConnectionMonitor] subscription required - tunnel refused (terminal 402); reconnect stopped");
        Changed?.Invoke();
    }

    /// <summary>
    /// The Gateway refused this Director's key with a TERMINAL 401 and the tunnel has STOPPED reconnecting
    /// (devthrottle_internal#2311, live proof F3). Red, worded by <paramref name="refusal"/>: removed from the team
    /// only when the Gateway said so. Sticky NotConfigured. Cleared by <see cref="Reset"/> when the Director is set up
    /// again, which restarts the tunnel.
    /// </summary>
    public void MarkKeyRefused(GatewayKeyRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        lock (_lock)
        {
            if (Status == GatewayConnectionStatus.NotConfigured) return;
            if (Status == GatewayConnectionStatus.KeyRefused && KeyRefusal == refusal) return; // no churn
            Status = GatewayConnectionStatus.KeyRefused;
            KeyRefusal = refusal;
            FailureSummary = refusal.Summary;
        }
        FileLog.Write($"[GatewayConnectionMonitor] key refused ({refusal.Kind}) - tunnel refused (terminal 401); reconnect stopped");
        Changed?.Invoke();
    }

}
