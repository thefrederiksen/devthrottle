using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Every scheduled run is told to report, by the product (Factory Control, step 1). The wording is tested on its own,
/// and every way a seed schedule starts a session is driven through the REAL <see cref="CronEngine"/> and the REAL
/// <see cref="DirectorCronSessionStarter"/> down to the create a Director would be handed - so what is asserted is the
/// text the run is given, not a copy of it built by the test.
/// </summary>
public sealed class ScheduledRunClosingInstructionTests : IDisposable
{
    private const string OwnersPrompt = "Triage the overnight errors and file what you find.";

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();
    private NewSessionRequest? _handedToTheDirector;
    private int _creates;

    public void Dispose() => _h.Dispose();

    // ---- the wording --------------------------------------------------------------------------

    [Fact]
    public void PromptFor_AnOrdinaryPrompt_EndsWithTheClosingInstruction()
    {
        var given = ScheduledRunClosingInstruction.PromptFor(OwnersPrompt);

        Assert.Equal(OwnersPrompt + "\n\n" + ScheduledRunClosingInstruction.Text, given);
    }

    [Fact]
    public void Text_NamesBothResultsARunCanReport()
    {
        Assert.Contains("cc-devthrottle run result ok", ScheduledRunClosingInstruction.Text);
        Assert.Contains("cc-devthrottle run result problem \"<one line why>\"", ScheduledRunClosingInstruction.Text);
        Assert.DoesNotContain('\n', ScheduledRunClosingInstruction.Text);
        Assert.All(ScheduledRunClosingInstruction.Text, c => Assert.InRange(c, ' ', '~'));
    }

    [Theory]
    [InlineData("Do the work, then cc-devthrottle run result ok.")]
    [InlineData("Do the work. At the end: CC-DevThrottle Run Result problem \"why\" if it failed.")]
    public void PromptFor_APromptThatAlreadyNamesRunResult_IsLeftExactlyAsItIs(string ownersWording)
    {
        Assert.Equal(ownersWording, ScheduledRunClosingInstruction.PromptFor(ownersWording));
    }

    [Theory]
    [InlineData("Check the test run results and fix what failed.")]
    [InlineData("Read the last run result in the log, then carry on.")]
    public void PromptFor_APromptThatOnlyHappensToSayRunResult_StillGetsTheInstruction(string prompt)
    {
        // Only the command itself counts as the owner's own wording: these prompts have not told their run to report.
        Assert.Equal(prompt + "\n\n" + ScheduledRunClosingInstruction.Text, ScheduledRunClosingInstruction.PromptFor(prompt));
    }

    [Fact]
    public void PromptFor_APromptEndingInBlankLines_GetsOneBlankLineBeforeTheInstruction()
    {
        var given = ScheduledRunClosingInstruction.PromptFor("/mail-desk\n\n");

        Assert.Equal("/mail-desk\n\n" + ScheduledRunClosingInstruction.Text, given);
    }

    [Fact]
    public void PromptFor_ABlankPrompt_Throws()
    {
        Assert.Throws<ArgumentException>(() => ScheduledRunClosingInstruction.PromptFor("  "));
    }

    // ---- every way a scheduled run is started --------------------------------------------------

    [Fact]
    public async Task EvaluateDue_ARunStartedAtItsOwnTime_IsGivenTheClosingInstruction()
    {
        var (store, history) = Stores();
        var created = store.Create(Job());

        var fired = await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        Assert.Equal("started", Assert.Single(fired).InfraStatus);
        Assert.Equal(OwnersPrompt + "\n\n" + ScheduledRunClosingInstruction.Text, _handedToTheDirector!.PrePrompt);
    }

    [Fact]
    public async Task EvaluateDue_ARunCaughtUpLate_IsGivenTheClosingInstruction()
    {
        var (store, history) = Stores();
        var created = store.Create(Job());

        var fired = await Engine(store, history, created.NextRunUtc!.Value.AddMinutes(10)).EvaluateDueAsync(CancellationToken.None);

        Assert.Equal("catch-up", Assert.Single(fired).InfraStatus);
        Assert.Equal(OwnersPrompt + "\n\n" + ScheduledRunClosingInstruction.Text, _handedToTheDirector!.PrePrompt);
    }

