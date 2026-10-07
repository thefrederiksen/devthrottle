using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Gateway.Teams;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The Team page over real HTTP on a hosted Gateway with Teams released (devthrottle_internal#2303): the caller is the
/// account behind their own device key, every request passes the real pipeline - the device-key middleware, the team
/// gate (#2302) and then the route - and every refusal is read as a person reads it. The bill's seat sync is proven in
/// the unit suite's <c>TeamPageTests</c> against a recording website; here the hosted Gateway has no service credential,
/// so it never calls one.
/// </summary>
/// This class sets the process-wide CC_GATEWAY_HOSTED, so it belongs to the hosted-mode collection.
[Collection("GatewayHostedMode")]
public sealed class HostedTeamPageEndpointsTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private readonly string _owner = "sub-page-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _manager = "sub-page-manager-" + Guid.NewGuid().ToString("N");
    private readonly string _manager2 = "sub-page-manager2-" + Guid.NewGuid().ToString("N");
    private readonly string _developer = "sub-page-dev-" + Guid.NewGuid().ToString("N");
    private readonly string _collaborator = "sub-page-collab-" + Guid.NewGuid().ToString("N");
    private readonly string _stranger = "sub-page-stranger-" + Guid.NewGuid().ToString("N");
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-team-page-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private readonly Dictionary<string, string> _keys = new();
    private string _team = "";
    private string? _priorHosted;
    private string? _priorRoot;

    public HostedTeamPageEndpointsTests(ITestOutputHelper output) => _output = output;

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
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _keys[_owner] = Enroll("dev-page-owner", _owner, "soren@acme.example");
        _keys[_manager] = Enroll("dev-page-manager", _manager, "priya@acme.example");
        _keys[_manager2] = Enroll("dev-page-manager2", _manager2, "pat@acme.example");
        _keys[_developer] = Enroll("dev-page-dev", _developer, "rob@acme.example");
        _keys[_collaborator] = Enroll("dev-page-collab", _collaborator, "mike@client.example");
        _keys[_stranger] = Enroll("dev-page-stranger", _stranger, "stranger@else.example");

        var (status, body) = await Send(HttpMethod.Post, "teams", _owner, new { name = "Acme" });
        Assert.Equal(HttpStatusCode.Created, status);
        _team = body.GetProperty("team").GetProperty("id").GetString()!;
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _manager, TeamRole.Manager).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _manager2, TeamRole.Manager).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _developer, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _collaborator, TeamRole.Collaborator).IsDone);
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
    public async Task Page_EachRoleGetsItsOwnVerdicts_ACollaboratorIsRefused_AndAStrangerIsNotFound()
    {
        var (owner, ownerPage) = await Send(HttpMethod.Get, $"teams/{_team}/page", _owner);
        Assert.Equal(HttpStatusCode.OK, owner);
        Assert.True(Member(ownerPage, "rob@acme.example").GetProperty("canChangeRole").GetBoolean());
        Assert.False(Member(ownerPage, "soren@acme.example").GetProperty("canRemove").GetBoolean());

        var (manager, managerPage) = await Send(HttpMethod.Get, $"teams/{_team}/page", _manager);
        Assert.Equal(HttpStatusCode.OK, manager);
        Assert.False(Member(managerPage, "rob@acme.example").GetProperty("canChangeRole").GetBoolean());
        Assert.True(Member(managerPage, "rob@acme.example").GetProperty("canRemove").GetBoolean());
        Assert.False(Member(managerPage, "pat@acme.example").GetProperty("canRemove").GetBoolean());

        var (developer, developerPage) = await Send(HttpMethod.Get, $"teams/{_team}/page", _developer);
        Assert.Equal(HttpStatusCode.OK, developer);
        Assert.All(developerPage.GetProperty("members").EnumerateArray(), m =>
        {
            Assert.False(m.GetProperty("canChangeRole").GetBoolean());
            Assert.False(m.GetProperty("canRemove").GetBoolean());
        });

        // A Collaborator has no Team page: the gate refuses it in the role table's words. They may still read the list.
        var (collaborator, refusal) = await Send(HttpMethod.Get, $"teams/{_team}/page", _collaborator);
        Assert.Equal(HttpStatusCode.Forbidden, collaborator);
        Assert.Equal(TeamEndpointGate.RefusalCode, refusal.GetProperty("code").GetString());
        Assert.Equal(TeamAccessDecision.RoleRefusal(TeamRole.Collaborator, TeamPermissions.Row(TeamAction.SeeTeamPage)),
            refusal.GetProperty("error").GetString());
        var (list, _) = await Send(HttpMethod.Get, $"teams/{_team}/members", _collaborator);
        Assert.Equal(HttpStatusCode.OK, list);

        var (stranger, _) = await Send(HttpMethod.Get, $"teams/{_team}/page", _stranger);
        Assert.Equal(HttpStatusCode.NotFound, stranger);
    }

    [Fact]
    public async Task ChangeRole_TheOwnerChangesIt_AManagerIsRefused_DeveloperAndCollaboratorAreRefused_AStrangerIsNotFound()
    {
        var rob = IdOf(_developer);

        var (manager, managerRefusal) = await Send(HttpMethod.Put, $"teams/{_team}/members/{rob}/role", _manager, new { role = "Collaborator" });
        Assert.Equal(HttpStatusCode.Forbidden, manager);
        Assert.Equal(TeamAccessDecision.RoleRefusal(TeamRole.Manager, TeamPermissions.Row(TeamAction.MakeManagersAndChangeRoles)),
            managerRefusal.GetProperty("error").GetString());
        foreach (var caller in new[] { _developer, _collaborator })
        {
            var (status, _) = await Send(HttpMethod.Put, $"teams/{_team}/members/{rob}/role", caller, new { role = "Manager" });
            Assert.Equal(HttpStatusCode.Forbidden, status);
        }
        var (stranger, _) = await Send(HttpMethod.Put, $"teams/{_team}/members/{rob}/role", _stranger, new { role = "Manager" });
        Assert.Equal(HttpStatusCode.NotFound, stranger);
        Assert.Equal(TeamRole.Developer, _gateway.TeamRegistry.RoleOf(_team, _developer));

        var (owner, _) = await Send(HttpMethod.Put, $"teams/{_team}/members/{rob}/role", _owner, new { role = "Collaborator" });
        Assert.Equal(HttpStatusCode.OK, owner);
        Assert.Equal(TeamRole.Collaborator, _gateway.TeamRegistry.RoleOf(_team, _developer));
        var (_, page) = await Send(HttpMethod.Get, $"teams/{_team}/page", _owner);
        Assert.Equal("Collaborator", Member(page, "rob@acme.example").GetProperty("role").GetString());
        Assert.Equal("No charge", Member(page, "rob@acme.example").GetProperty("seat").GetString());
    }

    [Fact]
    public async Task Remove_ManagerRemovesADeveloper_NotAManagerOrTheOwner_TheOwnerNotThemselves_DeveloperAndCollaboratorNobody()
    {
        var (managerOnManager, mm) = await Send(HttpMethod.Delete, $"teams/{_team}/members/{IdOf(_manager2)}", _manager);
        Assert.Equal(HttpStatusCode.Forbidden, managerOnManager);
        Assert.Equal(TeamRefusals.OnlyOwnerRemovesManager, mm.GetProperty("error").GetString());

        var (managerOnOwner, mo) = await Send(HttpMethod.Delete, $"teams/{_team}/members/{IdOf(_owner)}", _manager);
        Assert.Equal(HttpStatusCode.Conflict, managerOnOwner);
        Assert.Equal(TeamRefusals.RemoveOwner, mo.GetProperty("error").GetString());

        var (ownerOnSelf, os) = await Send(HttpMethod.Delete, $"teams/{_team}/members/{IdOf(_owner)}", _owner);
        Assert.Equal(HttpStatusCode.Conflict, ownerOnSelf);
        Assert.Equal(TeamRefusals.OwnerRemovesSelf, os.GetProperty("error").GetString());

        foreach (var caller in new[] { _developer, _collaborator })
        {
            var (status, body) = await Send(HttpMethod.Delete, $"teams/{_team}/members/{IdOf(_collaborator)}", caller);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal(TeamEndpointGate.RefusalCode, body.GetProperty("code").GetString());
        }
        var (stranger, _) = await Send(HttpMethod.Delete, $"teams/{_team}/members/{IdOf(_developer)}", _stranger);
        Assert.Equal(HttpStatusCode.NotFound, stranger);
        Assert.Equal(5, _gateway.TeamRegistry.ListMembers(_team, _owner).Members.Count);

        var (removed, _) = await Send(HttpMethod.Delete, $"teams/{_team}/members/{IdOf(_developer)}", _manager);
        Assert.Equal(HttpStatusCode.OK, removed);
        Assert.Null(_gateway.TeamRegistry.RoleOf(_team, _developer));
    }

    /// <summary>
    /// The whole page flow as an Owner, a Manager and a Developer meet it, written to the test output - the transcript
    /// the proof in docs/proof/teams-2303 commits.
    /// </summary>
    [Fact]
    public async Task Transcript_TheTeamPage()
    {
        var rob = IdOf(_developer);
        // Each step asserts its answer (review F3), so the transcript cannot pass while a route answers wrongly.
        await Show(HttpStatusCode.OK, HttpMethod.Get, $"teams/{_team}/page", "teams/{teamId}/page", _owner, "soren@acme.example (Owner)");
        await Show(HttpStatusCode.Forbidden, HttpMethod.Get, $"teams/{_team}/page", "teams/{teamId}/page", _collaborator, "mike@client.example (Collaborator)");
        await Show(HttpStatusCode.Forbidden, HttpMethod.Put, $"teams/{_team}/members/{rob}/role", "teams/{teamId}/members/{rob}/role", _manager, "priya@acme.example (Manager)", new { role = "Collaborator" });
        await Show(HttpStatusCode.OK, HttpMethod.Put, $"teams/{_team}/members/{rob}/role", "teams/{teamId}/members/{rob}/role", _owner, "soren@acme.example (Owner)", new { role = "Collaborator" });
        await Show(HttpStatusCode.Forbidden, HttpMethod.Delete, $"teams/{_team}/members/{IdOf(_manager2)}", "teams/{teamId}/members/{pat}", _manager, "priya@acme.example (Manager)");
        await Show(HttpStatusCode.Conflict, HttpMethod.Delete, $"teams/{_team}/members/{IdOf(_owner)}", "teams/{teamId}/members/{soren}", _owner, "soren@acme.example (Owner)");
        await Show(HttpStatusCode.Forbidden, HttpMethod.Delete, $"teams/{_team}/members/{rob}", "teams/{teamId}/members/{rob}", _developer, "rob@acme.example (Developer, now Collaborator)");
        await Show(HttpStatusCode.OK, HttpMethod.Delete, $"teams/{_team}/members/{rob}", "teams/{teamId}/members/{rob}", _manager, "priya@acme.example (Manager)");
        await Show(HttpStatusCode.NotFound, HttpMethod.Get, $"teams/{_team}/page", "teams/{teamId}/page", _developer, "rob@acme.example (removed)");
        Assert.Null(_gateway.TeamRegistry.RoleOf(_team, _developer));
    }

    private async Task Show(HttpStatusCode expected, HttpMethod method, string path, string shownPath, string caller, string who, object? body = null)
    {
        var (status, answer) = await Send(method, path, caller, body);
        Assert.True(status == expected, $"{method} /{shownPath} as {who}: expected {(int)expected}, got {(int)status}");
        _output.WriteLine("");
        _output.WriteLine($"{method} /{shownPath}{(body is null ? "" : " " + JsonSerializer.Serialize(body))} as {who} -> {(int)status} {status}");
        _output.WriteLine(JsonSerializer.Serialize(answer, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonElement Member(JsonElement page, string email) =>
        page.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("email").GetString() == email);

    private string IdOf(string subject) => TeamMemberIds.For(_team, subject);

    private string Enroll(string deviceId, string subject, string email)
    {
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        var key = _gateway.Devices.Register(deviceId, "M-" + deviceId).DeviceKey;
        _gateway.Devices.SetAccountBinding(deviceId, subject, tenant.Value);
        return key;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, string caller, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _keys[caller]);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone());
    }
}
