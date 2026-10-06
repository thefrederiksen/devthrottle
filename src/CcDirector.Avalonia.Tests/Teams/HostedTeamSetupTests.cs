using System.Net;
using System.Net.Http;
using System.Text;
using Avalonia.Headless.XUnit;
using CcDirector.Core.Account;
using CcDirector.Core.Teams;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Avalonia.Tests.Teams;

/// <summary>
/// The D1 transaction BOTH desktop surfaces run - <see cref="HostedTeamSetup"/> - driven with a real runner over
/// a fake hosted Gateway (review finding F5): the name is saved before the team is recorded, a failed rename is
/// reported as a join that happened, and the first-run wizard and the Gateway connection panel both call it.
/// </summary>
public sealed class HostedTeamSetupTests
{
    private const string TeamsHealth = "{\"status\":\"ok\",\"teams\":true}";
    private const string PreTeamsHealth = "{\"status\":\"ok\",\"version\":\"2.13.0\"}";

    private const string OneTeam =
        "{\"teams\":[{\"teamId\":\"t-dev\",\"name\":\"DevThrottle\",\"role\":\"owner\",\"memberCount\":5}]}";

    // Two teams, so D1 is shown.
    private const string TwoTeams =
        "{\"teams\":[{\"teamId\":\"t-dev\",\"name\":\"DevThrottle\",\"role\":\"owner\",\"memberCount\":5}," +
        "{\"teamId\":\"t-acme\",\"name\":\"Acme\",\"role\":\"developer\",\"memberCount\":3}]}";

    private sealed class FakeGateway(string health, string teams) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/healthz" => health,
                "/devices/enroll-hosted/teams" => teams,
                "/devices/enroll-hosted" => "{\"deviceKey\":\"team-key\",\"deviceCount\":1}",
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static Func<Action<DirectorTeam?>, GatewayAccountEnrollRunner> Runner(string health, List<string> log, string teams = TwoTeams) =>
        persistTeam => new GatewayAccountEnrollRunner(
            signIn: _ => Task.FromResult(new DevThrottleTokens("account-token", "refresh")),
            handlerFactory: () => new FakeGateway(health, teams),
            persist: (_, key) => log.Add("key:" + key),
            persistTeam: persistTeam);

    // Records what setup says about the name (live proof F4): the suggestion it carries, or none.
    private static Action<DirectorNameSuggestion?> Suggest(List<string> log) =>
        s => log.Add("suggestion:" + (s is null ? "none" : s.TeamId + "|" + s.MachineName + "|" + s.Name));

    private static Func<TeamQuestion, Task<TeamAnswer?>> Choose(int index, string name) =>
        q => Task.FromResult<TeamAnswer?>(new TeamAnswer(q.Choices[index], name));

    [AvaloniaFact]
    public async Task RunAsync_TeamChosen_KeyThenNameThenTeam()
    {
        var log = new List<string>();

        var result = await HostedTeamSetup.RunAsync(Runner(TeamsHealth, log), Choose(0, "SOREN_NORTH - DevThrottle"),
            name => log.Add("rename:" + name), Suggest(log), (team, key) => log.Add("team:" + team?.TeamId + " for " + key), "dir-1", "SOREN_NORTH", CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("team-key", result.Value!.DeviceKey);
        // The team is recorded last: that write redraws the title bar, and the name must already be saved.
        Assert.Equal(new[] { "key:team-key", "rename:SOREN_NORTH - DevThrottle",
            "suggestion:t-dev|SOREN_NORTH|SOREN_NORTH - DevThrottle", "team:t-dev for team-key" }, log);
    }

    // Live proof F4: the person typed a name of their own on D1, so no suggestion is recorded and a move never renames it.
    [AvaloniaFact]
    public async Task RunAsync_TypedName_RecordsNoSuggestion()
    {
        var log = new List<string>();

        var result = await HostedTeamSetup.RunAsync(Runner(TeamsHealth, log), Choose(1, "Build box"),
            name => log.Add("rename:" + name), Suggest(log), (team, _) => log.Add("team:" + team?.TeamId), "dir-1", "SOREN_NORTH", CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "key:team-key", "rename:Build box", "suggestion:none", "team:t-acme" }, log);
    }

