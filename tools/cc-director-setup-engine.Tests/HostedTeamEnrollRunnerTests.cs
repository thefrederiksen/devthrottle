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
/// The fake answers <c>GET /healthz</c> with what the real hosted Gateway sends: today's production answer,
/// which carries NO <c>teams</c> field (review finding F1 - a Gateway from before Teams sends the signal
/// missing, and answers 401 on the teams route, never 404), or that answer with <c>"teams": true</c>.
///
/// What is proven:
///  - Signal missing or false: no teams call, nothing asked, the enrollment body is the pinned literal sent
///    before Teams, and no team is recorded.
///  - Signal true: no team listed means nothing asked and the same literal body, personal recorded; teams listed
///    means the offer is exactly the Gateway's teams plus personal and the chosen id is sent; any non-200 from
///    the teams route - 404 and 401 included - is an error in the Gateway's words, and nothing is enrolled.
///  - An unreadable teams reply or a role that cannot run sessions is a failure result, not an exception.
///  - The move sends the device and the team; a 401 says to sign in again; a refusal comes back as it is.
/// </summary>
[Collection(HostedGatewayUrlCollection.Name)]
public class HostedTeamEnrollRunnerTests
{
    private const string DeviceId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string MachineName = "SOREN_NORTH";
    private const string AccountToken = "account-access-token-teams";
    private const string CurrentKey = "current-device-key-of-this-director";

    // The hosted Gateway's real /healthz answer on 4 October 2026 (production, before Teams): no "teams" field.
    private const string PreTeamsHealth =
        "{\"status\":\"ok\",\"version\":\"2.13.0\",\"commit\":\"a53d6bd\",\"subsystems\":{\"statistics\":\"available\",\"ai-attribution\":\"available\"},\"serverTime\":\"2026-10-04T15:21:48.0737644Z\",\"directorId\":null,\"machineName\":null}";

    private const string TeamsHealth =
        "{\"status\":\"ok\",\"version\":\"2.14.0\",\"commit\":\"0000000\",\"teams\":true,\"directorId\":null,\"machineName\":null}";

    // The enrollment body a Director sent before Teams existed, written out by hand (review finding F6), so a
    // change to the no-team request fails this test on its own.
    private static readonly string TodaysEnrollBody =
        "{\"deviceId\":\"" + DeviceId + "\",\"machineName\":\"" + MachineName + "\",\"platform\":\""
        + GatewayAccountEnrollRunner.WorkstationPlatform + "\",\"deviceType\":\"workstation\"}";

