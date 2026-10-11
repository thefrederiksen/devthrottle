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

    /// <summary>A Director not heard from in this long is dropped from memory altogether, so Director ids that are gone
    /// for good do not pile up until the Gateway restarts. Far longer than <see cref="StaleAfter"/>: a Director that
    /// comes back simply says its key again on its next Hello.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(1);

    private sealed record Said(string Machine, byte[] PublicKey, DateTime SeenUtc, string? Credential);

    private readonly ConcurrentDictionary<(TenantId Tenant, string DirectorId), Said> _said = new();

    /// <summary>
    /// Record what a Director said on Hello. An empty or malformed key means the Director cannot take part, and any
    /// earlier key it said is forgotten - a Director that comes back as an older build must stop being listed.
    /// </summary>
    /// <param name="credential">The credential the Hello authenticated with ("device:&lt;id&gt;"), so a call made later with
    /// that same credential can be told which machine it comes from (<see cref="MachineOfCredential"/>).</param>
    public void Record(TenantId tenant, string directorId, string machineName, string? publicKeyBase64, DateTime nowUtc,
        string? credential = null)
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
        _said[(tenant, directorId)] = new Said(machineName.Trim(), key, nowUtc, credential);
        ForgetLongGone(nowUtc);
        if (previous is null || !previous.PublicKey.AsSpan().SequenceEqual(key))
            FileLog.Write($"[SecretMachineRegistry] Record: director={directorId}, machine={machineName.Trim()}, fingerprint={Fingerprint(key)[..16]}");
    }

    /// <summary>
    /// The account's machines that can receive: one row per machine name (compared without regard to case), from the
    /// Directors that are connected now and said a key within <see cref="StaleAfter"/>.
    /// </summary>
    /// <param name="isConnected">Whether that account's Director id has an active stream connection now.</param>
    /// <remarks>No entry or exit line: it is called on every list, and the endpoint that calls it logs the row count.
    /// <see cref="Record"/> logs only when a key changes, because Hello arrives every ten seconds.</remarks>
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

    /// <summary>
    /// The live Director on <paramref name="machine"/> to send a transfer command to: connected, heard from within
    /// <see cref="StaleAfter"/>, the most recently heard first. Null when there is none.
    /// </summary>
    public string? DirectorFor(TenantId tenant, string machine, Func<string, bool> isConnected, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(isConnected);
        return _said
            .Where(pair => pair.Key.Tenant.Equals(tenant)
                           && string.Equals(pair.Value.Machine, machine, StringComparison.OrdinalIgnoreCase)
                           && nowUtc - pair.Value.SeenUtc <= StaleAfter
                           && isConnected(pair.Key.DirectorId))
            .OrderByDescending(pair => pair.Value.SeenUtc)
            .Select(pair => pair.Key.DirectorId)
            .FirstOrDefault();
    }

    /// <summary>
    /// THE MACHINE A CREDENTIAL BELONGS TO, by the name the machine list uses. A transfer names machines as the list
    /// does - by the name their Directors report - and two things on a real machine make that differ from the device:
    /// a device's enrollment name can differ from the name its Director reports (a Mac enrolled as "Sorens-Mac-mini"
    /// whose Director calls it "devthrottle-mac-mini"), and one machine can hold two device credentials (SOREN_NORTH's
    /// cc-secrets reads the machine's own config.json while its Directors use their instance's, a different device
    /// enrolled under the same name).
    ///
    /// So the answer is the machine reported by the live Directors that say Hello with this SAME credential, or with a
    /// credential whose device is enrolled under the same machine name as this one's. The enrollment name was already
    /// what the Gateway trusted before this; it is only ever compared within the caller's own account. Null when no
    /// such live Director that can take part is connected, or when they name different machines (the Gateway will not
    /// guess between them). Spelled as the most recently heard Director spells it, as the machine list does.
    /// </summary>
    /// <param name="enrolledMachineOf">The machine name a credential's device is enrolled under, or null when it has
    /// none (not a device credential, or no such device).</param>
    public string? MachineOfCredential(TenantId tenant, string credential, Func<string, string?> enrolledMachineOf,
        Func<string, bool> isConnected, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(enrolledMachineOf);
        ArgumentNullException.ThrowIfNull(isConnected);
        if (string.IsNullOrEmpty(credential)) return null;
        var enrolled = new Dictionary<string, string?>(StringComparer.Ordinal);   // one device lookup per credential
        string? EnrolledAs(string c) => enrolled.TryGetValue(c, out var n) ? n : enrolled[c] = enrolledMachineOf(c);
        var enrolledAs = EnrolledAs(credential);
        bool SameMachine(string? other) =>
            other is not null
            && (string.Equals(other, credential, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(enrolledAs)
                    && string.Equals(EnrolledAs(other)?.Trim(), enrolledAs.Trim(), StringComparison.OrdinalIgnoreCase)));
        var names = _said
            .Where(pair => pair.Key.Tenant.Equals(tenant)
                           && nowUtc - pair.Value.SeenUtc <= StaleAfter
                           && isConnected(pair.Key.DirectorId)
                           && SameMachine(pair.Value.Credential))
            .OrderByDescending(pair => pair.Value.SeenUtc)
            .Select(pair => pair.Value.Machine)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 1 ? names[0] : null;
    }

    /// <summary>How many Directors are held in memory (for tests).</summary>
    internal int Count => _said.Count;

    private void ForgetLongGone(DateTime nowUtc)
    {
        foreach (var pair in _said)
        {
            if (nowUtc - pair.Value.SeenUtc > ForgetAfter && _said.TryRemove(pair.Key, out _))
                FileLog.Write($"[SecretMachineRegistry] ForgetLongGone: director={pair.Key.DirectorId}, last seen {pair.Value.SeenUtc:O}");
        }
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
