using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Voice;

/// <summary>
/// The in-memory answer to "which sessions hold a PENDING dictation", kept current by the WRITE path so the
/// read path never touches the disk.
///
/// WHY THIS EXISTS. <see cref="VoiceUploadStore.IsSessionLocked"/> is asked once per session, every five
/// seconds, by the display-state fold. It used to answer by re-listing the staging root and re-reading every
/// upload's record.json - so the cost was O(sessions x staged uploads) per pass, and because a staging
/// directory is only removed on the success path, every abandoned dictation raised that cost permanently.
/// On a self-hosted Gateway that is wasted local I/O. On the HOSTED Gateway the staging root is an Azure
/// Files share that BILLS PER FILE OPEN, and it measured 5.5 million opens a day - more than the App Service
/// plan the Gateway runs on, to answer a question whose answer is identical for every session in a tick.
///
/// The same defect was found and fixed on the Director side (see
/// <c>CcDirector.Core.Sessions.DictationLockReader.LockedSessionIds</c>, issue #1111) by enumerating ONCE per
/// tick instead of once per session. This goes further, because it can: the Gateway is the ONLY writer of
/// these records - every transition arrives through its own REST endpoints in
/// <c>GatewayDictationEndpoint</c> - so it does not need to re-read a file to learn something it just wrote.
/// The disk stays the DURABLE record (it must: a restart, and the Director's own cross-process reader on
/// self-host, both still read it); this is a cache of it, owned by its single writer.
///
/// WHY IT IS AN OBJECT AND NOT A STATIC. A static index keyed by tenant would be shared by every
/// <see cref="VoiceUploadStore"/> in the process, including two stores rooted at different directories with
/// the same tenant - which is exactly the shape the unit tests build (many temp roots, all
/// <c>TenantId.Local</c>), so one test's pending marker would answer another test's question. The index is
/// therefore keyed by the PARTITION ROOT - the same directory the enumeration it replaces would have walked -
/// and is handed down through <see cref="VoiceUploadStore.ForTenant"/>, so one real Gateway shares one index
/// across all its tenants while a freshly constructed store starts empty.
///
/// FAIL-OPEN IS PRESERVED EXACTLY. The enumeration this replaces treats an unreadable root or a half-written
/// marker as NO LOCK, never a false lock, because a false lock silently refuses a user's typing. So does
/// this: a hydration that throws leaves the partition UNHYDRATED (it is marked hydrated only on success), the
/// caller is told "no lock" for that pass, and the next call tries again. The failure mode of a broken disk
/// is therefore "the lock stops being enforced", identical to before - not "the session is locked forever".
/// </summary>
internal sealed class DictationLockIndex
{
    // One entry per partition root. The value maps uploadId -> owning sessionId for PENDING records ONLY;
    // a record in any other state is absent rather than present-and-false, so "is anything pending" is a
    // question about existence and cannot be answered wrongly by a stale flag.
    private readonly ConcurrentDictionary<string, Partition> _partitions =
        new(StringComparer.Ordinal);

    private sealed class Partition
    {
        // The gate for hydration and mutation. Reads do not take it: they walk the concurrent map, so a
        // read can never be blocked by a write, and the worst a racing read sees is the pre-write answer -
        // which is the same staleness the old disk enumeration had between its listing and its file reads.
        public readonly object Gate = new();
        public readonly ConcurrentDictionary<string, string> Pending = new(StringComparer.OrdinalIgnoreCase);
        // Upload ids whose record could NOT be read when the partition was hydrated (a share fault, a file held by
        // another writer, a permission error). Their state and target are unknown, so they are neither pending nor
        // not-pending: the session lock ignores them, exactly as the disk walk always did, and the factory inbox
        // thread re-reads each of them on every question until a read or a write settles it. Without this set a
        // single failed read during hydration was cached as "not held" for the life of the process.
        public readonly ConcurrentDictionary<string, byte> Unreadable = new(StringComparer.OrdinalIgnoreCase);
        public bool Hydrated;
        public bool RootEnsured;
    }

    private Partition For(string root) => _partitions.GetOrAdd(root, _ => new Partition());

    /// <summary>
    /// Create the partition root at most ONCE per root for the life of this index.
    ///
    /// <see cref="VoiceUploadStore"/> is constructed per call on the read path (GatewayHost builds one with
    /// ForTenant for every session of every fold), and its constructor calls Directory.CreateDirectory. On a
    /// local disk that is free; against a billed network share it is another metadata round trip per session
    /// per five seconds, for a directory that has existed since the first upload. The creation still HAPPENS -
    /// it is simply not repeated once this process has done it.
    /// </summary>
    public void EnsureRoot(string root, Action create)
    {
        var p = For(root);
        if (p.RootEnsured) return;
        lock (p.Gate)
        {
            if (p.RootEnsured) return;
            create();
            p.RootEnsured = true;
        }
    }

    /// <summary>
    /// Record what the single writer just wrote. <paramref name="pending"/> false REMOVES the entry, which is
    /// how delivered, abandoned and failed all release the session lock through one call rather than three.
    /// </summary>
    public void RecordWritten(string root, string uploadId, string? sessionId, bool pending)
    {
        var p = For(root);
        lock (p.Gate)
        {
            if (pending && !string.IsNullOrWhiteSpace(sessionId)) p.Pending[uploadId] = sessionId!;
            else p.Pending.TryRemove(uploadId, out _);
            // A record this process just wrote, or just read successfully, is known: it is no longer unreadable.
            p.Unreadable.TryRemove(uploadId, out _);
        }
    }

