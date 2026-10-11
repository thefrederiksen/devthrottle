using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hosted Multi-Tenancy (session-serving PR2): <c>/healthz</c> reports NO per-tenant facts on the hosted
/// Gateway.
///
/// <c>/healthz</c> is deliberately PUBLIC - it is the unauthenticated liveness probe the Director's
/// connectivity self-test, the gateway endpoint selector, and the settings gateway test all dial - so it
/// carries no credential and therefore has NO TENANT. Its Directors/Sessions counts are fleet-GLOBAL
/// aggregates, so on a hosted Gateway an anonymous caller was reading a count over EVERY account's Directors.
/// That is not fixable by making the count tenant-aware: a request with no tenant has no correct number to
/// print. So on hosted the aggregate is not computed at all - deny-by-default applies to metrics too.
///
/// POSITIVE CONTROL / self-host-untouched: the same handler on a NON-hosted Gateway still reports the counts,
/// which is what proves the hosted assertion is about the tenancy branch and not about a Gateway that simply
/// has no Directors registered.
///
/// Revert-prove: delete the hosted branch from the <c>/healthz</c> handler in
/// <c>GatewayEndpoints.Map</c> and <see cref="Hosted_healthz_reports_no_fleet_counts"/> goes RED - the hosted
/// probe reports the registered Director again. (The branch gated on the nullable <c>tenantBoundary</c>
/// argument until the 2026-07-31 tenant-boundary hardening; it now gates on the process-level hosted flag so
/// a null! boundary cannot reopen the aggregate - <see cref="NullBoundaryHostedGateFailClosedTests"/> pins
/// that direction.)
/// </summary>
public sealed class HealthzTenantLeakTests
{
    private const string Token = "test-token";

    [Fact]
    public async Task Hosted_healthz_reports_no_fleet_counts()
    {
        var (health, rawJson) = await ProbeHealthz(hosted: true);

        // ABSENT, not zero. The fields were plain ints before, so "not computed" serialized as 0 - which is
        // not a leak (0 is not the real count) but IS a false statement, and /healthz is what every Director
        // and endpoint selector dials, so a permanent 0 reads as a dead fleet. Assert on the RAW body: a
        // deserialized null cannot tell "field absent" from "field present and null".
        Assert.DoesNotContain("\"directors\"", rawJson);
        Assert.DoesNotContain("\"sessions\"", rawJson);

        // The Gateway HAS a registered Director (see ProbeHealthz) - the hosted probe simply must not say so.
        // This is the assertion that pins the change: with the hosted branch removed, the anonymous probe
        // reports that Director again.
        Assert.Equal("ok", health.Status);
        Assert.Null(health.Directors);
        Assert.Null(health.Sessions);
        // Liveness is still answered: a probe needs to know it is alive and what version it is.
        Assert.False(string.IsNullOrWhiteSpace(health.Version));
    }

    [Fact]
    public async Task Selfhost_healthz_still_reports_its_fleet_counts()
    {
        // POSITIVE CONTROL for the test above, and the self-host-untouched proof: identical arrangement, the
        // ONLY difference being the hosted value, and the counts are still served. This one deliberately does
        // NOT pin the changed line - it exists to prove the hosted assertion above is about the tenancy branch
        // and not about a Gateway that simply has no Directors to count.
        var (health, _) = await ProbeHealthz(hosted: false);

        Assert.Equal("ok", health.Status);
        Assert.Equal(1, health.Directors);
    }

    private static async Task<(HealthDto Health, string RawJson)> ProbeHealthz(bool hosted)
    {
        var instancesDir = Path.Combine(Path.GetTempPath(), "cc-hz-" + Guid.NewGuid().ToString("N"));
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: instancesDir,
            workListsPath: Path.Combine(instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = hosted });
        try
        {
            await gateway.StartAsync();
            gateway.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = "dir-health",
                TailnetEndpoint = "http://127.0.0.1:1/",
                MachineName = "MH",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            // NO credential: /healthz is public, which is exactly why it must not carry per-tenant facts.
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
            // Deliberately NOT deleting instancesDir. Registering a Director writes a discovery file there, and
            // deleting the directory raises a FileSystemWatcher delete event on a thread-pool thread that can
            // land AFTER the host is torn down - reaching a disposed database and killing the whole test
            // process (the run aborts mid-suite). The directory is a unique temp path the OS reclaims.
        }
    }

}
