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
    private const string RemovedBody =
        "{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\",\"reason\":\"team_member_removed\"}";
    private const string PlainRevokedBody = "{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\"}";

    /// <summary>A one-answer HTTP server on a loopback port that counts the tunnel's negotiate requests.</summary>
    private sealed class StandInGateway : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly int _status;
        private readonly string _body;
        private int _negotiates;
        private readonly Task _loop;

        public StandInGateway(int status, string body)
        {
            _status = status;
            _body = body;
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        public int Negotiates => Volatile.Read(ref _negotiates);

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { return; }
                _ = Task.Run(() => AnswerAsync(client));
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream);
                if (head.Contains("/director-stream/negotiate", StringComparison.Ordinal))
                    Interlocked.Increment(ref _negotiates);
                var body = Encoding.UTF8.GetBytes(_body);
                var reason = _status switch { 401 => "Unauthorized", 503 => "Service Unavailable", _ => "Status" };
                var response = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {_status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\n" +
                    $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response);
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
        }

        // The request line and headers; a negotiate carries no body.
        private static async Task<string> ReadHeadAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read));
                if (n == 0) break;
                read += n;
                if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            return Encoding.ASCII.GetString(buffer, 0, read);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }

    private static (GatewayStreamClient Client, GatewayConnectionMonitor Monitor) Dial(string url, string? teamName)
    {
        var monitor = new GatewayConnectionMonitor();
        monitor.Reset(gatewayConfigured: true);
        var client = new GatewayStreamClient(
            new GatewayConfig { Url = url, Token = "the-directors-key" },
            "director-1", "test", () => new List<SessionDto>(),
            monitor: monitor,
            teamName: () => teamName);
        client.Start();
        return (client, monitor);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task Tunnel_KeyRevokedForTeamRemoval_StopsInTheRemovedStateNamingTheTeam_AndStopsRetrying()
    {
        await using var gateway = new StandInGateway(401, RemovedBody);
        var (client, monitor) = Dial(gateway.Url, "Team B");
        await using var _ = client;

        Assert.True(await WaitForAsync(() => monitor.Status == GatewayConnectionStatus.KeyRefused, TimeSpan.FromSeconds(20)),
            $"the tunnel never stopped on the refused key; status={monitor.Status}");

        Assert.Equal(GatewayKeyRefusalKind.RemovedFromTeam, monitor.KeyRefusal!.Kind);
        Assert.Equal("Removed from Team B", monitor.KeyRefusal.ChipText);
        Assert.StartsWith("This Director was removed from Team B", monitor.FailureSummary);

        // Stopped for good: the client's long-outage restart would re-dial within 5 seconds; it must not.
        var negotiatesAtStop = gateway.Negotiates;
        await Task.Delay(TimeSpan.FromSeconds(7));
        Assert.Equal(negotiatesAtStop, gateway.Negotiates);
        Assert.Equal(GatewayConnectionStatus.KeyRefused, monitor.Status);
    }

    [Fact]
    public async Task Tunnel_KeyRevokedForAnotherReason_StopsAsAPlainRevoke_NeverAsARemoval()
    {
        await using var gateway = new StandInGateway(401, PlainRevokedBody);
        var (client, monitor) = Dial(gateway.Url, "Team B");
        await using var _ = client;

        Assert.True(await WaitForAsync(() => monitor.Status == GatewayConnectionStatus.KeyRefused, TimeSpan.FromSeconds(20)),
            $"the tunnel never stopped on the refused key; status={monitor.Status}");

        Assert.Equal(GatewayKeyRefusalKind.KeyRevoked, monitor.KeyRefusal!.Kind);
        Assert.DoesNotContain("removed", monitor.FailureSummary!, StringComparison.OrdinalIgnoreCase);
    }

    // What the main window paints from the monitor: where the removed Director said "Connecting..." for good, it now
    // says it was removed from its team, in red, with what to do in the tooltip.
    [Fact]
    public async Task StatusBox_AfterTheTunnelStopsOnATeamRemoval_SaysRemovedFromTheTeam()
    {
        await using var gateway = new StandInGateway(401, RemovedBody);
        var (client, monitor) = Dial(gateway.Url, "Team B");
        await using var _ = client;
        Assert.True(await WaitForAsync(() => monitor.Status == GatewayConnectionStatus.KeyRefused, TimeSpan.FromSeconds(20)));

        var inputs = MainWindow.StatusBoxInputs(monitor, boxGatewayConfigured: true, wasEverConnected: true,
            deviceKeyPresent: true, GatewayAccountSignInState.Unavailable);
        var box = GatewayStatusBoxPresenter.Describe(inputs, "gw.example", null);

        Assert.Equal(GatewayStatusBoxVisual.Red, box.Visual);
        Assert.Equal("Removed from Team B", box.ChipText);
        Assert.Contains("set it up again", box.Tooltip);
    }

    [Fact]
    public void StatusBox_WhileDialing_StillSaysConnecting()
    {
        var monitor = new GatewayConnectionMonitor();
        monitor.Reset(gatewayConfigured: true);

        var inputs = MainWindow.StatusBoxInputs(monitor, boxGatewayConfigured: true, wasEverConnected: false,
            deviceKeyPresent: true, GatewayAccountSignInState.Unknown);

        Assert.Equal("Connecting...", GatewayStatusBoxPresenter.Describe(inputs, "gw.example", null).ChipText);
    }

    /// <summary>
    /// A real SignalR hub on a loopback port, behind a front that answers the first negotiate with a 401 that is NOT the
    /// Gateway's credential answer - an empty body, or a proxy's HTML page - and lets every later request through.
    /// </summary>
    private sealed class HubBehindAFlakyFront : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _negotiates;

        public sealed class EmptyHub : Microsoft.AspNetCore.SignalR.Hub { }

        public HubBehindAFlakyFront(string firstAnswerBody, string contentType)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSignalR().AddMessagePackProtocol();
            _app = builder.Build();
            _app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Path.StartsWithSegments("/director-stream/negotiate")
                    && Interlocked.Increment(ref _negotiates) == 1)
                {
                    ctx.Response.StatusCode = 401;
                    if (firstAnswerBody.Length > 0)
                    {
                        ctx.Response.ContentType = contentType;
                        await ctx.Response.WriteAsync(firstAnswerBody);
                    }
                    return;
                }
                await next();
            });
            _app.MapHub<EmptyHub>("/director-stream");
        }

        public int Negotiates => Volatile.Read(ref _negotiates);

        public async Task<string> StartAsync()
        {
            await _app.StartAsync();
            return _app.Urls.Single();
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    // Review RM-F1: a 401 with no body is no evidence the GATEWAY refused the key. The tunnel keeps dialing, stays
    // "Connecting...", and connects as soon as the next attempt gets through.
    [Fact]
    public async Task Tunnel_EmptyUnauthorized_KeepsRetrying_AndRecoversWhenTheNextAttemptSucceeds()
    {
        await using var front = new HubBehindAFlakyFront("", "text/plain");
        var (client, monitor) = Dial(await front.StartAsync(), "Team B");
        await using var _ = client;

        Assert.True(await WaitForAsync(() => monitor.Status == GatewayConnectionStatus.Connected, TimeSpan.FromSeconds(20)),
            $"the tunnel did not recover after an empty 401; status={monitor.Status}, negotiates={front.Negotiates}");
        Assert.True(front.Negotiates >= 2);
        Assert.Null(monitor.KeyRefusal);
    }

    // Review RM-F1: a proxy's HTML 401 is not the Gateway's answer either.
    [Fact]
    public async Task Tunnel_HtmlUnauthorized_KeepsRetrying_AndRecoversWhenTheNextAttemptSucceeds()
    {
        await using var front = new HubBehindAFlakyFront("<html><body><h1>401 Authorization Required</h1></body></html>", "text/html");
        var (client, monitor) = Dial(await front.StartAsync(), "Team B");
        await using var _ = client;

        Assert.True(await WaitForAsync(() => monitor.Status == GatewayConnectionStatus.Connected, TimeSpan.FromSeconds(20)),
            $"the tunnel did not recover after an HTML 401; status={monitor.Status}, negotiates={front.Negotiates}");
        Assert.True(front.Negotiates >= 2);
        Assert.Null(monitor.KeyRefusal);
    }

    // The Gateway down behind a proxy is a network problem, not a refusal: still "Connecting...", still dialing.
    [Fact]
    public async Task Tunnel_ServerError_StaysConnectingAndKeepsRetrying()
    {
        await using var gateway = new StandInGateway(503, "{\"error\":\"down\"}");
        var (client, monitor) = Dial(gateway.Url, "Team B");
        await using var _ = client;

        Assert.True(await WaitForAsync(() => gateway.Negotiates >= 2, TimeSpan.FromSeconds(15)),
            $"the tunnel did not retry after a server error; negotiates={gateway.Negotiates}");
        Assert.Equal(GatewayConnectionStatus.Connecting, monitor.Status);
        Assert.Null(monitor.KeyRefusal);
    }
}
