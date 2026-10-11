using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Production-readiness B2: POST /shutdown must be REFUSED on the hosted Gateway.
///
/// The route triggers a PROCESS-WIDE shutdown of the whole Gateway. On self-host that is correct - the single
/// owner's self-update helper POSTs it so the process exits and the exe unlocks. But on the HOSTED Gateway the
/// process is SHARED infrastructure serving every tenant, and the route was mapped with NO hosted refusal and
/// NO owner check - so ANY authenticated tenant's device key could POST /shutdown and take the Gateway down for
/// every other tenant at once. It is now refused on hosted through the shared refusal primitive
/// (<see cref="Gateway.Tenancy.HostedRouteDeny"/>), the same boundary #1904 adopted for /vault: on hosted a
/// verb-less refusal is mapped in place of the handler and the handler is never bound.
///
/// The proof observes REACHING THE HANDLER, not a status code alone: <see cref="GatewayHost.OnShutdownRequested"/>
/// is wired to a recorder that flips a flag WITHOUT tearing the host down, so a request that reaches the real
/// handler flips it and a request that meets the refusal does not. This is what makes "the shared Gateway was
/// not asked to shut down" a direct assertion.
///
/// Revert-prove: point the mapping back at the ungrouped builder (<c>app.MapPost("/shutdown", ...)</c>) and
/// <see cref="Hosted_shutdown_is_refused_and_the_handler_is_never_reached"/> goes RED - on hosted the handler
/// maps again, answers 200 { shuttingDown = true }, and the recorder flips.
///
/// Self-host is the control: <see cref="Selfhost_shutdown_still_reaches_the_handler"/> proves the deny is about
/// the hosted branch and not about /shutdown being broken - off hosted the shared token still drives a real
/// shutdown request through to the handler.
/// </summary>
public sealed class HostedShutdownDenyTests
{
    private const string Token = "test-token";

    [Fact]
    public async Task Hosted_shutdown_is_refused_and_the_handler_is_never_reached()
    {
        // An authenticated NON-OWNER: a device key bound to some tenant. On hosted the shared machine token is
        // rejected (MH-2), so this bound device key is exactly the "any authenticated tenant" credential the
        // exploit used. The request passes the auth gate and reaches routing - where the refusal, not the
        // handler, answers it.
        var probe = await RunShutdownProbe(hosted: true, useDeviceKey: true);

        // NOT a 200 shuttingDown, NOT a 501, NOT the fallback's plain 404 - EXACTLY the refusal, so the assertion
        // pins the deny and not a Gateway that merely lacks the route.
        Assert.Equal(HttpStatusCode.NotFound, probe.Status);
        Assert.Contains("shutdown is not available on the hosted Gateway", probe.Body);
        Assert.StartsWith("application/json", probe.ContentType);

        // The whole point: the SHARED Gateway was never asked to shut down. The handler is never mapped on
        // hosted, so nothing was even queued behind the refusal.
        Assert.False(probe.ShutdownRequested,
            "the hosted Gateway reached its process-wide shutdown handler - a single tenant could kill it for everyone");
    }

    [Fact]
    public async Task Selfhost_shutdown_still_reaches_the_handler()
    {
        // The control, and the self-host-untouched proof: identical arrangement, the ONLY difference being
        // the hosted value, and the self-update helper's POST /shutdown still drives a real shutdown request
        // through to the handler with the shared token (self-host's credential).
        var probe = await RunShutdownProbe(hosted: false, useDeviceKey: false);

        Assert.Equal(HttpStatusCode.OK, probe.Status);
        Assert.Contains("shuttingDown", probe.Body);
        Assert.True(probe.ShutdownRequested,
            "self-host POST /shutdown did not reach the shutdown handler - the deny leaked onto self-host");
    }

    private sealed record ShutdownProbeResult(
        HttpStatusCode Status, string Body, string ContentType, bool ShutdownRequested);

    private static async Task<ShutdownProbeResult> RunShutdownProbe(bool hosted, bool useDeviceKey)
    {
        var instancesDir = Path.Combine(Path.GetTempPath(), "cc-shutdown-b2-" + Guid.NewGuid().ToString("N"));
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: instancesDir,
            workListsPath: Path.Combine(instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = hosted });
        try
        {
            await gateway.StartAsync();

            // Observe REACHING the handler without tearing the host down: the recorder just flips a flag.
            var shutdownRequested = 0;
            gateway.OnShutdownRequested = () => Interlocked.Exchange(ref shutdownRequested, 1);

            // A device key bound to a tenant is the hosted "authenticated non-owner" credential; the shared
            // token is self-host's credential (and is rejected on hosted).
            var deviceKey = HostedTestEnrollment.Enroll(
                gateway, "sub-b2", "b2@example.com", "dev-b2", "MB2").DeviceKey;
            var credential = useDeviceKey ? deviceKey : Token;

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
            var req = new HttpRequestMessage(HttpMethod.Post, "shutdown");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "";

            // The handler answers first and hands off to the shutdown recorder AFTER a 250ms delay (it lets the
            // 200 flush before teardown). Wait past that window so a handler that WAS reached has flipped the
            // flag - which is what a revert would do on hosted.
            await Task.Delay(1000);

            return new ShutdownProbeResult(
                resp.StatusCode, body, contentType, Volatile.Read(ref shutdownRequested) == 1);
        }
        finally
        {
            await gateway.StopAsync();
            // Deliberately NOT deleting instancesDir - a background watcher delete event can land after teardown
            // and reach a disposed store (see HealthzTenantLeakTests). The path is a unique temp dir the OS
            // reclaims.
        }
    }

}
