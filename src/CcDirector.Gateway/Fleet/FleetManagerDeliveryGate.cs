using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// ONE PER ACCOUNT: TYPING AN EVENT INTO THE FLEET MANAGER, STARTING A REPLACEMENT, AND CLOSING THE OLD FLEET MANAGER
/// NEVER OVERLAP (the steps 5 and 6 fixes, round 2, finding 4). One instance is shared by
/// <see cref="FleetManagerEventService"/> and <see cref="FleetManagerPlacementService"/>.
///
/// The event service holds it from its "is a replacement under way" check until the send has been answered and saved.
/// The placement service holds it while it records a successor, and while the replacement loop reads the old Fleet
/// Manager's state, re-checks the delivery generation and sends the close. So a delivery either finished before the
/// successor was recorded, or saw the successor and typed nothing; and a delivery can never land between the loop's
/// Idle read and its close.
///
/// THE DELIVERY GENERATION counts the deliveries typed into each session in this process. The replacement loop closes
/// only after two looks in a row saw the session Idle with the same generation, so a delivery typed just before the
/// successor was recorded - whose Working state the Director has not pushed yet - is given its turn first.
/// </summary>
public sealed class FleetManagerDeliveryGate
{
    private readonly ConcurrentDictionary<TenantId, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), long> _generations = new();

    /// <summary>Wait for the account's turn. Dispose the result to release it.</summary>
    public async Task<IDisposable> EnterAsync(TenantId tenant, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(tenant, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Release(gate);
    }

    /// <summary>How many deliveries this process has typed into the session.</summary>
    public long Generation(TenantId tenant, string sessionId)
        => _generations.TryGetValue((tenant, sessionId.ToLowerInvariant()), out var g) ? g : 0;

    /// <summary>A delivery was typed into the session.</summary>
    public void Delivered(TenantId tenant, string sessionId)
    {
        var g = _generations.AddOrUpdate((tenant, sessionId.ToLowerInvariant()), 1, (_, old) => old + 1);
        FileLog.Write($"[FleetManagerDeliveryGate] Delivered: sid={sessionId}, generation={g}");
    }

    private sealed class Release : IDisposable
    {
        private SemaphoreSlim? _gate;
        public Release(SemaphoreSlim gate) => _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
