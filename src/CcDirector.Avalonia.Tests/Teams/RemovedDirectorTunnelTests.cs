using System.Net;
using System.Net.Sockets;
using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.GatewayConnection;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Avalonia.Tests.Teams;

/// <summary>
/// Live proof F3 (devthrottle_internal#2311), through the REAL tunnel client: a <see cref="GatewayStreamClient"/> dials
/// a local stand-in Gateway over a real socket, exactly as it dials the hosted one, and the stand-in answers its
/// negotiate the way the Gateway does. A 401 for a key revoked because its person left the team stops the client for
/// good, in the removed state, naming the team; a revoke for anything else stops it as a plain revoke; and a server
/// error - the Gateway down behind a proxy - is still retried and still reads "Connecting...".
/// They live here because this is the test project the default local gate runs that can see the control plane.
/// </summary>
public sealed class RemovedDirectorTunnelTests
{
    [Fact]
    public void StatusBox_WhileDialing_StillSaysConnecting()
    {
        var monitor = new GatewayConnectionMonitor();
        monitor.Reset(gatewayConfigured: true);

        var inputs = MainWindow.StatusBoxInputs(monitor, boxGatewayConfigured: true, wasEverConnected: false,
            deviceKeyPresent: true, GatewayAccountSignInState.Unknown);

        Assert.Equal("Connecting...", GatewayStatusBoxPresenter.Describe(inputs, "gw.example", null).ChipText);
    }
}
