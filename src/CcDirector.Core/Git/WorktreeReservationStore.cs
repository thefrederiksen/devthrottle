using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>Whether a reservation's owning Director process could be found.</summary>
public enum OwnerState
{
    /// <summary>The process exists (start time follows for a pid-reuse check).</summary>
    Alive,
    /// <summary>The process definitively does not exist - the reservation is stale.</summary>
    Gone,
    /// <summary>The process could not be inspected (access denied, transient error) - we must NOT
    /// conclude it is gone. Treated as still-live so protection is never dropped on uncertainty.</summary>
    Unknown,
}

/// <summary>
/// A machine-local reservation a session holds on its working directory while it is alive - the
/// coordination the destructive worktree reaper needs and the Gateway roster cannot provide
/// (inspection). A worktree is a LOCAL folder, so only sessions on THIS machine can be in it; when
/// any Director slot launches a session it writes a reservation here BEFORE the process starts, and
/// the reaper (in any slot) refuses to remove a worktree that a live reservation covers.
///
/// EXCLUSION, not just a snapshot (inspection round 5). The reserve-write and the reaper's
/// "read reservations then remove" are serialized by a machine-wide lock file
/// (<see cref="EnterCriticalSection"/>), so there is no check-to-remove gap: either a session's
/// reservation is written before the reaper reads (it is protected) or the reaper finishes removing
/// before the reservation is written (the session launches into an already-gone worktree and fails,
/// which is correct). Combined with reserve-BEFORE-launch ordering, a started session has always
/// reserved its worktree before the reaper could act.
///
/// FAIL CLOSED (inspection round 5). Every uncertainty resolves toward keeping protection: an owner
/// process that cannot be inspected is treated as live (<see cref="OwnerState.Unknown"/>), and an
/// enumeration failure THROWS so the reaper aborts rather than acting on an empty set. Only a
/// definitively-gone owner (or a reused pid whose start time no longer matches) prunes a reservation.
/// </summary>
public sealed class WorktreeReservationStore
{
    private readonly string _dir;
    private readonly int _ownerPid;
    private readonly DateTime _ownerStartUtc;
    private readonly Func<int, (OwnerState State, DateTime? StartUtc)> _probeOwner;

    /// <summary>How long to wait for the machine-wide reap lock before giving up.</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(30);

    /// <param name="dir">Reservation directory (defaults to the machine-local cc-director path).</param>
    /// <param name="ownerPid">This Director's process id (test seam; defaults to the current process).</param>
    /// <param name="ownerStartUtc">This Director's process start time (test seam).</param>
    /// <param name="probeOwner">Probes a pid: whether it exists and its start time (test seam;
    /// defaults to a real process lookup).</param>
    public WorktreeReservationStore(
        string? dir = null,
        int? ownerPid = null,
        DateTime? ownerStartUtc = null,
        Func<int, (OwnerState State, DateTime? StartUtc)>? probeOwner = null)
    {
        _dir = dir ?? DefaultDir();
        var self = Process.GetCurrentProcess();
        _ownerPid = ownerPid ?? self.Id;
        _ownerStartUtc = ownerStartUtc ?? self.StartTime.ToUniversalTime();
        _probeOwner = probeOwner ?? DefaultProbe;
    }

    private static string DefaultDir() => CcStorage.WorktreeReservations();

    private sealed record Reservation(string WorktreePath, int OwnerPid, DateTime OwnerStartUtc, string SessionId);

    /// <summary>
    /// Acquire the machine-wide lock that serializes reservation writes against the reaper's
    /// read-then-remove. The reaper wraps its per-worktree reservation check plus the destructive git
    /// removal in this; <see cref="Reserve"/> takes it around its write. Cross-process via a lock file
    /// (released automatically if the holder dies). Throws <see cref="TimeoutException"/> if it cannot
    /// be acquired within the wait - the reaper treats that as fail-closed and removes nothing.
    /// </summary>
    public IDisposable EnterCriticalSection() => EnterCriticalSection(LockWait);

