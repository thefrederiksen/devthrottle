using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// Picks a stable HTTP port for a Director's Control API.
///
/// Strategy:
///   1. Read the persisted port (if any) for this Director GUID and reuse it if free.
///   2. Otherwise pick the first free port in [PortRangeStart .. PortRangeEnd].
///   3. Persist the chosen port to per-Director state file so restart picks the same.
///
/// State file: %LOCALAPPDATA%\cc-director\config\director\ports\{directorId}.port
/// </summary>
public static class PortAllocator
{
    public const int PortRangeStart = 7879;
    public const int PortRangeEnd = 7898;

    public static string PortStateDirectory { get; } =
        Path.Combine(CcStorage.Config(), "director", "ports");

    /// <summary>Allocate a stable port for the given Director ID. Throws if the range is exhausted.</summary>
    public static int Allocate(string directorId)
    {
        if (TryAllocate(directorId, out var port))
            return port;

        throw new InvalidOperationException(
            $"All ports in range {PortRangeStart}..{PortRangeEnd} are busy. " +
            "Either close some Director instances or extend PortAllocator.PortRangeEnd.");
    }

    /// <summary>
    /// Try to allocate a stable port for the given Director ID. Returns false (without throwing)
    /// when the fixed range is genuinely exhausted, so the caller can degrade gracefully -- e.g.
    /// the Control API falls back to an ephemeral loopback port instead of going dark (issue #697).
    /// </summary>
    public static bool TryAllocate(string directorId, out int port)
    {
        FileLog.Write($"[PortAllocator] TryAllocate: directorId={directorId}");
        Directory.CreateDirectory(PortStateDirectory);

        // 1) Try the previously-used port for this director
        var stateFile = Path.Combine(PortStateDirectory, $"{directorId}.port");
        if (File.Exists(stateFile))
        {
            try
            {
                var raw = File.ReadAllText(stateFile).Trim();
                if (int.TryParse(raw, out var prev) && prev >= PortRangeStart && prev <= PortRangeEnd)
                {
                    if (IsPortFree(prev))
                    {
                        FileLog.Write($"[PortAllocator] Reusing previous port {prev}");
                        port = prev;
                        return true;
                    }
                    FileLog.Write($"[PortAllocator] Previous port {prev} busy, picking new");
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[PortAllocator] read state failed: {ex.Message}");
            }
        }

        // 2) Scan range
        var usedByOthers = ReadPortsInUseByOtherDirectors(except: directorId);
        for (int p = PortRangeStart; p <= PortRangeEnd; p++)
        {
            if (usedByOthers.Contains(p)) continue;
            if (!IsPortFree(p)) continue;

            // 3) Persist
            try { File.WriteAllText(stateFile, p.ToString()); }
            catch (Exception ex) { FileLog.Write($"[PortAllocator] persist failed: {ex.Message}"); }

            FileLog.Write($"[PortAllocator] Allocated port {p}");
            port = p;
            return true;
        }

        FileLog.Write($"[PortAllocator] Range {PortRangeStart}..{PortRangeEnd} exhausted");
        port = 0;
        return false;
    }

    /// <summary>Release the persisted port file for the given Director ID.</summary>
    public static void Release(string directorId)
    {
        try
        {
            var stateFile = Path.Combine(PortStateDirectory, $"{directorId}.port");
            if (File.Exists(stateFile)) File.Delete(stateFile);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PortAllocator] Release failed: {ex.Message}");
        }
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            // Check listeners on all addresses (0.0.0.0 binding would conflict with anything bound there)
            var props = IPGlobalProperties.GetIPGlobalProperties();
            foreach (var ep in props.GetActiveTcpListeners())
                if (ep.Port == port) return false;

            // Try binding to confirm
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HashSet<int> ReadPortsInUseByOtherDirectors(string except)
    {
        return CollectLivePortReservations(PortStateDirectory, except, IsPortFree);
    }

    /// <summary>
    /// Read every other Director's <c>.port</c> reservation and return only the ports that are
    /// genuinely live - that is, ports that are NOT currently bindable. A reservation whose claimed
    /// port is free to bind is treated as a ghost left behind by a Director that did not shut down
    /// gracefully (hard-kill, crash, reboot), because a live Director would still be holding its
    /// port. Such stale reservation files are deleted in passing so the directory cannot fill up
    /// with ghosts and exhaust the whole range over time (issue #685).
    /// </summary>
    /// <param name="directory">The reservation directory to scan.</param>
    /// <param name="except">The current Director's ID, whose own reservation is ignored.</param>
    /// <param name="isPortFree">Predicate that returns true when the port can be bound right now.</param>
    /// <returns>The set of ports held by other Directors that are still genuinely in use.</returns>
    internal static HashSet<int> CollectLivePortReservations(
        string directory, string except, Func<int, bool> isPortFree)
    {
        var ports = new HashSet<int>();
        if (!Directory.Exists(directory))
            return ports;

        foreach (var file in Directory.EnumerateFiles(directory, "*.port"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(id, except, StringComparison.OrdinalIgnoreCase))
                continue;

            // A reservation file can be deleted by another starting Director between enumeration and
            // read. That single file is then irrelevant to us; log and skip it rather than aborting
            // the whole scan (which would wrongly fail allocation).
            string raw;
            try { raw = File.ReadAllText(file).Trim(); }
            catch (Exception ex)
            {
                FileLog.Write($"[PortAllocator] Skipping reservation {Path.GetFileName(file)}: read failed: {ex.Message}");
                continue;
            }

            if (!int.TryParse(raw, out var port))
            {
                // A malformed reservation can never identify a live Director - prune it.
                PrunePortReservation(file, $"malformed contents \"{raw}\"");
                continue;
            }

            if (isPortFree(port))
            {
                // The claimed port is bindable, so no live Director is holding it. This reservation
                // is a ghost from an ungraceful exit - prune it and do not count it as in use.
                PrunePortReservation(file, $"claimed port {port} is free to bind");
                continue;
            }

            ports.Add(port);
        }

        return ports;
    }

    /// <summary>Delete a stale reservation file, logging the reason. Best-effort: a delete failure
    /// (for example a transient lock) is logged and does not abort the scan.</summary>
    private static void PrunePortReservation(string file, string reason)
    {
        FileLog.Write($"[PortAllocator] Pruning stale reservation {Path.GetFileName(file)}: {reason}");
        try { File.Delete(file); }
        catch (Exception ex) { FileLog.Write($"[PortAllocator] Prune failed for {Path.GetFileName(file)}: {ex.Message}"); }
    }
}
