using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.History;

/// <summary>
/// The background pass of the work-history feature (issue #2194), on the per-tenant worker seam. Each
/// pass, per tenant:
///
///  1. THE SILENCE RULE: every open row not refreshed within <see cref="InterruptedThreshold"/> is
///     concluded "interrupted". The recorder refreshes a live session's row at least every
///     <see cref="SessionHistoryRecorder.FreshnessInterval"/> (5 minutes), so the threshold has three
///     missed heartbeats of slack - a network blip or Gateway restart never rules a live session
///     interrupted, because its Director re-pushes within seconds of reconnecting and the row reopens
///     even if it did.
///  2. Retention: rows older than <see cref="Retention"/> are pruned.
///
/// It no longer writes AI summaries. The per-session summary and the per-repository day paragraph were
/// removed in October 2026: the History page was their only reader, and the owner does not read them,
/// while they were about three quarters of all hosted AI spend.
///
/// Timer cadence and lifecycle are owned by GatewayHost (the ActivityRetentionSweep pattern).
/// </summary>
public sealed class SessionHistorySweep : TenantScopedSweep
{
    /// <summary>How long an open row may go unrefreshed before the Gateway concludes "interrupted".</summary>
    public static readonly TimeSpan InterruptedThreshold = TimeSpan.FromMinutes(15);

    /// <summary>How long history rows live (the API's 30-day range sits well inside this).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private readonly SessionHistoryStore _store;
    private readonly SessionTurnStore _turns;
    private readonly Receipts.ApiModeReceiptStore _receipts;
    private readonly ApiModeConversation.ApiModeConversationStore _conversation;

    public SessionHistorySweep(HostedTenantBoundary boundary, TenantRegistry tenants,
        SessionHistoryStore store, SessionTurnStore turns, Receipts.ApiModeReceiptStore receipts,
        ApiModeConversation.ApiModeConversationStore conversation)
        : base(boundary, tenants)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _turns = turns ?? throw new ArgumentNullException(nameof(turns));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
    }

    public async Task SweepAsync(CancellationToken ct = default)
    {
        await ForEachTenantAsync(() =>
        {
            var now = DateTime.UtcNow;

            _store.ConcludeInterrupted(now - InterruptedThreshold);

            _store.PurgeOlderThan(now - Retention);
            // The stored conversation lives exactly as long as the session-history row it belongs to.
            _turns.PurgeOlderThan(now - Retention);
            // An API-mode prompt's receipt lives as long as the conversation it was part of.
            _receipts.PurgeOlderThan(now - Receipts.ApiModeReceiptStore.Retention);
            // An API-mode session's stored conversation lives as long as its receipts, per session (never a silent gap
            // at the start of a session still in use).
            _conversation.PurgeOlderThan(now - ApiModeConversation.ApiModeConversationStore.Retention);
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }
}
