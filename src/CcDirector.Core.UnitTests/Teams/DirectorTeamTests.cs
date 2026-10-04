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
        public List<(string DeviceId, string? TeamId)> Moves { get; } = new();
        public OperationResult<string> Answer { get; set; } = OperationResult<string>.Ok("new-key");

        public Task<OperationResult<HostedTeamsAnswer>> ListTeamsAsync(CancellationToken ct) =>
            Task.FromResult(OperationResult<HostedTeamsAnswer>.Ok(new HostedTeamsAnswer(true, Array.Empty<HostedTeam>())));

        public Task<OperationResult<string>> MoveAsync(string deviceId, string? teamId, CancellationToken ct)
        {
            Moves.Add((deviceId, teamId));
            return Task.FromResult(Answer);
        }
    }

    private static readonly TeamChoice DevThrottle = new("t-dev", "DevThrottle", "You are the Owner, 5 people, you pay");

    [Fact]
    public async Task MoveAsync_ASessionRunning_RefusedWithoutCallingTheGatewayOrStoring()
    {
        var service = new FakeService();
        var keys = new List<string>();
        var teams = new List<DirectorTeam>();
        var mover = new DirectorTeamMover(service, () => 1, keys.Add, teams.Add);

        var result = await mover.MoveAsync("dir-1", DevThrottle, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Close the 1 running session first.", result.ErrorMessage);
        Assert.Empty(service.Moves);
        Assert.Empty(keys);
        Assert.Empty(teams);
    }

    [Fact]
    public async Task MoveAsync_NoSessionRunning_CallsTheMoveAndStoresTheNewKeyAndTeam()
    {
        var service = new FakeService();
        var stored = new List<string>();
        var mover = new DirectorTeamMover(service, () => 0, key => stored.Add("key:" + key), team => stored.Add("team:" + team.TeamId));

        var result = await mover.MoveAsync("dir-1", DevThrottle, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new DirectorTeam("t-dev", "DevThrottle"), result.Value);
        Assert.Equal(("dir-1", (string?)"t-dev"), Assert.Single(service.Moves));
        // The key first: a new team name over an old, revoked key would show one team and reach none.
        Assert.Equal(new[] { "key:new-key", "team:t-dev" }, stored);
    }

    [Fact]
    public async Task MoveAsync_ToPersonal_SendsNullTeam()
    {
        var service = new FakeService();
        var mover = new DirectorTeamMover(service, () => 0, _ => { }, _ => { });

        await mover.MoveAsync("dir-1", new TeamChoice(null, "Personal", "Just you"), CancellationToken.None);

        Assert.Null(Assert.Single(service.Moves).TeamId);
    }

    [Fact]
    public async Task MoveAsync_GatewayRefuses_ReturnsItsWordsAndStoresNothing()
    {
        var service = new FakeService { Answer = OperationResult<string>.Fail("This Director still has a session registered.") };
        var keys = new List<string>();
        var mover = new DirectorTeamMover(service, () => 0, keys.Add, _ => throw new InvalidOperationException("must not store"));

        var result = await mover.MoveAsync("dir-1", DevThrottle, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("This Director still has a session registered.", result.ErrorMessage);
        Assert.Empty(keys);
    }

    [Fact]
    public async Task MoveAsync_CountsSessionsAtTheMomentOfTheMove()
    {
        var running = 0;
        var service = new FakeService();
        var mover = new DirectorTeamMover(service, () => running, _ => { }, _ => { });

        running = 3; // a session started after the button was drawn enabled
        var result = await mover.MoveAsync("dir-1", DevThrottle, CancellationToken.None);

        Assert.Equal("Close the 3 running sessions first.", result.ErrorMessage);
        Assert.Empty(service.Moves);
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
