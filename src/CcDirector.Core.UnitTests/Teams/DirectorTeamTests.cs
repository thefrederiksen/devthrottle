using System.Net;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.UnitTests.Sessions;
using CcDirector.Core.Sessions;
using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Teams;

/// <summary>
/// The Director's side of Teams (devthrottle_internal#2311): which team a Director works for, stored per
/// Director instance (never machine-wide); the chip colour derived from the team id; the D1 options; and the D3
/// rule that a Director changes team only with every session closed.
/// </summary>
public sealed class DirectorTeamStoreTests : IDisposable
{
    private readonly string _machineRoot = Path.Combine(Path.GetTempPath(), "dt-team-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_machineRoot))
            Directory.Delete(_machineRoot, recursive: true);
    }

    private string Home(string slug) => Path.Combine(_machineRoot, "instances", slug);

    [Fact]
    public void SaveAt_TwoInstanceHomes_HoldTwoDifferentTeams()
    {
        DirectorTeamStore.SaveAt(Home("default"), new DirectorTeam("t-dev", "DevThrottle"));
        DirectorTeamStore.SaveAt(Home("home-lab"), DirectorTeam.Personal);

        Assert.Equal(new DirectorTeam("t-dev", "DevThrottle"), DirectorTeamStore.LoadAt(Home("default")));
        Assert.Equal(DirectorTeam.Personal, DirectorTeamStore.LoadAt(Home("home-lab")));
    }

    [Fact]
    public void TeamFileAt_LivesBesideTheInstancesOwnKey()
    {
        var file = DirectorTeamStore.TeamFileAt(Home("default"));

        Assert.Equal(Path.Combine(Home("default"), "config", "director", "gateway-team.json"), file);
        // Not at the machine root: a team is never machine-wide.
        Assert.DoesNotContain(Path.Combine(_machineRoot, "config"), file);
    }

    [Fact]
    public void LoadAt_NothingRecorded_ReturnsNull()
    {
        Assert.Null(DirectorTeamStore.LoadAt(Home("never-enrolled")));
    }

    [Fact]
    public void SaveAt_Personal_RoundTripsAsNullTeamId()
    {
        DirectorTeamStore.SaveAt(Home("a"), DirectorTeam.Personal);

        var loaded = DirectorTeamStore.LoadAt(Home("a"));

        Assert.NotNull(loaded);
        Assert.True(loaded!.IsPersonal);
        Assert.Equal("Personal", loaded.Name);
    }

    [Fact]
    public void SaveAt_ASecondTeam_ReplacesTheFirst()
    {
        DirectorTeamStore.SaveAt(Home("a"), new DirectorTeam("t-1", "One"));
        DirectorTeamStore.SaveAt(Home("a"), new DirectorTeam("t-2", "Two"));

        Assert.Equal(new DirectorTeam("t-2", "Two"), DirectorTeamStore.LoadAt(Home("a")));
    }

    [Fact]
    public void ClearAt_ForgetsTheTeamOfThatInstanceOnly()
    {
        DirectorTeamStore.SaveAt(Home("a"), new DirectorTeam("t-1", "One"));
        DirectorTeamStore.SaveAt(Home("b"), new DirectorTeam("t-2", "Two"));

        DirectorTeamStore.ClearAt(Home("a"));

        Assert.Null(DirectorTeamStore.LoadAt(Home("a")));
        Assert.Equal(new DirectorTeam("t-2", "Two"), DirectorTeamStore.LoadAt(Home("b")));
    }

    [Fact]
    public void LoadAt_FileWithNoName_Throws()
    {
        var file = DirectorTeamStore.TeamFileAt(Home("bad"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"teamId\":\"t-1\"}");

        Assert.Throws<InvalidDataException>(() => DirectorTeamStore.LoadAt(Home("bad")));
    }

    [Fact]
    public void TeamFileAt_BlankRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => DirectorTeamStore.TeamFileAt(" "));
    }
}

public sealed class TeamColorTests
{
    [Fact]
    public void For_SameTeamId_SameColourEveryTime()
    {
        Assert.Equal(TeamColor.For("8f1d2c34-0000-4000-8000-000000000001"), TeamColor.For("8f1d2c34-0000-4000-8000-000000000001"));
    }