    [Fact]
    public async Task RunNow_ARunStartedByHand_IsGivenTheClosingInstruction()
    {
        var (store, history) = Stores();
        var created = store.Create(Job());

        // Well before the schedule is next due: this run is started by hand, not by its time.
        var result = await Engine(store, history, created.NextRunUtc!.Value.AddHours(-6)).RunNowAsync(created.Id, CancellationToken.None);

        Assert.Equal(CronFireOutcome.Fired, result.Outcome);
        Assert.Equal(OwnersPrompt + "\n\n" + ScheduledRunClosingInstruction.Text, _handedToTheDirector!.PrePrompt);
    }

    [Fact]
    public async Task EvaluateDue_ARunOfAScheduleLinkedToAFactory_IsGivenTheClosingInstruction()
    {
        var (store, history) = Stores();
        var job = Job();
        job.Factory = "mail";
        job.Seat = "mail-desk";
        var created = store.Create(job);

        await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        Assert.Equal("mail", _handedToTheDirector!.Factory);
        Assert.Equal(OwnersPrompt + "\n\n" + ScheduledRunClosingInstruction.Text, _handedToTheDirector.PrePrompt);
    }

    [Fact]
    public async Task EvaluateDue_APromptThatAlreadyNamesRunResult_IsHandedOverUnchanged()
    {
        const string ownersWording = "Sort the inbox. When done: cc-devthrottle run result ok";
        var (store, history) = Stores();
        var created = store.Create(Job(ownersWording));

        await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        Assert.Equal(ownersWording, _handedToTheDirector!.PrePrompt);
    }

    // ---- what must not change ------------------------------------------------------------------

    [Fact]
    public async Task EvaluateDueAndRunNow_AfterRunsHaveStarted_TheStoredPromptIsAsTheOwnerWroteIt()
    {
        var (store, history) = Stores();
        var created = store.Create(Job());
        var engine = Engine(store, history, created.NextRunUtc!.Value);

        await engine.EvaluateDueAsync(CancellationToken.None);
        await engine.RunNowAsync(created.Id, CancellationToken.None);

        Assert.Equal(2, _creates);
        Assert.Equal(OwnersPrompt, store.Get(created.Id)!.Action.Seed);
        // Read again from the database, not from the store that started the runs.
        var reopened = new CronJobStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        Assert.Equal(OwnersPrompt, reopened.Get(created.Id)!.Action.Seed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EvaluateDue_TheCloseItselfSetting_IsHandedOverAndStoredAsTheScheduleHasIt(bool closesItself)
    {
        var (store, history) = Stores();
        var job = Job();
        job.Action.AutoDismiss = closesItself;
        var created = store.Create(job);

        await Engine(store, history, created.NextRunUtc!.Value).EvaluateDueAsync(CancellationToken.None);

        Assert.Equal(closesItself, _handedToTheDirector!.AutoDismiss);
        Assert.Equal(closesItself, store.Get(created.Id)!.Action.AutoDismiss);
    }

    // ---- the rig -------------------------------------------------------------------------------

    private (CronJobStore store, CronRunHistoryStore history) Stores() =>
        (new CronJobStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json")),
         new CronRunHistoryStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json")));

    // The real engine and the real starter; only the Director itself is replaced, by a create that keeps what it was handed.
    private CronEngine Engine(CronJobStore store, CronRunHistoryStore history, DateTime utcNow)
    {
        var clock = new FakeClock(utcNow);
        var spawner = new MachineSessionSpawner(new StubResolver(), (directorId, req, ct) =>
        {
            _creates++;
            _handedToTheDirector = req;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = $"sid-{_creates}" }, null));
        });
        var starter = new DirectorCronSessionStarter(spawner, clock, refuseFactoryWork: _ => null);
        return new CronEngine(store, history, starter, new UnusedWorkListRunner(), new NullCronNotifier(), clock);
    }

    private static CronJobDto Job(string prompt = OwnersPrompt) => new()
    {
        Name = "nightly",
        Enabled = true,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = prompt },
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
