using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Network;

/// <summary>
/// Resolves the local process behind a loopback TCP connection (issue #212 L3).
///
/// On loopback every client shows as 127.0.0.1, so an access log alone can never say
/// WHICH local process made a call - the exact question the 2026-06-06 post-mortem could
/// not answer for the destructive endpoints. Given the client's ephemeral port (the
/// "remote" port from the server's point of view) and the server's listening port, this
/// walks the OS TCP table (GetExtendedTcpTable) to find the socket the client owns and
/// returns its PID and process name. The mapping is taken from the kernel, so a caller
/// cannot spoof it the way it could a header.
///
/// Windows-only and best-effort: returns null off Windows, for non-loopback callers whose
/// socket is not in the IPv4 table, or on any failure. Forensic identification must never
/// break a shutdown, so the single guard here is deliberate - this is the subsystem
/// boundary, not a fallback that hides a fixable error.
/// </summary>
public static class LoopbackPeerResolver
{
    public sealed record Peer(int Pid, string ProcessName)
    {
        public override string ToString() => $"{ProcessName} (pid {Pid})";
    }

    /// <param name="clientPort">The remote port of the inbound connection (HttpContext.Connection.RemotePort).</param>
    /// <param name="serverPort">The local port the request arrived on (HttpContext.Connection.LocalPort).</param>
    public static Peer? Resolve(int clientPort, int serverPort)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (clientPort is <= 0 or > 65535 || serverPort is <= 0 or > 65535) return null;

        try
        {
            var pid = FindOwningPid(clientPort, serverPort);
            if (pid is null) return null;

            string name;
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid.Value);
                name = proc.ProcessName;
            }
            catch { name = "?"; }
            return new Peer(pid.Value, name);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LoopbackPeerResolver] Resolve FAILED (clientPort={clientPort}, serverPort={serverPort}): {ex.Message}");
            return null;
        }
    }

    /// <summary>Convenience: the resolved peer as a log-ready string, or "unknown".</summary>
    public static string Describe(int clientPort, int serverPort)
        => Resolve(clientPort, serverPort)?.ToString() ?? "unknown";

    // ---- Win32 interop (IPv4 TCP table, by owning PID) ----

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;   // big-endian in the low two bytes
        public uint RemoteAddr;
        public uint RemotePort;  // big-endian in the low two bytes
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    private static int? FindOwningPid(int clientPort, int serverPort)
    {
        int size = 0;
        // First call sizes the buffer.
        var rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (rc != ERROR_INSUFFICIENT_BUFFER && rc != 0)
            return null;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc != 0) return null;

            var count = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + sizeof(int);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr + i * rowSize);
                // The client owns the socket whose LOCAL endpoint is its ephemeral port and
                // whose REMOTE endpoint is the server's listening port.
                if (DecodePort(row.LocalPort) == clientPort && DecodePort(row.RemotePort) == serverPort)
                    return (int)row.OwningPid;
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Ports in the TCP table are stored big-endian in the low two bytes.</summary>
    private static int DecodePort(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
}