    [Fact]
    public void For_KnownIds_GiveFixedColours()
    {
        // Pinned values: the hash must not depend on the process (string.GetHashCode is randomised per run),
        // or one team would be a different colour on each Director.
        Assert.Equal(TeamColor.Palette[Index("t-dev")], TeamColor.For("t-dev"));
        Assert.Equal(TeamColor.For("T-DEV "), TeamColor.For("t-dev"));
    }

    [Fact]
    public void For_ManyTeamIds_UseEveryColourInThePalette()
    {
        var used = Enumerable.Range(0, 200).Select(i => TeamColor.For($"team-{i}")).Distinct().ToList();

        Assert.Equal(TeamColor.Palette.Count, used.Count);
    }

    [Fact]
    public void For_TwoDifferentTeams_CanDiffer()
    {
        var colours = new[] { "team-0", "team-1", "team-2", "team-3" }.Select(TeamColor.For).Distinct();

        Assert.True(colours.Count() > 1);
    }

    [Fact]
    public void For_Personal_IsTheDefaultChipWhichNoTeamGets()
    {
        Assert.Equal(TeamColor.Default, TeamColor.For(null));
        Assert.DoesNotContain(TeamColor.Default, TeamColor.Palette);
    }

    [Fact]
    public void For_BlankId_Throws()
    {
        Assert.Throws<ArgumentException>(() => TeamColor.For(""));
    }

    // FNV-1a, written out independently here, so the test checks the arithmetic rather than repeating a call.
    private static int Index(string id)
    {
        uint h = 2166136261;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(id)) { h ^= b; h *= 16777619; }
        return (int)(h % (uint)TeamColor.Palette.Count);
    }
}

public sealed class TeamChoicesTests
{
    private static HostedTeam Team(string id, string name, string role, int members) =>
        new() { TeamId = id, Name = name, Role = role, MemberCount = members };

    [Fact]
    public void Build_NoTeams_OnlyThePersonalAccountSoNothingIsAsked()
    {
        var choices = TeamChoices.Build(Array.Empty<HostedTeam>());

        var only = Assert.Single(choices);
        Assert.True(only.IsPersonal);
        Assert.Equal("Just you", only.Detail);
        Assert.False(TeamChoices.MustAsk(choices));
    }

    [Fact]
    public void Build_WithTeams_IsTheGatewaysListInOrderThenPersonal()
    {
        var choices = TeamChoices.Build(new[] { Team("t-b", "Beta", "manager", 2), Team("t-a", "Alpha", "developer", 1) });

        Assert.Equal(new string?[] { "t-b", "t-a", null }, choices.Select(c => c.TeamId));
        Assert.True(TeamChoices.MustAsk(choices));
    }

    [Theory]
    [InlineData("t", "Team", "owner", null)]
    [InlineData("t", "Team", "Developer", null)]
    [InlineData("", "Team", "owner", "a team with no id")]
    [InlineData("t", "", "owner", "team t with no name")]
    [InlineData("t", "Team", "collaborator", "team t with the role \"collaborator\", which cannot run sessions")]
    public void Unreadable_EachProblem_IsNamed(string id, string name, string role, string? expected)
    {
        Assert.Equal(expected, TeamChoices.Unreadable(Team(id, name, role, 1)));
    }

    [Fact]
    public void Build_TeamWithNoId_Throws()
    {
        Assert.Throws<InvalidDataException>(() => TeamChoices.Build(new[] { Team("", "X", "owner", 1) }));
    }

    [Theory]
    [InlineData("owner", 5, "You are the Owner, 5 people, you pay")]
    [InlineData("Manager", 2, "You are a Manager, 2 people")]
    [InlineData("developer", 1, "You are a Developer, 1 person")]
    public void Describe_EachRole_ReadsAsTheMockupDoes(string role, int members, string expected)
    {
        Assert.Equal(expected, TeamChoices.Describe(Team("t", "T", role, members)));
    }

    [Fact]
    public void Describe_Collaborator_ThrowsBecauseTheGatewayMustNotListIt()
    {
        Assert.Throws<InvalidDataException>(() => TeamChoices.Describe(Team("t", "T", "collaborator", 3)));
    }

    [Fact]
    public void SuggestDirectorName_StartsWithTheComputerName()
    {
        Assert.Equal("SOREN_NORTH - DevThrottle", TeamChoices.SuggestDirectorName("SOREN_NORTH", new TeamChoice("t", "DevThrottle", "")));
        Assert.Equal("SOREN_NORTH - Personal", TeamChoices.SuggestDirectorName("SOREN_NORTH", new TeamChoice(null, "Personal", "Just you")));
    }

