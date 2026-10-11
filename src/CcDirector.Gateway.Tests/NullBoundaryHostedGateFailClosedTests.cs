using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tenant-boundary hardening (release 2026-07-31, the census sweep's ruling on the six adjacent sites):
/// the hosted-only gates in <c>GatewayEndpoints.Map</c> that used to read <c>tenantBoundary?.IsHosted ==
/// true</c> now gate on <see cref="GatewayHostedMode.IsHosted"/> - the PROCESS-level hosted flag - so a
/// literal <c>null!</c> boundary on a hosted process can no longer fail them OPEN.
///
/// Phase 2 (<see cref="OmittedTenantBoundaryFailClosedTests"/>) made the boundary parameter required and
/// the RESOLVERS fail closed; these six sites were the flagged leftovers that gated hosted-only DENIALS on
/// the nullable argument itself. With a null boundary on a hosted process, <c>?.</c> answered false and:
///
///   - <c>GET /healthz</c> computed and served the fleet-GLOBAL Director/session counts to an anonymous
///     probe - the exact cross-tenant aggregate the hosted branch exists to withhold;
///   - the four legacy same-machine discovery-plane legs (<c>POST /directors/register</c>,
///     <c>POST /directors/{id}/heartbeat</c>, <c>POST /directors/{id}/doorbell</c>,
///     <c>DELETE /directors/{id}/registration</c>) stayed OPEN - and the register leg is exactly the
///     Local-shadow path: a hosted caller fabricates a Local registration and can then act as that id;
///   - the <c>/sessions</c> pushed-roster intersection was skipped (that one is unobservable through the
///     route on a hosted process, because <c>ResolveReadTenant</c> already answers 403 before the branch
///     runs - it is converted for the same discipline but CANNOT be probed here; see the phase report).
///
/// MECHANICS, per <see cref="OmittedTenantBoundaryFailClosedTests"/>: a minimal host maps the production
/// <c>GatewayEndpoints.Map</c> with <c>tenantBoundary: null!</c> - the deliberately miswired configuration
/// the compiler now forces a caller to SPELL OUT - and the refusal is asserted positively (exact status,
/// exact message, property set as an allow-list). The self-host control runs the IDENTICAL null-boundary
/// host with hosted mode off and the plane serves, so the refusals are the hosted flag biting, not a dead
/// plane. The registry is asserted directly for the effect that matters (no shadow row created, the seeded
/// Local row surviving), never by response shape alone.
///
/// REVERT-PROVE: restore any of the six sites to <c>tenantBoundary?.IsHosted == true</c> and its test here
/// goes RED - the null-boundary hosted twin serves the counts, registers the shadow, heartbeats, records
/// the doorbell, or removes the registration again. The self-host control stays green in both directions.
///
/// The assembly runs sequentially (TestParallelization), so toggling CC_GATEWAY_HOSTED here is safe; it is
/// restored per helper.
/// </summary>
public sealed class NullBoundaryHostedGateFailClosedTests
{
    /// <summary>The exact refusal every discovery-plane leg emits on hosted. Asserted as the full payload.</summary>
    private const string PlaneDenyMessage = "the same-machine HTTP discovery plane is not available on the hosted Gateway";

    private const string LocalSecretMachine = "local-partition-secret-machine";

    // ===== site 1: the /healthz fleet-count redaction ===============================================

    [Fact]
    public async Task Healthz_withholds_the_fleet_counts_on_hosted_even_when_the_boundary_is_null()
    {
        // The disclosure at stake: a Director row in the Local partition. Pre-fix, hosted + null boundary
        // took the SELF-HOST branch and served "directors": 1 to an anonymous probe - an aggregate over
        // every account's fleet.
        await using var host = await NullBoundaryHost.StartAsync(hosted: true, seedLocalDirector: true);

        using var resp = await host.Http.GetAsync("/healthz");
        var raw = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // ABSENT, not zero - asserted on the raw body exactly as HealthzTenantLeakTests does, because a
        // deserialized null cannot tell "field absent" from "field present and null".
        Assert.DoesNotContain("\"directors\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sessions\"", raw, StringComparison.Ordinal);
        // Liveness itself still serves - the redaction is the counts, never the probe.
        Assert.Equal("ok", JsonDocument.Parse(raw).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Healthz_still_reports_the_counts_on_selfhost_with_the_same_null_boundary()
    {
        // The control that pins the gate to the HOSTED FLAG: the identical null-boundary host, hosted mode
        // off, and the seeded Local Director is counted. Green in both directions of the revert proof.
        await using var host = await NullBoundaryHost.StartAsync(hosted: false, seedLocalDirector: true);

        using var resp = await host.Http.GetAsync("/healthz");
        var raw = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(raw).RootElement.GetProperty("directors").GetInt32());
    }

    // ===== sites 2-5: the four legacy discovery-plane legs ==========================================

    [Fact]
    public async Task Register_is_refused_on_hosted_with_a_null_boundary_and_no_local_shadow_is_created()
    {
        await using var host = await NullBoundaryHost.StartAsync(hosted: true, seedLocalDirector: false);

        using var resp = await host.Http.PostAsync("/directors/register", RegistrationBody("shadow-dir"));
        await AssertPlaneRefusal(resp);

        // The breach the deny exists to stop, asserted at the store: no Local-shadow registration was
        // written. Pre-fix this is exactly what a hosted caller obtained with a null boundary.
        Assert.Empty(host.Registry.ListDirectors(TenantId.Local));
    }

    [Fact]
    public async Task Heartbeat_doorbell_and_unregister_are_refused_on_hosted_with_a_null_boundary_and_the_local_row_survives()
    {
        // One seeded Local registration; all three by-id legs against it. Each must meet the exact plane
        // refusal, and the row must be byte-alive afterwards - the unregister leg pre-fix REMOVED it.
        await using var host = await NullBoundaryHost.StartAsync(hosted: true, seedLocalDirector: true);

        using (var heartbeat = await host.Http.PostAsync($"/directors/{NullBoundaryHost.LocalDirectorId}/heartbeat", null))
            await AssertPlaneRefusal(heartbeat);

        using (var doorbell = await host.Http.PostAsync($"/directors/{NullBoundaryHost.LocalDirectorId}/doorbell",
                   JsonBody("{\"sessionId\":\"s-1\",\"newState\":\"Idle\"}")))
            await AssertPlaneRefusal(doorbell);

        using (var unregister = await host.Http.DeleteAsync($"/directors/{NullBoundaryHost.LocalDirectorId}/registration"))
            await AssertPlaneRefusal(unregister);

        var survivor = host.Registry.Get(TenantId.Local, NullBoundaryHost.LocalDirectorId);
        Assert.NotNull(survivor);
        Assert.Equal(LocalSecretMachine, survivor!.MachineName);
    }

    [Fact]
    public async Task The_discovery_plane_still_serves_selfhost_with_the_same_null_boundary()
    {
        // The self-host control for all four legs, on the identical null-boundary host with hosted mode
        // off: register creates the row, heartbeat and doorbell answer, unregister removes it. This is
        // what makes the refusals above the hosted flag biting rather than a plane that no longer exists.
        await using var host = await NullBoundaryHost.StartAsync(hosted: false, seedLocalDirector: false);

        using (var register = await host.Http.PostAsync("/directors/register", RegistrationBody("selfhost-dir")))
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Assert.NotNull(host.Registry.Get(TenantId.Local, "selfhost-dir"));

        using (var heartbeat = await host.Http.PostAsync("/directors/selfhost-dir/heartbeat", null))
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);

        using (var doorbell = await host.Http.PostAsync("/directors/selfhost-dir/doorbell",
                   JsonBody("{\"sessionId\":\"s-1\",\"newState\":\"Idle\"}")))
            Assert.Equal(HttpStatusCode.OK, doorbell.StatusCode);

        using (var unregister = await host.Http.DeleteAsync("/directors/selfhost-dir/registration"))
            Assert.Equal(HttpStatusCode.OK, unregister.StatusCode);
        Assert.Null(host.Registry.Get(TenantId.Local, "selfhost-dir"));
    }

    // ===== the refusal itself =======================================================================

    /// <summary>
    /// The plane refusal asserted positively: the concrete 403, the exact message only
    /// <c>LegacyDiscoveryPlaneUnavailable</c> emits, and the property set as an ALLOW-LIST of exactly one
    /// error field - a wrong-but-plausible 403 from anywhere else, or a body carrying extra data, fails.
    /// </summary>
    private static async Task AssertPlaneRefusal(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(HttpStatusCode.Forbidden == resp.StatusCode,
            $"expected the 403 plane refusal but got {(int)resp.StatusCode}; body was: {body}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(new[] { "error" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(PlaneDenyMessage, root.GetProperty("error").GetString());
    }

    // ===== helpers ==================================================================================

    private static StringContent RegistrationBody(string directorId)
        => JsonBody("{\"directorId\":\"" + directorId + "\",\"tailnetEndpoint\":\"http://127.0.0.1:1/\"," +
                    "\"machineName\":\"SHADOW\",\"pid\":1,\"version\":\"test\"}");

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// A minimal host mapping the production <see cref="GatewayEndpoints.Map"/> with <c>tenantBoundary:
    /// null!</c> - the miswire under test - over a real <see cref="DirectorRegistry"/> the tests assert
    /// directly. The hosted flag is the ONLY variable between the twin configurations, captured at Map
    /// time and restored immediately (the sites under test read <see cref="GatewayHostedMode"/> live, and
    /// the assembly runs sequentially).
    /// </summary>
    private sealed class NullBoundaryHost : IAsyncDisposable
    {
        internal const string LocalDirectorId = "local-director";

        private readonly WebApplication _app;
        private readonly string _dir;
        private readonly string? _priorHosted;
        public DirectorRegistry Registry { get; }
        public HttpClient Http { get; }

        private NullBoundaryHost(WebApplication app, HttpClient http, DirectorRegistry registry, string dir, string? priorHosted)
        {
            _app = app;
            Http = http;
            Registry = registry;
            _dir = dir;
            _priorHosted = priorHosted;
        }

        internal static async Task<NullBoundaryHost> StartAsync(bool hosted, bool seedLocalDirector)
        {
            var prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : null);

            var dir = Path.Combine(Path.GetTempPath(), "cc-nullgate-" + Guid.NewGuid().ToString("N"));
            var registry = new DirectorRegistry(Path.Combine(dir, "directors"));
            if (seedLocalDirector)
                registry.RegisterFromStream(LocalDirectorId, "local-partition-secret-machine", "local-user",
                    "1.0", 11, DateTime.UtcNow, TenantId.Local);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");
            GatewayEndpoints.Map(app, hosted, registry, "test", "test-token", tenantBoundary: null!, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown);
            await app.StartAsync();

            return new NullBoundaryHost(app, new HttpClient
            {
                BaseAddress = new Uri(app.Urls.First()),
                Timeout = TimeSpan.FromSeconds(30),
            }, registry, dir, prior);
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* cleanup */ }
        }
    }
}
