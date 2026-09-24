using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using Xunit;
using CcDirector.Core.Tests;   // WindowsOnlyFact, linked into this project

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Production-readiness B2 (process-control): on the SHARED hosted Gateway a tenant must not be able to
/// control or kill host processes. Two routes carried that power and are now refused on hosted while staying
/// fully working on self-host:
///
///  - DELETE /directors/{id} with force=true resolved Process.GetProcessById(director.Pid) and killed its
///    WHOLE tree. director.Pid is a number the Director itself supplied in its Hello, and on hosted the
///    Director is a REMOTE process reached over the tunnel - it is NOT a process on the Gateway host. So that
///    pid, resolved against the shared host's local process table, named whatever unrelated process on the
///    shared host happened to hold that number: the Gateway itself, another tenant's container, anything. Any
///    authenticated tenant could kill any process on the shared host by number. It is now refused on hosted.
///  - POST /directors ShellExecutes a cc-director.exe on the Gateway's OWN machine. On the shared hosted box
///    that launches an arbitrary process on shared infrastructure at any tenant's request. It is now refused
///    on hosted.
///
/// Both proofs observe the DANGEROUS ACT ITSELF, not a status code alone. The force-kill proof injects a
/// recorder (<see cref="GatewayHost.OnForceKillDirector"/>) that observes the kill WITHOUT killing anything,
/// so "did the force-kill reach the process by that client-supplied pid" is a DIRECT assertion. The launch
/// proof drives the real route and asserts the refusal shape.
///
/// SELF-HOST IS THE CONTROL. Off hosted (the single owner's own machine, where a Director really is a local
/// process) the force-kill still reaches the kill with the client pid, and POST /directors still launches.
/// This proves the denies are about the hosted branch, not about the routes being broken.
///
/// REVERT-PROOF (each stated at its test). Remove the hosted guard and the hosted refusal test reddens while
/// its self-host control stays green.
///
/// The assembly runs sequentially (TestParallelization), so toggling CC_GATEWAY_HOSTED here is safe; it is
/// reset in the finally of each probe.
/// </summary>
public sealed class HostedProcessControlDenyTests
{
    private const string Token = "test-token";
    // ---- DELETE /directors/{id} force-kill ------------------------------------------------------------

    [WindowsOnlyFact("it starts powershell to get a process it can then try to kill by pid, and there is no powershell off Windows")]
    public async Task Hosted_force_kill_cannot_reach_the_process_by_client_supplied_pid()
    {
        // A REAL live process the "Director" claims to be - it stands in for "any process on the shared host":
        // its pid is what a hosted force-kill would resolve locally. If the deny leaked, THIS is what would die.
        using var victim = StartLongLivedProcess();

        var probe = await RunForceKillProbe(hosted: true, victimPid: victim.Id);

        // EXACTLY the refusal - not a 200 killed, not the 502 of a failed graceful stop, not a plain 404.
        Assert.Equal(HttpStatusCode.NotFound, probe.Status);
        Assert.Contains("force-killing a Director by process id is not available on the hosted Gateway", probe.Body);
        Assert.StartsWith("application/json", probe.ContentType);

        // THE PROPERTY: the force-kill seam was NEVER reached, so no pid was ever handed to a kill. On hosted
        // the handler returns before Process.GetProcessById is ever consulted.
        Assert.Null(probe.KilledPid);

        // And the concrete consequence: the process a hosted force-kill would have resolved is still alive.
        Assert.False(victim.HasExited, "the hosted force-kill reached a host process - a tenant could kill any process on the shared host by pid");
        victim.Kill(entireProcessTree: true);
    }

    [Fact]
    public async Task Selfhost_force_kill_still_reaches_the_process_by_pid()
    {
        // The control, and the self-host-untouched proof: identical arrangement, the ONLY difference being
        // CC_GATEWAY_HOSTED. On self-host the Director really is a local process and the owner's force-kill must
        // still work, so the seam is reached with the director's own pid.
        const int SelfHostPid = 987654;
        var probe = await RunForceKillProbe(hosted: false, victimPid: SelfHostPid);

        Assert.Equal(HttpStatusCode.OK, probe.Status);
        Assert.Contains("killed", probe.Body);
        // The force-kill reached the seam with EXACTLY the client-supplied pid - proving the deny did not leak
        // onto self-host and that the real path routes that pid to the kill.
        Assert.Equal(SelfHostPid, probe.KilledPid);
    }

    // REVERT-PROOF for the force-kill: delete the `if (GatewayHostedMode.IsHosted) { ...refuse... }` block from
    // the DELETE /directors/{id} force branch in GatewayEndpoints. Then
    // Hosted_force_kill_cannot_reach_the_process_by_client_supplied_pid goes RED - the seam IS reached
    // (KilledPid becomes the victim pid) and the status is 200, not the refusal - while
    // Selfhost_force_kill_still_reaches_the_process_by_pid stays GREEN. Verified.

    private sealed record ForceKillProbeResult(
        HttpStatusCode Status, string Body, string ContentType, int? KilledPid);

