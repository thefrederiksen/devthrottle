using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue 495 endpoint proof through the running Gateway. A hosted request must finish at the hosted branch:
/// it must not reach the Tailscale command boundary, must not return shared tailnet peers, and must carry the
/// Gateway's finished connection verdict. The self-host control proves the collector and direct-versus-relay
/// behavior remain wired on the same production route.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedNetworkDiagEndpointTests
{
    [Fact]
    public async Task GetNetworkDiag_Hosted_DoesNotCollectTailscaleAndReturnsConnectedVerdictWithoutPeers()
    {
        var collected = 0;
        await WithGateway(hosted: true, () =>
        {
            collected++;
            return SharedTailnetDiagnostic(direct: false);
        }, async http =>
        {
            using var response = await SendAsync(http, forwardedFor: "203.0.113.42");
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = body.RootElement;

            Assert.Equal(0, collected);
            Assert.False(root.GetProperty("tailscaleAvailable").GetBoolean());
            Assert.Empty(root.GetProperty("peers").EnumerateArray());
            Assert.Equal(
                "Tailscale peer diagnostics do not apply to the hosted Gateway's public internet connection.",
                Assert.Single(root.GetProperty("notes").EnumerateArray()).GetString());
            var verdict = root.GetProperty("connectionVerdict");
            Assert.Equal("green", verdict.GetProperty("level").GetString());
            Assert.Equal("Connected", verdict.GetProperty("label").GetString());
            Assert.Equal("Connected to the hosted Gateway.", verdict.GetProperty("detail").GetString());
        });
    }

    [Fact]
    public async Task GetNetworkDiag_SelfHosted_CollectsTailscaleAndReturnsDirectVerdict()
    {
        var collected = 0;
        await WithGateway(hosted: false, () =>
        {
            collected++;
            return SharedTailnetDiagnostic(direct: true);
        }, async http =>
        {
            using var response = await SendAsync(http, forwardedFor: "100.86.144.11");
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = body.RootElement;

            Assert.Equal(1, collected);
            Assert.True(root.GetProperty("tailscaleAvailable").GetBoolean());
            Assert.Single(root.GetProperty("peers").EnumerateArray());
            var verdict = root.GetProperty("connectionVerdict");
            Assert.Equal("green", verdict.GetProperty("level").GetString());
            Assert.Equal("Fast", verdict.GetProperty("label").GetString());
        });
    }

    [Fact]
    public async Task GetNetworkDiag_SelfHostedPersistentRelay_WarmsBeforeReturningSlowVerdict()
    {
        var collected = 0;
        await WithGateway(hosted: false, () =>
        {
            collected++;
            return SharedTailnetDiagnostic(direct: false);
        }, async http =>
        {
            using var firstResponse = await SendAsync(http, forwardedFor: "100.86.144.11");
            firstResponse.EnsureSuccessStatusCode();
            using var firstBody = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
            var firstVerdict = firstBody.RootElement.GetProperty("connectionVerdict");
            Assert.Equal("amber", firstVerdict.GetProperty("level").GetString());
            Assert.Equal("Warming up", firstVerdict.GetProperty("label").GetString());

            using var secondResponse = await SendAsync(http, forwardedFor: "100.86.144.11");
            secondResponse.EnsureSuccessStatusCode();
            using var secondBody = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
            var secondVerdict = secondBody.RootElement.GetProperty("connectionVerdict");
            Assert.Equal("red", secondVerdict.GetProperty("level").GetString());
            Assert.Equal("Slow", secondVerdict.GetProperty("label").GetString());
            Assert.Equal(2, collected);
        });
    }

    private static TailscaleDiagnostics.NetworkDiag SharedTailnetDiagnostic(bool direct) => new()
    {
        TailscaleAvailable = true,
        Peers =
        {
            new TailscaleDiagnostics.PeerDiag
            {
                Name = "another-tenant-device.tailnet.example",
                TailscaleIp = "100.86.144.11",
                Os = "android",
                Online = true,
                Direct = direct,
                Path = direct ? "192.168.1.15:52091" : "DERP(tor)",
                LatencyMs = direct ? 11 : 84,
            },
        },
    };

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "diag/network");
        request.Headers.TryAddWithoutValidation("X-Test-Remote-Address", forwardedFor);
        return await http.SendAsync(request);
    }

    private static async Task WithGateway(
        bool hosted,
        Func<TailscaleDiagnostics.NetworkDiag> collectNetworkDiagnostic,
        Func<HttpClient, Task> assertion)
    {
        var priorHosted = Environment.GetEnvironmentVariable(GatewayHostedMode.HostedEnvVar);
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, hosted ? "1" : null);
        var instancesDirectory = Path.Combine(Path.GetTempPath(), "cc-netstatus-" + Guid.NewGuid().ToString("N"));
        WebApplication? app = null;
        DirectorRegistry? registry = null;
        var started = false;
        try
        {
            var builder = WebApplication.CreateBuilder();
            // Issue #2161: bind an operating-system-assigned port; the number is read back after start.
            builder.WebHost.UseUrls($"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}");
            app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (System.Net.IPAddress.TryParse(
                        context.Request.Headers["X-Test-Remote-Address"].ToString(), out var remoteAddress))
                    context.Connection.RemoteIpAddress = remoteAddress;
                await next();
            });
            registry = new DirectorRegistry(instancesDirectory);
            GatewayEndpoints.Map(
                app, hosted,
                registry,
                version: "test",
                token: "test-token",
                // The boundary is required and non-nullable now (finding I1-01), so this harness wires the
                // REAL boundary for the mode it just selected: the hosted arm needs the per-account ambient
                // context (a hosted process constructing the single-tenant one fails loud by design), and
                // the self-host arm gets the SingleTenantContext, which always resolves Local.
                tenantBoundary: hosted
                    ? new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                        new CcDirector.Core.Tenancy.AsyncLocalTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: hosted)
                    : new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                        new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: hosted),
                collectNetworkDiagnostic: collectNetworkDiagnostic, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown);
            await app.StartAsync();
            var port = BoundPort.Of(app);
            started = true;
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            await assertion(http);
        }
        finally
        {
            if (app is not null)
            {
                if (started)
                    await app.StopAsync();
                await app.DisposeAsync();
            }
            registry?.Dispose();
            Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, priorHosted);
        }
    }
}
