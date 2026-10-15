using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// The Gateway opens a seat's scheduled run (Factory Seats mission, phase 2c), driven through the REAL
/// <see cref="CronEngine"/>, the REAL <see cref="DirectorCronSessionStarter"/>, the REAL registry and the REAL brief
/// store, wired the way the host wires them, down to the create a Director would be handed. What is asserted is the
/// text the run was given and the version recorded on the run - not a copy of either built by the test. Only the
/// Director itself is replaced, by a create that keeps what it was handed.
/// </summary>
public sealed class ScheduledRunOpeningTests : IDisposable
{
    private const string OwnersPrompt = "Do the morning run: read the overnight numbers and write the summary.";
    private const string TheFactory = "warmforward";
    private const string Owner = "device:the-owners-browser";
    private const string ASession = "5e551011-0000-0000-0000-000000000001";
    private static readonly TenantId Account = TenantId.Local;
    private static readonly DateTime Day = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();
    private FactoryRegistryStore? _registry;
    private FactoryRegistryStore Registry => _registry ??= new FactoryRegistryStore(Db);
    private FactoryBriefStore? _briefs;
    private FactoryBriefStore Briefs => _briefs ??= new FactoryBriefStore(Db, Registry);
    private FactoryDefinitionStore? _definitions;
    private FactoryDefinitionStore Definitions => _definitions ??= new FactoryDefinitionStore(Db);
    /// <summary>What a run is given, as the host reads it: from the definition for a defined factory, else the brief store.</summary>
    private FactoryDefinitionBriefs SeatBriefs => new(Definitions, Briefs);
    private readonly List<NewSessionRequest> _handedToTheDirector = new();
    /// <summary>When set, reading a seat's brief throws with these words, as a database that cannot be read would.</summary>
    private string? _briefReadFails;

    public void Dispose() => _h.Dispose();

    // ---- a published brief and a newer draft --------------------------------------------------------------------