    [Fact]
    public void ToTeam_KeepsIdAndName()
    {
        Assert.Equal(new DirectorTeam("t", "T"), new TeamChoice("t", "T", "d").ToTeam());
    }
}

public sealed class DirectorTeamMoverTests
{
    private sealed class FakeService : IDirectorTeamService
    {
        public List<(string Key, string? TeamId)> Moves { get; } = new();
        public OperationResult<string> Answer { get; set; } = OperationResult<string>.Ok("new-key");
        public Action? DuringMove { get; set; }

        public Task<OperationResult<HostedTeamsAnswer>> ListTeamsAsync(CancellationToken ct) =>
            Task.FromResult(OperationResult<HostedTeamsAnswer>.Ok(new HostedTeamsAnswer(true, Array.Empty<HostedTeam>())));

        public Task<OperationResult<string>> MoveAsync(string currentDeviceKey, string? teamId, CancellationToken ct)
        {
            Moves.Add((currentDeviceKey, teamId));
            DuringMove?.Invoke();
            return Task.FromResult(Answer);
        }
    }

    // A hold that records when it is taken and released, reporting a fixed session count.
    private sealed class Holds
    {
        public int Running { get; set; }
        public List<string> Log { get; } = new();
        public bool Held { get; private set; }

        public SessionCreationHold Take(string reason)
        {
            Held = true;
            Log.Add("hold");
            return new SessionCreationHold(Running, () => { Held = false; Log.Add("release"); });
        }
    }

    private static readonly TeamChoice DevThrottle = new("t-dev", "DevThrottle", "You are the Owner, 5 people, you pay");

