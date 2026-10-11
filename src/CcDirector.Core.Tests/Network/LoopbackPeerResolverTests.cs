using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Core.Tests.Network;

/// <summary>
/// Verifies the loopback caller-PID resolver (issue #212 L3) against a real loopback
/// socket: it must name THIS process as the owner of a connection it actually opened,
/// and must not invent an owner for ports nobody is using.
/// </summary>
public sealed class LoopbackPeerResolverTests
{
    [Fact]
    public void Resolve_identifies_this_process_as_the_loopback_caller()
    {
        if (!OperatingSystem.IsWindows())
            return; // Windows-only resolver; nothing to assert elsewhere.

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, serverPort);
        using var accepted = listener.AcceptTcpClient();

        // From the server's view, the inbound connection's remote port is the client's
        // ephemeral port - exactly what an endpoint reads from HttpContext.Connection.RemotePort.
        var clientPort = ((IPEndPoint)accepted.Client.RemoteEndPoint!).Port;

        try
        {
            var peer = LoopbackPeerResolver.Resolve(clientPort, serverPort);

            Assert.NotNull(peer);
            Assert.Equal(Environment.ProcessId, peer!.Pid);
            Assert.False(string.IsNullOrWhiteSpace(peer.ProcessName));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Resolve_returns_null_for_ports_with_no_matching_connection()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (clientPort, serverPort) = PortsNobodyIsUsing();
        var peer = LoopbackPeerResolver.Resolve(clientPort, serverPort);
        Assert.Null(peer);
    }

    /// <summary>
    /// Two high ports that appear in no connection on this machine right now, read from the same table the resolver
    /// reads. The test used to name 64999 and 65000 as "extremely unlikely to be a live pair"; on 10 October 2026 they
    /// were a live pair twice in one night - the ephemeral range on Windows runs to 65535 - and the test failed. A
    /// pair chosen from the table cannot be in it.
    /// </summary>
    private static (int ClientPort, int ServerPort) PortsNobodyIsUsing()
    {
        var inUse = new HashSet<int>();
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        foreach (var connection in properties.GetActiveTcpConnections())
        {
            inUse.Add(connection.LocalEndPoint.Port);
            inUse.Add(connection.RemoteEndPoint.Port);
        }
        foreach (var listener in properties.GetActiveTcpListeners())
            inUse.Add(listener.Port);

        var free = new List<int>(2);
        for (var port = 65000; port > 1024 && free.Count < 2; port--)
            if (!inUse.Contains(port)) free.Add(port);
        Assert.Equal(2, free.Count);
        return (free[0], free[1]);
    }

    [Theory]
    [InlineData(0, 7879)]
    [InlineData(7879, 0)]
    [InlineData(-1, 7879)]
    [InlineData(70000, 7879)]
    public void Resolve_rejects_out_of_range_ports(int clientPort, int serverPort)
    {
        Assert.Null(LoopbackPeerResolver.Resolve(clientPort, serverPort));
    }

    [Fact]
    public void Describe_returns_unknown_when_unresolved()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("unknown", LoopbackPeerResolver.Describe(64999, 65000));
            return;
        }

        var (clientPort, serverPort) = PortsNobodyIsUsing();
        Assert.Equal("unknown", LoopbackPeerResolver.Describe(clientPort, serverPort));
    }
}
