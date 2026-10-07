using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Teams;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// THE TEAM DIRECTOR ROUTES CHANGE NOTHING OUTSIDE A TEAM (devthrottle_internal#2311). A REAL hosted Gateway, run twice:
/// with Teams released and dark. A person with NO team has two Directors on their personal account, each with a session;
/// every route this change cut or checks inside a team answers them exactly as before - both Directors' sessions, the
/// number filed under the Director the body names, the hand-written workspace listed, the settings changeable, the
/// placement of both machines. A team exists beside them, with a member's Director and session in it, and the two
/// accounts never see each other's (tenant isolation). On the dark Gateway a key bound to the team's tenant is not
/// accepted at all.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate.
/// </summary>
public abstract class HostedTeamDirectorRoutesNoTeamTestsBase : IAsyncLifetime
{
    private const string Token = "test-token-team-routes-no-team";
    private const string FirstDirector = "director-personal-1";
    private const string SecondDirector = "director-personal-2";
    private const string TeamMemberDirector = "director-team-member";

    private readonly string _person = "sub-noteam-" + Guid.NewGuid().ToString("N");
    private readonly string _teamOwner = "sub-noteam-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _member = "sub-noteam-member-" + Guid.NewGuid().ToString("N");
    private readonly string _firstSession = Guid.NewGuid().ToString();
    private readonly string _secondSession = Guid.NewGuid().ToString();
    private readonly string _teamSession = Guid.NewGuid().ToString();
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-team-routes-noteam-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector? _first;
    private FakeTunnelDirector? _second;
    private FakeTunnelDirector? _teamDirector;
    private TenantId _personal;
    private string _firstKey = "";
    private string _secondKey = "";
    private string _teamId = "";
    private string? _teamKey;
    private string? _priorHosted;
    private string? _priorRoot;

    protected HostedTeamDirectorRoutesNoTeamTestsBase(ITestOutputHelper output) => _out = output;