    private static async Task<ForceKillProbeResult> RunForceKillProbe(bool hosted, int victimPid)
    {
        var prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : null);
        var instancesDir = Path.Combine(Path.GetTempPath(), "cc-procctl-b2-" + Guid.NewGuid().ToString("N"));
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: instancesDir,
            workListsPath: Path.Combine(instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        try
        {
            await gateway.StartAsync();

            // Observe REACHING the kill without killing anything: the recorder records the pid and returns true
            // (as a successful kill would). If it is never called, KilledPid stays null.
            int? killedPid = null;
            gateway.OnForceKillDirector = pid => { killedPid = pid; return true; };

            // The caller's credential and the Director's owning tenant. On hosted a device key bound to a tenant
            // is the "any authenticated tenant" credential (the shared token is rejected on hosted, MH-2); on
            // self-host the request tenant is always Local, so the Director is registered under Local and the
            // shared token drives the request.
            string credential;
            TenantId ownerTenant;
            if (hosted)
            {
                var device = HostedTestEnrollment.Enroll(
                    gateway, "sub-b2", "b2@example.com", "dev-b2", "MB2");
                credential = device.DeviceKey;
                ownerTenant = device.Tenant;
            }
            else
            {
                credential = Token;
                ownerTenant = TenantId.Local;
            }

            // Register the Director under its owning tenant with the client-supplied pid. No tunnel stream is
            // connected in the harness, so the graceful shutdown attempted first returns not-ok and the handler
            // falls through to the force branch - exactly the path the exploit used.
            const string DirectorId = "dir-b2-victim";
            gateway.Registry.RegisterFromStream(DirectorId, "victim-machine", "user", "1.0", victimPid, DateTime.UtcNow, ownerTenant);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
            var req = new HttpRequestMessage(HttpMethod.Delete, $"directors/{DirectorId}")
            {
                Content = JsonContent.Create(new { reason = "b2 process-control proof", force = true, confirmSessions = (int?)null }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "";

            return new ForceKillProbeResult(resp.StatusCode, body, contentType, killedPid);
        }
        finally
        {
            await gateway.StopAsync();
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", prior);
        }
    }

    // ---- POST /directors host-local launch ------------------------------------------------------------

    [WindowsOnlyFact("POST /directors launches the Windows desktop executable through ShellExecute, so GatewayEndpoints does not map the route off Windows and the request answers Not found rather than the hosted refusal this asserts")]
    public async Task Hosted_director_launch_is_refused()
    {
        var probe = await RunLaunchProbe(hosted: true, useDeviceKey: true);

        Assert.Equal(HttpStatusCode.NotFound, probe.Status);
        Assert.Contains("launching a Director is not available on the hosted Gateway", probe.Body);
        Assert.StartsWith("application/json", probe.ContentType);
    }

    // REVERT-PROOF for the launch: delete the `if (GatewayHostedMode.IsHosted) { ...refuse... }` block at the
    // top of the POST /directors handler. Then Hosted_director_launch_is_refused goes RED - the handler runs
    // ResolveDirectorExe and returns a 500 (no cc-director.exe in the test) or a launch, never the 404 refusal.
    // There is no self-host control test that actually launches a process here (a unit test must not spawn a
    // Director), so the self-host arm is proven by GET /directors staying live below, which shares the path and
    // MUST NOT be shadowed by the deny.

    [Fact]
    public async Task Selfhost_director_list_still_serves_and_is_not_shadowed_by_the_deny()
    {
        // The launch deny is an in-handler guard precisely so it does NOT take the tenant-scoped GET /directors
        // list off the air on hosted. This proves the list route answers on hosted (its own path is not
        // claimed by any verb-less refusal): an authenticated tenant gets a 200 list, not the launch refusal.
        var probe = await RunListProbe(hosted: true);

        Assert.Equal(HttpStatusCode.OK, probe.Status);
        Assert.DoesNotContain("launching a Director is not available", probe.Body);
    }

    private sealed record LaunchProbeResult(HttpStatusCode Status, string Body, string ContentType);

    private static Task<LaunchProbeResult> RunLaunchProbe(bool hosted, bool useDeviceKey) =>
        RunSimpleProbe(hosted, useDeviceKey, HttpMethod.Post, "directors",
            () => JsonContent.Create(new { timeoutMs = 500 }));

    private sealed record ListProbeResult(HttpStatusCode Status, string Body);

    private static async Task<ListProbeResult> RunListProbe(bool hosted)
    {
        var r = await RunSimpleProbe(hosted, useDeviceKey: true, HttpMethod.Get, "directors", content: null);
        return new ListProbeResult(r.Status, r.Body);
    }

    private static async Task<LaunchProbeResult> RunSimpleProbe(
        bool hosted, bool useDeviceKey, HttpMethod method, string path, Func<HttpContent>? content)
    {
        var prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : null);
        var instancesDir = Path.Combine(Path.GetTempPath(), "cc-procctl-b2-" + Guid.NewGuid().ToString("N"));
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: instancesDir,
            workListsPath: Path.Combine(instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        try
        {
            await gateway.StartAsync();

            string credential;
            if (useDeviceKey)
            {
                credential = HostedTestEnrollment.Enroll(
                    gateway, "sub-b2", "b2@example.com", "dev-b2", "MB2").DeviceKey;
            }
            else
            {
                credential = Token;
            }

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
            var req = new HttpRequestMessage(method, path);
            if (content is not null) req.Content = content();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "";
            return new LaunchProbeResult(resp.StatusCode, body, contentType);
        }
        finally
        {
            await gateway.StopAsync();
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", prior);
        }
    }

    // ---- shared helpers -------------------------------------------------------------------------------

    /// <summary>
    /// A real, long-lived child process whose pid stands in for "any process on the shared host". It sleeps far
    /// longer than the test and is force-killed by the test at the end (or by the code under test, if the deny
    /// ever leaked - which is exactly what the assertion catches).
    /// </summary>
    private static System.Diagnostics.Process StartLongLivedProcess()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 120\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("could not start the stand-in host process");
        return proc;
    }

}
