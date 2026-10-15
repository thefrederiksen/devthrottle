using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>What a read of "does this seat carry rules" came to. <see cref="Read"/> false means the version or the
/// seat could not be read, and <see cref="Why"/> says why in words; the answer is then unknown, never "no rules".</summary>
public sealed record SeatRulesRead(bool Read, bool CarriesRules, string? Why)
{
    public static SeatRulesRead Carries(bool carriesRules) => new(true, carriesRules, null);
    public static SeatRulesRead Unread(string why) => new(false, false, why);
}

/// <summary>Reads, for one tenant, whether a seat in one definition version names any rule set. Bound to the tenant by
/// the door that asks, so the settle that uses it never chooses an account.</summary>
public delegate SeatRulesRead SeatRulesReader(string factory, int definitionVersion, string seat);

/// <summary>
/// Whether a seat of a defined factory carries rules in one definition version (Factory Definition mission, phase 5):
/// the read behind "a child whose inherited kit carries rules, on an agent that cannot take rules, is refused" (A4
/// applied to the child). It reads the version from the definition store - the version the seat was started from,
/// whatever its state now, because that is the kit the seat and its children run under - and parses it once.
/// </summary>
public static class FactorySeatRules
{
    /// <summary>Read the answer for one seat. Never throws for a missing version, an unparseable one or a seat it
    /// does not hold: each is an <see cref="SeatRulesRead.Unread"/> with the reason, which the caller refuses on.</summary>
    public static SeatRulesRead Read(FactoryDefinitionStore store, TenantId tenant, string factory, int definitionVersion, string seat)
    {
        ArgumentNullException.ThrowIfNull(store);
        FileLog.Write($"[FactorySeatRules] Read: factory={factory}, version={definitionVersion}, seat={seat}");

        if (store.Find(tenant, factory, definitionVersion) is not { } version)
        {
            FileLog.Write($"[FactorySeatRules] Read: no version {definitionVersion} of {factory}");
            return SeatRulesRead.Unread($"version {definitionVersion} of the definition of '{factory}' cannot be found");
        }

        var parsed = FactoryDefinitionCheck.Read(version.Files);
        if (parsed.Definition is not { } definition)
        {
            FileLog.Write($"[FactorySeatRules] Read: version {definitionVersion} of {factory} does not parse");
            return SeatRulesRead.Unread($"version {definitionVersion} of the definition of '{factory}' cannot be read");
        }

        var held = definition.Seats.FirstOrDefault(s => string.Equals(s.Seat, seat, StringComparison.Ordinal));
        if (held is null)
        {
            FileLog.Write($"[FactorySeatRules] Read: version {definitionVersion} of {factory} holds no seat {seat}");
            return SeatRulesRead.Unread($"version {definitionVersion} of the definition of '{factory}' has no seat '{seat}'");
        }

        var carries = held.Rules.Count > 0;
        FileLog.Write($"[FactorySeatRules] Read: {factory}/{seat} v{definitionVersion} carries rules={carries} ({held.Rules.Count} rule set(s))");
        return SeatRulesRead.Carries(carries);
    }
}
