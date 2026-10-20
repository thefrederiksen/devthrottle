using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// BACKUP MACHINES (Mail Desk, phase 5, contract section 2, tests 1 to 5): a definition trigger names its own machine
/// and backups in order, and the claim follows them. Against a real database file, with an injected clock: "the first
/// machine goes quiet" is the clock moving past <see cref="TriggerStore.ClaimLapse"/> with no assignment poll from it.
/// No Director runs and no port is opened - each "Director" is a name asking <see cref="TriggerService.AssignmentsFor"/>
/// and reporting through <see cref="TriggerService.ReportCheckAsync"/>, exactly as the routes call them.
/// The memory is the check's state, kept on the Gateway; "the cursor continues" is the backup being handed the state
/// the first machine's last report left.
/// </summary>
public sealed class BackupMachinesClaimTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-backup-a");
    private const string Factory = "mail-desk";
    private const string MachineA = "machine-a";
    private const string MachineB = "machine-b";
    private const string MachineC = "machine-c";
    private const string DirectorA = "dir-a";
    private const string DirectorB = "dir-b";
    private const string DirectorC = "dir-c";
    private const int Interval = 600;

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly List<(string machine, NewSessionRequest request)> _starts = new();
    private readonly Dictionary<string, SessionDto> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PublishedFactoryDefinition> _published = new(StringComparer.Ordinal);
    private DateTime _now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private int _nextSession;
    private TaskCompletionSource? _holdStart;

    public BackupMachinesClaimTests()
    {
        _db = _harness.Open();
        var d = FactoryDefinitionCheck.Read(DefinitionFixture.Valid()).Definition!;
        var desk = d.Seats.Single(s => s.Seat == "outreach") with { Seat = "desk", Name = "Mail Desk" };
        _published[Factory] = new PublishedFactoryDefinition(4, d with { Factory = Factory, BossSeat = "desk", Seats = new[] { desk } });
    }

    public void Dispose() => _harness.Dispose();

    private TriggerService Service()
        => new(new TriggerStore(_db), new FactoryActivityRecord(_harness.Open()),
            async (machine, request, _) =>
            {
                lock (_starts) _starts.Add((machine, request));
                if (_holdStart is { } hold) await hold.Task;
                var id = $"cccccccc-0000-4000-8000-{++_nextSession:D12}";
                _sessions[id] = new SessionDto { SessionId = id, ActivityState = "Working", Name = request.Name };
                return TriggerStartAttempt.Started(id);
            },
            findSession: (_, sid) => _sessions.TryGetValue(sid, out var s) ? s : null,
            findSessionByName: (_, name) => _sessions.Values.FirstOrDefault(s => s.Name == name),
            timeZone: _ => TimeZoneInfo.Utc,
            nowUtc: () => _now,
            startLifetime: CancellationToken.None,
            directorRunsDefinitionChecks: (_, _) => true,
            machineRunsDefinitionChecks: (_, _) => true,
            definitionOf: (_, factory) => _published.TryGetValue(factory, out var p) ? p : null, mailDeskOn: _ => false);

    /// <summary>A definition trigger as a publish writes it, on <see cref="MachineA"/> with the backups given.</summary>
    private Guid AddTrigger(params string[] backups)
    {
        using var ctx = _db.CreateContext(Tenant);
        var row = new TriggerEntity
        {
            Name = "mail-desk-desk",
            Factory = Factory,
            FactoryAgent = "desk",
            Machine = MachineA,
            BackupMachinesJson = TriggerMachines.ToJson(backups),
            RepoPath = "",
            CheckCommand = "",
            IntervalSeconds = Interval,
            Prompt = "New mail: {count}. {context}",
            CreatedBy = "definition of mail-desk, published by the owner",
            CreatedUtc = _now.AddMinutes(-1),
            Seat = "desk",
            DefinitionVersion = 4,
            CheckPath = "checks/new_mail.py",
        };
        row.TenantId = ctx.ActiveTenant!;
        ctx.Triggers.Add(row);
        ctx.SaveChanges();
        return row.Id;
    }

    private TriggerCheckReport Nothing(string state)
        => new() { CheckedAtUtc = _now, ExitCode = 0, Output = """{"count": 0}""", State = state };

    private static Task<TriggerReportResult> Report(TriggerService service, string director, string machine, Guid trigger,
        TriggerCheckReport report)
        => service.ReportCheckAsync(Tenant, director, trigger.ToString("D"), report, CancellationToken.None, machine);

    private int Runs(Guid trigger)
    {
        using var ctx = _db.CreateContext(Tenant);
        return ctx.TriggerRuns.Count(r => r.TriggerId == trigger);
    }

    private TriggerEntity Row(TriggerService service, Guid trigger) => service.Store.Find(Tenant, trigger.ToString("D"))!;

    /// <summary>Just past the lapse of a claim last renewed now.</summary>
    private void GoQuiet() => _now = _now.Add(TriggerStore.ClaimLapse(Interval)).AddSeconds(1);

    // ---------- 1. handover ----------

    [Fact]
    public async Task ClaimForDirector_FirstMachineGoesQuiet_BackupIsHandedNothingBeforeTheLapse_ThenTakesOverWithTheMemory()
    {
        // Arrange: the first machine's Director claims the check and reports, leaving its cursor.
        var service = Service();
        var t = AddTrigger(MachineB);
        Assert.Single(service.AssignmentsFor(Tenant, DirectorA, MachineA));
        var first = await Report(service, DirectorA, MachineA, t, Nothing("cursor after message 41"));
        Assert.Equal(TriggerReportRefusal.None, first.Refusal);

        // Act and assert: before the lapse, the backup is handed nothing, and holds nothing.
        _now = _now.Add(TriggerStore.ClaimLapse(Interval));
        Assert.Empty(service.AssignmentsFor(Tenant, DirectorB, MachineB));
        Assert.Equal(DirectorA, Row(service, t).ClaimedByDirectorId);

        // After it, the backup is handed the trigger, with the memory the first machine left.
        _now = _now.AddSeconds(1);
        var handed = Assert.Single(service.AssignmentsFor(Tenant, DirectorB, MachineB));
        Assert.Equal("cursor after message 41", handed.State);
        Assert.Equal((DirectorB, MachineB), (Row(service, t).ClaimedByDirectorId, Row(service, t).ClaimedByMachine));

        // Its report is recorded, and the cursor continues from it.
        var second = await Report(service, DirectorB, MachineB, t, Nothing("cursor after message 42"));
        Assert.Equal(TriggerReportRefusal.None, second.Refusal);
        Assert.Equal(DirectorB, second.Run!.DirectorId);
        Assert.Equal(2, Runs(t));
        Assert.Equal("cursor after message 42", Assert.Single(service.AssignmentsFor(Tenant, DirectorB, MachineB)).State);
    }

    // ---------- 2. hand-back ----------

    [Fact]
    public async Task ClaimForDirector_FirstMachineAsksAgainWhileTheBackupHoldsALiveClaim_TakesItBack_AndTheBackupIsRefused()
    {
        // Arrange: the backup holds a live claim, taken over after the first machine went quiet.
        var service = Service();
        var t = AddTrigger(MachineB);
        Assert.Single(service.AssignmentsFor(Tenant, DirectorA, MachineA));
        GoQuiet();
        Assert.Single(service.AssignmentsFor(Tenant, DirectorB, MachineB));
        await Report(service, DirectorB, MachineB, t, Nothing("cursor from the backup"));
        var runsBefore = Runs(t);

        // Act: the first machine's Director asks for its assignments, well inside the backup's claim.
        _now = _now.AddSeconds(30);
        var back = Assert.Single(service.AssignmentsFor(Tenant, DirectorA, MachineA));

        // Assert: it is handed the trigger, with the backup's cursor; the backup's next report is refused and records
        // nothing; and the backup's next assignment poll hands it nothing.
        Assert.Equal("cursor from the backup", back.State);
        Assert.Equal((DirectorA, MachineA), (Row(service, t).ClaimedByDirectorId, Row(service, t).ClaimedByMachine));
        var refused = await Report(service, DirectorB, MachineB, t, Nothing("must not be kept"));
        Assert.Equal(TriggerReportRefusal.HeldByAnotherDirector, refused.Refusal);
        Assert.Equal(runsBefore, Runs(t));
        Assert.Empty(service.AssignmentsFor(Tenant, DirectorB, MachineB));
        Assert.Equal("cursor from the backup", Assert.Single(service.AssignmentsFor(Tenant, DirectorA, MachineA)).State);
    }

    [Fact]
    public async Task RecordStartResult_TheBackupsStartEndsAfterTheFirstMachineTookItsCheckBack_TheClaimStaysWithTheFirst()
    {
        // Arrange: the backup holds the claim, and its check counts work; the session's start is held open.
        var service = Service();
        var t = AddTrigger(MachineB);
        Assert.Single(service.AssignmentsFor(Tenant, DirectorA, MachineA));
        GoQuiet();
        Assert.Single(service.AssignmentsFor(Tenant, DirectorB, MachineB));
        _holdStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counted = await Report(service, DirectorB, MachineB, t,
            new TriggerCheckReport { CheckedAtUtc = _now, ExitCode = 0, Output = """{"count": 1}""" });
        Assert.NotNull(counted.Starting);

        // Act: the first machine takes its check back while that start runs; then the start returns.
        _now = _now.AddSeconds(10);
        Assert.Single(service.AssignmentsFor(Tenant, DirectorA, MachineA));
        _holdStart.SetResult();
        var run = await counted.Starting!;

        // Assert: the start's row is written, and the claim stays with the first machine - the late result does not
        // take it back for the backup.
        Assert.Equal(TriggerRunOutcome.Started, run!.Outcome);
        Assert.Equal((DirectorA, MachineA), (Row(service, t).ClaimedByDirectorId, Row(service, t).ClaimedByMachine));
        Assert.Equal(TriggerReportRefusal.HeldByAnotherDirector,
            (await Report(service, DirectorB, MachineB, t, Nothing("late"))).Refusal);
    }

    // ---------- where a session a backup's check begins starts ----------

    [Fact]
    public async Task ReportCheckAsync_ABackupsCheckCountsWork_TheSessionStartsOnTheBackup_NamingItsDirector()
    {
        var service = Service();
        var t = AddTrigger(MachineB);
        Assert.Single(service.AssignmentsFor(Tenant, DirectorA, MachineA));
        GoQuiet();
        Assert.Single(service.AssignmentsFor(Tenant, DirectorB, MachineB));

        var counted = await Report(service, DirectorB, MachineB, t,
            new TriggerCheckReport { CheckedAtUtc = _now, ExitCode = 0, Output = """{"count": 2}""" });
        await counted.Starting!;

        var (machine, request) = Assert.Single(_starts);
        Assert.Equal((MachineB, DirectorB), (machine, request.Director));
    }

    [Fact]
    public async Task ReportCheckAsync_TheTriggersOwnMachineCountsWork_TheSessionStartsThere_AsBefore()
    {
        var service = Service();
        var t = AddTrigger(MachineB);
        Assert.Single(service.AssignmentsFor(Tenant, DirectorA, MachineA));

        var counted = await Report(service, DirectorA, MachineA, t,
            new TriggerCheckReport { CheckedAtUtc = _now, ExitCode = 0, Output = """{"count": 2}""" });
        await counted.Starting!;

        var (machine, request) = Assert.Single(_starts);
        Assert.Equal((MachineA, DirectorA), (machine, request.Director));
    }

    // ---------- 3. a machine on no list ----------

    [Fact]
    public async Task ClaimForDirector_AMachineOnNoList_IsNeverHandedTheTrigger_HeldOrNot()
    {
        var service = Service();
        var t = AddTrigger(MachineB);

        // Free: never handed.
        Assert.Empty(service.AssignmentsFor(Tenant, DirectorC, MachineC));
        Assert.True(string.IsNullOrEmpty(Row(service, t).ClaimedByDirectorId));

        // Held live: never handed.
        Assert.Single(service.AssignmentsFor(Tenant, DirectorA, MachineA));
        await Report(service, DirectorA, MachineA, t, Nothing("cursor"));
        Assert.Empty(service.AssignmentsFor(Tenant, DirectorC, MachineC));

        // Lapsed: still never handed, and the claim it never had is not touched.
        GoQuiet();
        Assert.Empty(service.AssignmentsFor(Tenant, DirectorC, MachineC));
        Assert.Equal(DirectorA, Row(service, t).ClaimedByDirectorId);
    }

    // ---------- 4. three machines ----------

    [Fact]
    public void ClaimForDirector_ThreeMachines_SecondTakesOver_ThenTheThird_AndTheSecondTakesItBackFromTheThird()
    {
        var service = Service();
        var t = AddTrigger(MachineB, MachineC);
        Assert.Single(service.AssignmentsFor(Tenant, DirectorA, MachineA));

        // The first goes quiet: the second takes over, and the third - later than the second - is handed nothing
        // while the second holds it.
        GoQuiet();
        Assert.Single(service.AssignmentsFor(Tenant, DirectorB, MachineB));
        Assert.Equal(MachineB, Row(service, t).ClaimedByMachine);
        Assert.Empty(service.AssignmentsFor(Tenant, DirectorC, MachineC));

        // The first is still away and the second goes quiet: the third takes over.
        GoQuiet();
        Assert.Single(service.AssignmentsFor(Tenant, DirectorC, MachineC));
        Assert.Equal((DirectorC, MachineC), (Row(service, t).ClaimedByDirectorId, Row(service, t).ClaimedByMachine));

        // The second returns, the first still away: it takes the check back from the third, live claim or not.
        _now = _now.AddSeconds(30);
        Assert.Single(service.AssignmentsFor(Tenant, DirectorB, MachineB));
        Assert.Equal((DirectorB, MachineB), (Row(service, t).ClaimedByDirectorId, Row(service, t).ClaimedByMachine));
        Assert.Empty(service.AssignmentsFor(Tenant, DirectorC, MachineC));
    }

    // ---------- 5. two Directors on the same machine ----------

    [Fact]
    public void ClaimForDirector_TwoDirectorsOnTheSameMachineOfATriggerWithBackups_HandOverOnlyAtTheLapse()
    {
        // The existing tests prove a trigger with no backups is unchanged; this proves a backup list does not change it
        // between two Directors on one machine either - they stand at the same place, so neither takes it from the other.
        var service = Service();
        var t = AddTrigger(MachineB);
        Assert.Single(service.AssignmentsFor(Tenant, DirectorA, MachineA));

        _now = _now.Add(TriggerStore.ClaimLapse(Interval));
        Assert.Empty(service.AssignmentsFor(Tenant, "dir-a-second", MachineA));

        _now = _now.AddSeconds(1);
        Assert.Single(service.AssignmentsFor(Tenant, "dir-a-second", MachineA));
        Assert.Equal("dir-a-second", Row(service, t).ClaimedByDirectorId);
        Assert.Empty(service.AssignmentsFor(Tenant, DirectorA, MachineA));
    }

    [Fact]
    public void ClaimForDirector_MachineNamesDifferingOnlyInCase_AreTheSameMachine()
    {
        var service = Service();
        AddTrigger(MachineB);
        Assert.Single(service.AssignmentsFor(Tenant, DirectorA, MachineA));
        GoQuiet();

        Assert.Single(service.AssignmentsFor(Tenant, DirectorB, "MACHINE-B"));
        Assert.Single(service.AssignmentsFor(Tenant, DirectorA, "Machine-A"));
    }

    // ---------- 9. the words ----------

    [Fact]
    public void ToDto_ATriggerWithAndWithoutBackups_NamesEveryMachineInOrder()
    {
        var service = Service();
        var none = service.ToDto(Row(service, AddTrigger()));
        Assert.Equal(MachineA, none.MachinesText);
        Assert.Empty(none.BackupMachines);

        using (var ctx = _db.CreateContext(Tenant)) { ctx.Triggers.RemoveRange(ctx.Triggers); ctx.SaveChanges(); }
        var one = service.ToDto(Row(service, AddTrigger(MachineB)));
        Assert.Equal("machine-a, then machine-b if machine-a is away", one.MachinesText);
        Assert.Equal(new[] { MachineB }, one.BackupMachines);

        using (var ctx = _db.CreateContext(Tenant)) { ctx.Triggers.RemoveRange(ctx.Triggers); ctx.SaveChanges(); }
        var two = service.ToDto(Row(service, AddTrigger(MachineB, MachineC)));
        Assert.Equal("machine-a, then machine-b if machine-a is away, then machine-c if machine-a and machine-b are away",
            two.MachinesText);
    }
}