    [Fact]
    public async Task ASeatWithAPublishedBriefAndANewerDraft_ItsRunIsGivenThePublishedVersion_NeverTheDraft()
    {
        Register();
        SaveDraft("PUBLISHED: be careful with money.", expect: 0);
        Publish(1);
        SaveDraft("DRAFT: spend freely, nobody has approved this.", expect: 1);
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "boss"));

        var fired = await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        var given = Assert.Single(_handedToTheDirector).PrePrompt;
        Assert.Contains("--- your brief, version 1 ---\nPUBLISHED: be careful with money.\n--- end of your brief, version 1 ---", given);
        Assert.DoesNotContain("DRAFT", given);
        Assert.DoesNotContain("version 2", given);
        Assert.Equal(1, Assert.Single(fired).BriefVersion);
    }

    [Fact]
    public async Task ASeatWithOnlyDrafts_HasNoPublishedBrief_SoItsRunIsStartedExactlyAsBefore()
    {
        Register();
        SaveDraft("DRAFT: nobody has approved this.", expect: 0);
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "boss"));

        var fired = await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        Assert.Equal(ScheduledRunClosingInstruction.PromptFor(OwnersPrompt), Assert.Single(_handedToTheDirector).PrePrompt);
        Assert.Null(Assert.Single(fired).BriefVersion);
    }

    // ---- the published version changing between two runs --------------------------------------------------------

    [Fact]
    public async Task ThePublishedVersionChangingBetweenTwoRuns_EachRunIsGivenAndRecordsTheVersionPublishedWhenItStarted()
    {
        Register();
        SaveDraft("FIRST: be careful with money.", expect: 0);
        Publish(1);
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "boss"));
        var engine = Engine(store, history, created.NextRunUtc!.Value);

        await engine.RunNowAsync(created.Id, CancellationToken.None);
        SaveDraft("SECOND: be careful with money, and say so every morning.", expect: 1);
        Publish(2);
        await engine.RunNowAsync(created.Id, CancellationToken.None);

        Assert.Equal(2, _handedToTheDirector.Count);
        Assert.Contains("--- your brief, version 1 ---\nFIRST:", _handedToTheDirector[0].PrePrompt);
        Assert.DoesNotContain("SECOND", _handedToTheDirector[0].PrePrompt);
        Assert.Contains("--- your brief, version 2 ---\nSECOND:", _handedToTheDirector[1].PrePrompt);
        Assert.DoesNotContain("FIRST", _handedToTheDirector[1].PrePrompt);

        // "Which brief was this run given" has an answer afterwards, read back from the database by another store.
        var reopened = new CronRunHistoryStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        var runs = reopened.List(created.Id);
        Assert.Equal(new int?[] { 2, 1 }, runs.Select(r => r.BriefVersion).ToArray());   // newest first
        Assert.Equal(new[] { "sid-2", "sid-1" }, runs.Select(r => r.SessionId).ToArray());
    }

    [Fact]
    public async Task ARunsActivityRow_CarriesTheVersionItWasGiven_AsTheFactoryAgentsVersion()
    {
        Register();
        SaveDraft("FIRST: be careful with money.", expect: 0);
        Publish(1);
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "boss"));
        var run = (await Engine(store, history, created.NextRunUtc!.Value).RunNowAsync(created.Id, CancellationToken.None)).Record;
        Assert.NotNull(run);
        run.Result = CronRunResults.Ok;

        var row = CronRunResultService.ActivityFor(created.Id, run);

        Assert.NotNull(row);
        Assert.Equal("1", row.FactoryAgentVersion);
        // A run the Gateway gave no brief leaves a row with no version, as every run did before.
        run.BriefVersion = null;
        Assert.Null(CronRunResultService.ActivityFor(created.Id, run)!.FactoryAgentVersion);
    }

    // ---- a seat renamed in the registration ---------------------------------------------------------------------

    [Fact]
    public async Task ASeatRenamedInTheRegistration_TheNextRunSaysTheNewName_WithTheScheduleAndTheBriefUntouched()
    {
        Register(scoutName: "Scout");
        SaveDraft("Find three leads a day.", expect: 0, seat: "scout");
        Publish(1, seat: "scout");
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "scout"));
        var engine = Engine(store, history, created.NextRunUtc!.Value);

        await engine.RunNowAsync(created.Id, CancellationToken.None);
        Register(scoutName: "Pathfinder");     // the one change: the factory is registered again with the new name
        await engine.RunNowAsync(created.Id, CancellationToken.None);

        var before = _handedToTheDirector[0].PrePrompt!;
        var after = _handedToTheDirector[1].PrePrompt!;
        Assert.StartsWith("You are Scout, Scout of WarmForward (factory id: warmforward; your agent id: scout).", before);
        Assert.StartsWith("You are Pathfinder, Scout of WarmForward (factory id: warmforward; your agent id: scout).", after);
        Assert.Equal(before, after.Replace("You are Pathfinder,", "You are Scout,"));
        // Nothing else was touched: the schedule's prompt is as the owner wrote it, and the brief is still version 1.
        Assert.Equal(OwnersPrompt, store.Get(created.Id)!.Action.Seed);
        Assert.Equal(1, Briefs.GivenTo(Account, TheFactory, "scout")!.Version);
        Assert.Equal(1, Briefs.History(Account, TheFactory, "scout").Total);
    }

    // ---- a schedule in no factory, and a seat with no brief -----------------------------------------------------

    [Fact]
    public async Task AScheduleInNoFactory_IsStartedExactlyAsBefore_EvenWhileAnotherSeatHasAPublishedBrief()
    {
        Register();
        SaveDraft("FIRST: be careful with money.", expect: 0);
        Publish(1);
        var (store, history) = Stores();
        var created = store.Create(Job(seat: null));

        var fired = await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        var handed = Assert.Single(_handedToTheDirector);
        Assert.Equal(OwnersPrompt + "\n\n" + ScheduledRunClosingInstruction.Text, handed.PrePrompt);
        Assert.Null(handed.Factory);
        Assert.Null(Assert.Single(fired).BriefVersion);
    }

    [Fact]
    public async Task ASeatWithNoBriefOnTheGateway_ItsRunIsStartedExactlyAsBefore_AndRecordsNoVersion()
    {
        Register();
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "boss"));

        var fired = await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        var handed = Assert.Single(_handedToTheDirector);
        Assert.Equal(OwnersPrompt + "\n\n" + ScheduledRunClosingInstruction.Text, handed.PrePrompt);
        Assert.Equal(TheFactory, handed.Factory);
        Assert.Null(Assert.Single(fired).BriefVersion);
    }

    [Fact]
    public async Task ASeatWhoseBriefBelongsToAnotherSeat_IsNotGivenIt()
    {
        Register();
        SaveDraft("The boss's brief.", expect: 0, seat: "boss");
        Publish(1, seat: "boss");
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "scout"));

        await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        Assert.Equal(OwnersPrompt + "\n\n" + ScheduledRunClosingInstruction.Text, Assert.Single(_handedToTheDirector).PrePrompt);
    }

    // ---- a brief at the size cap --------------------------------------------------------------------------------

    [Fact]
    public async Task ABriefAtTheSizeCap_IsGivenWhole_ToTheRunAndToATalk()
    {
        Register();
        // Exactly the cap, and recognisable at both ends so a cut anywhere would show.
        var atTheCap = "BEGIN-" + new string('x', FactoryBriefStore.MaxBriefBytes - "BEGIN-".Length - "-END".Length) + "-END";
        Assert.Equal(FactoryBriefStore.MaxBriefBytes, System.Text.Encoding.UTF8.GetByteCount(atTheCap));
        SaveDraft(atTheCap, expect: 0);
        Publish(1);
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "boss"));

        var fired = await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        var given = Assert.Single(_handedToTheDirector).PrePrompt!;
        Assert.Contains("--- your brief, version 1 ---\n" + atTheCap + "\n--- end of your brief, version 1 ---", given);
        Assert.EndsWith(OwnersPrompt + "\n\n" + ScheduledRunClosingInstruction.Text, given);
        Assert.Equal(1, Assert.Single(fired).BriefVersion);

        var factory = Registry.Find(Account, TheFactory)!;
        var talk = FactoryTalkSeed.Compose(factory, factory.Seats[0], created.Id, OwnersPrompt, Briefs.GivenTo(Account, TheFactory, "boss"));
        Assert.Contains(atTheCap, talk);
    }

    // ---- which seat a schedule's run is opened as ---------------------------------------------------------------

    [Fact]
    public void SeatFor_AScheduleWhoseFactoryOrSeatIsNotRegistered_IsNotOpenedByTheGateway()
    {
        Register();
        SaveDraft("FIRST", expect: 0);
        Publish(1);
        Func<string, RegisteredFactoryDto?> find = id => Registry.Find(Account, id);
        Func<string, string, FactoryGivenBrief?> brief = (factory, seat) => Briefs.GivenTo(Account, factory, seat);

        Assert.NotNull(ScheduledRunOpening.SeatFor(Job(seat: "boss"), find, brief));
        Assert.NotNull(ScheduledRunOpening.SeatFor(Job(seat: " BOSS "), find, brief));
        Assert.Null(ScheduledRunOpening.SeatFor(Job(seat: "night-watch"), find, brief));
        var elsewhere = Job(seat: "boss");
        elsewhere.Factory = "another-factory";
        Assert.Null(ScheduledRunOpening.SeatFor(elsewhere, find, brief));
        Assert.Null(ScheduledRunOpening.SeatFor(Job(seat: null), find, brief));
    }

    [Fact]
    public async Task ARunThatDidNotStart_RecordsNoVersion_BecauseNoSessionWasGivenTheBrief()
    {
        Register();
        SaveDraft("FIRST", expect: 0);
        Publish(1);
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "boss"));

        var fired = await Engine(store, history, created.NextRunUtc!.Value, directorStarts: false).EvaluateDueAsync(CancellationToken.None);

        var run = Assert.Single(fired);
        Assert.Null(run.SessionId);
        Assert.Null(run.BriefVersion);
    }

    // ---- the brief cannot be read (review 1, finding 1) ---------------------------------------------------------

    [Fact]
    public async Task TheBriefCannotBeRead_TheRunIsNotStarted_AndIsRecordedAsARunThatDidNotRunWithTheReason()
    {
        Register();
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "boss"));
        _briefReadFails = "the database is locked";

        var fired = await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        // Never started without its brief: the Director was handed nothing.
        Assert.Empty(_handedToTheDirector);
        // And not lost: the run is on record, read back from the database by another store, saying why.
        Assert.Single(fired);
        var reopened = new CronRunHistoryStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        var row = Assert.Single(reopened.List(created.Id));
        Assert.Null(row.SessionId);
        Assert.Equal("not-started", row.InfraStatus);
        Assert.Equal(CronRunResults.Problem, row.Result);
        Assert.Equal(CronRunProblems.DidNotRun, row.Problem);
        Assert.Equal("it did not start a session: " + DirectorCronSessionStarter.BriefNotRead + ": the database is locked", row.ResultReason);
        Assert.Null(row.BriefVersion);
        Assert.Equal(TheFactory, row.Factory);
        // The schedule moved on, as it does after any start that failed: it is not left due for ever.
        Assert.True(store.Get(created.Id)!.NextRunUtc > created.NextRunUtc);
    }

    [Fact]
    public async Task TheBriefCanBeReadAgain_TheNextRunStartsWithItsBrief()
    {
        Register();
        SaveDraft("FIRST: be careful with money.", expect: 0);
        Publish(1);
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "boss"));
        var engine = Engine(store, history, created.NextRunUtc!.Value);
        _briefReadFails = "the database is locked";
        await engine.RunNowAsync(created.Id, CancellationToken.None);

        _briefReadFails = null;
        var second = await engine.RunNowAsync(created.Id, CancellationToken.None);

        Assert.Contains("--- your brief, version 1 ---", Assert.Single(_handedToTheDirector).PrePrompt);
        Assert.Equal(1, second.Record!.BriefVersion);
        Assert.Equal(2, history.List(created.Id).Count);
    }

    // ---- a defined factory: the brief comes from the published definition (Factory Definition, phase 4) ----------

    /// <summary>A definition of WarmForward whose seat files hold these briefs; published unless told otherwise. The
    /// store holds what it is given - the whole-definition check is the routes' - so only the seat files matter here.</summary>
    private void Define(string bossBrief, bool publish = true, bool withScout = true)
    {
        var content = new Dictionary<string, string>
        {
            ["factory.yaml"] = "factory: warmforward\ntitle: WarmForward\nbossSeat: boss\ndefaults:\n  machine: workstation-A\n  agent: ClaudeCode\n",
            ["seats/boss.yaml"] = $"seat: boss\nname: Boss\nrole: Boss\nbrief: |\n  {bossBrief}\n",
        };
        if (withScout) content["seats/scout.yaml"] = "seat: scout\nname: Scout\nrole: Scout\nbrief: Scout the market.\n";
        Assert.True(Definitions.SaveDraft(Account, TheFactory, content, 0, FactoryMemoryAuthorKinds.Person, Owner, null, Day).Ok);
        if (publish) Assert.True(Definitions.Publish(Account, TheFactory, 1, FactoryMemoryAuthorKinds.Person, Owner, Day.AddMinutes(1)).Ok);
    }

    [Fact]
    public async Task ADefinedFactory_ItsRunIsGivenTheBriefInThePublishedDefinition_AndRecordsADefinitionVersion()
    {
        Register();
        SaveDraft("BRIEF STORE: the brief store is not read for a defined factory.", expect: 0);
        Publish(1);
        Define("DEFINED: be careful with money.");
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "boss"));

        var fired = await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        var given = Assert.Single(_handedToTheDirector).PrePrompt;
        Assert.Contains("(definition version 1, part of the factory's definition)", given);
        Assert.Contains("--- your brief, definition version 1 ---\nDEFINED: be careful with money.\n--- end of your brief, definition version 1 ---", given);
        Assert.DoesNotContain("BRIEF STORE", given);
        var run = Assert.Single(fired);
        Assert.Equal(1, run.BriefVersion);
        Assert.Equal(FactoryBriefVersionKinds.Definition, run.BriefVersionKind);

        // Read back from the database by another store, and onto the activity row as a definition version.
        var reopened = new CronRunHistoryStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        var stored = Assert.Single(reopened.List(created.Id));
        Assert.Equal(FactoryBriefVersionKinds.Definition, stored.BriefVersionKind);
        stored.Result = CronRunResults.Ok;
        Assert.Equal("definition v1", CronRunResultService.ActivityFor(created.Id, stored)!.FactoryAgentVersion);
    }

    [Fact]
    public async Task ADraftOnlyDefinition_UNDEFINED_FACTORY_ItsRunIsGivenTheBriefStoresBrief_AsABriefVersion()
    {
        Register();
        SaveDraft("BRIEF STORE: what this seat is told.", expect: 0);
        Publish(1);
        Define("DEFINED BUT NEVER PUBLISHED.", publish: false);
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "boss"));

        var fired = await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        var given = Assert.Single(_handedToTheDirector).PrePrompt;
        Assert.Contains("--- your brief, version 1 ---\nBRIEF STORE: what this seat is told.\n--- end of your brief, version 1 ---", given);
        Assert.DoesNotContain("DEFINED", given);
        var run = Assert.Single(fired);
        Assert.Equal(FactoryBriefVersionKinds.Brief, run.BriefVersionKind);
        run.Result = CronRunResults.Ok;
        Assert.Equal("1", CronRunResultService.ActivityFor(created.Id, run)!.FactoryAgentVersion);
    }

    [Fact]
    public async Task ADefinedFactoryWithNoFileForTheSeat_ItsRunIsNotStarted_AndSaysWhy_NeverToldAFilePath()
    {
        Register();
        Define("DEFINED: be careful with money.", withScout: false);
        var (store, history) = Stores();
        var created = store.Create(Job(seat: "scout"));

        var fired = await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        Assert.Empty(_handedToTheDirector);
        var run = Assert.Single(fired);
        Assert.Null(run.SessionId);
        Assert.Null(run.BriefVersion);
        Assert.Null(run.BriefVersionKind);
    }

    // ---- the rig ------------------------------------------------------------------------------------------------

    private void Register(string scoutName = "Scout") => Registry.Register(Account, new RegisterFactoryRequest
    {
        Factory = TheFactory,
        Title = "WarmForward",
        Folder = "/home/soren/factories/warmforward",
        Computer = "workstation-A",
        BossSeat = "boss",
        Seats =
        {
            new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" },
            new FactorySeatManifest { Id = "scout", Name = scoutName, Role = "Scout", BriefFile = "agents/scout.yaml" },
        },
    }, "the owner (test)", Day);

    private void SaveDraft(string text, int expect, string seat = "boss") =>
        Assert.True(Briefs.SaveDraft(Account, TheFactory, seat, text, expect, FactoryMemoryAuthorKinds.Session, ASession, Day).Ok);

    private void Publish(int version, string seat = "boss") =>
        Assert.True(Briefs.Publish(Account, TheFactory, seat, version, FactoryMemoryAuthorKinds.Person, Owner, Day.AddMinutes(5)).Ok);

    private (CronJobStore store, CronRunHistoryStore history) Stores() =>
        (new CronJobStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json")),
         new CronRunHistoryStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json")));

    private CronEngine Engine(CronJobStore store, CronRunHistoryStore history, DateTime utcNow, bool directorStarts = true)
    {
        var clock = new FakeClock(utcNow);
        var spawner = new MachineSessionSpawner(new StubResolver(), (directorId, req, ct) =>
        {
            if (!directorStarts)
                return Task.FromResult<(bool, SessionDto?, string?)>((false, null, "the machine is asleep"));
            _handedToTheDirector.Add(req);
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = $"sid-{_handedToTheDirector.Count}" }, null));
        });
        // The two factory lines, as GatewayHost writes them.
        var starter = new DirectorCronSessionStarter(spawner, clock,
            refuseFactoryWork: job => FactoryScheduleLink.Check(job.Factory, job.Seat, id => Registry.Find(Account, id), out _, running: true),
            seatWithABrief: job => ScheduledRunOpening.SeatFor(job, id => Registry.Find(Account, id),
                (factory, seat) => _briefReadFails is { } why
                    ? throw new InvalidOperationException(why)
                    : SeatBriefs.GivenTo(Account, factory, seat)));
        return new CronEngine(store, history, starter, new UnusedWorkListRunner(), new NullCronNotifier(), clock);
    }

    private static CronJobDto Job(string? seat) => new()
    {
        Name = "WarmForward - morning run",
        Enabled = true,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        PreventOverlap = false,
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = "/home/soren/factories/warmforward", Seed = OwnersPrompt },
        Factory = seat is null ? null : TheFactory,
        Seat = seat,
    };

    private sealed class StubResolver : IDirectorTargetResolver
    {
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct) =>
            Task.FromResult(new DirectorTargetResult("d-1", null));
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow) => UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public DateTime UtcNow { get; }
    }

    private sealed class UnusedWorkListRunner : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("a seed schedule must not trigger the work-list runner");
    }
}
