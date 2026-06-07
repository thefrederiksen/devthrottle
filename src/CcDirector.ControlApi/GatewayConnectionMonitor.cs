using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>Connection states of the Director &lt;-&gt; Gateway two-way handshake (issues #223/#224).</summary>
public enum GatewayConnectionStatus
{
    /// <summary>No gateway.url configured - a legitimate local-only Director (gray, not an error).</summary>
    NotConfigured,

    /// <summary>Gateway configured; registration or verification pending or in flight (yellow).</summary>
    Connecting,

    /// <summary>The last handshake proved BOTH legs (green). Earned, never assumed.</summary>
    Verified,

    /// <summary>The last handshake failed; <see cref="GatewayConnectionMonitor.FailureSummary"/> names the failing leg (red).</summary>
    Failed,
}

/// <summary>
/// The one home of this Director's Gateway-connection truth (issues #223/#224).
///
/// Owned by ControlApiHost - NOT by GatewayClient - so it survives the client being
/// replaced on a settings change (ReapplyGatewayAsync). Three writers, one reader model:
///   - GatewayClient begins handshakes and records verdicts here.
///   - The Control API's GET /verify/{nonce} endpoint records callback receipts here.
///   - The desktop indicator and the troubleshooting dialog read state and subscribe
///     to <see cref="Changed"/>.
///
/// Green is EARNED: only a completed nonce handshake (both legs proven, nonce correlated
/// on this side too) sets <see cref="GatewayConnectionStatus.Verified"/>. Heartbeats
/// succeeding is NOT "connected" - that exact lie hid the SORENLAPTOP outage for days
/// while the Gateway could not call back.
/// </summary>
public sealed class GatewayConnectionMonitor
{
    private readonly object _lock = new();

    // Nonce -> whether the Gateway's callback arrived for it. Entries live only for the
    // duration of one handshake attempt; CompleteHandshake removes them.
    private readonly Dictionary<string, bool> _pending = new(StringComparer.Ordinal);

    public GatewayConnectionStatus Status { get; private set; } = GatewayConnectionStatus.NotConfigured;

    /// <summary>UTC time of the last PASSING handshake. Survives later failures so the UI
    /// can show "verified until HH:mm" next to a red X.</summary>
    public DateTime? LastVerifiedAt { get; private set; }

    /// <summary>One human-readable line naming the failing leg while <see cref="Status"/> is
    /// <see cref="GatewayConnectionStatus.Failed"/>; null otherwise.</summary>
    public string? FailureSummary { get; private set; }

    /// <summary>Full Gateway verdict of the last COMPLETED handshake (per-leg detail for the
    /// troubleshooting dialog). Null until one handshake round-trips, and after Reset.</summary>
    public DirectorVerifyResultDto? LastResult { get; private set; }

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
            _pending.Clear();
            Status = gatewayConfigured ? GatewayConnectionStatus.Connecting : GatewayConnectionStatus.NotConfigured;
            LastVerifiedAt = null;
            FailureSummary = null;
            LastResult = null;
        }
        FileLog.Write($"[GatewayConnectionMonitor] Reset: status={Status}");
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
        }
        FileLog.Write($"[GatewayConnectionMonitor] Registration failure: {summary}");
        Changed?.Invoke();
    }

    /// <summary>Open a handshake attempt: returns the fresh nonce to send to the Gateway.</summary>
    public string BeginHandshake()
    {
        var nonce = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            _pending[nonce] = false;
        }
        return nonce;
    }

    /// <summary>
    /// Record the Gateway's callback arriving (GET /verify/{nonce}). Returns true when the
    /// nonce matches a handshake currently in flight - echoed to the Gateway as "Known".
    /// </summary>
    public bool RecordCallback(string nonce)
    {
        if (string.IsNullOrEmpty(nonce)) return false;
        lock (_lock)
        {
            if (!_pending.ContainsKey(nonce)) return false;
            _pending[nonce] = true;
            return true;
        }
    }

    /// <summary>Whether the callback for this in-flight handshake has arrived on this side.</summary>
    public bool CallbackReceived(string nonce)
    {
        lock (_lock)
        {
            return _pending.TryGetValue(nonce, out var received) && received;
        }
    }

    /// <summary>Retire an in-flight nonce without a verdict (shutdown mid-handshake).
    /// No state flip, no event - the attempt simply never happened.</summary>
    public void AbandonHandshake(string nonce)
    {
        lock (_lock)
        {
            _pending.Remove(nonce);
        }
    }

    /// <summary>
    /// Close a handshake attempt with its verdict. <paramref name="failureSummary"/> null
    /// means PASS -> Verified; otherwise -> Failed with that summary. Either way the nonce
    /// is retired and <see cref="Changed"/> fires.
    /// </summary>
    public void CompleteHandshake(string nonce, DirectorVerifyResultDto? result, string? failureSummary)
    {
        lock (_lock)
        {
            _pending.Remove(nonce);
            LastResult = result;
            if (failureSummary is null)
            {
                Status = GatewayConnectionStatus.Verified;
                LastVerifiedAt = DateTime.UtcNow;
                FailureSummary = null;
            }
            else
            {
                Status = GatewayConnectionStatus.Failed;
                FailureSummary = failureSummary;
            }
        }
        FileLog.Write(failureSummary is null
            ? "[GatewayConnectionMonitor] Handshake PASSED: two-way connection verified"
            : $"[GatewayConnectionMonitor] Handshake FAILED: {failureSummary}");
        Changed?.Invoke();
    }
}
