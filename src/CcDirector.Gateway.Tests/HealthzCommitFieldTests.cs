using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// <c>/healthz</c> reports the exact commit the running image was built from (the COCKPIT_COMMIT the
/// Dockerfile bakes into the container as an ENV), so the deploy pipeline can tell the OLD container apart
/// from the NEW one during a redeploy. Without this the pipeline's readiness check accepts the still-running
/// old container's 200 and exits BEFORE the new image is actually serving - it believes the outage is ~0
/// when the real external outage was ~95s (see deploy-ledger baseline rep 1). The pipeline now polls until
/// this field equals the commit it just shipped, which is the only honest "the new image is live" signal.
///
/// Reported on BOTH the hosted and self-host branches of the handler: build identity is identical for every
/// tenant, so it carries no per-tenant fact and is safe on the public hosted probe.
///
/// The assembly runs sequentially (TestParallelization), so setting COCKPIT_COMMIT here is safe.
/// </summary>
public sealed class HealthzCommitFieldTests
{
    private const string Token = "test-token";

    [Theory]
    [InlineData(true)]   // hosted branch
    [InlineData(false)]  // self-host branch
    public async Task Healthz_reports_the_built_commit_when_stamped(bool hosted)
    {
        var (health, rawJson) = await ProbeHealthz(hosted, commitEnv: "abc1234");

        // The precise deployed build. This is what the deploy pipeline polls for; with the field dropped
        // from the handler it goes absent and the pipeline can no longer distinguish the new image, so this
        // pins the change on both tenancy branches.
        Assert.Equal("abc1234", health.Commit);
        Assert.Contains("\"commit\"", rawJson);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Healthz_omits_the_commit_when_unstamped(bool hosted)
    {
        // No COCKPIT_COMMIT (a local dev run). Absent, not empty-string: HealthDto omits a null Commit, so a
        // probe reading it can tell "no build stamp" from "built at commit ''" - the same honesty rule the
        // Directors/Sessions counts follow.
        var (health, rawJson) = await ProbeHealthz(hosted, commitEnv: null);

        Assert.Null(health.Commit);
        Assert.DoesNotContain("\"commit\"", rawJson);
    }

    private static async Task<(HealthDto Health, string RawJson)> ProbeHealthz(bool hosted, string? commitEnv)
    {
        var priorCommit = Environment.GetEnvironmentVariable("COCKPIT_COMMIT");
        Environment.SetEnvironmentVariable("COCKPIT_COMMIT", commitEnv);
        var instancesDir = Path.Combine(Path.GetTempPath(), "cc-hz-commit-" + Guid.NewGuid().ToString("N"));
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: instancesDir,
            workListsPath: Path.Combine(instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = hosted });
        try
        {
            await gateway.StartAsync();

            // NO credential: /healthz is public.
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
            var resp = await http.GetAsync("healthz");
            resp.EnsureSuccessStatusCode();
            var raw = await resp.Content.ReadAsStringAsync();
            var dto = System.Text.Json.JsonSerializer.Deserialize<HealthDto>(raw,
                          new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                      ?? throw new InvalidOperationException("healthz returned no body");
            return (dto, raw);
        }
        finally
        {
            await gateway.StopAsync();
            Environment.SetEnvironmentVariable("COCKPIT_COMMIT", priorCommit);
            // Deliberately NOT deleting instancesDir (see HealthzTenantLeakTests for why a delete here can
            // crash the whole test process via a late FileSystemWatcher event). The OS reclaims the temp dir.
        }
    }

}
