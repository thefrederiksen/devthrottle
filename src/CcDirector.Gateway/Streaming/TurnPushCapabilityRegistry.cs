using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Which Directors told this Gateway they SEND their sessions' conversations (the turn-push mission,
/// phase 2). Learned from each Director's <c>Hello</c>, and overwritten by the next one - so a machine that
/// comes back on an older build corrects the record itself.
///
/// It exists so the Chat screen can tell two silences apart. When the Gateway holds no conversation for a
/// session, "the computer has not sent it yet" and "that computer cannot send it at all" look identical in
/// the store and are completely different news to the person reading an empty screen - one is a moment's
/// wait, the other never ends until the Director is updated. Without this, the honest sentence could not be
/// written, and the screen would go on saying "waiting" forever.
///
/// In memory by design: a Gateway restart re-learns it from the next Hello, and until then a Director reads
/// as not-pushing, which is the safe direction - it says "that computer cannot send it", never "wait a
/// moment" about a wait that would not end.
///
/// NOTHING FORGETS AN ENTRY ON DISCONNECT, deliberately. A disconnected Director cannot send anything
/// whatever its build, and the fold that uses this asks about connection FIRST - so during the reconnect
/// blips the roster is built to ride out, the screen says "that computer is offline", which is true, rather
/// than "that computer cannot send conversations", which would be alarming and wrong. A Forget method was
/// written here and deleted: nothing called it, and a cleanup entry point with no caller reads as a policy
/// that exists when it does not.
/// </summary>
public sealed class TurnPushCapabilityRegistry
{
    // Keyed by TENANT AND Director, not by Director alone. A Director id is caller-supplied on the stream,
    // and every other fact this Gateway holds about a Director is partitioned by the tenant its connection
    // authenticated as - a shared key would let one account's Director decide what another account's Chat
    // screen says about a session (found in review).
    private readonly ConcurrentDictionary<(TenantId Tenant, string DirectorId), bool> _pushes = new();

    // The second thing a Director says about its build on Hello (the Fleet Manager mission, step 4): whether it checks a
    // session is waiting for a prompt before typing text the product sends by itself. Kept the same way, for the same
    // reasons: per tenant, overwritten by each Hello, and "not said" reads as "cannot", the safe direction.
    private readonly ConcurrentDictionary<(TenantId Tenant, string DirectorId), bool> _checksIdle = new();

    /// <summary>Record what a Director said about itself on Hello, under the tenant its connection is bound to.</summary>
    public void Record(TenantId tenant, string directorId, bool pushesTurns, bool checksIdleBeforeTyping = false)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        _pushes[(tenant, directorId)] = pushesTurns;
        _checksIdle[(tenant, directorId)] = checksIdleBeforeTyping;
    }

    /// <summary>Whether this tenant's Director said it checks a session is waiting for a prompt before it types. False
    /// for one that never said so - an older build, or one this Gateway has not heard from since it started.</summary>
    public bool ChecksIdleBeforeTyping(TenantId tenant, string? directorId)
        => !string.IsNullOrEmpty(directorId) && _checksIdle.TryGetValue((tenant, directorId), out var checks) && checks;

    /// <summary>Whether this tenant's Director said it sends conversations. False for one that never said so -
    /// an older build, or one this Gateway has not heard from since it started.</summary>
    public bool PushesTurns(TenantId tenant, string? directorId)
        => !string.IsNullOrEmpty(directorId) && _pushes.TryGetValue((tenant, directorId), out var pushes) && pushes;
}
