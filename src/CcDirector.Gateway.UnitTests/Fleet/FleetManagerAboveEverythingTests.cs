using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// The Factory Manager is retired and the Fleet Manager sits above everything (the owner's decision, 1 November 2026).
/// Held down here: a problem of a factory with no boss reaches the Fleet Manager as a durable event, an account with the
/// old stored mark upgrades without losing a row, and the conduct every Fleet Manager reads carries each job it took on.
/// </summary>
public sealed class FleetManagerAboveEverythingTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string RunId = "3f1c2b7a-0000-4000-8000-0000000000a1";
    private const string FleetManagerId = "81000000-0000-4000-8000-000000000001";

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static FleetManagerProblemEvent Problem() =>
        new(RunId, "81000000-0000-4000-8000-000000000002", "WarmForward - Scout - sweep", "WarmForward has no boss.");

    // ---- the problem event ----------------------------------------------------------------------------------------

    [Fact]
    public void RecordProblem_IsOwedToTheFleetManager_WhicheverSessionHoldsTheMark()
    {
        var store = new FleetManagerEventStore(_harness.Open());

        var stored = store.RecordProblem(TenantId.Local, Problem(), Now);

        Assert.NotNull(stored);
        var owed = Assert.Single(store.Owed(TenantId.Local, FleetManagerId, 10).Events);
        Assert.Equal(FleetManagerEventStore.KindProblem, owed.Kind);
        Assert.Equal(RunId, owed.OutcomeId);
        Assert.Equal("WarmForward has no boss.", owed.Words);
        Assert.False(owed.ReadingPending);
    }

    [Fact]
    public void RecordProblem_ASecondPressWhileItWaits_AddsNothing_AndAfterItIsAcknowledgedIsStoredAgain()
    {
        var store = new FleetManagerEventStore(_harness.Open());
        var first = store.RecordProblem(TenantId.Local, Problem(), Now)!;

        Assert.Null(store.RecordProblem(TenantId.Local, Problem(), Now.AddMinutes(1)));
        Assert.Equal(1, store.Count(TenantId.Local, FleetManagerEventStore.StatusUnacknowledged));

        store.Acknowledge(TenantId.Local, new[] { Guid.Parse(first.Id) }, all: false, FleetManagerId, Now.AddMinutes(2));

        Assert.NotNull(store.RecordProblem(TenantId.Local, Problem(), Now.AddMinutes(3)));
    }

    [Fact]
    public void Prompt_ToldOfAProblem_NamesTheRun_AndCarriesTheBriefExactly()
    {
        var problem = new FleetManagerEventDto
        {
            Id = "e-1", Kind = FleetManagerEventStore.KindProblem, SessionName = "WarmForward - Scout - sweep",
            OutcomeId = RunId, Words = "WarmForward has no boss.\nthe second line",
        };

        var prompt = FleetManagerEventPrompt.Build(new[] { problem });

        Assert.StartsWith(FleetManagerEventPrompt.FirstLinePrefix, prompt);
        Assert.Contains("1 problem sent by the owner", prompt);
        Assert.Contains("problem: WarmForward - Scout - sweep", prompt);
        Assert.Contains("run: " + RunId, prompt);
        Assert.Contains("<<<WarmForward has no boss.\nthe second line>>>", prompt);
    }

    // ---- an account that already holds the old mark --------------------------------------------------------------

    [Theory]
    [InlineData("factory_manager_session_id", "dead0000-0000-4000-8000-000000000001")]
    [InlineData("factory_manager_machine", "devlinux")]
    [InlineData("factory_manager_folder", "/home/soren/factories")]
    [InlineData("factory_manager_earlier_session_ids", "dead0000-0000-4000-8000-000000000002")]
    public void AStoredFactoryManagerRow_StaysStored_AndChangesNoSetting(string key, string value)
    {
        var db = _harness.Open();
        using (var ctx = db.CreateContext(TenantId.Local))
        {
            ctx.TenantSettings.Add(new TenantSettingEntity { TenantId = TenantId.Local.Value, Key = key, Value = value, UpdatedAtUtc = Now });
            ctx.SaveChanges();
        }
        var store = new TenantSettingsStore(db);
        var settings = new TenantSettingsResolver(store);

        Assert.Equal(value, store.GetAll(TenantId.Local)[key]);
        Assert.Null(settings.FleetManagerSessionId(TenantId.Local));
        // The retired key is no setting of the product now, so nothing can write it again.
        Assert.DoesNotContain(key, TenantSettingKeys.All);
    }

    // ---- the conduct ----------------------------------------------------------------------------------------------

    private static string Repo(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln"))) dir = dir.Parent;
        Assert.True(dir is not null, "the repository root was not found above the test binary");
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(path).ToArray()));
    }

    private static string Workflow() => Repo("src", "CcDirector.Gateway", "Workflows", "Content", "fleet-manager.instructions.md");
    private static string Skill() => Repo("src", "CcDirector.Gateway", "Skills", "Content", "fleet-manager.skill.md");

    [Theory]
    [InlineData("A question about one factory belongs to its boss")]
    [InlineData("Maintain and manage the bosses")]
    [InlineData("Chase every open problem with the boss concerned")]
    [InlineData("Deal with what no single boss owns")]
    [InlineData("Stand in for a missing boss")]
    [InlineData("Resolve a problem only when it is fixed")]
    [InlineData("Flag every finding of the name check to the owner")]
    [InlineData("Watch for too many random small sessions")]
    public void TheWorkflow_CarriesEveryJobTheFleetManagerTookOn(string job)
        => Assert.Contains(job, Workflow());

    [Theory]
    [InlineData("cc-devthrottle run problems")]
    [InlineData("cc-devthrottle factory status --factory")]
    [InlineData("cc-devthrottle factory name-check")]
    [InlineData("cc-devthrottle schedule list")]
    [InlineData("cc-devthrottle run resolve")]
    [InlineData("cc-devthrottle factory list")]
    [InlineData("--factory <id> --seat <boss seat>")]
    public void TheSkill_GivesTheCommandForEachJob(string command)
        => Assert.Contains(command, Skill());

    [Fact]
    public void TheOwnersRuleAboutSessionsOpenedDirectly_StaysAsItWas()
        => Assert.Contains("Sessions the owner opened directly still ask the owner.", Workflow());

    [Fact]
    public void NoShippedConduct_OffersAFactoryManager()
    {
        Assert.DoesNotContain("Factory Manager", Workflow());
        Assert.DoesNotContain("Factory Manager", Skill());
        Assert.DoesNotContain("factory manager", Repo("docs", "cli-reference.md"));
        // The terminology skill names it exactly to say it is gone.
        Assert.Contains("There is no Factory Manager", Repo("src", "CcDirector.Gateway", "Skills", "Content", "terminology.skill.md"));
    }
}
