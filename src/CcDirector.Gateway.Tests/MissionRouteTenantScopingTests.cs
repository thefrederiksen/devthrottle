using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// devthrottle_internal issue #1039: the Gateway-native mission surface scoped to the CALLER's own tenant,
/// proven over the REAL mapped routes and the REAL auth middleware, with two separately-enrolled accounts.
///
/// What was live on the deployed hosted image: <c>GET /missions</c> called <c>missions.List()</c> - no
/// tenant argument at all - so two accounts created minutes apart, sharing nothing, were served
/// BYTE-IDENTICAL mission lists. A mission name is free text a person typed, so the payload disclosed
/// other customers' project and people names. <c>GET /missions/{mid}</c> resolved by bare id and
/// <c>POST /missions</c> wrote with no owner, which is how the shared store came to hold several accounts'
/// records in the first place.
///
/// HOW THESE CASES ARE WRITTEN, and it matters more than the count:
///
///  - The assertion is the INVARIANT - every record served to a caller belongs to that caller - not "this
///    particular id is absent". The #1017 matrix nearly missed this leak because its cross-tenant check
///    looked for a DEVICE ID inside the response, and a mission is not keyed by one, so it passed while the
///    box handed over the whole list. A check that knows only one kind of identifier certifies every
///    surface keyed by a different one.
///  - Every refusal is paired with a PERMITTED request in the same test. A Gateway that refused everybody
///    would satisfy every refusal assertion here while being completely broken, so a refusal on its own
///    proves nothing.
///  - <see cref="The_leak_detector_itself_detects_a_leak_when_there_is_one"/> is the SELF-TEST: it points
///    the same structural comparison these tests refuse with at a record the caller genuinely owns and
///    requires it to come out POSITIVE. If that ever goes green-by-absence, the detector cannot tell a
///    refusal from a success and none of the other cases mean anything.
///
/// Revert-prove: put <c>missions.List()</c> / <c>missions.Get(missionId)</c> back and
/// <see cref="A_missions_list_serves_only_the_callers_own_missions"/> and
/// <see cref="A_mission_belonging_to_another_tenant_is_not_resolvable_by_id"/> both go RED.
/// </summary>
public sealed class MissionRouteTenantScopingTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private string _keyA = "";
    private string _keyB = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-1039-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            missionsPath: Path.Combine(_instancesDir, "missions", "missions.json"),   // a folder of its own: the registry reads every *.json directly in the instances folder
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Two accounts enrolled through the product's own hosted enrollment, each atomically bound to its
        // own canonically-minted tenant - the same shape the two throwaway signups in the field run had.
        _keyA = HostedTestEnrollment.Enroll(
            _gateway, "sub-alice", "alice@example.com", "dev-a", "MACHINE-A").DeviceKey;
        _keyB = HostedTestEnrollment.Enroll(
            _gateway, "sub-bob", "bob@example.com", "dev-b", "MACHINE-B").DeviceKey;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_missions_list_serves_only_the_callers_own_missions()
    {
        var aMission = await CreateMission(_keyA, "Payments cutover for Northwind");
        var bMission = await CreateMission(_keyB, "B's own unrelated mission");

        var (aStatus, aBody) = await GetRaw("missions", _keyA);
        var (bStatus, bBody) = await GetRaw("missions", _keyB);
        Assert.Equal(HttpStatusCode.OK, aStatus);
        Assert.Equal(HttpStatusCode.OK, bStatus);

        // The leak as it was observed in the field: identical bodies to two unrelated accounts. This is the
        // cheapest check there is and it needs no knowledge of what the surface is keyed by.
        Assert.NotEqual(aBody, bBody);

        // The invariant, structurally: no record of B's appears anywhere in A's payload, and vice versa.
        Assert.False(ContainsMission(aBody, bMission), "tenant A was served tenant B's mission record");
        Assert.False(ContainsMission(bBody, aMission), "tenant B was served tenant A's mission record");

        // The permitted half: each account IS served its own mission. Without this a Gateway answering
        // "[]" to everyone would pass every assertion above.
        Assert.True(ContainsMission(aBody, aMission), "tenant A was not served its own mission");
        Assert.True(ContainsMission(bBody, bMission), "tenant B was not served its own mission");

        // And the list is exactly the caller's own set, by count as well as by content.
        Assert.Equal(new[] { aMission.MissionId }, Parse(aBody).Select(m => m.MissionId).ToArray());
        Assert.Equal(new[] { bMission.MissionId }, Parse(bBody).Select(m => m.MissionId).ToArray());
    }

    [Fact]
    public async Task The_leak_detector_itself_detects_a_leak_when_there_is_one()
    {
        // THE SELF-TEST. Every refusal above is "ContainsMission(...) is false". That assertion is worth
        // exactly as much as its ability to come out TRUE, so point it at a record the caller genuinely
        // owns and require a positive. If this case ever fails, every other case in this file is passing on
        // a detector that cannot see anything, and the file certifies nothing.
        var aMission = await CreateMission(_keyA, "A mission the caller definitely owns");

        var (_, aBody) = await GetRaw("missions", _keyA);

        Assert.True(ContainsMission(aBody, aMission),
            "the structural comparison failed to find a record that IS in the payload - it cannot tell a " +
            "refusal from a success, so no refusal assertion in this file means anything");
    }

    [Fact]
    public async Task A_mission_belonging_to_another_tenant_is_not_resolvable_by_id()
    {
        var aMission = await CreateMission(_keyA, "Kickoff with Ana and Ravi");

        // The refusal: naming a valid id from another account reaches nothing. 404 rather than 403, so the
        // id cannot even be probed for existence - "someone else has it" and "nobody has it" read alike.
        var (foreignStatus, _) = await GetRaw($"missions/{aMission.MissionId}", _keyB);
        Assert.Equal(HttpStatusCode.NotFound, foreignStatus);

        // The control: that same id, asked for by its OWNER, resolves and carries the name. So the 404 is a
        // refusal, not a route that resolves nothing for anybody.
        var (ownStatus, ownBody) = await GetRaw($"missions/{aMission.MissionId}", _keyA);
        Assert.Equal(HttpStatusCode.OK, ownStatus);
        Assert.Contains("Kickoff with Ana and Ravi", ownBody);
    }

    [Fact]
    public async Task A_created_mission_is_owned_by_its_creator_and_appears_for_nobody_else()
    {
        // The write side. A read-side filter alone would be a deferred leak: unattributed rows would keep
        // accumulating behind it, which is how the shared store came to hold several accounts' missions.
        var bMission = await CreateMission(_keyB, "written by B, owned by B");

        var (_, aBody) = await GetRaw("missions", _keyA);
        Assert.False(ContainsMission(aBody, bMission));

        var (_, bBody) = await GetRaw("missions", _keyB);
        Assert.True(ContainsMission(bBody, bMission));
    }

    // REMOVED 2026-08-07 WITH THE FEATURE IT GUARDED, not because it stopped mattering.
    //
    // There was a test here - Another_tenants_mission_cannot_be_named_as_a_parent - proving a caller could
    // not name another tenant's mission as the parent of its own. Mission NESTING has been removed (see
    // Mission.cs for why): there is no parent field, POST /missions takes a name and nothing else, and so
    // there is no longer any path by which a caller-supplied mission id enters the create route at all.
    //
    // That is the deletion of an ATTACK SURFACE, not of a defence. The tenant boundary on create, list and
    // get-by-id is still held by the tests around this comment, and the attach route - the other place a
    // caller hands in a mission id - has its own suite. If nesting is ever reintroduced, this test comes
    // back with it: a parent is a reference INTO the mission set and must resolve under the caller's own
    // scope, exactly as it did before.

    // ---- helpers -------------------------------------------------------------------------------------

    /// <summary>
    /// The leak detector: does this payload contain THIS WHOLE RECORD? Structural rather than keyed on one
    /// field, because the leak that got through #1017 got through precisely by not being keyed by the one
    /// identifier the check knew about. Validated by
    /// <see cref="The_leak_detector_itself_detects_a_leak_when_there_is_one"/> before any refusal here
    /// relies on it.
    /// </summary>
    private static bool ContainsMission(string body, MissionDto mission) =>
        Parse(body).Any(m => m.MissionId == mission.MissionId || m.MissionName == mission.MissionName);

    private static List<MissionDto> Parse(string body) =>
        JsonSerializer.Deserialize<List<MissionDto>>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<MissionDto>();

    private async Task<MissionDto> CreateMission(string deviceKey, string name)
    {
        var resp = await PostMission(deviceKey, new NewMissionRequest { MissionName = name });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<MissionDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private Task<HttpResponseMessage> PostMission(string deviceKey, NewMissionRequest body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "missions")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private async Task<(HttpStatusCode Status, string Body)> GetRaw(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        var resp = await _http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }
}
