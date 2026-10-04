using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Core.Teams;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Setting a Director up FOR A TEAM (devthrottle_internal#2311, screen D1), and moving it to another team
/// (screen D3), against a fake hosted Gateway. No browser, no network, no disk: the sign-in, the HTTP handler
/// and both persist actions are injected and captured.
///
/// What is proven:
///  - 404 from the teams route: nothing is asked, the enrollment request is byte-for-byte the one sent before
///    Teams, and no team is recorded (no chip).
///  - No team listed: nothing is asked, the request is today's, and the personal account is recorded.
///  - Teams listed: the person is offered exactly the Gateway's teams plus the personal account, and the chosen
///    team's id is what is sent; choosing personal sends no team id.
///  - A 403 is shown in the Gateway's own words; a cancelled question and a failed teams listing enroll nothing.
///  - The move call sends the device and the team and returns the new key; its refusal comes back as it is.
/// </summary>
[Collection(HostedGatewayUrlCollection.Name)]
public class HostedTeamEnrollRunnerTests
{
    private const string DeviceId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string MachineName = "SOREN_NORTH";

    private const string AccountToken = "account-access-token-teams";

    private sealed record Captured(string Method, string Path, string Body, string? Bearer);

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder, List<Captured> log)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            log.Add(new Captured(request.Method.Method, request.RequestUri!.AbsolutePath, body,
                request.Headers.Authorization?.Parameter));
            return responder(request);
        }
    }

    // Everything a run captured. The lists are the ones the runner's injected actions write into.
    private sealed record Harness(
        GatewayAccountEnrollRunner Runner, List<Captured> Requests, List<(string Url, string Key)> Keys, List<DirectorTeam?> Teams);

    private static Harness Build(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var requests = new List<Captured>();
        var keys = new List<(string, string)>();
        var teams = new List<DirectorTeam?>();
        var runner = new GatewayAccountEnrollRunner(
            signIn: _ => Task.FromResult(new DevThrottleTokens(AccountToken, "refresh")),
            handlerFactory: () => new CapturingHandler(responder, requests),
            persist: (url, key) => keys.Add((url, key)),
            persistTeam: team => teams.Add(team));
        return new Harness(runner, requests, keys, teams);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string KeyReply = "{\"deviceKey\":\"team-key-1\",\"deviceCount\":1}";

    private static string TeamsReply(params (string Id, string Name, string Role, int Members)[] teams) =>
        JsonSerializer.Serialize(new
        {
            teams = teams.Select(t => new { teamId = t.Id, name = t.Name, role = t.Role, memberCount = t.Members }),
        });

    private static Func<HttpRequestMessage, HttpResponseMessage> Gateway(HttpResponseMessage teams, Func<HttpResponseMessage>? enroll = null) =>
        req => req.RequestUri!.AbsolutePath switch
        {
            "/devices/enroll-hosted/teams" => teams,
            "/devices/enroll-hosted" => (enroll ?? (() => Json(HttpStatusCode.OK, KeyReply)))(),
            _ => throw new InvalidOperationException("unexpected route " + req.RequestUri.AbsolutePath),
        };

    private static Func<TeamQuestion, CancellationToken, Task<TeamAnswer?>> NeverAsked =>
        (_, _) => throw new InvalidOperationException("The person must not be asked.");

    // The enrollment body the client sent BEFORE Teams existed, for the same device - the reference "today's
    // request" is compared against.
    private static async Task<string> TodaysEnrollBodyAsync()
    {
        var log = new List<Captured>();
        var runner = new GatewayAccountEnrollRunner(
            signIn: _ => Task.FromResult(new DevThrottleTokens(AccountToken, "refresh")),
            handlerFactory: () => new CapturingHandler(_ => Json(HttpStatusCode.OK, KeyReply), log),
            persist: (_, _) => { },
            persistTeam: _ => { });
        var result = await runner.SignInAndEnrollHostedAsync(DeviceId, MachineName, CancellationToken.None);
        Assert.True(result.Success);
        return Assert.Single(log).Body;
    }

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_TeamsRouteAnswers404_NotAskedTodaysRequestNoTeamRecorded()
    {
        var h = Build(Gateway(Json(HttpStatusCode.NotFound, "")));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName, NeverAsked, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.Value!.Team);
        Assert.Null(result.Value.DirectorName);
        var enroll = Assert.Single(h.Requests, r => r.Path == "/devices/enroll-hosted");
        Assert.Equal(await TodaysEnrollBodyAsync(), enroll.Body);
        Assert.Equal("team-key-1", Assert.Single(h.Keys).Key);
        // null = "the Gateway has no teams": the store forgets any team, so no chip is drawn.
        Assert.Null(Assert.Single(h.Teams));
    }

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_NoTeamListed_NotAskedTodaysRequestPersonalRecorded()
    {
        var h = Build(Gateway(Json(HttpStatusCode.OK, TeamsReply())));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName, NeverAsked, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        var enroll = Assert.Single(h.Requests, r => r.Path == "/devices/enroll-hosted");
        Assert.Equal(await TodaysEnrollBodyAsync(), enroll.Body);
        var team = Assert.Single(h.Teams);
        Assert.NotNull(team);
        Assert.True(team!.IsPersonal);
        Assert.Equal("Personal", team.Name);
    }

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_TeamsListed_OffersTheGatewayListPlusPersonalAndSendsTheChosenId()
    {
        var h = Build(Gateway(Json(HttpStatusCode.OK, TeamsReply(("t-dev", "DevThrottle", "owner", 5), ("t-acme", "Acme", "developer", 3)))));
        TeamQuestion? asked = null;

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName,
            (q, _) => { asked = q; return Task.FromResult<TeamAnswer?>(new TeamAnswer(q.Choices[1], "SOREN_NORTH - Acme")); },
            CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(asked);
        Assert.Equal(new string?[] { "t-dev", "t-acme", null }, asked!.Choices.Select(c => c.TeamId));
        Assert.Equal(new[] { "DevThrottle", "Acme", "Personal" }, asked.Choices.Select(c => c.Name));
        Assert.Equal("You are the Owner, 5 people, you pay", asked.Choices[0].Detail);
        Assert.Equal("Just you", asked.Choices[2].Detail);

        // The teams listing carried the account token, as enroll-hosted does.
        Assert.Equal(AccountToken, Assert.Single(h.Requests, r => r.Path == "/devices/enroll-hosted/teams").Bearer);

        var enroll = Assert.Single(h.Requests, r => r.Path == "/devices/enroll-hosted");
        var body = JsonNode.Parse(enroll.Body)!.AsObject();
        Assert.Equal("t-acme", (string?)body["teamId"]);
        Assert.Equal(DeviceId, (string?)body["deviceId"]);
        Assert.Equal(AccountToken, enroll.Bearer);

        Assert.Equal(new DirectorTeam("t-acme", "Acme"), Assert.Single(h.Teams));
        Assert.Equal("SOREN_NORTH - Acme", result.Value!.DirectorName);
        Assert.Equal(new DirectorTeam("t-acme", "Acme"), result.Value.Team);
    }

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_ChoosesPersonal_SendsNoTeamId()
    {
        var h = Build(Gateway(Json(HttpStatusCode.OK, TeamsReply(("t-dev", "DevThrottle", "manager", 4)))));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName,
            (q, _) => Task.FromResult<TeamAnswer?>(new TeamAnswer(q.Choices.Single(c => c.IsPersonal), "Soren - home lab")),
            CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        var enroll = Assert.Single(h.Requests, r => r.Path == "/devices/enroll-hosted");
        Assert.Equal(await TodaysEnrollBodyAsync(), enroll.Body);
        Assert.True(Assert.Single(h.Teams)!.IsPersonal);
    }

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_QuestionCancelled_EnrollsAndStoresNothing()
    {
        var h = Build(Gateway(Json(HttpStatusCode.OK, TeamsReply(("t-dev", "DevThrottle", "owner", 5)))));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName,
            (_, _) => Task.FromResult<TeamAnswer?>(null), CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(h.Requests, r => r.Path == "/devices/enroll-hosted");
        Assert.Empty(h.Keys);
        Assert.Empty(h.Teams);
    }

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_Enroll403_ShowsTheGatewaysWordsAndStoresNothing()
    {
        const string refusal = "You are a Collaborator on DevThrottle, so you cannot run sessions there.";
        var h = Build(Gateway(
            Json(HttpStatusCode.OK, TeamsReply(("t-dev", "DevThrottle", "developer", 5))),
            () => Json(HttpStatusCode.Forbidden, JsonSerializer.Serialize(new { error = refusal }))));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName,
            (q, _) => Task.FromResult<TeamAnswer?>(new TeamAnswer(q.Choices[0], "Soren - DevThrottle")), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(refusal, result.ErrorMessage);
        Assert.Empty(h.Keys);
        Assert.Empty(h.Teams);
    }

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_TeamsRouteFails_FailsWithoutEnrollingOnPersonal()
    {
        var h = Build(Gateway(Json(HttpStatusCode.InternalServerError, "{\"error\":\"The team list could not be read just now.\"}")));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName, NeverAsked, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("The team list could not be read just now.", result.ErrorMessage);
        Assert.DoesNotContain(h.Requests, r => r.Path == "/devices/enroll-hosted");
        Assert.Empty(h.Teams);
    }

    [Fact]
    public async Task SignInAndListHostedTeams_404_SaysTeamsAreNotReleased()
    {
        var h = Build(Gateway(Json(HttpStatusCode.NotFound, "")));

        var result = await h.Runner.SignInAndListHostedTeamsAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value!.TeamsReleased);
        Assert.Empty(result.Value.Teams);
    }

    [Fact]
    public async Task MoveHostedDirector_AfterSignIn_SendsDeviceAndTeamAndReturnsTheNewKey()
    {
        var h = Build(req => req.RequestUri!.AbsolutePath switch
        {
            "/devices/enroll-hosted/teams" => Json(HttpStatusCode.OK, TeamsReply(("t-dev", "DevThrottle", "owner", 5))),
            "/devices/enroll-hosted/move" => Json(HttpStatusCode.OK, "{\"deviceKey\":\"moved-key\"}"),
            _ => throw new InvalidOperationException(req.RequestUri.AbsolutePath),
        });

        Assert.True((await h.Runner.SignInAndListHostedTeamsAsync(CancellationToken.None)).Success);
        var moved = await h.Runner.MoveHostedDirectorAsync(DeviceId, "t-dev", CancellationToken.None);

        Assert.True(moved.Success, moved.ErrorMessage);
        Assert.Equal("moved-key", moved.Value);
        var move = Assert.Single(h.Requests, r => r.Path == "/devices/enroll-hosted/move");
        Assert.Equal("POST", move.Method);
        Assert.Equal(AccountToken, move.Bearer);
        var body = JsonNode.Parse(move.Body)!.AsObject();
        Assert.Equal(DeviceId, (string?)body["deviceId"]);
        Assert.Equal("t-dev", (string?)body["teamId"]);
        // The runner stores nothing on a move; the mover stores key and team together.
        Assert.Empty(h.Keys);
        Assert.Empty(h.Teams);
    }

    [Fact]
    public async Task MoveHostedDirector_GatewayRefuses_ReturnsItsWords()
    {
        const string refusal = "This Director still has a session registered. Close every session, then move it.";
        var h = Build(req => req.RequestUri!.AbsolutePath switch
        {
            "/devices/enroll-hosted/teams" => Json(HttpStatusCode.OK, TeamsReply()),
            _ => Json(HttpStatusCode.Conflict, JsonSerializer.Serialize(new { error = refusal })),
        });

        await h.Runner.SignInAndListHostedTeamsAsync(CancellationToken.None);
        var moved = await h.Runner.MoveHostedDirectorAsync(DeviceId, null, CancellationToken.None);

        Assert.False(moved.Success);
        Assert.Equal(refusal, moved.ErrorMessage);
    }

    [Fact]
    public async Task MoveHostedDirector_WithoutSignIn_FailsAndSendsNothing()
    {
        var h = Build(_ => throw new InvalidOperationException("nothing may be sent"));

        var moved = await h.Runner.MoveHostedDirectorAsync(DeviceId, "t-dev", CancellationToken.None);

        Assert.False(moved.Success);
        Assert.Empty(h.Requests);
    }
}
