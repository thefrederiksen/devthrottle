using System.Collections.Concurrent;
using System.Security.Cryptography;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Secrets;

/// <summary>
/// THE MACHINES THAT CAN RECEIVE A SECRET (the Secret Handoff mission), learned from what each Director says on Hello.
///
/// IN MEMORY, ON PURPOSE. A Director re-sends its Hello about every ten seconds, so after a Gateway restart or a deploy
/// the list fills again within seconds - and a machine with no connected Director cannot open an envelope anyway, so
/// there is nothing a stored key would let it do. Holding only what connected Directors say means there is no key
/// table to migrate, and nothing at rest beyond the process.
///
/// PUBLIC KEYS ONLY. The private half never leaves the machine. Keyed by account AND Director, like the capability
/// registries, because a Director id is written by the client.
/// </summary>
public sealed class SecretMachineRegistry
{
    /// <summary>A Director whose last Hello is older than this is not counted, even while its connection looks
    /// open: Hello comes about every ten seconds, so three missed ones mean it is not really there.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);

    private sealed record Said(string Machine, byte[] PublicKey, DateTime SeenUtc);

    private readonly ConcurrentDictionary<(TenantId Tenant, string DirectorId), Said> _said = new();

    /// <summary>
    /// Record what a Director said on Hello. An empty or malformed key means the Director cannot take part, and any
    /// earlier key it said is forgotten - a Director that comes back as an older build must stop being listed.
    /// </summary>
    public void Record(TenantId tenant, string directorId, string machineName, string? publicKeyBase64, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        var key = Decode(publicKeyBase64);
        if (key is null || string.IsNullOrWhiteSpace(machineName))
        {
            if (_said.TryRemove((tenant, directorId), out _))
                FileLog.Write($"[SecretMachineRegistry] Record: director={directorId} no longer offers a secret key");
            return;
        }
        var previous = _said.TryGetValue((tenant, directorId), out var was) ? was : null;
        _said[(tenant, directorId)] = new Said(machineName.Trim(), key, nowUtc);
        if (previous is null || !previous.PublicKey.AsSpan().SequenceEqual(key))
            FileLog.Write($"[SecretMachineRegistry] Record: director={directorId}, machine={machineName.Trim()}, fingerprint={Fingerprint(key)[..16]}");
    }

    /// <summary>
    /// The account's machines that can receive: one row per machine name (compared without regard to case), from the
    /// Directors that are connected now and said a key within <see cref="StaleAfter"/>.
    /// </summary>
    /// <param name="isConnected">Whether that account's Director id has an active stream connection now.</param>
    public List<SecretMachineDto> Machines(TenantId tenant, Func<string, bool> isConnected, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(isConnected);
        var live = _said
            .Where(pair => pair.Key.Tenant.Equals(tenant) && nowUtc - pair.Value.SeenUtc <= StaleAfter && isConnected(pair.Key.DirectorId))
            .Select(pair => pair.Value)
            .ToList();
        var rows = new List<SecretMachineDto>();
        foreach (var group in live.GroupBy(s => s.Machine, StringComparer.OrdinalIgnoreCase))
        {
            var keys = group.Select(s => Convert.ToBase64String(s.PublicKey)).Distinct(StringComparer.Ordinal).ToList();
            var row = new SecretMachineDto
            {
                Machine = group.OrderByDescending(s => s.SeenUtc).First().Machine,
                LastSeenUtc = group.Max(s => s.SeenUtc),
                Directors = group.Count(),
            };
            if (keys.Count == 1)
            {
                row.PublicKey = keys[0];
                row.Fingerprint = Fingerprint(group.First().PublicKey);
            }
            else
            {
                row.Conflict = $"{keys.Count} different keys are reported by the Directors on {row.Machine}, so it cannot "
                               + "receive a secret until only one remains. Restart the older Director on that machine.";
            }
            rows.Add(row);
        }
        return rows.OrderBy(r => r.Machine, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The fingerprint of a public key: lower-case hex of SHA-256 over the 32 key bytes. cc-secrets computes
    /// exactly this, so the two can be compared.</summary>
    public static string Fingerprint(byte[] publicKey) => Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
