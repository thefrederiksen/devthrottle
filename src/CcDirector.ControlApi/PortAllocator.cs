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

    /// <summary>Allocate a stable port for the given Director ID. Throws if range is exhausted.</summary>
    public static int Allocate(string directorId)
    {
        FileLog.Write($"[PortAllocator] Allocate: directorId={directorId}");
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
                        return prev;
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
            return p;
        }

        throw new InvalidOperationException(
            $"All ports in range {PortRangeStart}..{PortRangeEnd} are busy. " +
            "Either close some Director instances or extend PortAllocator.PortRangeEnd.");
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
        var ports = new HashSet<int>();
        try
        {
            if (!Directory.Exists(PortStateDirectory)) return ports;
            foreach (var f in Directory.EnumerateFiles(PortStateDirectory, "*.port"))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                if (string.Equals(id, except, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var raw = File.ReadAllText(f).Trim();
                    if (int.TryParse(raw, out var p)) ports.Add(p);
                }
                catch { }
            }
        }
        catch { }
        return ports;
    }
}
