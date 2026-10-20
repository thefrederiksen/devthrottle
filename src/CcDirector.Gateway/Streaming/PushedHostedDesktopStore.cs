using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// WHICH CONNECTED DIRECTOR HOSTS WHICH DESKTOP (the Factory Boxes mission, phase 1, issue #4258). Each Director pushes
/// the list of desktops it hosts up its stream on every reseed; this holds those lists in memory, tenant first, then
/// Director, so <see cref="TryLocate"/> can answer "which of this account's Directors hosts box X" the way
/// <see cref="PushedSessionStore.TryLocate"/> answers it for a session.
///
/// The ownership discipline is <see cref="PushedRepositoryStore"/>'s: only the Director's CURRENT stream connection -
/// the one bound at Hello - may push, a superseded connection can never take ownership back, and within one connection
/// a push must carry a higher sequence than the last accepted one.
///
/// TWO DIFFERENCES FROM THE REPOSITORY STORE, both on purpose:
///
///  - A list is DROPPED the moment its Director's current connection goes. A desktop is only reachable through its
///    Director's live tunnel, so a list held past the disconnect would offer a desktop nobody can open. Phase 1 has
///    exactly one state, Online, and it means "its Director is connected now".
///  - A box id is UNIQUE WITHIN AN ACCOUNT. When a second Director in the same account pushes a box id another
///    connected Director already holds, that one box id is refused for the second Director and logged; the rest of
///    its list is accepted. The holder keeps it until its list stops naming it or it disconnects, and the second
///    Director's next push (ten seconds later) then claims it. Two accounts may use the same box id: each lookup is
///    inside one account.
///
/// Every box id and display name is checked again here (<see cref="BoxIdRule"/>) rather than trusted, because the
/// payload is written by the client.
/// </summary>
public sealed class PushedHostedDesktopStore
{
    private sealed class Entry
    {
        public string? ActiveConnectionId;
        public long CurrentEpoch;                 // 0 = no owner
        public long NextEpoch = 1;
        public readonly Dictionary<string, long> ConnectionEpochs = new(StringComparer.Ordinal);
        public long LastSequence = -1;
        public string MachineName = "";
        public string DirectorName = "";
        public List<HostedDesktopDto> Desktops = new();
    }

    // One lock for the whole store: the uniqueness rule reads every Director of a tenant at once, and the lists are
    // a handful of entries pushed every ten seconds, so a finer lock would buy nothing and cost a race.
    private readonly object _gate = new();
    private readonly Dictionary<TenantId, Dictionary<string, Entry>> _byTenant = new();

