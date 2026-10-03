using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway.Teams;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The team routes (devthrottle_internal#2300) driven END TO END through a REAL <see cref="GatewayHost"/> in hosted
/// mode, over REAL HTTP, with the REAL auth middleware and the REAL tenant and team registries. The caller is the
/// account behind their own device key, bound exactly as hosted enrolment binds it - nothing in a request names
/// who is asking. This is where the four tests of the issue meet the wire: a new account has no team, creating
/// one makes it the Owner, one account holds two roles in two teams, a member of one team cannot read another's
/// members, and the Owner cannot be removed.
///
/// This class sets the process-wide CC_GATEWAY_HOSTED, so it belongs to the hosted-mode collection.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamEndpointsTests : IAsyncLifetime
{
    private const string Token = "test-token";

    // Unique per instance, so no other class's accounts can share these.
    private readonly string _alice = "sub-team-alice-" + Guid.NewGuid().ToString("N");
    private readonly string _bob = "sub-team-bob-" + Guid.NewGuid().ToString("N");
    private readonly string _carol = "sub-team-carol-" + Guid.NewGuid().ToString("N");

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-teams-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _keyAlice = "";
    private string _keyBob = "";
    private string _keyCarol = "";
    private string _keyUnbound = "";
    private string? _priorHosted;
    private string? _priorRoot;
    private readonly ITestOutputHelper _output;

    public HostedTeamEndpointsTests(ITestOutputHelper output) => _output = output;

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
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _keyAlice = Enroll("dev-alice", _alice, "alice@example.com");
        _keyBob = Enroll("dev-bob", _bob, "bob@example.com");
        _keyCarol = Enroll("dev-carol", _carol, "carol@example.com");
        _keyUnbound = _gateway.Devices.Register("dev-unbound", "M9").DeviceKey;
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

    /// <summary>A device enrolled to a personal account, as hosted enrolment does it.</summary>
    private string Enroll(string deviceId, string subject, string email)
    {
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        var key = _gateway.Devices.Register(deviceId, "M-" + deviceId).DeviceKey;
        _gateway.Devices.SetAccountBinding(deviceId, subject, tenant.Value);
        return key;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, string key, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        // A refusal from the auth middleware may carry no body; that is still an answer, and an empty object stands for it.
        return (resp.StatusCode, JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone());
    }

    private async Task<string> CreateTeam(string key, string name)
    {
        var (status, body) = await Send(HttpMethod.Post, "teams", key, new { name });
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("team").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task NewAccount_HasNoTeams_AndCreatingOneOverTheWireMakesItTheOwner()
    {
        var (status, before) = await Send(HttpMethod.Get, "teams", _keyAlice);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, before.GetProperty("count").GetInt32());

        var (created, body) = await Send(HttpMethod.Post, "teams", _keyAlice, new { name = "Acme" });
        Assert.Equal(HttpStatusCode.Created, created);
        Assert.Equal("Owner", body.GetProperty("team").GetProperty("role").GetString());

        var (_, after) = await Send(HttpMethod.Get, "teams", _keyAlice);
        var team = Assert.Single(after.GetProperty("teams").EnumerateArray());
        Assert.Equal("Acme", team.GetProperty("name").GetString());
        Assert.Equal("Owner", team.GetProperty("role").GetString());

        // The account's personal tenant is unchanged by the team: the same subject still resolves to it.
        var personal = _gateway.TenantRegistry.LookupBySubject(_alice);
        Assert.NotNull(personal);
        Assert.NotEqual(personal!.Value.Value, team.GetProperty("id").GetString());
    }

    [Fact]
    public async Task MembersRoute_AMemberReadsTheList_ANonMemberIsToldThereIsNoSuchTeam()
    {
        var teamA = await CreateTeam(_keyAlice, "Team A");
        var teamB = await CreateTeam(_keyBob, "Team B");
        Assert.True(_gateway.TeamRegistry.AddMember(teamA, _carol, TeamRole.Developer).IsDone);

        var (okStatus, list) = await Send(HttpMethod.Get, $"teams/{teamA}/members", _keyCarol);
        Assert.Equal(HttpStatusCode.OK, okStatus);
        Assert.Equal(new[] { "alice@example.com", "carol@example.com" },
            list.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("email").GetString()));
        Assert.Equal(new[] { "Owner", "Developer" },
            list.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("role").GetString()));

        // Carol is in team A only: team B's list is refused, with the same answer as a team that does not exist.
        var (refused, refusal) = await Send(HttpMethod.Get, $"teams/{teamB}/members", _keyCarol);
        var (absent, absence) = await Send(HttpMethod.Get, $"teams/{Guid.NewGuid()}/members", _keyCarol);
        Assert.Equal(HttpStatusCode.NotFound, refused);
        Assert.Equal(HttpStatusCode.NotFound, absent);
        Assert.Equal(absence.GetRawText(), refusal.GetRawText());
        Assert.DoesNotContain("bob@example.com", refusal.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneAccountInTwoTeams_TheSwitcherShowsTheRightRoleInEach()
    {
        var teamA = await CreateTeam(_keyAlice, "Team A");
        var teamB = await CreateTeam(_keyBob, "Team B");
        _gateway.TeamRegistry.AddMember(teamA, _carol, TeamRole.Manager);
        _gateway.TeamRegistry.AddMember(teamB, _carol, TeamRole.Collaborator);

        var (_, body) = await Send(HttpMethod.Get, "teams", _keyCarol);

        var roles = body.GetProperty("teams").EnumerateArray()
            .ToDictionary(t => t.GetProperty("id").GetString()!, t => t.GetProperty("role").GetString());
        Assert.Equal(2, roles.Count);
        Assert.Equal("Manager", roles[teamA]);
        Assert.Equal("Collaborator", roles[teamB]);
    }

    [Fact]
    public async Task TheOwner_CannotBeRemovedOrDemoted_AndStaysTheOnlyOwner()
    {
        var team = await CreateTeam(_keyAlice, "Acme");
        _gateway.TeamRegistry.AddMember(team, _bob, TeamRole.Manager);

        Assert.Equal(TeamRefusals.RemoveOwner, _gateway.TeamRegistry.RemoveMember(team, _alice).Refusal);
        Assert.Equal(TeamRefusals.DemoteOwner, _gateway.TeamRegistry.ChangeRole(team, _alice, TeamRole.Developer).Refusal);
        Assert.Equal(TeamRefusals.SecondOwner, _gateway.TeamRegistry.ChangeRole(team, _bob, TeamRole.Owner).Refusal);

        var (_, list) = await Send(HttpMethod.Get, $"teams/{team}/members", _keyBob);
        Assert.Single(list.GetProperty("members").EnumerateArray(), m => m.GetProperty("role").GetString() == "Owner");
    }

    [Fact]
    public async Task ACallerNotBoundToAnAccount_IsRefusedOnEveryRoute()
    {
        var team = await CreateTeam(_keyAlice, "Acme");

        foreach (var (method, path) in new[] { (HttpMethod.Get, "teams"), (HttpMethod.Post, "teams"), (HttpMethod.Get, $"teams/{team}/members") })
        {
            var (status, _) = await Send(method, path, _keyUnbound, method == HttpMethod.Post ? new { name = "Sneaky" } : null);
            Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"{method} {path} answered {status}");
        }
        Assert.Single(_gateway.TeamRegistry.ListTeamsFor(_alice));
    }

    /// <summary>
    /// The proof transcript (devthrottle_internal#2300): the exact requests and responses of an Owner creating a
    /// test team, two test accounts joining it, and the member list read by a member and refused to an outsider.
    /// Written to the test output, which the proof in docs/proof/teams-2300 is captured from. Device keys are
    /// never written; the accounts are throwaway test accounts on a local Gateway.
    /// </summary>
    [Fact]
    public async Task Transcript_AnOwnerCreatesATeam_AndItsMembersAreListed()
    {
        async Task<JsonElement> Logged(HttpMethod method, string path, string key, string who, object? body = null)
        {
            _output.WriteLine($"> {method} /{path}    (as {who}){(body is null ? "" : "    " + JsonSerializer.Serialize(body))}");
            var (status, json) = await Send(method, path, key, body);
            _output.WriteLine($"< {(int)status} {status}");
            _output.WriteLine(JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
            _output.WriteLine("");
            return json;
        }

        await Logged(HttpMethod.Get, "teams", _keyAlice, "alice@example.com, a new account");
        var created = await Logged(HttpMethod.Post, "teams", _keyAlice, "alice@example.com", new { name = "Acme Test Team" });
        var team = created.GetProperty("team").GetProperty("id").GetString()!;

        // Invitations are devthrottle_internal#2301; until then a member is added through the registry directly.
        _output.WriteLine($"(registry) AddMember(team, bob@example.com, Developer) -> {_gateway.TeamRegistry.AddMember(team, _bob, TeamRole.Developer).IsDone}");
        _output.WriteLine($"(registry) AddMember(team, carol@example.com, Collaborator) -> {_gateway.TeamRegistry.AddMember(team, _carol, TeamRole.Collaborator).IsDone}");
        _output.WriteLine($"(registry) RemoveMember(team, alice@example.com the Owner) -> {_gateway.TeamRegistry.RemoveMember(team, _alice).Refusal}");
        _output.WriteLine("");

        var members = await Logged(HttpMethod.Get, $"teams/{team}/members", _keyBob, "bob@example.com, a Developer in the team");
        await Logged(HttpMethod.Get, "teams", _keyCarol, "carol@example.com, a Collaborator in the team");

        var outsider = Enroll("dev-dave", "sub-team-dave-" + Guid.NewGuid().ToString("N"), "dave@example.com");
        await Logged(HttpMethod.Get, $"teams/{team}/members", outsider, "dave@example.com, not a member");

        Assert.Equal(3, members.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task CreateTeam_WithoutAName_IsRefusedWithThePlainReason()
    {
        var (status, body) = await Send(HttpMethod.Post, "teams", _keyAlice, new { name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.StartsWith("A team needs a name.", body.GetProperty("error").GetString());
    }
}