    private const string KeyReply = "{\"deviceKey\":\"team-key-1\",\"deviceCount\":1}";

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
        GatewayAccountEnrollRunner Runner, List<Captured> Requests, List<(string Url, string Key)> Keys,
        List<DirectorTeam?> Teams, Func<int> SignIns);

    private static Harness Build(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var requests = new List<Captured>();
        var keys = new List<(string, string)>();
        var teams = new List<DirectorTeam?>();
        var signIns = 0;
        var runner = new GatewayAccountEnrollRunner(
            signIn: _ => { signIns++; return Task.FromResult(new DevThrottleTokens(AccountToken, "refresh")); },
            handlerFactory: () => new CapturingHandler(responder, requests),
            persist: (url, key) => keys.Add((url, key)),
            persistTeam: team => teams.Add(team));
        return new Harness(runner, requests, keys, teams, () => signIns);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ok(string json) => Json(HttpStatusCode.OK, json);

    private static string TeamsReply(params (string Id, string Name, string Role, int Members)[] teams) =>
        JsonSerializer.Serialize(new
        {
            teams = teams.Select(t => new { teamId = t.Id, name = t.Name, role = t.Role, memberCount = t.Members }),
        });

    private static HttpResponseMessage AuthGate401() => Json(HttpStatusCode.Unauthorized, "{\"error\":\"missing or invalid token\"}");

    private static Func<HttpRequestMessage, HttpResponseMessage> Gateway(
        string health, Func<HttpResponseMessage>? teams = null, Func<HttpResponseMessage>? enroll = null, Func<HttpResponseMessage>? move = null) =>
        req => req.RequestUri!.AbsolutePath switch
        {
            "/healthz" => Ok(health),
            // A Gateway without the teams route: the auth gate answers 401 before routing.
            "/devices/enroll-hosted/teams" => (teams ?? AuthGate401)(),
            "/devices/enroll-hosted" => (enroll ?? (() => Ok(KeyReply)))(),
            "/devices/enroll-hosted/move" => (move ?? AuthGate401)(),
            _ => throw new InvalidOperationException("unexpected route " + req.RequestUri.AbsolutePath),
        };

    private static Func<TeamQuestion, CancellationToken, Task<TeamAnswer?>> NeverAsked =>
        (_, _) => throw new InvalidOperationException("The person must not be asked.");

    // ===================== Gateway without Teams =====================

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_PreTeamsGateway_NoTeamsCallNotAskedTodaysBodyNoTeamRecorded()
    {
        var h = Build(Gateway(PreTeamsHealth));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName, NeverAsked, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.Value!.Team);
        Assert.Null(result.Value.DirectorName);
        Assert.DoesNotContain(h.Requests, r => r.Path == "/devices/enroll-hosted/teams");
        Assert.Equal(TodaysEnrollBody, Assert.Single(h.Requests, r => r.Path == "/devices/enroll-hosted").Body);
        Assert.Equal("team-key-1", Assert.Single(h.Keys).Key);
        // null = "the Gateway has no teams": the store forgets any team.
        Assert.Null(Assert.Single(h.Teams));
    }

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_HealthSaysTeamsFalse_SameAsPreTeams()
    {
        var h = Build(Gateway(PreTeamsHealth.Replace("\"status\":\"ok\"", "\"status\":\"ok\",\"teams\":false")));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName, NeverAsked, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain(h.Requests, r => r.Path == "/devices/enroll-hosted/teams");
        Assert.Equal(TodaysEnrollBody, Assert.Single(h.Requests, r => r.Path == "/devices/enroll-hosted").Body);
    }

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_HealthCheckFails_NothingSignedInOrEnrolled()
    {
        var h = Build(req => req.RequestUri!.AbsolutePath == "/healthz"
            ? Json(HttpStatusCode.ServiceUnavailable, "")
            : throw new InvalidOperationException("nothing else may be asked"));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName, NeverAsked, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("did not answer its health check (HTTP 503)", result.ErrorMessage);
        Assert.Equal(0, h.SignIns());
        Assert.Empty(h.Keys);
        Assert.Empty(h.Teams);
    }

    [Fact]
    public async Task SignInAndListHostedTeams_PreTeamsGateway_SaysNoTeamsWithoutSigningIn()
    {
        var h = Build(Gateway(PreTeamsHealth));

        var result = await h.Runner.SignInAndListHostedTeamsAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value!.TeamsReleased);
        Assert.Equal(0, h.SignIns());
        Assert.DoesNotContain(h.Requests, r => r.Path == "/devices/enroll-hosted/teams");
    }

    // ===================== Gateway with Teams =====================

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_NoTeamListed_NotAskedTodaysBodyPersonalRecorded()
    {
        var h = Build(Gateway(TeamsHealth, teams: () => Ok(TeamsReply())));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName, NeverAsked, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(TodaysEnrollBody, Assert.Single(h.Requests, r => r.Path == "/devices/enroll-hosted").Body);
        var team = Assert.Single(h.Teams);
        Assert.NotNull(team);
        Assert.True(team!.IsPersonal);
        Assert.Equal("Personal", team.Name);
    }

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_TeamsListed_OffersTheGatewayListPlusPersonalAndSendsTheChosenId()
    {
        var h = Build(Gateway(TeamsHealth, teams: () => Ok(TeamsReply(("t-dev", "DevThrottle", "owner", 5), ("t-acme", "Acme", "developer", 3)))));
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
    public async Task SignInChooseTeamAndEnrollHosted_ChoosesPersonal_SendsTodaysBody()
    {
        var h = Build(Gateway(TeamsHealth, teams: () => Ok(TeamsReply(("t-dev", "DevThrottle", "manager", 4)))));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName,
            (q, _) => Task.FromResult<TeamAnswer?>(new TeamAnswer(q.Choices.Single(c => c.IsPersonal), "Soren - home lab")),
            CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(TodaysEnrollBody, Assert.Single(h.Requests, r => r.Path == "/devices/enroll-hosted").Body);
        Assert.True(Assert.Single(h.Teams)!.IsPersonal);
    }

    [Fact]
    public async Task SignInChooseTeamAndEnrollHosted_QuestionCancelled_EnrollsAndStoresNothing()
    {
        var h = Build(Gateway(TeamsHealth, teams: () => Ok(TeamsReply(("t-dev", "DevThrottle", "owner", 5)))));

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
        var h = Build(Gateway(TeamsHealth,
            teams: () => Ok(TeamsReply(("t-dev", "DevThrottle", "developer", 5))),
            enroll: () => Json(HttpStatusCode.Forbidden, JsonSerializer.Serialize(new { error = refusal }))));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName,
            (q, _) => Task.FromResult<TeamAnswer?>(new TeamAnswer(q.Choices[0], "SOREN_NORTH - DevThrottle")), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(refusal, result.ErrorMessage);
        Assert.Empty(h.Keys);
        Assert.Empty(h.Teams);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{\"error\":\"There is no such route.\"}", "There is no such route.")]
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":\"missing or invalid token\"}", "missing or invalid token")]
    [InlineData(HttpStatusCode.InternalServerError, "{\"error\":\"The team list could not be read just now.\"}", "The team list could not be read just now.")]
    public async Task SignInChooseTeamAndEnrollHosted_TeamsReleasedButListNot200_AnErrorAsItIsAndNothingEnrolled(
        HttpStatusCode status, string body, string shown)
    {
        var h = Build(Gateway(TeamsHealth, teams: () => Json(status, body)));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName, NeverAsked, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(shown, result.ErrorMessage);
        Assert.DoesNotContain(h.Requests, r => r.Path == "/devices/enroll-hosted");
        Assert.Empty(h.Teams);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"teams\":[{\"teamId\":\"t-1\",\"name\":\"One\",\"role\":\"collaborator\",\"memberCount\":2}]}")]
    [InlineData("{\"teams\":[{\"teamId\":\"\",\"name\":\"One\",\"role\":\"owner\",\"memberCount\":2}]}")]
    public async Task SignInChooseTeamAndEnrollHosted_UnreadableTeamList_AFailureResultNotAnException(string reply)
    {
        var h = Build(Gateway(TeamsHealth, teams: () => Ok(reply)));

        var result = await h.Runner.SignInChooseTeamAndEnrollHostedAsync(DeviceId, MachineName, NeverAsked, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("hosted gateway", result.ErrorMessage);
        Assert.DoesNotContain(h.Requests, r => r.Path == "/devices/enroll-hosted");
    }

    // ===================== The move (D3) =====================

    [Fact]
    public async Task MoveHostedDirector_AfterSignIn_SendsDeviceAndTeamAndReturnsTheNewKey()
    {
        var h = Build(Gateway(TeamsHealth,
            teams: () => Ok(TeamsReply(("t-dev", "DevThrottle", "owner", 5))),
            move: () => Ok("{\"deviceKey\":\"moved-key\"}")));

        Assert.True((await h.Runner.SignInAndListHostedTeamsAsync(CancellationToken.None)).Success);
        var moved = await h.Runner.MoveHostedDirectorAsync(CurrentKey, "t-dev", CancellationToken.None);

        Assert.True(moved.Success, moved.ErrorMessage);
        Assert.Equal("moved-key", moved.Value);
        var move = Assert.Single(h.Requests, r => r.Path == "/devices/enroll-hosted/move");
        Assert.Equal("POST", move.Method);
        Assert.Equal(AccountToken, move.Bearer);
        var body = JsonNode.Parse(move.Body)!.AsObject();
        // The Gateway contract: the Director is named by the key it holds now, not by its id.
        Assert.Equal(CurrentKey, (string?)body["deviceKey"]);
        Assert.Null(body["deviceId"]);
        Assert.Equal("t-dev", (string?)body["teamId"]);
        // The runner stores nothing on a move; the mover stores team and key.
        Assert.Empty(h.Keys);
        Assert.Empty(h.Teams);
    }

    [Fact]
    public async Task MoveHostedDirector_GatewayRefuses_ReturnsItsWords()
    {
        const string refusal = "This Director still has a session registered. Close every session, then move it.";
        var h = Build(Gateway(TeamsHealth,
            teams: () => Ok(TeamsReply()),
            move: () => Json(HttpStatusCode.Conflict, JsonSerializer.Serialize(new { error = refusal }))));

        await h.Runner.SignInAndListHostedTeamsAsync(CancellationToken.None);
        var moved = await h.Runner.MoveHostedDirectorAsync(CurrentKey, null, CancellationToken.None);

        Assert.False(moved.Success);
        Assert.Equal(refusal, moved.ErrorMessage);
    }

    [Fact]
    public async Task MoveHostedDirector_SignInExpired_SaysToSignInAgain()
    {
        var h = Build(Gateway(TeamsHealth, teams: () => Ok(TeamsReply())));

        await h.Runner.SignInAndListHostedTeamsAsync(CancellationToken.None);
        var moved = await h.Runner.MoveHostedDirectorAsync(CurrentKey, "t-dev", CancellationToken.None);

        Assert.False(moved.Success);
        Assert.Equal(GatewayAccountEnrollRunner.MoveSignInExpired, moved.ErrorMessage);
    }

    [Fact]
    public async Task MoveHostedDirector_200WithNoKey_ReportsTheMoveWithAnEmptyKey()
    {
        // A 200 means the Gateway moved the Director; an unreadable reply must not read as "not moved".
        var h = Build(Gateway(TeamsHealth, teams: () => Ok(TeamsReply()), move: () => Ok("garbage")));

        await h.Runner.SignInAndListHostedTeamsAsync(CancellationToken.None);
        var moved = await h.Runner.MoveHostedDirectorAsync(CurrentKey, "t-dev", CancellationToken.None);

        Assert.True(moved.Success);
        Assert.Equal("", moved.Value);
    }

    [Fact]
    public async Task MoveHostedDirector_WithoutSignIn_FailsAndSendsNothing()
    {
        var h = Build(_ => throw new InvalidOperationException("nothing may be sent"));

        var moved = await h.Runner.MoveHostedDirectorAsync(CurrentKey, "t-dev", CancellationToken.None);

        Assert.False(moved.Success);
        Assert.Empty(h.Requests);
    }
}
