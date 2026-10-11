using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// CR-6, missions half - the WIRING proof that completes <see cref="MissionRouteTenantScopingTests"/>.
///
/// That class proves the per-request partition over the mapped routes: list, resolve-by-id, create
/// ownership, and the parent reference, each refusal paired with a permitted control. This class proves
/// the three properties those tests cannot see:
///
///  1. QUARANTINE ON HOSTED (Architect ruling: quarantine on hosted, adopt-as-Local on self-host,
///     nothing deleted). The hosted <see cref="GatewayHost"/> must construct its mission store with
///     <c>adoptUnattributedAs: null</c>, so a row written before missions carried an owner matches NO
///     tenant: never listed, never resolvable, never deletable - and still on disk afterwards. This is
///     proven through the REAL host construction (GatewayHost.cs decides from its options' Hosted value),
///     not by constructing a store with the right argument in the test, which would prove only that the
///     store honors an argument nobody is proven to pass.
///  2. THE DELETE LEG of the exit row "tenant A cannot list, resolve, or delete tenant B's missions".
///     No HTTP delete route exists on the Gateway (MissionStore.Delete has no production route), so the
///     surface a future route would go through is the store seam the existing routes already use -
///     <see cref="GatewayHost.Missions"/> - and the property is proven there, with a destructibility
///     control proving the same operation IS capable of deleting when the owner runs it.
///  3. THE COMM-QUEUE ROW in the same two-tenant setting: with two enrolled tenants standing, neither
///     can read the operator's comm queue - the refusal is the exact
///     <see cref="Api.CommQueueEndpoints.RefusalMessage"/> payload, not a vanished route (the self-host
///     control for that family is <see cref="SelfHostCommQueueControlTests"/>).
///
/// The mirror half of the quarantine is <see cref="SelfHostMissionAdoptionControlTests"/>: the SAME
/// seeded file, hosted mode off, and the legacy row is the single owner's - listed and resolvable. One
/// direction alone cannot tell quarantine from a store that lost the row.
///
/// Revert-prove (the negative control the phase report records): in GatewayHost.cs change the store
/// construction to the pre-#1039 single-tenant shape - <c>adoptUnattributedAs: Core.Tenancy.TenantId.Local</c>
/// unconditionally - and the quarantine tests here go RED (the legacy row surfaces to nobody's benefit:
/// it is Local's, and Local is exactly the shared partition hosted must never serve). Feed the mission
/// routes a constant tenant instead of the caller's and MissionRouteTenantScopingTests reddens. Map the
/// comm-queue on the ungrouped builder and the comm-queue row here reddens with a 200.
/// </summary>
public sealed class HostedMissionPartitionWiringTests : IAsyncLifetime
{
    private const string Token = "test-token";

    /// <summary>Free text a person typed - the thing an unpartitioned list disclosed. Distinctive on purpose.</summary>
    private const string LegacyMissionName = "Legacy pre-partition mission 77c3 - Contoso payroll rescue";

    private static readonly Guid LegacyMissionId = Guid.Parse("b8a31c2e-7d44-4f0a-9c66-2f5d1e8a9b01");

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private HostedTestDevice _deviceA;
    private HostedTestDevice _deviceB;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-cr6-wiring-" + Guid.NewGuid().ToString("N"));
    private string _missionsPath = "";

