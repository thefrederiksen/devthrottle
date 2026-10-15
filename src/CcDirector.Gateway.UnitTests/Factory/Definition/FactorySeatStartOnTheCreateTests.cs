using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE CREATE VERB TURNS A SEAT'S FIELDS INTO ITS START (Factory Definition mission, phase 5, design sections 2 and 4).
/// <see cref="SessionCommandExecutor.SettleSeatStart"/> decides the kit the Director writes and where the seat works;
/// <see cref="SessionCommandExecutor.LaunchLineWithModel"/> puts the model on the launch line. What the SessionManager
/// then does with the kit is proven through the real create in FactoryKitAtLaunchTests. The refusals here are also run
/// through <see cref="SessionCommandExecutor.Create"/> itself, which refuses before any session or process exists, so
/// the verb is proven to call them.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactorySeatStartOnTheCreateTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string _repo = Directory.CreateTempSubdirectory("dt-seat-start-").FullName;

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private static NewSessionRequest ASeat(Action<NewSessionRequest>? more = null)
    {
        var req = new NewSessionRequest
        {
            Agent = "ClaudeCode",
            Factory = "devthrottle",
            FactorySeat = "outreach",
            FactoryDefinitionVersion = 4,
            FactoryKit = FactoryKits.Seat,
        };
        more?.Invoke(req);
        return req;
    }

    private List<RepositoryConfig> Registered() => new() { new RepositoryConfig { Name = "DevThrottle", Path = _repo } };

    private string? Settle(NewSessionRequest req, out FactoryKitRequest? kit, out bool force, List<RepositoryConfig>? registered = null)
        => SessionCommandExecutor.SettleSeatStart(req, registered ?? Registered(), "SOREN_NORTH", out kit, out force);

    [Fact]
    public void SettleSeatStart_UNDEFINED_FACTORY_ACreateWithNoSeat_SettlesNothingAndChangesNothing()
    {
        var req = new NewSessionRequest { Agent = "ClaudeCode", RepoPath = _repo, Factory = "website-factory", Model = "claude-sonnet-5-5" };

        var refusal = Settle(req, out var kit, out var force);

        Assert.Null(refusal);
        Assert.Null(kit);
        Assert.False(force);
        Assert.Equal(_repo, req.RepoPath);
    }

    [Fact]
    public void SettleSeatStart_ASeatStart_AsksForTheSeatsKitOfItsVersion()
    {
        var refusal = Settle(ASeat(r => r.RepoPath = _repo), out var kit, out var force);

        Assert.Null(refusal);
        Assert.Equal(new FactoryKitRequest("devthrottle", 4, "outreach", FactoryKits.Seat), kit);
        Assert.False(force);
    }

    [Fact]
    public void SettleSeatStart_ASeatInARegisteredRepository_WorksInItsFolderOnAForcedPooledSlot()
    {
        var req = ASeat(r => { r.FactoryWorktree = "devthrottle"; r.RepoPath = @"C:\whatever-the-caller-sent"; });

        var refusal = Settle(req, out var kit, out var force);

        Assert.Null(refusal);
        Assert.Equal(_repo, req.RepoPath);
        Assert.True(force);
        Assert.False(kit!.Home);
    }

    [Fact]
    public void SettleSeatStart_ASeatWithNoRepository_IsAHomeSeatWhateverPathWasSent()
    {
        var req = ASeat(r => { r.FactoryHome = true; r.RepoPath = ""; });

        var refusal = Settle(req, out var kit, out var force);

        Assert.Null(refusal);
        Assert.True(kit!.Home);
        Assert.False(force);
    }

    [Fact]
    public void SettleSeatStart_ARestore_HandsTheSeatsKitFolderBack()
    {
        var folder = @"C:\Users\someone\AppData\Local\cc-director\factory-run\5b0c";

        var refusal = Settle(ASeat(r => { r.RepoPath = _repo; r.FactoryKitDirectory = folder; }), out var kit, out _);

        Assert.Null(refusal);
        Assert.Equal(folder, kit!.ReturningDirectory);
    }

    [Fact]
    public void SettleSeatStart_AChildOfASeat_AsksForTheRulesOnlyKit()
    {
        var refusal = Settle(ASeat(r => { r.RepoPath = _repo; r.FactoryKit = FactoryKits.RulesOnly; }), out var kit, out _);

        Assert.Null(refusal);
        Assert.Equal(FactoryKits.RulesOnly, kit!.Kind);
    }

    [Fact]
    public void SettleSeatStart_AKitFolderWithNoSeat_IsRefused()
    {
        var req = new NewSessionRequest { Agent = "ClaudeCode", RepoPath = _repo, FactoryKitDirectory = @"C:\factory-run\5b0c" };

        var refusal = Settle(req, out var kit, out _);

        Assert.Equal("The create hands back a kit folder but names no seat it belongs to, so no session was started; " +
                     "a kit folder is only ever taken with its seat.", refusal);
        Assert.Null(kit);
    }

    [Fact]
    public void SettleSeatStart_ARepositoryThisMachineHasNotRegistered_IsRefusedInTheDesignsWords()
    {
        var req = ASeat(r => r.FactoryWorktree = "mindzie-web");

        var refusal = Settle(req, out var kit, out var force);

        Assert.Equal("The seat outreach works in the repository 'mindzie-web', which is not registered on SOREN_NORTH, so " +
                     "no session was started. Register it there, or change the seat's repository in the definition.", refusal);
        Assert.Null(kit);
        Assert.False(force);
    }

    [Fact]
    public void SettleSeatStart_PartOfASeat_IsRefused()
    {
        var refusal = Settle(ASeat(r => r.FactoryDefinitionVersion = null), out _, out _);

        Assert.Equal("The create names part of a seat but not all of its factory, seat, published version and kit, so no " +
                     "session was started.", refusal);
    }

    [Fact]
    public void SettleSeatStart_AKitThisDirectorDoesNotBuild_IsRefused()
    {
        var refusal = Settle(ASeat(r => r.FactoryKit = "everything"), out _, out _);

        Assert.Equal("The kit 'everything' is not one this Director builds (seat or rules-only), so no session was started.", refusal);
    }

    [Fact]
    public void SettleSeatStart_BothARepositoryAndNone_IsRefused()
    {
        var refusal = Settle(ASeat(r => { r.FactoryHome = true; r.FactoryWorktree = "devthrottle"; }), out _, out _);

        Assert.Equal("The seat outreach is given both a repository ('devthrottle') and no repository, so no session was started.", refusal);
    }

    [Fact]
    public void LaunchLineWithModel_TheConfiguredLine_HasItsModelReplacedByTheOneAskedFor()
    {
        var result = SessionCommandExecutor.LaunchLineWithModel(AgentKind.ClaudeCode,
            "--dangerously-skip-permissions --model claude-opus-5-5", argsAreExplicit: false, "claude-sonnet-5-5", new AgentOptions());

        Assert.Equal("--dangerously-skip-permissions --model claude-sonnet-5-5", result.Arguments);
    }

    [Fact]
    public void LaunchLineWithModel_ThePersonsOwnArgsNamingAModel_WinUntouched()
    {
        var result = SessionCommandExecutor.LaunchLineWithModel(AgentKind.ClaudeCode,
            "--model claude-fable-5-1", argsAreExplicit: true, "claude-sonnet-5-5", new AgentOptions());

        Assert.Equal("--model claude-fable-5-1", result.Arguments);
        Assert.True(result.ArgsNamedAModel);
    }

    [Fact]
    public void LaunchLineWithModel_NoLineForClaudeCode_PutsTheModelOnTheConfiguredFallbackLine()
    {
        var result = SessionCommandExecutor.LaunchLineWithModel(AgentKind.ClaudeCode, null, argsAreExplicit: false,
            "claude-sonnet-5-5", new AgentOptions { DefaultClaudeArgs = "--verbose" });

        Assert.Equal("--verbose --model claude-sonnet-5-5", result.Arguments);
    }

    [Fact]
    public void LaunchLineWithModel_UNDEFINED_FACTORY_NoModel_LeavesTheLineAsItIs()
    {
        var result = SessionCommandExecutor.LaunchLineWithModel(AgentKind.Codex, "--full-auto", argsAreExplicit: true, null, new AgentOptions());

        Assert.True(result.Ok);
        Assert.Equal("--full-auto", result.Arguments);
    }

    private DirectorCommandResult CreateThroughTheVerb(NewSessionRequest req, RepositoryRegistry? registry = null)
    {
        using var manager = new SessionManager(new AgentOptions());
        return SessionCommandExecutor.Create(manager, "dir-1",
            new DirectorCommand { Verb = "create", PayloadJson = JsonSerializer.Serialize(req, Web) },
            new SessionCommandServices { Repositories = registry });
    }

    [Fact]
    public void Create_AKitFolderWithNoSeat_IsRefusedByTheVerbBeforeAnythingStarts()
    {
        var result = CreateThroughTheVerb(new NewSessionRequest
        {
            Agent = "ClaudeCode", RepoPath = _repo, FactoryKitDirectory = @"C:\factory-run\5b0c",
        });

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.StartsWith("The create hands back a kit folder but names no seat", result.Error);
    }

    [Fact]
    public void Create_ASeatInAnUnregisteredRepository_IsRefusedByTheVerb()
    {
        var registry = new RepositoryRegistry(Path.Combine(_repo, "repositories.json"));

        var result = CreateThroughTheVerb(ASeat(r => r.FactoryWorktree = "mindzie-web"), registry);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Equal(FactoryWorktreeRepository.NotRegistered("outreach", "mindzie-web", Environment.MachineName), result.Error);
    }

    [Fact]
    public void Create_AModelOnAnAgentWithNoModelFlag_IsRefusedByTheVerb()
    {
        var result = CreateThroughTheVerb(new NewSessionRequest
        {
            Agent = "RawCli", Command = "some-tool", RepoPath = _repo, Model = "claude-sonnet-5-5",
        });

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Equal(AgentLaunchModel.NoModelFlag(AgentKind.RawCli, "claude-sonnet-5-5"), result.Error);
    }
}