    // Review RM-F2: Personal chosen with its suggested name records nothing, so a personal Director is never renamed.
    [AvaloniaFact]
    public async Task RunAsync_PersonalWithItsSuggestedName_RecordsNoSuggestion()
    {
        var log = new List<string>();

        var result = await HostedTeamSetup.RunAsync(Runner(TeamsHealth, log), Choose(2, "SOREN_NORTH - Personal"),
            name => log.Add("rename:" + name), Suggest(log), (team, _) => log.Add("team:" + (team is { IsPersonal: true } ? "personal" : team?.TeamId)),
            "dir-1", "SOREN_NORTH", CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "key:team-key", "rename:SOREN_NORTH - Personal", "suggestion:none", "team:personal" }, log);
    }

    // The suggestion is the one for the team CHOSEN, not the first card's: D1 follows the selection.
    [AvaloniaFact]
    public async Task RunAsync_SecondTeamWithItsSuggestedName_RecordsThatSuggestion()
    {
        var log = new List<string>();

        var result = await HostedTeamSetup.RunAsync(Runner(TeamsHealth, log), Choose(1, "SOREN_NORTH - Acme"),
            name => log.Add("rename:" + name), Suggest(log), (team, _) => log.Add("team:" + team?.TeamId), "dir-1", "SOREN_NORTH", CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("suggestion:t-acme|SOREN_NORTH|SOREN_NORTH - Acme", log);
    }

    // A person with one team is never asked (devthrottle_internal#2311): D1 is not shown, and the Director takes
    // the name D1 would have started with, "<computer> - <team>"; the chip and the title show the recorded team.
    [AvaloniaFact]
    public async Task RunAsync_OneTeam_NotAskedNamedForTheTeamAndTeamRecorded()
    {
        var log = new List<string>();

        var result = await HostedTeamSetup.RunAsync(Runner(TeamsHealth, log, OneTeam),
            _ => throw new InvalidOperationException("must not ask"),
            name => log.Add("rename:" + name), Suggest(log), (team, _) => log.Add("team:" + team?.TeamId), "dir-1", "SOREN_NORTH", CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "key:team-key", "rename:SOREN_NORTH - DevThrottle",
            "suggestion:t-dev|SOREN_NORTH|SOREN_NORTH - DevThrottle", "team:t-dev" }, log);
    }

    [AvaloniaFact]
    public async Task RunAsync_PreTeamsGateway_NoRenameAndNoTeam()
    {
        var log = new List<string>();

        var result = await HostedTeamSetup.RunAsync(Runner(PreTeamsHealth, log),
            _ => throw new InvalidOperationException("must not ask"),
            name => log.Add("rename:" + name), Suggest(log), (team, _) => log.Add("team:" + (team is null ? "none" : team.TeamId)),
            "dir-1", "SOREN_NORTH", CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "key:team-key", "team:none" }, log);
    }

    [AvaloniaFact]
    public async Task RunAsync_RenameFails_TeamStillRecordedAndTheJoinIsReportedAsDone()
    {
        var log = new List<string>();

        var result = await HostedTeamSetup.RunAsync(Runner(TeamsHealth, log), Choose(0, "SOREN_NORTH - DevThrottle"),
            _ => throw new InvalidOperationException("the instance registry is unreadable"), Suggest(log),
            (team, _) => log.Add("team:" + team?.TeamId), "dir-1", "SOREN_NORTH", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(HostedTeamSetup.JoinedButNotNamed("DevThrottle", "the instance registry is unreadable"), result.ErrorMessage);
        Assert.StartsWith("This Director joined DevThrottle and its key is saved", result.ErrorMessage);
        Assert.Equal(new[] { "key:team-key", "team:t-dev" }, log);
    }

    [AvaloniaFact]
    public async Task RunAsync_TeamCannotBeRecorded_TheJoinIsReportedAsDoneWithWhatToDo()
    {
        var log = new List<string>();

        var result = await HostedTeamSetup.RunAsync(Runner(TeamsHealth, log), Choose(0, "SOREN_NORTH - DevThrottle"),
            name => log.Add("rename:" + name), Suggest(log), (_, _) => throw new IOException("disk full"),
            "dir-1", "SOREN_NORTH", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(HostedTeamSetup.JoinedButTeamNotRecorded("DevThrottle", "disk full"), result.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task RunAsync_Cancelled_NothingRenamedOrRecorded()
    {
        var log = new List<string>();

        var result = await HostedTeamSetup.RunAsync(Runner(TeamsHealth, log), _ => Task.FromResult<TeamAnswer?>(null),
            name => log.Add("rename:" + name), Suggest(log), (team, _) => log.Add("team"), "dir-1", "SOREN_NORTH", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(log);
    }

    /// <summary>
    /// The two real surfaces call this transaction, and neither still calls the pre-Teams hosted enroll: put a
    /// call site back to <c>SignInAndEnrollHostedAsync</c> and this fails, where every behaviour test above would
    /// stay green. It reads the source on purpose - both call sites need a live window and a browser to run.
    /// </summary>
    [Theory]
    [InlineData("src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs")]
    [InlineData("src/CcDirector.Avalonia/Controls/GatewayConnectionPanel.axaml.cs")]
    public void BothHostedJoinSurfaces_CallHostedTeamSetup_NotThePreTeamsEnroll(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

        Assert.Contains("HostedTeamSetup.SignInChooseTeamAndEnrollAsync(", source);
        Assert.DoesNotContain(".SignInAndEnrollHostedAsync(", source);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("cc-director.sln not found above " + AppContext.BaseDirectory);
    }
}
