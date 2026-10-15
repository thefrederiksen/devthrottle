using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Which connected Directors run checks from a factory's definition (Factory Definition mission, phase 5b, design 2.6),
/// from what each said about itself on Hello (<see cref="Contracts.DirectorStreamHello.DefinitionChecks"/>).
///
/// WHY A STATED CAPABILITY. A Director older than phase 5b reads a definition trigger's assignment as a command trigger
/// with an empty command and an empty folder, and would report every run of it as a failed check. So the Gateway hands
/// a definition trigger ONLY to a Director that said it runs them, and says so in the trigger's status when none on its
/// machine has - the way <see cref="FleetManagerHomeCapabilityRegistry"/> tells an older Director apart.
///
/// Keyed by account AND Director, for the reason that registry gives: a Director id is written by the client.
/// </summary>
public sealed class DefinitionCheckCapabilityRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(TenantId Tenant, string DirectorId), bool> _runs = new();

    /// <summary>Record what a Director said about itself on Hello, under the account its connection is bound to.</summary>
    public void Record(TenantId tenant, string directorId, bool runsDefinitionChecks)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        _runs[(tenant, directorId)] = runsDefinitionChecks;
        FileLog.Write($"[DefinitionCheckCapabilityRegistry] Record: tenant={tenant.ToLogString()}, director={directorId}, definitionChecks={runsDefinitionChecks}");
    }

    /// <summary>Whether this account's Director said it runs checks from a definition. False for one that never said
    /// so - an older build, or one this Gateway has not heard from since it started.</summary>
    public bool RunsDefinitionChecks(TenantId tenant, string? directorId)
        => !string.IsNullOrEmpty(directorId) && _runs.TryGetValue((tenant, directorId), out var runs) && runs;
}