    [Fact]
    public async Task MoveAsync_ASessionRunning_RefusedWithoutCallingTheGatewayOrStoring()
    {
        var service = new FakeService();
        var holds = new Holds { Running = 1 };
        var stored = new List<string>();
        var mover = new DirectorTeamMover(service, holds.Take, t => stored.Add("team"), k => stored.Add("key"), null);

        var result = await mover.MoveAsync("key-1", DevThrottle, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Close the 1 running session first.", result.ErrorMessage);
        Assert.Empty(service.Moves);
        Assert.Empty(stored);
        Assert.False(holds.Held);
    }

    [Fact]
    public async Task MoveAsync_NoSessionRunning_StoresTeamThenKeyThenReappliesAllUnderTheHold()
    {
        var service = new FakeService();
        var holds = new Holds();
        var mover = new DirectorTeamMover(service, holds.Take,
            team => holds.Log.Add($"team:{team.TeamId} held={holds.Held}"),
            key => holds.Log.Add($"key:{key} held={holds.Held}"),
            () => { holds.Log.Add($"reapply held={holds.Held}"); return Task.CompletedTask; });
        service.DuringMove = () => holds.Log.Add($"gateway held={holds.Held}");

        var result = await mover.MoveAsync("key-1", DevThrottle, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new DirectorTeam("t-dev", "DevThrottle"), result.Value);
        Assert.Equal(("key-1", (string?)"t-dev"), Assert.Single(service.Moves));
        // The team first and the key last, so the screen never names the old team over the new team's key; the
        // hold covers the Gateway call, both writes and the re-apply.
        Assert.Equal(new[] { "hold", "gateway held=True", "team:t-dev held=True", "key:new-key held=True", "reapply held=True", "release" }, holds.Log);
    }

    [Fact]
    public async Task MoveAsync_ToPersonal_SendsNullTeam()
    {
        var service = new FakeService();
        var mover = new DirectorTeamMover(service, new Holds().Take, _ => { }, _ => { }, null);

        await mover.MoveAsync("key-1", new TeamChoice(null, "Personal", "Just you"), CancellationToken.None);

        Assert.Null(Assert.Single(service.Moves).TeamId);
    }

    [Fact]
    public async Task MoveAsync_GatewayRefuses_ReturnsItsWordsAndStoresNothing()
    {
        var service = new FakeService { Answer = OperationResult<string>.Fail("This Director still has a session registered.") };
        var holds = new Holds();
        var mover = new DirectorTeamMover(service, holds.Take,
            _ => throw new InvalidOperationException("must not store"), _ => throw new InvalidOperationException("must not store"), null);

        var result = await mover.MoveAsync("key-1", DevThrottle, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("This Director still has a session registered.", result.ErrorMessage);
        Assert.False(holds.Held);
    }

    [Fact]
    public async Task MoveAsync_TeamCannotBeRecorded_SaysTheMoveHappenedAndWhatToDo()
    {
        var keys = new List<string>();
        var mover = new DirectorTeamMover(new FakeService(), new Holds().Take,
            _ => throw new IOException("disk full"), keys.Add, null);

        var result = await mover.MoveAsync("key-1", DevThrottle, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DirectorTeamMover.MovedButTeamNotRecorded("DevThrottle", "disk full"), result.ErrorMessage);
        Assert.StartsWith("The Gateway moved this Director to DevThrottle", result.ErrorMessage);
        Assert.Contains("Connect it again from the Gateway tab", result.ErrorMessage);
        // The key is not stored over an unrecorded team: the screen and the key cannot disagree.
        Assert.Empty(keys);
    }

    [Fact]
    public async Task MoveAsync_KeyCannotBeSaved_SaysTheMoveHappenedTheDirectorIsDisconnectedAndWhatToDo()
    {
        var teams = new List<DirectorTeam>();
        var reapplied = false;
        var mover = new DirectorTeamMover(new FakeService(), new Holds().Take,
            teams.Add, _ => throw new UnauthorizedAccessException("file locked"), () => { reapplied = true; return Task.CompletedTask; });

        var result = await mover.MoveAsync("key-1", DevThrottle, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DirectorTeamMover.MovedButKeyNotSaved("DevThrottle", "file locked"), result.ErrorMessage);
        Assert.Contains("not connected", result.ErrorMessage);
        Assert.Equal(new DirectorTeam("t-dev", "DevThrottle"), Assert.Single(teams));
        Assert.False(reapplied);
    }

    [Fact]
    public async Task MoveAsync_ReapplyFails_SaysEverythingIsStoredAndToRestart()
    {
        var mover = new DirectorTeamMover(new FakeService(), new Holds().Take, _ => { }, _ => { },
            () => throw new InvalidOperationException("stream down"));

        var result = await mover.MoveAsync("key-1", DevThrottle, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DirectorTeamMover.MovedButNotApplied("DevThrottle", "stream down"), result.ErrorMessage);
        Assert.Contains("Restart the Director", result.ErrorMessage);
    }

    [Fact]
    public async Task MoveAsync_GatewaySentNoKey_SaysTheMoveHappenedAndTheKeyDidNotArrive()
    {
        var service = new FakeService { Answer = OperationResult<string>.Ok("") };
        var stored = new List<string>();
        var mover = new DirectorTeamMover(service, new Holds().Take, _ => stored.Add("team"), _ => stored.Add("key"), null);

        var result = await mover.MoveAsync("key-1", DevThrottle, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DirectorTeamMover.MovedButKeyNotSaved("DevThrottle", "the Gateway sent no new key"), result.ErrorMessage);
        Assert.Empty(stored);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "Close the 1 running session first.")]
    [InlineData(4, "Close the 4 running sessions first.")]
    public void RefusalFor_Counts_WordedAsTheMockup(int running, string? expected)
    {
        Assert.Equal(expected, DirectorTeamMover.RefusalFor(running));
    }

    [Fact]
    public void RefusalFor_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectorTeamMover.RefusalFor(-1));
    }
}