    /// <summary>Forget one upload id - its staging directory is gone.</summary>
    public void Removed(string root, string uploadId)
    {
        var p = For(root);
        lock (p.Gate)
        {
            p.Pending.TryRemove(uploadId, out _);
            p.Unreadable.TryRemove(uploadId, out _);
        }
    }

    /// <summary>
    /// Forget that one upload's record could not be read, without claiming anything about it: it was re-read and
    /// found to be a record nobody can act on (malformed - durable evidence the sweeps leave standing, skipped as
    /// the disk walk always skipped it) or not a record of this partition.
    /// </summary>
    public void Settled(string root, string uploadId)
    {
        var p = For(root);
        lock (p.Gate) p.Unreadable.TryRemove(uploadId, out _);
    }

    /// <summary>
    /// Drop this partition's cache so the next read re-hydrates from disk.
    ///
    /// Called by the sweeps, which delete staging directories WHOLESALE by walking the root rather than
    /// through <see cref="Removed"/>. Re-reading once after a sweep that actually removed something is far
    /// cheaper than threading a callback through every delete site, and a sweep runs on a fifteen-minute or
    /// six-hour timer - not per session, which is the cost that mattered.
    /// </summary>
    public void Invalidate(string root)
    {
        var p = For(root);
        lock (p.Gate)
        {
            p.Pending.Clear();
            p.Unreadable.Clear();
            p.Hydrated = false;
        }
    }

    /// <summary>
    /// The session ids holding a PENDING dictation in this partition, hydrating once from
    /// <paramref name="hydrate"/> if this process has not read the partition yet.
    ///
    /// Returns null when hydration failed, which the caller must read as "no lock" - see the fail-open note
    /// on the class. Null rather than an empty set so a caller cannot confuse "nothing is pending" with
    /// "I could not tell", even though both currently take the same branch.
    /// </summary>
    public IReadOnlyCollection<string>? LockedSessions(
        string root, Func<IEnumerable<HydratedRecord>> hydrate)
    {
        var p = Hydrated(root, hydrate);
        return p is null ? null : new HashSet<string>(p.Pending.Values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The upload ids whose PENDING record names exactly <paramref name="target"/> (ordinal), hydrating once the
    /// same way as <see cref="LockedSessions"/>. The factory inbox thread asks this to find its held spoken
    /// messages without walking every upload of the account; the target is stored where a session id is, so the
    /// index already holds it.
    ///
    /// <see cref="PendingLookup.Unreadable"/> names every upload whose record could not be read when the partition was
    /// hydrated: its target is unknown, so the caller must re-read each one rather than take its absence as "not held".
    ///
    /// Null when hydration failed. Unlike the session lock, that caller must NOT read null as "nothing" - a held
    /// message hidden by a disk error is the loss the thread exists to prevent - so it raises an error instead.
    /// </summary>
    public PendingLookup? PendingUploadsFor(
        string root, string target, Func<IEnumerable<HydratedRecord>> hydrate)
    {
        var p = Hydrated(root, hydrate);
        if (p is null) return null;
        var ids = new List<string>();
        foreach (var entry in p.Pending)
            if (string.Equals(entry.Value, target, StringComparison.Ordinal))
                ids.Add(entry.Key);
        return new PendingLookup(ids, p.Unreadable.Keys.ToList());
    }

    // The partition, hydrated from disk if this process has not read it yet; null when that read failed, in which
    // case it is left unhydrated so the next call tries again rather than caching "nothing is pending".
    private Partition? Hydrated(string root, Func<IEnumerable<HydratedRecord>> hydrate)
    {
        var p = For(root);
        if (p.Hydrated) return p;
        lock (p.Gate)
        {
            if (p.Hydrated) return p;
            try
            {
                // Build into the live map rather than replacing it, so a write-through that landed
                // while this scan was running is not thrown away by the snapshot. Both paths hold
                // the gate, so the only interleaving is scan-then-write or write-then-scan; a write
                // seen by the scan is simply re-asserted with the same value.
                foreach (var entry in hydrate())
                {
                    if (entry.Unreadable) p.Unreadable[entry.UploadId] = 0;
                    else if (!string.IsNullOrWhiteSpace(entry.Target)) p.Pending[entry.UploadId] = entry.Target!;
                }
                p.Hydrated = true;
                FileLog.Write($"[DictationLockIndex] hydrated {root}: {p.Pending.Count} pending, " +
                    $"{p.Unreadable.Count} could not be read");
                return p;
            }
            catch (Exception ex)
            {
                // Left UNHYDRATED on purpose: marking it hydrated here would cache "nothing is
                // locked" from a failed read and never look again.
                FileLog.Write($"[DictationLockIndex] hydrate {root} FAILED: {ex.Message}");
                return null;
            }
        }
    }
}

/// <summary>
/// One upload as the one-time partition read found it: a PENDING record and its target, or a record that could not be
/// read at all (<see cref="Unreadable"/>, target unknown).
/// </summary>
internal readonly record struct HydratedRecord(string UploadId, string? Target, bool Unreadable)
{
    public static HydratedRecord PendingFor(string uploadId, string target) => new(uploadId, target, false);
    public static HydratedRecord CouldNotRead(string uploadId) => new(uploadId, null, true);
}

/// <summary>The uploads the index names as PENDING for one target, and the uploads whose record it could not read.</summary>
internal sealed record PendingLookup(IReadOnlyList<string> Pending, IReadOnlyList<string> Unreadable);