    public IDisposable EnterCriticalSection(TimeSpan timeout)
    {
        Directory.CreateDirectory(_dir);
        var lockPath = Path.Combine(_dir, ".reap.lock");
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new Releaser(fs);
            }
            catch (IOException)
            {
                if (sw.Elapsed >= timeout)
                    throw new TimeoutException($"could not acquire the worktree-reap lock within {timeout.TotalSeconds:0}s");
                Thread.Sleep(15);
            }
        }
    }

    private sealed class Releaser : IDisposable
    {
        private FileStream? _fs;
        public Releaser(FileStream fs) => _fs = fs;
        public void Dispose() { try { _fs?.Dispose(); } catch { } finally { _fs = null; } }
    }

    /// <summary>Reserve <paramref name="workingDirectory"/> for a live session, holding the machine-wide
    /// lock so the write is serialized against the reaper. Best-effort - never throws into the session
    /// lifecycle. Written atomically (temp + move) so a reader never sees a partial record.
    ///
    /// <paramref name="ownerPid"/>/<paramref name="ownerStartUtc"/> override the owning process whose
    /// liveness keeps this reservation alive (inspection round 6). The launch path reserves TWICE: once
    /// before the process starts, owned by this Director (which is alive during launch), then again
    /// after the session process exists, owned by that SESSION process - so a session (or a detached
    /// child) that OUTLIVES a force-killed Director keeps its worktree protected, because liveness now
    /// tracks the session's own process, not the Director's. Omit both to own by this Director.</summary>
    public void Reserve(string workingDirectory, string sessionId, int? ownerPid = null, DateTime? ownerStartUtc = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(sessionId))
            return;

        // A cc-worktrees pool slot is not reserved here. A reservation is the Director's own claim on a
        // directory it manages, and a pool slot already has an owner and a claim: cc-worktrees holds
        // the lease on it, the reaper refuses to touch it on that evidence alone, and a second claim
        // written here would say the Director manages a directory it does not. The layout check is used
        // rather than the tool's records so that a machine whose records cannot be read still skips the
        // slot instead of writing a reservation for it.
        if (CcWorktreesPoolSlots.HasSlotLayout(workingDirectory))
        {
            FileLog.Write($"[WorktreeReservationStore] Reserve skipped for {sessionId}: {workingDirectory} is a cc-worktrees pool slot, which that tool owns");
            return;
        }

        try
        {
            Directory.CreateDirectory(_dir);
            var pid = ownerPid ?? _ownerPid;
            var startUtc = ownerStartUtc ?? _ownerStartUtc;
            var row = new Reservation(WorktreeReaperService.NormalizePath(workingDirectory), pid, startUtc, sessionId);
            var json = JsonSerializer.Serialize(row);
            using (EnterCriticalSection())
            {
                var final = FileFor(sessionId);
                var tmp = final + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, final, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeReservationStore] Reserve failed for {sessionId}: {ex.Message}");
        }
    }

    /// <summary>Release a session's reservation. Best-effort.</summary>
    public void Release(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;
        try
        {
            var f = FileFor(sessionId);
            if (File.Exists(f))
                File.Delete(f);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeReservationStore] Release failed for {sessionId}: {ex.Message}");
        }
    }

    /// <summary>
    /// The normalized worktree paths currently reserved by a LIVE session (its owning Director is
    /// alive). Prunes reservations whose owning Director is DEFINITIVELY gone as it reads. THROWS if
    /// the reservation directory cannot be enumerated, so the reaper fails closed rather than treating
    /// an unreadable store as "no reservations".
    /// </summary>
    public IReadOnlySet<string> LiveReservedPaths()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_dir))
            return live;

        // A failure to enumerate is NOT "no reservations" - it is "cannot tell", which must abort the
        // reap. Let it propagate (the reaper catches it and fails closed).
        var files = Directory.GetFiles(_dir, "*.json");

        foreach (var file in files)
        {
            Reservation? r;
            try
            {
                r = JsonSerializer.Deserialize<Reservation>(File.ReadAllText(file));
            }
            catch (FileNotFoundException)
            {
                // A benign race: another reader pruned this file between the enumeration and the read.
                // It protected nothing we still need to know about - skip it.
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex)
            {
                // FAIL CLOSED (inspection round 6): we could not READ or PARSE a reservation record that
                // exists (a locked file, a corrupt record). We cannot know what worktree it protects, so
                // treating it as "no protection" would delete under a live session. Abort the whole reap.
                throw new IOException($"could not read reservation record {file}: {ex.Message}", ex);
            }
            if (r is null || string.IsNullOrWhiteSpace(r.WorktreePath))
            {
                // Parsed but empty/degenerate - same fail-closed reasoning: we cannot tell what it guards.
                throw new IOException($"reservation record {file} is unreadable (empty or malformed)");
            }

            var (state, start) = _probeOwner(r.OwnerPid);
            bool alive;
            bool definitivelyGone = false;
            switch (state)
            {
                case OwnerState.Gone:
                    alive = false;
                    definitivelyGone = true; // the owning Director is gone - its sessions are gone too
                    break;
                case OwnerState.Unknown:
                    alive = true; // cannot inspect - keep protection (fail safe), do NOT prune
                    break;
                default: // Alive
                    if (start is null)
                        alive = true; // exists but start unreadable - keep (fail safe)
                    else if (Math.Abs((start.Value - r.OwnerStartUtc).TotalSeconds) < 2)
                        alive = true; // our owner
                    else
                    {
                        alive = false; // the pid was reused by a different process - original owner gone
                        definitivelyGone = true;
                    }
                    break;
            }

            if (alive)
                live.Add(r.WorktreePath);
            else if (definitivelyGone)
                TryDelete(file); // prune ONLY when we are certain the owner is gone
        }
        return live;
    }

    private string FileFor(string sessionId)
    {
        // sessionId is a Guid/opaque id; sanitize defensively for a filename.
        var safe = string.Concat(sessionId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(_dir, safe + ".json");
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { /* another reader won the race - fine */ }
    }

    private static (OwnerState, DateTime?) DefaultProbe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return (OwnerState.Alive, p.StartTime.ToUniversalTime());
        }
        catch (ArgumentException)
        {
            return (OwnerState.Gone, null); // no such process - definitively gone
        }
        catch
        {
            return (OwnerState.Unknown, null); // could not inspect - do NOT conclude gone
        }
    }
}
