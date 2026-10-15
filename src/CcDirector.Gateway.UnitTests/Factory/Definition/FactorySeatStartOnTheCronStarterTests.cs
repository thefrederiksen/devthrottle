using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A DEFINED FACTORY'S SCHEDULED RUN IS BUILT FROM ITS DEFINITION (Factory Definition mission, phase 5; design section 3),
/// read at what LEAVES the Gateway: the create the cron starter hands the machine spawner. The schedule row still carries
/// the folder it was written with before the factory was defined, to show it is no longer where a run starts. An
/// undefined factory's run is built exactly as before.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactorySeatStartOnTheCronStarterTests
{
    private static readonly DateTime FiredAt = new(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);
    private const string OwnFolder = @"D:\ReposFred\devthrottle";

    private sealed class StubResolver : IDirectorTargetResolver
    {
        public readonly List<string> Machines = new();
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct)
        {
            Machines.Add(machine);
            return Task.FromResult(new DirectorTargetResult("d-1", null));
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => FiredAt;
    }

    private readonly StubResolver _resolver = new();
    private readonly List<NewSessionRequest> _sent = new();

    private static PublishedFactoryDefinition Published()
        => new(3, FactoryDefinitionCheck.Read(DefinitionFixture.Publishable()).Definition!);

    /// <summary>The seat as the registry holds it after a publish, with the brief the Gateway gives a defined seat.</summary>
    private static ScheduledRunSeat Registered(string seat, string name) => new(
        new RegisteredFactoryDto
        {
            Factory = DefinitionFixture.FactoryId,
            Title = "DevThrottle",
            Folder = OwnFolder,
            BossSeat = "boss",
            Seats = { new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Role = "Boss" }, new RegisteredFactorySeatDto { Id = "outreach", Name = "Outreach", Role = "Sales" } },
        },
        new RegisteredFactorySeatDto { Id = seat, Name = name, Role = seat == "boss" ? "Boss" : "Sales" },
        new FactoryGivenBrief(3, "Read from the brief reader.", FactoryBriefVersionKinds.Definition));

    private DirectorCronSessionStarter Starter(Func<CronJobDto, PublishedFactoryDefinition?> definitionOf,
        Func<CronJobDto, ScheduledRunSeat?>? seatWithABrief = null)
    {
        var spawner = new MachineSessionSpawner(_resolver, (_, req, _) =>
        {
            _sent.Add(req);
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-1" }, null));
        })
        {
            // The resolved Director builds a seat's kit.
            RefuseSeatStart = (_, _) => null,
        };
        return new DirectorCronSessionStarter(spawner, new FixedClock(), refuseFactoryWork: _ => null,
            seatWithABrief: seatWithABrief ?? (j => j.Seat is null ? null : Registered(j.Seat, j.Seat == "boss" ? "Boss" : "Outreach")),
            definitionOf: definitionOf);
    }

    private static CronJobDto Job(string? seat, string machine = "SOREN_NORTH") => new()
    {
        Id = "cj_boss",
        Name = "DevThrottle - Boss",
        TimeZoneId = "UTC",
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 6 * * *",
        Factory = seat is null ? null : DefinitionFixture.FactoryId,
        Seat = seat,
        Target = new CronJobTarget { Machine = machine },
        // Written before the factory was defined: the folder is still on the row, and is no longer where a run starts.
        Action = new CronJobAction { RepoPath = OwnFolder, Seed = "Morning round.", AutoDismiss = true },
    };

    [Fact]
    public async Task Start_ADefinedFactorysSeatWithNoRepository_IsAHomeSeatStart_CarryingTheSeatFields_AndNoFolder()
    {
        var start = await Starter(_ => Published()).StartRunAsync(Job("boss"), CancellationToken.None);

        Assert.Null(start.Error);
        Assert.Equal("sid-1", start.SessionId);
        var req = Assert.Single(_sent);
        Assert.Equal("", req.RepoPath);
        Assert.True(req.FactoryHome);
        Assert.Null(req.FactoryWorktree);
        Assert.Equal("boss", req.FactorySeat);
        Assert.Equal(3, req.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, req.FactoryKit);
        Assert.Equal("ClaudeCode", req.Agent);
        Assert.Equal("claude-opus-5-5", req.Model);
        Assert.Equal(DefinitionFixture.FactoryId, req.Factory);
        Assert.Null(req.FactoryStartOverride);
        Assert.Equal(SessionOriginKinds.Schedule, req.Origin);
        Assert.Equal(SessionOriginSurfaces.Cron, req.OriginSurface);
        Assert.True(req.AutoDismiss);
        Assert.Equal("DevThrottle - Boss - 2026-10-15 06:00", req.Name);
        Assert.Equal(new[] { "SOREN_NORTH" }, _resolver.Machines);
        // The brief given is the published version's, the version recorded on the run.
        Assert.Contains("--- your brief, definition version 3 ---\nRun the factory.\n--- end of your brief, definition version 3 ---", req.PrePrompt);
        Assert.DoesNotContain("Read from the brief reader.", req.PrePrompt);
        Assert.Equal(3, start.BriefVersion);
        Assert.Equal(FactoryBriefVersionKinds.Definition, start.BriefVersionKind);
    }

    [Fact]
    public async Task Start_ADefinedFactorysRepositorySeat_GoesToTheSeatsMachine_NamingItsRepository()
    {
        var job = Job("outreach", machine: "SOREN_SOUTH");

        await Starter(_ => Published()).StartRunAsync(job, CancellationToken.None);

        var req = Assert.Single(_sent);
        Assert.Equal("", req.RepoPath);
        Assert.Equal("devthrottle", req.FactoryWorktree);
        Assert.Null(req.FactoryHome);
        Assert.Equal("claude-sonnet-5-5", req.Model);
        Assert.Equal(new[] { "SOREN_SOUTH" }, _resolver.Machines);
    }

    [Fact]
    public async Task Start_ADefinedFactorysRun_IsToldWhereItsKitIs_AndHowItsGoalChanges_NeverAFolderOrAGoalFile()
    {
        await Starter(_ => Published()).StartRunAsync(Job("boss"), CancellationToken.None);

        var given = Assert.Single(_sent).PrePrompt!;
        Assert.Contains(FactorySeatOpening.KitSentence, given);
        Assert.Contains("CC_FACTORY_KIT_DIR", given);
        Assert.Contains("save a draft of the definition with the new goal (cc-devthrottle factory definition save); the owner publishes it.", given);
        Assert.DoesNotContain("GOAL.md", given);
        Assert.DoesNotContain("factory register", given);
        Assert.DoesNotContain(OwnFolder, given);
    }

    [Fact]
    public async Task Start_ASeatThePublishedVersionDoesNotHold_IsNotStarted_AndSaysWhy()
    {
        var job = Job("boss");
        job.Seat = "scout";

        var start = await Starter(_ => Published(), seatWithABrief: _ => Registered("scout", "Scout")).StartRunAsync(job, CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Null(start.SessionId);
        Assert.Equal("not started: the seat 'scout' is not in version 3 of the definition of 'devthrottle', the published one, so no " +
                     "session was started. Publish a definition that holds it, or use one of its seats: boss, outreach, writer.", start.Error);
    }

    [Fact]
    public async Task Start_APublishedDefinitionThatCannotBeRead_IsNotStarted_AndSaysWhy()
    {
        var start = await Starter(_ => throw new InvalidOperationException("version 3 of the definition of 'devthrottle' is published but cannot be read; publish a version that passes the check"))
            .StartRunAsync(Job("boss"), CancellationToken.None);

        Assert.Empty(_sent);
        Assert.Equal("not started: version 3 of the definition of 'devthrottle' is published but cannot be read; publish a version that passes the check", start.Error);
    }

    [Fact]
    public async Task Start_UNDEFINED_FACTORY_IsBuiltExactlyAsBefore_InTheSchedulesFolder_WithNoSeatField()
    {
        var job = Job("boss");

        await Starter(_ => null).StartRunAsync(job, CancellationToken.None);

        var req = Assert.Single(_sent);
        Assert.Equal(OwnFolder, req.RepoPath);
        Assert.Equal("ClaudeCode", req.Agent);
        Assert.False(req.CarriesSeatStartFields());
        Assert.Null(req.FactoryStartOverride);
        Assert.Equal(DefinitionFixture.FactoryId, req.Factory);
        Assert.Equal(new[] { "SOREN_NORTH" }, _resolver.Machines);
        Assert.Equal(ScheduledRunOpening.PromptFor(job.Action.Seed, job.Name, job.Id, Registered("boss", "Boss")), req.PrePrompt);
    }

    [Fact]
    public async Task Start_UNDEFINED_FACTORY_AScheduleInNoFactory_IsNotAskedForADefinition()
    {
        var asked = false;
        var job = Job(null);

        await Starter(j => { asked = j.Factory is not null; return null; }).StartRunAsync(job, CancellationToken.None);

        Assert.False(asked);
        Assert.Equal(OwnFolder, Assert.Single(_sent).RepoPath);
    }
}