    /// <summary>Whether this run's Gateway has Teams released.</summary>
    protected abstract bool TeamsReleased { get; }

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
            streamMode: true, teamsReleased: TeamsReleased);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"), Timeout = TimeSpan.FromMinutes(2) };

        var first = HostedTestEnrollment.Enroll(_gateway, _person, "person@example.com", "dev-noteam-1-" + Guid.NewGuid().ToString("N"), "M-1");
        var second = HostedTestEnrollment.Enroll(_gateway, _person, "person@example.com", "dev-noteam-2-" + Guid.NewGuid().ToString("N"), "M-2");
        Assert.Equal(first.Tenant, second.Tenant);
        _personal = first.Tenant;
        _firstKey = first.DeviceKey;
        _secondKey = second.DeviceKey;
        _first = await FakeTunnelDirector.StartAsync(_gateway, _firstKey, FirstDirector);
        _second = await FakeTunnelDirector.StartAsync(_gateway, _secondKey, SecondDirector);
        await _first.PushSnapshotAsync(Row(_firstSession));
        await _second.PushSnapshotAsync(Row(_secondSession));

        // A team beside the person, made through the registry (the team routes are not mapped on a dark Gateway).
        _teamId = _gateway.TeamRegistry.CreateTeam(_teamOwner, "Beside").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(_teamId, _member, TeamRole.Developer).IsDone);
        if (TeamsReleased)
        {
            HostedTeamBill.Start(_gateway, _teamId, seats: 3);
            _teamKey = _gateway.Devices.RegisterForTenant(new TenantId(_teamId), _member,
                Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(_teamId, _member, TeamMemberDirector), "M-team").DeviceKey;
            _teamDirector = await FakeTunnelDirector.StartAsync(_gateway, _teamKey, TeamMemberDirector);
            await _teamDirector.PushSnapshotAsync(Row(_teamSession));
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var director in new[] { _first, _second, _teamDirector })
            if (director is not null)
                await director.DisposeAsync();
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task APersonWithNoTeam_GetSessions_ServesEveryOneOfTheirDirectors_AndNothingOfTheTeam()
    {
        var (status, body) = await Send(HttpMethod.Get, "sessions?envelope=true", _firstKey);
        Assert.Equal(HttpStatusCode.OK, status);
        var sessions = body.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("sessionId").GetString()).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { _firstSession, _secondSession }.OrderBy(s => s).ToArray(), sessions);
        Assert.DoesNotContain(_teamSession, body.GetRawText());
        Assert.DoesNotContain(TeamMemberDirector, body.GetRawText());
    }

    [Fact]
    public async Task APersonWithNoTeam_ANumberIsFiledUnderTheDirectorTheBodyNames_AsBefore()
    {
        var (status, _) = await Send(HttpMethod.Post, "session-numbers/allocate", _firstKey,
            JsonSerializer.Serialize(new { sessionId = _secondSession, directorId = SecondDirector }));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(SecondDirector, _gateway.SessionNumbers.DirectorFor(_personal, _secondSession));

        var (free, _) = await Send(HttpMethod.Delete, $"session-numbers/{_secondSession}", _firstKey);
        Assert.Equal(HttpStatusCode.NoContent, free);
        Assert.Null(_gateway.SessionNumbers.NumberFor(_personal, _secondSession));
    }

    [Fact]
    public async Task APersonWithNoTeam_TheWorkspaceListKeepsAHandWrittenOne_AndTheSettingsCanBeChanged()
    {
        using (_gateway.TenantBoundaryForTests.EnterScope(_personal))
            _gateway.WorkspacesForTest.CreateAuthored(new WorkspaceDocument { Id = "by-hand", Name = "by hand" }, DateTime.UtcNow);
        var (list, listBody) = await Send(HttpMethod.Get, "gateway/workspaces", _firstKey);
        Assert.Equal(HttpStatusCode.OK, list);
        Assert.Contains("by-hand", listBody.GetProperty("workspaces").EnumerateArray().Select(w => w.GetProperty("id").GetString()));

        var (presets, _) = await Send(HttpMethod.Put, "gateway/snooze-presets", _firstKey, "{\"presets\":[5,10],\"defaultMinutes\":5}");
        Assert.Equal(HttpStatusCode.OK, presets);
    }

    [Fact]
    public async Task APersonWithNoTeam_ThePlacementFleetView_ShowsBothOfTheirMachines()
    {
        foreach (var (key, director) in new[] { (_firstKey, FirstDirector), (_secondKey, SecondDirector) })
        {
            var push = JsonSerializer.Serialize(new SkillPlacementPushRequest
            {
                DirectorId = director,
                MachineName = "M-" + director,
                Reports = new() { new SkillPlacementReportDto { AgentKind = "ClaudeCode", Held = 1, Reachable = 1, ObservedAtUtc = DateTime.UtcNow } },
            });
            var (status, _) = await Send(HttpMethod.Post, "gateway/skills/placement", key, push);
            Assert.Equal(HttpStatusCode.OK, status);
        }
        var (fleet, body) = await Send(HttpMethod.Get, "gateway/skills/placement", _firstKey);
        Assert.Equal(HttpStatusCode.OK, fleet);
        var directors = body.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("directorId").GetString()).Distinct().OrderBy(d => d).ToArray();
        Assert.Equal(new[] { FirstDirector, SecondDirector }, directors);
    }

    [Fact]
    public async Task TheTeamsMember_SeesOnlyTheTeamsOwn_WhereReleased_AndIsNotAcceptedWhereDark()
    {
        if (!TeamsReleased)
        {
            // Dark: a key bound to a team's tenant is not accepted, so no team request reaches these routes. Minted
            // straight into the registry the way the released enrollment would, and then used.
            var darkKey = _gateway.Devices.RegisterForTenant(new TenantId(_teamId), _member,
                Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(_teamId, _member, TeamMemberDirector), "M-team").DeviceKey;
            foreach (var path in new[] { "sessions", "account/status", "gateway/snooze-presets", "gateway/workspaces" })
            {
                var (dark, _) = await Send(HttpMethod.Get, path, darkKey);
                Assert.Equal(HttpStatusCode.Unauthorized, dark);
            }
            return;
        }

        var (status, body) = await Send(HttpMethod.Get, "sessions", _teamKey!);
        Assert.Equal(HttpStatusCode.OK, status);
        var sessions = body.EnumerateArray().Select(s => s.GetProperty("sessionId").GetString()).ToArray();
        Assert.Equal(new[] { _teamSession }, sessions);
    }

    private static SessionDto Row(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, string key, string? json = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (json is not null)
            req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"{method} {path} -> {(int)resp.StatusCode}: {(raw.Length > 400 ? raw[..400] + "..." : raw)}");
        return (resp.StatusCode, string.IsNullOrEmpty(raw) ? default : JsonDocument.Parse(raw).RootElement.Clone());
    }
}

/// <summary>The no-team run on a Gateway where Teams is released.</summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamDirectorRoutesNoTeamReleasedTests : HostedTeamDirectorRoutesNoTeamTestsBase
{
    public HostedTeamDirectorRoutesNoTeamReleasedTests(ITestOutputHelper output) : base(output) { }

    protected override bool TeamsReleased => true;
}

/// <summary>The no-team run on a dark Gateway (CC_GATEWAY_TEAMS off).</summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamDirectorRoutesNoTeamDarkTests : HostedTeamDirectorRoutesNoTeamTestsBase
{
    public HostedTeamDirectorRoutesNoTeamDarkTests(ITestOutputHelper output) : base(output) { }

    protected override bool TeamsReleased => false;
}
