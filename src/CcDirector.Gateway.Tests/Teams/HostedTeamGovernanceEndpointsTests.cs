using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Gateway.Teams;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The team's Governance tab over real HTTP on a hosted Gateway with Teams released (Teams v1): the caller is the account
/// behind their own device key, and every request passes the real pipeline - the device-key middleware, the team gate and
/// then the route. Each role is asked as the role table says: the Owner and a Manager change the rules, a Developer reads
/// them and is refused a change, a Collaborator has no tab, a stranger is told there is no such team. A skill the team
/// really holds is made Required, so the library is read inside the team's tenant, not handed in by the test.
/// </summary>
/// This class sets the process-wide CC_GATEWAY_HOSTED, so it belongs to the hosted-mode collection.
[Collection("GatewayHostedMode")]
public sealed class HostedTeamGovernanceEndpointsTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private readonly string _owner = "sub-gov-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _manager = "sub-gov-manager-" + Guid.NewGuid().ToString("N");
    private readonly string _developer = "sub-gov-dev-" + Guid.NewGuid().ToString("N");
    private readonly string _collaborator = "sub-gov-collab-" + Guid.NewGuid().ToString("N");
    private readonly string _stranger = "sub-gov-stranger-" + Guid.NewGuid().ToString("N");
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-team-gov-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private readonly Dictionary<string, string> _keys = new();
    private string _team = "";
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
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _keys[_owner] = Enroll("dev-gov-owner", _owner, "soren@acme.example");
        _keys[_manager] = Enroll("dev-gov-manager", _manager, "peter@acme.example");
        _keys[_developer] = Enroll("dev-gov-dev", _developer, "rob@acme.example");
        _keys[_collaborator] = Enroll("dev-gov-collab", _collaborator, "mike@client.example");
        _keys[_stranger] = Enroll("dev-gov-stranger", _stranger, "stranger@else.example");

        _team = _gateway.TeamRegistry.CreateTeam(_owner, "Acme").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _manager, TeamRole.Manager).IsDone);
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

    private string Path_ => $"teams/{_team}/governance";

    [Fact]
    public async Task Read_TheOwnerManagerAndDeveloperSeeTheRules_OnlyTheFirstTwoMayChange_ACollaboratorAndAStrangerAreRefused()
    {
        foreach (var (caller, mayChange) in new[] { (_owner, true), (_manager, true), (_developer, false) })
        {
            var (status, body) = await Send(HttpMethod.Get, Path_, caller);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(mayChange, body.GetProperty("canChange").GetBoolean());
            Assert.Equal(3, body.GetProperty("review").GetArrayLength());
            Assert.Equal(4, body.GetProperty("readAccess").GetArrayLength());
            if (!mayChange)
                Assert.Equal(TeamRegistry.OwnerAndManagersChangeGovernance, body.GetProperty("note").GetString());
        }

        var (collaborator, refusal) = await Send(HttpMethod.Get, Path_, _collaborator);
        Assert.Equal(HttpStatusCode.Forbidden, collaborator);
        Assert.Contains("governance", refusal.GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, Path_, _stranger)).Status);
    }

    [Fact]
    public async Task Change_ByTheOwnerAndAManager_IsSaved_AndEveryChangeIsInTheRecord()
    {
        var (owner, afterOwner) = await Send(HttpMethod.Put, Path_, _owner, new { agents = new { otherAgents = false } });
        Assert.Equal(HttpStatusCode.OK, owner);
        Assert.False(Row(afterOwner, "agents", "otherAgents").GetProperty("on").GetBoolean());

        var (manager, afterManager) = await Send(HttpMethod.Put, Path_, _manager,
            new { review = new { noSelfMerge = true }, limits = new { sessionsAtOnce = 8 } });
        Assert.Equal(HttpStatusCode.OK, manager);

        var sentences = afterManager.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("sentence").GetString()).ToList();
        Assert.Equal(3, sentences.Count);
        Assert.Contains("peter@acme.example switched on \"Nobody merges their own agent's work\"", sentences);
        Assert.Contains("peter@acme.example set \"Sessions running at once per member\" to 8", sentences);
        Assert.Equal("soren@acme.example switched off \"Any other agent\"", sentences[^1]);

        // The Developer reads exactly what was saved.
        var (_, seen) = await Send(HttpMethod.Get, Path_, _developer);
        Assert.True(Row(seen, "review", "noSelfMerge").GetProperty("on").GetBoolean());
        Assert.Equal("8", Row(seen, "limits", "sessionsAtOnce").GetProperty("display").GetString());
    }

    [Fact]
    public async Task Change_ByADeveloperCollaboratorOrStranger_IsRefused_AndNothingIsSavedOrRecorded()
    {
        var body = new { review = new { noSelfMerge = true } };
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, Path_, _developer, body)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, Path_, _collaborator, body)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, Path_, _stranger, body)).Status);

        using var ctx = _gateway.GatewayDatabaseForTests.CreateUnscopedContext();
        Assert.False(ctx.TeamGovernance.AsNoTracking().Any(g => g.TeamId == _team));
        Assert.False(ctx.TeamGovernanceChanges.AsNoTracking().Any(c => c.TeamId == _team));
    }

    [Fact]
    public async Task Change_ASkillTheTeamHolds_CanBeRequired_AndOneItDoesNotHoldIsRefused()
    {
        var (created, createdText) = await Send(HttpMethod.Post, $"teams/{_team}/skills", _manager, new
        {
            id = "review-before-merge",
            name = "Review before merge",
            summary = "A second session reviews every change.",
            triggers = new[] { "review before merge" },
            bodyMarkdown = "# Review before merge\n\nAsk a second session to review.",
        });
        Assert.True(created == HttpStatusCode.Created, $"create: {created} {createdText}");
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"teams/{_team}/skills/review-before-merge/publish", _manager)).Status);

        var (before, offered) = await Send(HttpMethod.Get, Path_, _owner);
        Assert.Equal(HttpStatusCode.OK, before);
        Assert.Contains(offered.GetProperty("library").GetProperty("choices").EnumerateArray(),
            c => c.GetProperty("id").GetString() == "review-before-merge");

        var (required, after) = await Send(HttpMethod.Put, Path_, _owner,
            new { items = new[] { new { kind = "Skill", id = "review-before-merge", level = "Required" } } });
        Assert.Equal(HttpStatusCode.OK, required);
        var item = Assert.Single(after.GetProperty("library").GetProperty("items").EnumerateArray());
        Assert.Equal(("Review before merge", "Required"), (item.GetProperty("name").GetString(), item.GetProperty("level").GetString()));

        var (refused, why) = await Send(HttpMethod.Put, Path_, _owner,
            new { items = new[] { new { kind = "Skill", id = "dev-throttle", level = "Required" } } });
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Contains("not in the team's library", why.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Change_WithABodyOfTheWrongShape_IsABadRequest()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, Path_, _owner, new { review = new { noSelfMerge = "yes" } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, Path_, _owner, new { limits = new { sessionsAtOnce = 0 } })).Status);
    }

    private static JsonElement Row(JsonElement view, string section, string id) =>
        view.GetProperty(section).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);

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
        return (resp.StatusCode, JsonDocument.Parse(string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{') ? "{}" : text).RootElement.Clone());
    }
}
