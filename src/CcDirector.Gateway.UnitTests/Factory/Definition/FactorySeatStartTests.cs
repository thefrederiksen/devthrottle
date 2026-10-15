using CcDirector.Core.Agents;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// WHERE A SEAT START IS BUILT (Factory Definition mission, phase 5; design section 3), on the pure function alone: a
/// published definition version and a seat in, the create and its machine out. The fixture's boss seat names nothing of
/// its own (it inherits the factory's defaults and has no repository); the outreach seat names its machine, agent, model
/// and repository, and carries rules.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactorySeatStartTests : IDisposable
{
    private static PublishedFactoryDefinition Published(IReadOnlyDictionary<string, string>? content = null, int version = 4)
    {
        var read = FactoryDefinitionCheck.Read(content ?? DefinitionFixture.Publishable());
        Assert.True(read.Definition is not null, string.Join("\n", read.Findings.Select(f => $"{f.File}:{f.Line} {f.Message}")));
        return new PublishedFactoryDefinition(version, read.Definition!);
    }

    private static SeatDefinition Seat(PublishedFactoryDefinition p, string id) => p.Seat(id)!;

    // ---------- the create ----------

    [Fact]
    public void Build_ARepositorySeat_CarriesItsRepositoryByName_ItsOwnAgentModelAndMachine_AndNoFolder()
    {
        var p = Published();

        var built = FactorySeatStart.Build(p, Seat(p, "outreach"), "DevThrottle - Outreach - run", "Go.");

        var req = built.Request;
        Assert.Equal("SOREN_SOUTH", built.Machine);
        Assert.Equal("", req.RepoPath);
        Assert.Equal("devthrottle", req.FactoryWorktree);
        Assert.Null(req.FactoryHome);
        Assert.Equal("ClaudeCode", req.Agent);
        Assert.Equal("claude-sonnet-5-5", req.Model);
        Assert.Equal("devthrottle", req.Factory);
        Assert.Equal("outreach", req.FactorySeat);
        Assert.Equal(4, req.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, req.FactoryKit);
        Assert.Equal("DevThrottle - Outreach - run", req.Name);
        Assert.Equal("Go.", req.PrePrompt);
        Assert.Null(req.FactoryStartOverride);
        Assert.True(CcDirector.Gateway.Api.SpawnFactory.StartsWithoutAFolder(req));
    }

    [Fact]
    public void Build_ASeatWithNoRepository_IsAHome_AndInheritsTheFactorysDefaults()
    {
        var p = Published();

        var built = FactorySeatStart.Build(p, Seat(p, "boss"), "DevThrottle - Boss - run", "Go.");

        var req = built.Request;
        Assert.Equal("SOREN_NORTH", built.Machine);
        Assert.Equal("ClaudeCode", req.Agent);
        Assert.Equal("claude-opus-5-5", req.Model);
        Assert.True(req.FactoryHome);
        Assert.Null(req.FactoryWorktree);
        Assert.Equal("", req.RepoPath);
        Assert.Equal("boss", req.FactorySeat);
        Assert.Equal(FactoryKits.Seat, req.FactoryKit);
    }

    [Fact]
    public void Build_AFactoryWithNoDefaultModel_SendsNone_SoTheAgentsOwnDefaultIsUsed()
    {
        var p = Published(DefinitionFixture.PublishableEdit("factory.yaml", "  model: claude-opus-5-5\n", ""));

        var built = FactorySeatStart.Build(p, Seat(p, "boss"), "n", "Go.");

        Assert.Null(built.Request.Model);
        Assert.True(built.Request.CarriesSeatStartFields());
    }

    [Fact]
    public void Build_WithAPersonsOverride_UsesWhatWasChosen_AndRecordsItInWords()
    {
        var p = Published();

        var built = FactorySeatStart.Build(p, Seat(p, "boss"), "n", "Go.",
            new FactorySeatStartOverride("Codex", "gpt-6", "SOREN_SOUTH"));

        Assert.Equal("Codex", built.Request.Agent);
        Assert.Equal("gpt-6", built.Request.Model);
        Assert.Equal("SOREN_SOUTH", built.Machine);
        Assert.Equal("agent Codex, model gpt-6, machine SOREN_SOUTH, chosen by the person", built.Request.FactoryStartOverride);
    }

    [Fact]
    public void Build_WithAnOverrideOfTheMachineAlone_KeepsTheRest_AndNamesOnlyTheMachine()
    {
        var p = Published();

        var built = FactorySeatStart.Build(p, Seat(p, "outreach"), "n", "Go.", new FactorySeatStartOverride(null, " ", "SOREN_NORTH"));

        Assert.Equal("claude-sonnet-5-5", built.Request.Model);
        Assert.Equal("SOREN_NORTH", built.Machine);
        Assert.Equal("machine SOREN_NORTH, chosen by the person", built.Request.FactoryStartOverride);
    }

    [Fact]
    public void Build_WithAnEmptyOverride_RecordsNone()
    {
        var p = Published();

        var built = FactorySeatStart.Build(p, Seat(p, "boss"), "n", "Go.", new FactorySeatStartOverride(" ", null, ""));

        Assert.Null(built.Request.FactoryStartOverride);
        Assert.Equal("SOREN_NORTH", built.Machine);
    }

    // ---------- the override's refusals ----------

    [Fact]
    public void RefuseOverride_AnAgentThatCannotTakeTheSeatsRules_IsRefusedWithThatAgentsReason()
    {
        var p = Published();

        Assert.Equal(FactoryRulesSupport.CodexReason,
            FactorySeatStart.RefuseOverride(Seat(p, "outreach"), new FactorySeatStartOverride("Codex", null, null)));
    }

    [Fact]
    public void RefuseOverride_AnAgentThatCannotLoadTheSeatsFactorySkills_IsRefusedWithThatAgentsReason()
    {
        var p = Published();
        var writerWithASkill = Seat(p, "writer") with { FactorySkills = new[] { "mail-voice" } };

        Assert.Equal(FactorySkillSupport.CodexReason,
            FactorySeatStart.RefuseOverride(writerWithASkill, new FactorySeatStartOverride("Codex", null, null)));
    }

    [Fact]
    public void RefuseOverride_AnAgentOnASeatWithNoRulesAndNoSkills_IsTaken()
    {
        var p = Published();

        Assert.Null(FactorySeatStart.RefuseOverride(Seat(p, "writer"), new FactorySeatStartOverride("Codex", "gpt-6", null)));
    }

    [Fact]
    public void RefuseOverride_AnAgentNoSeatCanRun_IsRefusedByName_ListingTheAgents()
    {
        var p = Published();

        Assert.Equal("'RawCli' is not an agent a seat can run. Choose one of: " + string.Join(", ", FactorySkillSupport.SupportedAgents) + ".",
            FactorySeatStart.RefuseOverride(Seat(p, "writer"), FactorySeatStart.Normalize(new FactorySeatStartOverride("RawCli", null, null))));
    }

    [Fact]
    public void Normalize_SpellsTheAgentAsADefinitionDoes_AndDropsBlanks()
    {
        Assert.Equal(new FactorySeatStartOverride("Codex", null, "SOREN_SOUTH"),
            FactorySeatStart.Normalize(new FactorySeatStartOverride(" codex ", "  ", " SOREN_SOUTH ")));
        Assert.Null(FactorySeatStart.Normalize(new FactorySeatStartOverride(null, "", " ")));
        Assert.Null(FactorySeatStart.Normalize(null));
    }

    // ---------- reading the published definition ----------

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();
    private static readonly DateTime At = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    [Fact]
    public void ReadPublished_UNDEFINED_FACTORY_ADraftOnly_IsNotDefined()
    {
        var store = new FactoryDefinitionStore(Db);
        Assert.True(store.SaveDraft(TenantId.Local, DefinitionFixture.FactoryId, DefinitionFixture.Publishable(), 0,
            FactoryMemoryAuthorKinds.Person, "dev-1", null, At).Ok);

        Assert.Null(FactorySeatStart.ReadPublished(store, TenantId.Local, DefinitionFixture.FactoryId));
        Assert.Null(FactorySeatStart.ReadPublished(store, TenantId.Local, null));
        Assert.Null(FactorySeatStart.ReadPublished(store, TenantId.Local, "no-such-factory"));
    }

    [Fact]
    public void ReadPublished_APublishedVersion_IsReadWithItsNumber()
    {
        var store = new FactoryDefinitionStore(Db);
        Assert.True(store.SaveDraft(TenantId.Local, DefinitionFixture.FactoryId, DefinitionFixture.Publishable(), 0,
            FactoryMemoryAuthorKinds.Person, "dev-1", null, At).Ok);
        Assert.True(store.Publish(TenantId.Local, DefinitionFixture.FactoryId, 1, FactoryMemoryAuthorKinds.Person, "dev-1", At).Ok);

        var published = FactorySeatStart.ReadPublished(store, TenantId.Local, DefinitionFixture.FactoryId);

        Assert.NotNull(published);
        Assert.Equal(1, published!.Version);
        Assert.Equal(new[] { "boss", "outreach", "writer" }, published.Definition.Seats.Select(s => s.Seat));
        Assert.Equal(new CcDirector.Gateway.Factory.Talk.FactoryGivenBrief(1, "Run the factory.\n", FactoryBriefVersionKinds.Definition),
            published.BriefOf(published.Seat("boss")!));
    }

    [Fact]
    public void ReadPublished_APublishedVersionThatDoesNotParse_IsRefusedInWords()
    {
        var store = new FactoryDefinitionStore(Db);
        // The store holds what it is given; a version published before a rule tightened can fail the check today.
        var content = new Dictionary<string, string> { ["factory.yaml"] = "factory: devthrottle\ntitle: DevThrottle\n" };
        Assert.True(store.SaveDraft(TenantId.Local, DefinitionFixture.FactoryId, content, 0, FactoryMemoryAuthorKinds.Person, "dev-1", null, At).Ok);
        Assert.True(store.Publish(TenantId.Local, DefinitionFixture.FactoryId, 1, FactoryMemoryAuthorKinds.Person, "dev-1", At).Ok);

        var ex = Assert.Throws<InvalidOperationException>(() => FactorySeatStart.ReadPublished(store, TenantId.Local, DefinitionFixture.FactoryId));

        Assert.Equal("version 1 of the definition of 'devthrottle' is published but cannot be read; publish a version that passes the check", ex.Message);
    }

    [Fact]
    public void SeatNotInVersion_NamesTheVersionAndTheSeatsItHolds()
    {
        Assert.Equal("the seat 'scout' is not in version 4 of the definition of 'devthrottle', the published one, so no session was " +
                     "started. Publish a definition that holds it, or use one of its seats: boss, outreach, writer.",
            FactorySeatStart.SeatNotInVersion(Published(), "scout"));
    }
}