    public async Task InitializeAsync()
    {
        // The legacy row exists BEFORE the host starts, exactly as a pre-#1039 file would: written by a
        // Gateway that stamped no owner. Seeded on disk rather than through the store, because the store
        // no longer has an unscoped write to create one with.
        _missionsPath = Path.Combine(_instancesDir, "missions.json");
        SeedUnattributedMission(_missionsPath);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            missionsPath: _missionsPath,
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _deviceA = HostedTestEnrollment.Enroll(
            _gateway, "sub-wiring-a", "wiring-a@example.com", "dev-wa", "MACHINE-WA");
        _deviceB = HostedTestEnrollment.Enroll(
            _gateway, "sub-wiring-b", "wiring-b@example.com", "dev-wb", "MACHINE-WB");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task An_unattributed_mission_is_quarantined_on_hosted_and_stays_on_disk()
    {
        // Each tenant holds a real mission of its own, so "the legacy row is absent" is asserted against
        // NON-EMPTY lists - an empty list satisfies any absence claim and proves nothing.
        var aMission = await CreateMission(_deviceA.DeviceKey, "A's own mission beside the legacy row");
        var bMission = await CreateMission(_deviceB.DeviceKey, "B's own mission beside the legacy row");

        var (aStatus, aBody) = await GetRaw("missions", _deviceA.DeviceKey);
        var (bStatus, bBody) = await GetRaw("missions", _deviceB.DeviceKey);
        Assert.Equal(HttpStatusCode.OK, aStatus);
        Assert.Equal(HttpStatusCode.OK, bStatus);

        // The permitted half first: each list really serves, and serves the caller's own record.
        Assert.Contains(aMission.MissionId, Parse(aBody).Select(m => m.MissionId));
        Assert.Contains(bMission.MissionId, Parse(bBody).Select(m => m.MissionId));

        // The quarantine: the legacy row is in NEITHER list, by id and by its human-typed name.
        Assert.DoesNotContain(LegacyMissionId, Parse(aBody).Select(m => m.MissionId));
        Assert.DoesNotContain(LegacyMissionId, Parse(bBody).Select(m => m.MissionId));
        Assert.DoesNotContain(LegacyMissionName, aBody, StringComparison.Ordinal);
        Assert.DoesNotContain(LegacyMissionName, bBody, StringComparison.Ordinal);

        // Not resolvable by id either - for anyone. 404, indistinguishable from a mission that never
        // existed, so the id cannot even be probed.
        Assert.Equal(HttpStatusCode.NotFound, (await GetRaw($"missions/{LegacyMissionId}", _deviceA.DeviceKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetRaw($"missions/{LegacyMissionId}", _deviceB.DeviceKey)).Status);

        // The wiring's DIRECT observable: on hosted the legacy row is owned by NOBODY - not even the
        // Local partition adopts it. Without this line, a GatewayHost that constructed the store the
        // pre-#1039 single-tenant way (adoptUnattributedAs: Local) on a hosted process would pass every
        // assertion above, because a Local-owned row is just as absent from tenant A's and B's lists as
        // a quarantined one. This is the assertion that reddens for that exact miswire.
        Assert.Null(_gateway.Missions.Get(TenantId.Local, LegacyMissionId));

        // QUARANTINED, NOT DESTROYED: after every refusal above, the row is still in the file for the
        // deployment operator to inspect or remove out of band. Ruling: nothing is deleted.
        var fileAfter = File.ReadAllText(_missionsPath);
        Assert.Contains(LegacyMissionId.ToString(), fileAfter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LegacyMissionName, fileAfter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_tenants_mission_cannot_be_deleted_and_the_owners_delete_still_works()
    {
        // No HTTP delete route exists (MissionStore.Delete has no production caller), so the seam a
        // future route would call - the SAME store instance every mapped route uses,
        // GatewayHost.Missions - is where the property is proven.
        var bMission = await CreateMission(_deviceB.DeviceKey, "B's mission A will try to delete");

        // The refusal: A deleting B's mission by its true id removes nothing.
        Assert.False(_gateway.Missions.Delete(_deviceA.Tenant, bMission.MissionId),
            "tenant A deleted tenant B's mission through the store seam");

        // And B's record genuinely survived - asserted through the route, not the store that just
        // answered false.
        var (survivedStatus, survivedBody) = await GetRaw($"missions/{bMission.MissionId}", _deviceB.DeviceKey);
        Assert.Equal(HttpStatusCode.OK, survivedStatus);
        Assert.Contains("B's mission A will try to delete", survivedBody, StringComparison.Ordinal);

        // The quarantined legacy row is not deletable by anyone through this API either.
        Assert.False(_gateway.Missions.Delete(_deviceA.Tenant, LegacyMissionId));
        Assert.False(_gateway.Missions.Delete(_deviceB.Tenant, LegacyMissionId));

        // DESTRUCTIBILITY CONTROL: the identical call by the OWNER does delete - so the refusals above
        // stopped a capable operation, they were not a Delete that cannot delete anything.
        Assert.True(_gateway.Missions.Delete(_deviceB.Tenant, bMission.MissionId),
            "the owner's own delete no longer works - the refusal above proved nothing");
        Assert.Equal(HttpStatusCode.NotFound, (await GetRaw($"missions/{bMission.MissionId}", _deviceB.DeviceKey)).Status);
    }

    [Fact]
    public async Task Neither_tenant_can_read_the_comm_queue_on_hosted()
    {
        // The comm-queue row of the same two-tenant matrix family: with both tenants enrolled and
        // standing, each read is the EXACT hosted refusal - the same assertion, payload and status the
        // deny family's own tests pin, so this row grades FAIL-to-PASS rather than route-vanished.
        foreach (var key in new[] { _deviceA.DeviceKey, _deviceB.DeviceKey })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "comm-queue");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            await HostedCommQueueDenyTests.AssertBodyIsNothingButTheRefusal(await _http.SendAsync(req));
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------

    /// <summary>
    /// One row exactly as a pre-#1039 Gateway serialized it: PascalCase properties, no TenantId. Written
    /// with the same serializer settings the store reads with, so the seed cannot drift from the format.
    ///
    /// ParentMissionId is written on purpose even though Mission no longer HAS that property (nesting was
    /// removed on 2026-08-07). Every missions.json already on disk carries the key, so this row is what a
    /// real file looks like - and keeping it here proves those files still load rather than assuming it.
    /// Do not "tidy" it away.
    /// </summary>
    private static void SeedUnattributedMission(string missionsPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(missionsPath)!);
        var legacyRow = new[]
        {
            new
            {
                MissionId = LegacyMissionId,
                MissionName = LegacyMissionName,
                ParentMissionId = (Guid?)null,
                CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                TenantId = (string?)null,
            },
        };
        File.WriteAllText(missionsPath,
            JsonSerializer.Serialize(legacyRow, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static List<MissionDto> Parse(string body) =>
        JsonSerializer.Deserialize<List<MissionDto>>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<MissionDto>();

    private async Task<MissionDto> CreateMission(string deviceKey, string name)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "missions")
        {
            Content = JsonContent.Create(new NewMissionRequest { MissionName = name }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<MissionDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private async Task<(HttpStatusCode Status, string Body)> GetRaw(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        var resp = await _http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }
}

/// <summary>
/// The adopt-as-Local half of the ruling, and the control without which the quarantine is
/// indistinguishable from a store that lost the row: the SAME unattributed legacy row, hosted mode
/// explicitly OFF, and the single owner lists it, resolves it, and its file row is untouched. Self-host
/// has exactly one tenant by construction, so "the unattributed row is Local's" is a fact about the
/// deployment, not a guess about the row - which is precisely why the same row must be NOBODY's when
/// the file is shared.
/// </summary>
public sealed class SelfHostMissionAdoptionControlTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string LegacyMissionName = "Legacy pre-partition mission 77c3 - Contoso payroll rescue";
    private static readonly Guid LegacyMissionId = Guid.Parse("b8a31c2e-7d44-4f0a-9c66-2f5d1e8a9b01");

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-cr6-adopt-" + Guid.NewGuid().ToString("N"));
    private string _missionsPath = "";

    public async Task InitializeAsync()
    {
        _missionsPath = Path.Combine(_instancesDir, "missions.json");
        Directory.CreateDirectory(_instancesDir);
        // ParentMissionId is deliberate here - see SeedUnattributedMission. Mission no longer has that
        // property, but every missions.json on disk still carries the key, and this row proves such a
        // file still loads.
        File.WriteAllText(_missionsPath, JsonSerializer.Serialize(new[]
        {
            new
            {
                MissionId = LegacyMissionId,
                MissionName = LegacyMissionName,
                ParentMissionId = (Guid?)null,
                CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                TenantId = (string?)null,
            },
        }, new JsonSerializerOptions { WriteIndented = true }));

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            missionsPath: _missionsPath,
            streamMode: true,
            options: new GatewayHostOptions { Hosted = false });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task The_single_owner_lists_and_resolves_the_legacy_unattributed_mission()
    {
        var list = await _http.GetFromJsonAsync<List<MissionDto>>("missions");
        Assert.NotNull(list);
        Assert.Contains(list!, m => m.MissionId == LegacyMissionId && m.MissionName == LegacyMissionName);

        var byId = await _http.GetAsync($"missions/{LegacyMissionId}");
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        Assert.Contains(LegacyMissionName, await byId.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Adoption is a read-side ownership rule, not a rewrite: the row on disk still carries no
        // TenantId. (A rewrite would silently attribute history, which the ruling forbids.)
        using var doc = JsonDocument.Parse(File.ReadAllText(_missionsPath));
        var row = doc.RootElement.EnumerateArray().Single();
        Assert.True(row.GetProperty("TenantId").ValueKind == JsonValueKind.Null,
            "the self-host Gateway rewrote the legacy row's owner on disk");
    }
}
