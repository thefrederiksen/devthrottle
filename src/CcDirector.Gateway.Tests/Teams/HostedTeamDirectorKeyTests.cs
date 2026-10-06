using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A Director's key bound to a team (devthrottle_internal#2311), END TO END through a REAL hosted
/// <see cref="GatewayHost"/> with Teams released, over REAL HTTP, through the REAL auth middleware. Keys are minted the
/// way hosted enrollment mints them.
///
/// WHAT A TEAM KEY GETS, AND WHY. A team key authenticates - the device registry accepts it while its person may run
/// sessions in the team - and the request-path access lease then reads the TEAM's bill and the person's role there
/// (Gateway step 2): a member is never refused for the bill, so with no bill they are served on the free tier (200). So
/// the wire-level proof here is the difference between "served" (200) and "the key itself is revoked" (401): removing
/// the person turns the first into the second, for their keys on that team only.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamDirectorKeyTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private readonly string _owner = "sub-tdk-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _alice = "sub-tdk-alice-" + Guid.NewGuid().ToString("N");
    private readonly string _bob = "sub-tdk-bob-" + Guid.NewGuid().ToString("N");
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-tdk-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _priorHosted;
    private string? _priorRoot;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: true);
        await _gateway.StartAsync();
        HostedTeamBill.CreateTable(_gateway);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    private string TeamKey(string team, string subject, string directorId) =>
        _gateway.Devices.RegisterForTenant(new TenantId(team), subject,
            CcDirector.Gateway.Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(team, subject, directorId), "M-" + directorId).DeviceKey;

    private async Task<(HttpStatusCode Status, string Code)> Get(string path, string? bearer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        var code = "";
        if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("code", out var c)) code = c.GetString() ?? "";
        }
        return (resp.StatusCode, code);
    }

    [Fact]
    public async Task RemovingAPerson_TurnsTheirTeamKeysIntoRevokedKeys_OnThatTeamOnly_OverTheWire()
    {
        var team = _gateway.TeamRegistry.CreateTeam(_owner, "Acme").Team!.TeamId;
        var otherTeam = _gateway.TeamRegistry.CreateTeam(_owner, "Other").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(team, _alice, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(team, _bob, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(otherTeam, _alice, TeamRole.Developer).IsDone);

        var aliceHere = TeamKey(team, _alice, "director-alice-here");
        var aliceThere = TeamKey(otherTeam, _alice, "director-alice-there");
        var bobHere = TeamKey(team, _bob, "director-bob-here");
        var alicePersonal = HostedTestEnrollment.Enroll(_gateway, _alice, "alice@example.com", "director-alice-home", "M").DeviceKey;

        // Authenticated, and served: the lease reads the team's bill, and a member is never refused for it.
        Assert.Equal(HttpStatusCode.OK, (await Get("gateway/skills", aliceHere)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get("gateway/skills", bobHere)).Status);

        Assert.True(_gateway.TeamRegistry.RemoveMember(team, _alice).IsDone);

        var revoked = await Get("gateway/skills", aliceHere);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.Status);
        Assert.Equal("device_credential_revoked", revoked.Code);
        Assert.Equal(HttpStatusCode.OK, (await Get("gateway/skills", aliceThere)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get("gateway/skills", bobHere)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get("teams", alicePersonal)).Status);
    }

    [Fact]
    public async Task MakingAPersonACollaborator_RevokesTheirTeamKey_OverTheWire()
    {
        var team = _gateway.TeamRegistry.CreateTeam(_owner, "Acme").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(team, _alice, TeamRole.Developer).IsDone);
        var key = TeamKey(team, _alice, "director-alice");
        Assert.Equal(HttpStatusCode.OK, (await Get("gateway/skills", key)).Status);

        Assert.True(_gateway.TeamRegistry.ChangeRole(team, _alice, TeamRole.Collaborator).IsDone);

        var revoked = await Get("gateway/skills", key);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.Status);
        Assert.Equal("device_credential_revoked", revoked.Code);
    }

    [Fact]
    public async Task Healthz_SaysTeamsIsOffered()
    {
        Assert.True(await HealthzTeams(_http));
    }

    internal static async Task<bool?> HealthzTeams(HttpClient http)
    {
        using var resp = await http.GetAsync("healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("teams", out var teams) ? teams.GetBoolean() : null;
    }

    [Fact]
    public async Task TheTwoTeamEnrollmentRoutes_AreMapped_AndAskForTheAccountToken()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("devices/enroll-hosted/teams", null)).Status);

        using var move = new HttpRequestMessage(HttpMethod.Post, "devices/enroll-hosted/move")
        {
            Content = JsonContent.Create(new { deviceId = "director-1" }),
        };
        using var resp = await _http.SendAsync(move);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}

/// <summary>
/// The dark half: with Teams NOT released, a hosted Gateway maps neither team enrollment route, and a key bound to a
/// team's tenant is refused as an invalid binding, exactly as before teams existed.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamDirectorKeyDarkTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private readonly string _owner = "sub-tdkd-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-tdkd-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _priorHosted;
    private string? _priorRoot;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: false);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Dark_HealthzSaysTeamsIsNotOffered_Explicitly()
    {
        Assert.False(await HostedTeamDirectorKeyTests.HealthzTeams(_http));
    }

    [Fact]
    public async Task Dark_TheTeamEnrollmentRoutesAreAbsent_AndATeamKeyIsRevoked()
    {
        using (var list = new HttpRequestMessage(HttpMethod.Get, "devices/enroll-hosted/teams"))
        using (var resp = await _http.SendAsync(list))
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        using (var move = new HttpRequestMessage(HttpMethod.Post, "devices/enroll-hosted/move") { Content = JsonContent.Create(new { deviceId = "director-1" }) })
        using (var resp = await _http.SendAsync(move))
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        // A team can only exist dark through the registry itself (its routes are not mapped); a key bound to it is
        // judged exactly as before: not this account's tenant, so revoked.
        var team = _gateway.TeamRegistry.CreateTeam(_owner, "Acme").Team!.TeamId;
        var key = _gateway.Devices.RegisterForTenant(new TenantId(team), _owner, team + "|d", "M").DeviceKey;
        Assert.Equal(CcDirector.Gateway.Pairing.DeviceCredentialResolutionKind.Revoked, _gateway.Devices.ResolveCredential(key).Kind);

        using var req = new HttpRequestMessage(HttpMethod.Get, "gateway/skills");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var refused = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }
}