/// <summary>
/// The session-creation hold a move takes (review finding F3): while it is held, no session can start from any
/// creation path, and the hold counts the sessions there were when it took effect.
/// </summary>
public sealed class SessionCreationHoldTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("dt-hold-").FullName;

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    [Fact]
    public void HoldSessionCreation_WhileHeld_EveryCreatePathIsRefusedWithTheReason()
    {
        var sessions = new SessionManager(new AgentOptions());

        using (sessions.HoldSessionCreation("this Director is moving to DevThrottle."))
        {
            var create = Assert.Throws<InvalidOperationException>(() => sessions.CreateSession(_repo));
            Assert.Contains("this Director is moving to DevThrottle.", create.Message);
            Assert.Throws<InvalidOperationException>(() => sessions.CreatePipeModeSession(_repo));
            Assert.Throws<InvalidOperationException>(() =>
                sessions.CreateEmbeddedSession(_repo, null, new ScriptedAgentTerminal(AgentKind.ClaudeCode, "", _repo)));
        }

        Assert.Empty(sessions.ListSessions());
    }

    [Fact]
    public void HoldSessionCreation_Released_SessionsStartAgain()
    {
        var sessions = new SessionManager(new AgentOptions());
        sessions.HoldSessionCreation("moving").Dispose();

        var session = sessions.CreateEmbeddedSession(_repo, null, new ScriptedAgentTerminal(AgentKind.ClaudeCode, "", _repo));

        Assert.Contains(session, sessions.ListSessions());
    }

    [Fact]
    public void HoldSessionCreation_CountsTheSessionsThereAre()
    {
        var sessions = new SessionManager(new AgentOptions());
        sessions.CreateEmbeddedSession(_repo, null, new ScriptedAgentTerminal(AgentKind.ClaudeCode, "", _repo));

        using var hold = sessions.HoldSessionCreation("moving");

        Assert.Equal(1, hold.SessionsAtHold);
    }

    [Fact]
    public void HoldSessionCreation_SecondHoldWhileHeld_Throws()
    {
        var sessions = new SessionManager(new AgentOptions());
        using var first = sessions.HoldSessionCreation("moving");

        Assert.Throws<InvalidOperationException>(() => sessions.HoldSessionCreation("moving again"));
    }

    [Fact]
    public async Task MoveAsync_ASessionStartedDuringTheMove_IsRefused()
    {
        // The case F3 names: a session started by another path (here, during the Gateway call) while the move is
        // under way. It is refused, and the move completes with no session on the Director.
        var sessions = new SessionManager(new AgentOptions());
        Exception? startDuringMove = null;
        var service = new StartsASessionDuringTheMove(() =>
            startDuringMove = Record.Exception(() =>
                sessions.CreateEmbeddedSession(_repo, null, new ScriptedAgentTerminal(AgentKind.ClaudeCode, "", _repo))));
        var mover = new DirectorTeamMover(service, sessions.HoldSessionCreation, _ => { }, _ => { }, null);

        var result = await mover.MoveAsync("key-1", new TeamChoice("t-dev", "DevThrottle", ""), CancellationToken.None);

        Assert.True(result.Success);
        Assert.IsType<InvalidOperationException>(startDuringMove);
        Assert.Contains("moving to DevThrottle", startDuringMove!.Message);
        Assert.Empty(sessions.ListSessions());
    }

    private sealed class StartsASessionDuringTheMove(Action start) : IDirectorTeamService
    {
        public Task<OperationResult<HostedTeamsAnswer>> ListTeamsAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<OperationResult<string>> MoveAsync(string currentDeviceKey, string? teamId, CancellationToken ct)
        {
            start();
            return Task.FromResult(OperationResult<string>.Ok("new-key"));
        }
    }
}

/// <summary>Review finding F7: two instance homes hold two different KEYS, as well as two teams.</summary>
public sealed class TwoInstanceKeysTests : IDisposable
{
    private readonly string _machineRoot = Directory.CreateTempSubdirectory("dt-keys-").FullName;

    public void Dispose() => Directory.Delete(_machineRoot, recursive: true);

    [Fact]
    public void SaveEnrolledKeyAt_TwoInstanceHomes_HoldTwoDifferentKeysAndTeams()
    {
        var a = Path.Combine(_machineRoot, "instances", "default");
        var b = Path.Combine(_machineRoot, "instances", "home-lab");

        CcDirector.Core.Configuration.GatewayCredentialStore.SaveEnrolledKeyAt(a, "https://gw.example", "key-for-devthrottle");
        DirectorTeamStore.SaveAt(a, new DirectorTeam("t-dev", "DevThrottle"));
        CcDirector.Core.Configuration.GatewayCredentialStore.SaveEnrolledKeyAt(b, "https://gw.example", "key-for-personal");
        DirectorTeamStore.SaveAt(b, DirectorTeam.Personal);

        Assert.Equal("key-for-devthrottle", File.ReadAllText(Path.Combine(a, "config", "director", "gateway-token.txt")));
        Assert.Equal("key-for-personal", File.ReadAllText(Path.Combine(b, "config", "director", "gateway-token.txt")));
        Assert.Equal("key-for-devthrottle", CcDirector.Core.Configuration.GatewayConfig.LoadFrom(a).Token);
        Assert.Equal("key-for-personal", CcDirector.Core.Configuration.GatewayConfig.LoadFrom(b).Token);
        Assert.Equal("t-dev", DirectorTeamStore.LoadAt(a)!.TeamId);
        Assert.True(DirectorTeamStore.LoadAt(b)!.IsPersonal);
    }
}

