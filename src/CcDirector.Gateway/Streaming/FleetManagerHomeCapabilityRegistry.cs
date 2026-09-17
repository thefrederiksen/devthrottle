using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Which connected Directors understand <c>NewSessionRequest.FleetManagerHome</c> (the Fleet Manager mission, step
/// 5), from what each said about itself on Hello.
///
/// WHY A STATED CAPABILITY AND NOT A VERSION NUMBER. A Director older than the flag ignores it and refuses the
/// create with "repoPath is required", which says nothing about the fix. A version comparison would need the
/// number of a release that does not exist yet, and a development build carries whatever number its branch was
/// cut at. What the Director says about itself on connecting is exact for both, and it is how the Gateway already
/// tells an older Director apart for conversations (<see cref="TurnPushCapabilityRegistry"/>).
///
/// Keyed by account AND Director, for the reason that registry gives: a Director id is written by the client.
///
/// STEP 8 records a second capability here on the same terms: whether the Director makes a <c>set-controller</c>
/// change only while the owner is still the expected one
/// (<see cref="Contracts.DirectorStreamHello.ChangesOwnerIfExpected"/>), which a hand over needs. The older
/// <see cref="Contracts.DirectorStreamHello.ChangesOwner"/> is deliberately NOT what is recorded: the first builds with
/// the verb said it and overwrote the owner unconditionally.
/// </summary>
public sealed class FleetManagerHomeCapabilityRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(TenantId Tenant, string DirectorId), bool> _creates = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(TenantId Tenant, string DirectorId), bool> _changesOwnerIfExpected = new();

    /// <summary>Record what a Director said about itself on Hello, under the account its connection is bound to.</summary>
    public void Record(TenantId tenant, string directorId, bool createsFleetManagerHome, bool changesOwnerIfExpected = false)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        _creates[(tenant, directorId)] = createsFleetManagerHome;
        _changesOwnerIfExpected[(tenant, directorId)] = changesOwnerIfExpected;
    }

    /// <summary>Whether this account's Director said it makes a <c>set-controller</c> change only while the owner is
    /// still the expected one. False for one that never said so, including one that said only the older verb flag.</summary>
    public bool ChangesOwnerIfExpected(TenantId tenant, string? directorId)
        => !string.IsNullOrEmpty(directorId) && _changesOwnerIfExpected.TryGetValue((tenant, directorId), out var changes) && changes;

    /// <summary>Whether this account's Director said it understands the flag. False for one that never said so - an
    /// older build, or one this Gateway has not heard from since it started.</summary>
    public bool CreatesFleetManagerHome(TenantId tenant, string? directorId)
        => !string.IsNullOrEmpty(directorId) && _creates.TryGetValue((tenant, directorId), out var creates) && creates;
}
