using System.Net;
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

        // Two arbitrary high ports that are extremely unlikely to be a live pair.
        var peer = LoopbackPeerResolver.Resolve(64999, 65000);
        Assert.Null(peer);
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
        Assert.Equal("unknown", LoopbackPeerResolver.Describe(64999, 65000));
    }
}