/// <summary>Review findings F1 and F2: the explicit Teams signal, and what a Director with no team file shows.</summary>
public sealed class HostedTeamsSignalTests
{
    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static async Task<OperationResult<bool>> Ask(HttpStatusCode status, string body)
    {
        var handler = new Answer(status, body);
        var result = await new HealthzTeamsSignal(() => handler).TeamsReleasedAsync("https://gw.example", CancellationToken.None);
        Assert.Equal("/healthz", Assert.Single(handler.Paths));
        return result;
    }

    [Fact]
    public async Task TeamsReleasedAsync_TodaysProductionAnswer_NoTeamsFieldMeansNoTeams()
    {
        var result = await Ask(HttpStatusCode.OK,
            "{\"status\":\"ok\",\"version\":\"2.13.0\",\"commit\":\"a53d6bd\",\"subsystems\":{\"statistics\":\"available\"},\"directorId\":null,\"machineName\":null}");

        Assert.True(result.Success);
        Assert.False(result.Value);
    }

    [Theory]
    [InlineData("{\"status\":\"ok\",\"teams\":true}", true)]
    [InlineData("{\"status\":\"ok\",\"teams\":false}", false)]
    [InlineData("{\"status\":\"ok\",\"teams\":null}", false)]
    public async Task TeamsReleasedAsync_TheField_DecidesIt(string body, bool expected)
    {
        var result = await Ask(HttpStatusCode.OK, body);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public async Task TeamsReleasedAsync_StillStarting503_ReadsTheSameAnswer()
    {
        var result = await Ask(HttpStatusCode.ServiceUnavailable, "{\"status\":\"starting\",\"teams\":true}");

        Assert.True(result.Success);
        Assert.True(result.Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"status\":\"starting\"}")]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>")]
    [InlineData(HttpStatusCode.OK, "{\"teams\":\"yes\"}")]
    public async Task TeamsReleasedAsync_CannotTell_IsAFailureNeverAGuess(HttpStatusCode status, string body)
    {
        var result = await Ask(status, body);

        Assert.False(result.Success);
        Assert.Contains("https://gw.example", result.ErrorMessage);
    }

    private sealed class FixedSignal(OperationResult<bool> answer) : IHostedTeamsSignal
    {
        public int Asked { get; private set; }

        public Task<OperationResult<bool>> TeamsReleasedAsync(string gatewayUrl, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task ResolveAsync_TeamRecorded_IsThatTeamWithoutAsking()
    {
        var signal = new FixedSignal(OperationResult<bool>.Ok(true));

        var result = await DirectorTeamView.ResolveAsync(new DirectorTeam("t", "T"), "https://gw.example", signal, CancellationToken.None);

        Assert.Equal(new DirectorTeam("t", "T"), result.Value);
        Assert.Equal(0, signal.Asked);
    }

    [Fact]
    public async Task ResolveAsync_NoFileOnAGatewayWithTeams_IsThePersonalAccount()
    {
        // F2: a Director enrolled before Teams or by the command line has no file and is on its personal account.
        var result = await DirectorTeamView.ResolveAsync(null, "https://gw.example", new FixedSignal(OperationResult<bool>.Ok(true)), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(DirectorTeam.Personal, result.Value);
    }

    [Fact]
    public async Task ResolveAsync_NoFileOnAGatewayWithoutTeams_IsNoChip()
    {
        var result = await DirectorTeamView.ResolveAsync(null, "https://gw.example", new FixedSignal(OperationResult<bool>.Ok(false)), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ResolveAsync_NotConnected_IsNoChipWithoutAsking()
    {
        var signal = new FixedSignal(OperationResult<bool>.Ok(true));

        var result = await DirectorTeamView.ResolveAsync(null, "", signal, CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(0, signal.Asked);
    }

    [Fact]
    public async Task ResolveAsync_GatewayCannotBeAsked_IsAFailure()
    {
        var result = await DirectorTeamView.ResolveAsync(null, "https://gw.example", new FixedSignal(OperationResult<bool>.Fail("unreachable")), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("unreachable", result.ErrorMessage);
    }
}
