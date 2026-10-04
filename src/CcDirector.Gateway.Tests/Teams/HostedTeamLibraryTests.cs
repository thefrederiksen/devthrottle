using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Teams;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// THE TEAM'S SHARED SKILLS AND WORKFLOWS (Teams 6, devthrottle_internal#2304), driven END TO END through a REAL hosted
/// <see cref="GatewayHost"/> with Teams released, over REAL HTTP, with the real auth middleware, the real team gate and
/// the real skill and workflow stores. Every person calls from their own account's device key, bound exactly as hosted
/// enrolment binds it; nothing in a request names the caller.
///
/// The three tests of #2304, plus: someone who is not a member, built-ins still read-only inside a team, and a personal
/// account's own library behaving exactly as before.
///
/// THE SESSION SIDE. A session on a Director set up for the team pulls with a key bound to the team's tenant through the
/// ordinary <c>/gateway/skills</c>. TODAY THAT REQUEST IS REFUSED: such a key is not accepted by the hosted device
/// registry, and the gate cannot name the person behind a team device key or a team session key - all three wait on
/// devthrottle_internal#2311's one resolver (seam-director-key.md). So the session-side test here proves only the role
/// table (with the caller SUPPLIED BY THE TEST, which the real middleware cannot yet do) and the tenant partition of the
/// store. What is proven over the wire is a Developer reaching the team's library from their OWN account.
///
/// This class sets the process-wide CC_GATEWAY_HOSTED, so it belongs to the hosted-mode collection.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamLibraryTests : IAsyncLifetime
{
    private const string Token = "test-token";

    /// <summary>What the client claims as the author. The team routes ignore it and record the member they identified.</summary>
    private const string ClientClaimedAuthor = "someone-the-client-named@example.com";

    private readonly string _owner = "sub-lib-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _manager = "sub-lib-manager-" + Guid.NewGuid().ToString("N");
    private readonly string _developer = "sub-lib-developer-" + Guid.NewGuid().ToString("N");
    private readonly string _collaborator = "sub-lib-collaborator-" + Guid.NewGuid().ToString("N");
    private readonly string _stranger = "sub-lib-stranger-" + Guid.NewGuid().ToString("N");

    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-teamlib-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _priorHosted;
    private string? _priorRoot;
    private string _team = "";
    private string _otherTeam = "";
    private string _keyOwner = "";
    private string _keyManager = "";
    private string _keyDeveloper = "";
    private string _keyCollaborator = "";
    private string _keyStranger = "";

    public HostedTeamLibraryTests(ITestOutputHelper output) => _output = output;

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
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _keyOwner = Enroll("dev-lib-owner", _owner, "owner@example.com");
        _keyManager = Enroll("dev-lib-manager", _manager, "manager@example.com");
        _keyDeveloper = Enroll("dev-lib-developer", _developer, "developer@example.com");
        _keyCollaborator = Enroll("dev-lib-collaborator", _collaborator, "collaborator@example.com");
        _keyStranger = Enroll("dev-lib-stranger", _stranger, "stranger@example.com");

        _team = _gateway.TeamRegistry.CreateTeam(_owner, "Library").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _manager, TeamRole.Manager).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _developer, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _collaborator, TeamRole.Collaborator).IsDone);

        // A second team, owned by someone who is not in the first, with the first team's Developer in it too - so
        // "not to any other team" is asked by a person who can see both.
        _otherTeam = _gateway.TeamRegistry.CreateTeam(_stranger, "Other").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(_otherTeam, _developer, TeamRole.Developer).IsDone);
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

    private string Enroll(string deviceId, string subject, string email)
    {
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        var key = _gateway.Devices.Register(deviceId, "M-" + deviceId).DeviceKey;
        _gateway.Devices.SetAccountBinding(deviceId, subject, tenant.Value);
        return key;
    }

    private async Task<(HttpStatusCode Status, string Text)> Send(HttpMethod method, string path, string key, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var resp = await _http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static string[] Ids(string text, string array) =>
        Json(text).GetProperty(array).EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();

    private static object Skill(string id, string body) => new
    {
        id,
        name = "Release checklist",
        summary = "The steps every release follows.",
        triggers = new[] { "release checklist" },
        bodyMarkdown = body,
        authoredBy = ClientClaimedAuthor,
    };

    private static object Workflow(string id) => new
    {
        id,
        name = "Team review",
        summary = "One person builds, another reviews.",
        steps = new[] { new { name = "Build", description = "Build it", doer = "Developer", done = "merged" } },
        instructionsMarkdown = "# Team review\n\nBuild, then review.",
        authoredBy = ClientClaimedAuthor,
    };

    /// <summary>A Manager adds a skill to the team: create the draft, publish it. Returns its id.</summary>
    private async Task<string> ManagerAddsSkill(string id, string body)
    {
        var (created, createdText) = await Send(HttpMethod.Post, $"teams/{_team}/skills", _keyManager, Skill(id, body));
        Assert.True(created == HttpStatusCode.Created, $"create: {created} {createdText}");
        var (published, publishedText) = await Send(HttpMethod.Post, $"teams/{_team}/skills/{id}/publish", _keyManager);
        Assert.True(published == HttpStatusCode.OK, $"publish: {published} {publishedText}");
        return id;
    }

    private async Task<string> ManagerAddsWorkflow(string id)
    {
        var (created, createdText) = await Send(HttpMethod.Post, $"teams/{_team}/workflows", _keyManager, Workflow(id));
        Assert.True(created == HttpStatusCode.Created, $"create: {created} {createdText}");
        var (published, publishedText) = await Send(HttpMethod.Post, $"teams/{_team}/workflows/{id}/publish", _keyManager);
        Assert.True(published == HttpStatusCode.OK, $"publish: {published} {publishedText}");
        return id;
    }

    // ---- #2304 test 1 --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Issue2304Test1_ASkillAddedByAManager_IsAvailableToADeveloperOnThatTeam_AndToNoOtherTeam()
    {
        var id = await ManagerAddsSkill("release-checklist", "# Release checklist\n\nTag, build, verify.");

        // The Developer, from their own account, lists it and fetches its body.
        var (listed, listedText) = await Send(HttpMethod.Get, $"teams/{_team}/skills", _keyDeveloper);
        Assert.Equal(HttpStatusCode.OK, listed);
        Assert.Contains(id, Ids(listedText, "skills"));
        var (body, bodyText) = await Send(HttpMethod.Get, $"teams/{_team}/skills/{id}/body", _keyDeveloper);
        Assert.Equal(HttpStatusCode.OK, body);
        Assert.Contains("Tag, build, verify.", bodyText);

        // Not to any other team: the same Developer, in the other team, does not see it and cannot fetch it.
        var (otherListed, otherText) = await Send(HttpMethod.Get, $"teams/{_otherTeam}/skills", _keyDeveloper);
        Assert.Equal(HttpStatusCode.OK, otherListed);
        Assert.DoesNotContain(id, Ids(otherText, "skills"));
        var (otherBody, _) = await Send(HttpMethod.Get, $"teams/{_otherTeam}/skills/{id}/body", _keyDeveloper);
        Assert.Equal(HttpStatusCode.NotFound, otherBody);

        // And not to anyone's personal library: the Manager who added it does not see it in their own account.
        var (personal, personalText) = await Send(HttpMethod.Get, "gateway/skills", _keyManager);
        Assert.Equal(HttpStatusCode.OK, personal);
        Assert.DoesNotContain(id, Ids(personalText, "skills"));
    }

    [Fact]
    public async Task Issue2304Test1_TheSessionSide_RoleTableAndStoreOnly_CallerSuppliedByTheTest_TheRealPullWaitsOn2311()
    {
        var id = await ManagerAddsSkill("session-skill", "# Session skill\n\nLoaded in the team's session.");
        var team = new TenantId(_team);

        // What a Developer's session on a Director set up for the team will send: GET /gateway/skills and the body, with
        // a key bound to the team's tenant, the Developer as the person behind it (#2311 supplies that). The host's own
        // gate allows both.
        foreach (var pattern in new[] { "/gateway/skills", "/gateway/skills/{id}/body", "/gateway/workflows" })
        {
            var verdict = _gateway.TeamGate.Check("GET", pattern, _ => null, team, () => _developer, _ => TeamOwnership.Callers);
            Assert.True(verdict.Outcome == TeamGateOutcome.Allowed, $"GET {pattern} in the team's tenant as its Developer: {verdict.Outcome} {verdict.Message}");
        }

        // And the store, inside the team's tenant, serves it - in the listing and as the body.
        using (_gateway.TenantBoundary.EnterScope(team))
        {
            Assert.Contains(_gateway.SkillLibrary.ListPublished(), s => s.Id == id && s.Enabled);
            Assert.Contains("Loaded in the team's session.", _gateway.SkillLibrary.GetBody(id, null));
        }

        // Inside the other team's tenant it is not there.
        using (_gateway.TenantBoundary.EnterScope(new TenantId(_otherTeam)))
        {
            Assert.DoesNotContain(_gateway.SkillLibrary.ListPublished(), s => s.Id == id);
            Assert.Null(_gateway.SkillLibrary.GetBody(id, null));
        }

        // A Collaborator's session would be refused (they run none), and so would a change from a Developer's session.
        Assert.Equal(TeamGateOutcome.Refused,
            _gateway.TeamGate.Check("GET", "/gateway/skills", _ => null, team, () => _collaborator, _ => TeamOwnership.Callers).Outcome);
        Assert.Equal(TeamGateOutcome.Refused,
            _gateway.TeamGate.Check("POST", "/gateway/skills/{id}/publish", _ => null, team, () => _developer, _ => TeamOwnership.Callers).Outcome);
    }

    [Fact]
    public async Task Issue2304Test1_AWorkflowAddedByAManager_IsAvailableToADeveloperOnThatTeam_AndToNoOtherTeam()
    {
        var id = await ManagerAddsWorkflow("team-review");

        var (listed, listedText) = await Send(HttpMethod.Get, $"teams/{_team}/workflows", _keyDeveloper);
        Assert.Equal(HttpStatusCode.OK, listed);
        Assert.Contains(id, Ids(listedText, "workflows"));
        var (instructions, instructionsText) = await Send(HttpMethod.Get, $"teams/{_team}/workflows/{id}/instructions", _keyDeveloper);
        Assert.Equal(HttpStatusCode.OK, instructions);
        Assert.Contains("Build, then review.", instructionsText);

        var (other, otherText) = await Send(HttpMethod.Get, $"teams/{_otherTeam}/workflows", _keyDeveloper);
        Assert.Equal(HttpStatusCode.OK, other);
        Assert.DoesNotContain(id, Ids(otherText, "workflows"));
    }

    // ---- #2304 test 2 --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Issue2304Test2_ADevelopersChangeOrRemoveOfATeamSkill_IsRefusedByTheServer()
    {
        var id = await ManagerAddsSkill("guarded-skill", "# Guarded\n\nOriginal words.");

        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, $"teams/{_team}/skills", Skill("developer-skill", "# Mine")),
            (HttpMethod.Put, $"teams/{_team}/skills/{id}/draft", Skill(id, "# Rewritten by the Developer")),
            (HttpMethod.Post, $"teams/{_team}/skills/{id}/publish", null),
            (HttpMethod.Post, $"teams/{_team}/skills/{id}/disable?by=developer", null),
            (HttpMethod.Post, $"teams/{_team}/skills/{id}/clone?newId=developer-copy&by=developer", null),
            (HttpMethod.Delete, $"teams/{_team}/skills/{id}", null),
        };
        foreach (var (method, path, body) in attempts)
        {
            var (status, text) = await Send(method, path, _keyDeveloper, body);
            Assert.True(status == HttpStatusCode.Forbidden, $"{method} {path} as the Developer answered {status}: {text}");
            Assert.Equal(TeamEndpointGate.RefusalCode, Json(text).GetProperty("code").GetString());
            Assert.Contains("Developer may not change the team's shared skills and workflows", Json(text).GetProperty("error").GetString());
        }

        // Nothing changed: the skill is still there, with its original words, and the Developer's own skill was never made.
        var (listed, listedText) = await Send(HttpMethod.Get, $"teams/{_team}/skills", _keyOwner);
        Assert.Equal(HttpStatusCode.OK, listed);
        Assert.Contains(id, Ids(listedText, "skills"));
        Assert.DoesNotContain("developer-skill", Ids(listedText, "skills"));
        Assert.DoesNotContain("developer-copy", Ids(listedText, "skills"));
        var (_, bodyText) = await Send(HttpMethod.Get, $"teams/{_team}/skills/{id}/body", _keyOwner);
        Assert.Contains("Original words.", bodyText);
    }

    [Fact]
    public async Task Issue2304Test2_ADevelopersChangeOrRemoveOfATeamWorkflow_IsRefusedByTheServer()
    {
        var id = await ManagerAddsWorkflow("guarded-flow");

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, $"teams/{_team}/workflows"),
                     (HttpMethod.Put, $"teams/{_team}/workflows/{id}/draft"),
                     (HttpMethod.Post, $"teams/{_team}/workflows/{id}/publish"),
                     (HttpMethod.Delete, $"teams/{_team}/workflows/{id}"),
                 })
        {
            var (status, text) = await Send(method, path, _keyDeveloper, method == HttpMethod.Delete ? null : Workflow(id));
            Assert.True(status == HttpStatusCode.Forbidden, $"{method} {path} as the Developer answered {status}: {text}");
        }

        var (_, listedText) = await Send(HttpMethod.Get, $"teams/{_team}/workflows", _keyOwner);
        Assert.Contains(id, Ids(listedText, "workflows"));
    }

    [Theory]
    [InlineData(TeamRole.Owner)]
    [InlineData(TeamRole.Manager)]
    public async Task OwnerAndManager_ChangeAndRemoveATeamSkill(TeamRole role)
    {
        var key = role == TeamRole.Owner ? _keyOwner : _keyManager;
        var id = await ManagerAddsSkill("changeable-" + role.ToString().ToLowerInvariant(), "# v1");

        var (put, putText) = await Send(HttpMethod.Put, $"teams/{_team}/skills/{id}/draft", key, Skill(id, "# v2 words"));
        Assert.True(put == HttpStatusCode.OK, $"{put} {putText}");
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"teams/{_team}/skills/{id}/publish", key)).Status);
        Assert.Contains("v2 words", (await Send(HttpMethod.Get, $"teams/{_team}/skills/{id}/body", _keyDeveloper)).Text);

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"teams/{_team}/skills/{id}", key)).Status);
        Assert.DoesNotContain(id, Ids((await Send(HttpMethod.Get, $"teams/{_team}/skills", _keyDeveloper)).Text, "skills"));
    }

    // ---- #2304 test 3 --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("skills")]
    [InlineData("workflows")]
    [InlineData("library")]
    public async Task Issue2304Test3_ACollaborator_CannotListThem(string what)
    {
        await ManagerAddsSkill("hidden-from-collaborators", "# Hidden");

        var (status, text) = await Send(HttpMethod.Get, $"teams/{_team}/{what}", _keyCollaborator);

        Assert.True(status == HttpStatusCode.Forbidden, $"GET teams/{{team}}/{what} as a Collaborator answered {status}: {text}");
        Assert.DoesNotContain("hidden-from-collaborators", text);
        Assert.Contains("Collaborator may not use the team's shared skills and workflows", Json(text).GetProperty("error").GetString());
    }

    // ---- not a member, built-ins, personal accounts ----------------------------------------------------------------

    [Theory]
    [InlineData("GET", "skills")]
    [InlineData("GET", "workflows")]
    [InlineData("GET", "library")]
    [InlineData("POST", "skills")]
    public async Task SomeoneWhoIsNotAMember_IsToldThereIsNoSuchTeam(string method, string what)
    {
        var (status, text) = await Send(new HttpMethod(method), $"teams/{_team}/{what}", _keyStranger,
            method == "POST" ? Skill("stranger-skill", "# Stranger") : null);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(Api.TeamEndpoints.NoSuchTeamRefusal, Json(text).GetProperty("error").GetString());
    }

    [Fact]
    public async Task BuiltInSkillsAndWorkflows_StayReadOnlyInsideATeam_EvenForItsOwner()
    {
        var (put, putText) = await Send(HttpMethod.Put, $"teams/{_team}/skills/move-session/draft", _keyOwner,
            new { name = "Mine", summary = "Rewritten.", bodyMarkdown = "# mine" });
        Assert.Equal(HttpStatusCode.BadRequest, put);
        Assert.Contains("built-in", Json(putText).GetProperty("error").GetString());

        var (delete, _) = await Send(HttpMethod.Delete, $"teams/{_team}/skills/move-session", _keyOwner);
        Assert.Equal(HttpStatusCode.BadRequest, delete);

        var (flowDelete, _) = await Send(HttpMethod.Delete, $"teams/{_team}/workflows/mission", _keyOwner);
        Assert.Equal(HttpStatusCode.BadRequest, flowDelete);

        // Still served to the team, unchanged and marked not editable.
        var (listed, listedText) = await Send(HttpMethod.Get, $"teams/{_team}/skills", _keyDeveloper);
        Assert.Equal(HttpStatusCode.OK, listed);
        var builtIn = Json(listedText).GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "move-session");
        Assert.True(builtIn.GetProperty("isBuiltIn").GetBoolean());
        Assert.False(builtIn.GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task APersonalAccountsOwnSkills_BehaveExactlyAsBefore_AndNeverReachTheTeam()
    {
        // The Owner authors a skill in their OWN account, on the ordinary routes.
        var (created, createdText) = await Send(HttpMethod.Post, "gateway/skills", _keyOwner, Skill("my-own-skill", "# Mine alone"));
        Assert.True(created == HttpStatusCode.Created, $"{created} {createdText}");
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "gateway/skills/my-own-skill/publish", _keyOwner)).Status);
        Assert.Contains("my-own-skill", Ids((await Send(HttpMethod.Get, "gateway/skills", _keyOwner)).Text, "skills"));
        Assert.Contains("Mine alone", (await Send(HttpMethod.Get, "gateway/skills/my-own-skill/body", _keyOwner)).Text);

        // A person in no team at all keeps the whole of today's library - built-ins included.
        var (strangerList, strangerText) = await Send(HttpMethod.Get, "gateway/skills", _keyStranger);
        Assert.Equal(HttpStatusCode.OK, strangerList);
        Assert.Contains("move-session", Ids(strangerText, "skills"));
        Assert.DoesNotContain("my-own-skill", Ids(strangerText, "skills"));

        // The team does not get the Owner's personal skill.
        Assert.DoesNotContain("my-own-skill", Ids((await Send(HttpMethod.Get, $"teams/{_team}/skills", _keyDeveloper)).Text, "skills"));
    }

    // ---- who changed it: the member the server identified (review findings F1, F3) ---------------------------------

    [Fact]
    public async Task ChangedBy_IsTheMemberTheServerIdentified_StoredAsAReferenceWithNoEmailOrSubject()
    {
        var id = await ManagerAddsSkill("stamped-skill", "# Stamped");
        var (cloned, clonedText) = await Send(HttpMethod.Post,
            $"teams/{_team}/skills/{id}/clone?newId=stamped-copy&by={Uri.EscapeDataString(ClientClaimedAuthor)}", _keyOwner);
        Assert.True(cloned == HttpStatusCode.Created, $"{cloned} {clonedText}");

        // The recorded author - the value the store also writes to the Gateway log - is the opaque member reference of
        // whoever made the request, whatever the client typed.
        var expected = new[]
        {
            (Id: id, Who: _manager),
            (Id: "stamped-copy", Who: _owner),
        };
        foreach (var (skillId, who) in expected)
        {
            var (status, text) = await Send(HttpMethod.Get, $"teams/{_team}/skills/{skillId}/versions", _keyOwner);
            Assert.Equal(HttpStatusCode.OK, status);
            foreach (var version in Json(text).GetProperty("versions").EnumerateArray())
            {
                var author = version.GetProperty("authoredBy").GetString()!;
                Assert.Equal(Api.TeamLibraryEndpoints.MemberReference(_team, who), author);
                Assert.DoesNotContain("@", author);
                Assert.DoesNotContain(who, author);
            }
        }

        // The page shows the member's name as the Team page shows it - the Owner's clone under the Owner.
        var (_, libraryText) = await Send(HttpMethod.Get, $"teams/{_team}/library", _keyDeveloper);
        var items = Json(libraryText).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal("manager@example.com", items.Single(i => i.GetProperty("id").GetString() == id).GetProperty("changedBy").GetString());
        Assert.Equal("owner@example.com", items.Single(i => i.GetProperty("id").GetString() == "stamped-copy").GetProperty("changedBy").GetString());
        Assert.DoesNotContain(ClientClaimedAuthor, libraryText);
    }

    [Fact]
    public async Task ChangedBy_APersonalAccountsAuthor_IsStillWhatTheClientTyped()
    {
        // Outside a team nothing changes: the ordinary routes record the author the client gives, as before.
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, "gateway/skills", _keyStranger, Skill("personal-author", "# P"))).Status);
        var (_, text) = await Send(HttpMethod.Get, "gateway/skills/personal-author/versions", _keyStranger);
        Assert.Equal(ClientClaimedAuthor, Json(text).GetProperty("versions")[0].GetProperty("authoredBy").GetString());
    }

    // ---- the page's read -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TeamRole.Owner, true)]
    [InlineData(TeamRole.Manager, true)]
    [InlineData(TeamRole.Developer, false)]
    public async Task Library_SaysWhatTheCallerMayDo_AndListsTheTeamsOwn(TeamRole role, bool mayChange)
    {
        await ManagerAddsSkill("listed-skill", "# Listed");
        await ManagerAddsWorkflow("listed-flow");
        var key = role switch { TeamRole.Owner => _keyOwner, TeamRole.Manager => _keyManager, _ => _keyDeveloper };

        var (status, text) = await Send(HttpMethod.Get, $"teams/{_team}/library", key);

        Assert.Equal(HttpStatusCode.OK, status);
        _output.WriteLine(text);
        var page = Json(text);
        Assert.Equal(TeamRoles.Label(role), page.GetProperty("team").GetProperty("role").GetString());
        Assert.Equal("Library", page.GetProperty("team").GetProperty("name").GetString());
        Assert.Equal(mayChange, page.GetProperty("canChange").GetBoolean());
        if (mayChange)
            Assert.Equal(JsonValueKind.Null, page.GetProperty("changeRefusal").ValueKind);
        else
            Assert.Equal("In this team you are a Developer, and a Developer may not change the team's shared skills and workflows.",
                page.GetProperty("changeRefusal").GetString());

        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Contains(items, i => i.GetProperty("id").GetString() == "listed-skill" && i.GetProperty("kind").GetString() == "Skill");
        Assert.Contains(items, i => i.GetProperty("id").GetString() == "listed-flow" && i.GetProperty("kind").GetString() == "Workflow");
        Assert.All(items, i => Assert.Equal(mayChange, i.GetProperty("canChange").GetBoolean()));
        Assert.All(items, i => Assert.Equal("manager@example.com", i.GetProperty("changedBy").GetString()));
        Assert.DoesNotContain(items, i => i.GetProperty("id").GetString() == "move-session");
    }
}