    /// <summary>
    /// Mark <paramref name="connectionId"/> as the Director's current stream connection; the hub calls this at every
    /// Hello. Also records the machine and the Director's display name the Hello carried, so a rename shows on the
    /// next Hello. The sequence baseline resets only when ownership moves to a different, newer connection.
    /// </summary>
    public void RegisterConnection(TenantId tenant, string directorId, string connectionId, string? machineName, string? directorName)
    {
        lock (_gate)
        {
            var entry = EntryFor(tenant, directorId);
            if (!entry.ConnectionEpochs.TryGetValue(connectionId, out var epoch))
            {
                epoch = entry.NextEpoch++;
                entry.ConnectionEpochs[connectionId] = epoch;
            }

            if (string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
            {
                entry.MachineName = machineName ?? "";
                entry.DirectorName = directorName ?? "";
                return;
            }

            if (epoch < entry.CurrentEpoch)
            {
                FileLog.Write($"[PushedHostedDesktopStore] Hello IGNORED from a superseded connection (epoch {epoch} < current {entry.CurrentEpoch}): director={directorId}");
                return;
            }

            entry.ActiveConnectionId = connectionId;
            entry.CurrentEpoch = epoch;
            entry.LastSequence = -1;
            entry.MachineName = machineName ?? "";
            entry.DirectorName = directorName ?? "";
            entry.Desktops = new List<HostedDesktopDto>();
        }
    }

    /// <summary>
    /// The connection went. When it was the Director's current one, its desktops are dropped at once. A late
    /// disconnect from a superseded connection changes nothing.
    /// </summary>
    public void UnregisterConnection(TenantId tenant, string directorId, string connectionId)
    {
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var directors) || !directors.TryGetValue(directorId, out var entry))
                return;
            entry.ConnectionEpochs.Remove(connectionId);
            if (!string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
                return;

            var dropped = entry.Desktops.Count;
            entry.ActiveConnectionId = null;
            entry.CurrentEpoch = 0;
            entry.Desktops = new List<HostedDesktopDto>();
            if (dropped > 0)
                FileLog.Write($"[PushedHostedDesktopStore] disconnected: director={directorId} - its {dropped} desktop(s) dropped");
        }
    }

    /// <summary>
    /// Replace a Director's whole list. Returns what was accepted and what was refused; <see cref="HostedDesktopPushResult.Accepted"/>
    /// is false when the push itself was refused (not the current connection, or a stale sequence) and nothing changed.
    /// </summary>
    public HostedDesktopPushResult ApplySnapshot(TenantId tenant, string directorId, string connectionId, long sequence,
        IReadOnlyList<HostedDesktopDto>? desktops)
    {
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var directors) || !directors.TryGetValue(directorId, out var entry))
                return HostedDesktopPushResult.NotAccepted("no Hello bound this Director");
            if (!string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
            {
                FileLog.Write($"[PushedHostedDesktopStore] push REJECTED (not the current connection): director={directorId}, seq={sequence}");
                return HostedDesktopPushResult.NotAccepted("not the Director's current connection");
            }
            if (sequence <= entry.LastSequence)
                return HostedDesktopPushResult.NotAccepted("a stale or repeated sequence");
            entry.LastSequence = sequence;

            var accepted = new List<HostedDesktopDto>();
            var refused = new List<string>();
            foreach (var desktop in desktops ?? Array.Empty<HostedDesktopDto>())
            {
                var why = WhyRefused(desktop, accepted, directors, directorId);
                if (why is not null)
                {
                    var boxId = BoxIdRule.ForLog(desktop?.BoxId);
                    refused.Add(boxId);
                    FileLog.Write($"[PushedHostedDesktopStore] box REFUSED: tenant={tenant.ToLogString()}, director={directorId}, box={boxId}: {why}");
                    continue;
                }
                accepted.Add(new HostedDesktopDto { BoxId = desktop!.BoxId, DisplayName = desktop.DisplayName.Trim() });
            }

            entry.Desktops = accepted.OrderBy(d => d.BoxId, StringComparer.Ordinal).ToList();
            return HostedDesktopPushResult.Applied(entry.Desktops.Count, refused);
        }
    }

    /// <summary>The connected Director in <paramref name="tenant"/> that hosts <paramref name="boxId"/>, or null.</summary>
    public LocatedHostedDesktop? TryLocate(TenantId tenant, string? boxId)
    {
        if (!BoxIdRule.IsValid(boxId)) return null;
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var directors)) return null;
            foreach (var (directorId, entry) in directors)
            {
                if (entry.ActiveConnectionId is null) continue;
                var desktop = entry.Desktops.FirstOrDefault(d => string.Equals(d.BoxId, boxId, StringComparison.Ordinal));
                if (desktop is not null)
                    return new LocatedHostedDesktop(desktop.BoxId, desktop.DisplayName, directorId, entry.DirectorName, entry.MachineName);
            }
            return null;
        }
    }

    /// <summary>Every desktop hosted by <paramref name="tenant"/>'s connected Directors, ordered by box id. Never another account's.</summary>
    public IReadOnlyList<LocatedHostedDesktop> ListFor(TenantId tenant)
    {
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var directors)) return Array.Empty<LocatedHostedDesktop>();
            return directors
                .Where(pair => pair.Value.ActiveConnectionId is not null)
                .SelectMany(pair => pair.Value.Desktops.Select(d =>
                    new LocatedHostedDesktop(d.BoxId, d.DisplayName, pair.Key, pair.Value.DirectorName, pair.Value.MachineName)))
                .OrderBy(d => d.BoxId, StringComparer.Ordinal)
                .ToList();
        }
    }

    private Entry EntryFor(TenantId tenant, string directorId)
    {
        if (!_byTenant.TryGetValue(tenant, out var directors))
        {
            directors = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            _byTenant[tenant] = directors;
        }
        if (!directors.TryGetValue(directorId, out var entry))
        {
            entry = new Entry();
            directors[directorId] = entry;
        }
        return entry;
    }

    private static string? WhyRefused(HostedDesktopDto? desktop, List<HostedDesktopDto> acceptedSoFar,
        Dictionary<string, Entry> directors, string directorId)
    {
        if (desktop is null) return "the entry is empty";
        var invalid = BoxIdRule.WhyInvalid(desktop.BoxId);
        if (invalid is not null) return invalid;
        var name = desktop.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name)) return "it has no display name";
        if (name.Length > BoxIdRule.MaxDisplayNameLength) return $"its display name is longer than {BoxIdRule.MaxDisplayNameLength} characters";
        if (acceptedSoFar.Any(d => string.Equals(d.BoxId, desktop.BoxId, StringComparison.Ordinal)))
            return "the push lists the box id twice";
        foreach (var (otherId, other) in directors)
        {
            if (string.Equals(otherId, directorId, StringComparison.OrdinalIgnoreCase) || other.ActiveConnectionId is null) continue;
            if (other.Desktops.Any(d => string.Equals(d.BoxId, desktop.BoxId, StringComparison.Ordinal)))
                return $"Director {otherId} in this account already hosts a desktop with this box id, and a box id is unique within an account";
        }
        return null;
    }
}

/// <summary>A desktop located on one of an account's connected Directors.</summary>
/// <param name="BoxId">The box id.</param>
/// <param name="DisplayName">The name a person reads.</param>
/// <param name="DirectorId">The Director that hosts it, and the one the relay is sent to.</param>
/// <param name="DirectorName">That Director's display name, as its last Hello carried it; empty when it has none.</param>
/// <param name="MachineName">The machine that Director runs on, as its last Hello carried it.</param>
public sealed record LocatedHostedDesktop(string BoxId, string DisplayName, string DirectorId, string DirectorName, string MachineName);

/// <summary>What <see cref="PushedHostedDesktopStore.ApplySnapshot"/> did with one push.</summary>
public sealed class HostedDesktopPushResult
{
    private HostedDesktopPushResult(bool accepted, int count, IReadOnlyList<string> refused, string? whyNotAccepted)
    {
        Accepted = accepted;
        AcceptedCount = count;
        RefusedBoxIds = refused;
        WhyNotAccepted = whyNotAccepted;
    }

    /// <summary>True when the push replaced the Director's list (even if some of its entries were refused).</summary>
    public bool Accepted { get; }

    /// <summary>How many desktops the Director's list now holds.</summary>
    public int AcceptedCount { get; }

    /// <summary>The box ids refused from an accepted push, made safe for a log line.</summary>
    public IReadOnlyList<string> RefusedBoxIds { get; }

    /// <summary>Why the whole push was refused, or null when it was accepted.</summary>
    public string? WhyNotAccepted { get; }

    internal static HostedDesktopPushResult Applied(int count, IReadOnlyList<string> refused) => new(true, count, refused, null);

    internal static HostedDesktopPushResult NotAccepted(string why) => new(false, 0, Array.Empty<string>(), why);
}
