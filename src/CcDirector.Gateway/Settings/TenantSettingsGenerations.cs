using System.Collections.Concurrent;

namespace CcDirector.Gateway.Settings;

/// <summary>
/// ONE NUMBER PER ACCOUNT THAT MOVES EVERY TIME ITS SETTINGS ROWS DO (issue #4405). <see cref="TenantSettingsStore"/>
/// keeps what it read in memory and trusts a remembered value only while this number is still the one it read under.
///
/// WHY A NUMBER AND NOT "THE STORE CLEARS ITS OWN CACHE". The store is not the only writer of <c>tenant_settings</c>:
/// the Mail Desk settings and the Fleet Manager promotion write the table through their own contexts, and the promotion
/// does it inside a transaction with other rows (<see cref="TenantSettingsStore.ApplyIn"/>). Every one of those writes
/// ends at <c>GatewayDbContext.SaveChanges</c>, so that is where the number is moved, and no writer can be missed.
///
/// WHEN IT MOVES. After the save, and - when the save ran inside a transaction - again after that transaction commits.
/// The second move closes the window in which a reader could load the old committed value under the new number: a
/// read between the save and the commit sees the old row, and the commit then moves the number past it.
///
/// Process-wide on purpose. Two Gateways in one process (tests) share it, which can only make a cache forget early,
/// never remember wrongly. A second Gateway PROCESS (the minute two containers overlap during a deploy) does not see
/// it at all, which is why <see cref="TenantSettingsStore.MaxAge"/> also bounds every remembered value.
/// </summary>
internal static class TenantSettingsGenerations
{
    private static readonly ConcurrentDictionary<string, long> Generations = new(StringComparer.Ordinal);

    /// <summary>The account's current number. Read it BEFORE reading the database, so a write that lands during
    /// the read moves the number past what is remembered.</summary>
    internal static long Current(string tenant) => Generations.TryGetValue(tenant, out var generation) ? generation : 0;

    /// <summary>Move the account's number, so every value remembered under the old one is read again.</summary>
    internal static void Bump(string tenant) => Generations.AddOrUpdate(tenant, 1, (_, generation) => generation + 1);
}
