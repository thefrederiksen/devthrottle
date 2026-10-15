using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// THE GATEWAY DECIDES ON A DEFINITION CHECK (Factory Definition, phase 5b, design 3.1, 3.2 and 1.4), against a real
/// database file. A definition trigger is written straight into the table, as a publish writes it. The session starter
/// is a fake that records every call, so "no session was started" is a count of zero calls, and the state store is
/// read back row by row, so "nothing was stored" is a count of rows too.
///
/// The goal-14 half: a command trigger's assignment and its prompt are exactly as they always were.
/// </summary>
public sealed class DefinitionTriggerServiceTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-defcheck-a");
    private const string Director = "dir-north-1";
    private const string Factory = "warmforward";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly List<(string machine, NewSessionRequest request)> _starts = new();
    private readonly Dictionary<string, SessionDto> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
    private int _nextSession;
    // What the fake starter answers next; null starts a session.
    private TriggerStartAttempt? _attempt;
    // Which Directors said on Hello that they run checks from a definition, and the machines that have one.
    private readonly HashSet<string> _definitionDirectors = new(StringComparer.Ordinal) { Director };
    private readonly HashSet<string> _definitionMachines = new(StringComparer.OrdinalIgnoreCase) { "SOREN_NORTH" };
    public DefinitionTriggerServiceTests()
    {
        _db = _harness.Open();
    }

    public void Dispose() => _harness.Dispose();

    private TriggerService Service()
        => new(new TriggerStore(_db), new FactoryActivityRecord(_harness.Open()),
            (machine, request, _) =>
            {
                lock (_starts) _starts.Add((machine, request));
                if (_attempt is not null) return Task.FromResult(_attempt);
                var id = $"bbbbbbbb-0000-4000-8000-{++_nextSession:D12}";
                _sessions[id] = new SessionDto { SessionId = id, ActivityState = "Working", Name = request.Name };
                return Task.FromResult(TriggerStartAttempt.Started(id));
            },
            findSession: (_, sid) => _sessions.TryGetValue(sid, out var s) ? s : null,
            findSessionByName: (_, name) => _sessions.Values.FirstOrDefault(s => s.Name == name),
            timeZone: _ => TimeZoneInfo.Utc,
            nowUtc: () => _now,
            startLifetime: CancellationToken.None,
            directorRunsDefinitionChecks: (_, director) => _definitionDirectors.Contains(director),
            machineRunsDefinitionChecks: (_, machine) => _definitionMachines.Contains(machine));

    /// <summary>A definition trigger as a publish writes it: a check path and no command.</summary>
    private Guid AddDefinitionTrigger(bool paused = false, string prompt = "New mail: {count}. {context}")
    {
        using var ctx = _db.CreateContext(Tenant);
        var row = new TriggerEntity
        {
            Name = "warmforward-frontdesk",
            Factory = Factory,
            FactoryAgent = "frontdesk",
            Machine = "SOREN_NORTH",
            RepoPath = @"D:\ReposFred\warmforward",
            CheckCommand = "",
            IntervalSeconds = 600,
            Prompt = prompt,
            Paused = paused,
            CreatedBy = "definition of warmforward, published by soren",
            CreatedUtc = _now.AddMinutes(-1),
            Seat = "frontdesk",
            DefinitionVersion = 4,
            CheckPath = "checks/new_mail.py",
            SecretsJson = """["mail-tenant","mail-client"]""",
        };
        row.TenantId = ctx.ActiveTenant!;
        ctx.Triggers.Add(row);
        ctx.SaveChanges();
        return row.Id;
    }

    private Guid AddCommandTrigger(TriggerService service)
    {
        var (created, error) = service.Store.Create(Tenant, TriggerDefinitionTests.Valid(), "session test", _now.AddMinutes(-1));
        Assert.Null(error);
        return created!.Id;
    }

    private TriggerCheckReport Report(string output, string? state = null, int exitCode = 0, bool oversize = false)
        => new() { CheckedAtUtc = _now, ExitCode = exitCode, Output = output, State = state, StateOversize = oversize };

    private async Task<TriggerRunDto> Send(TriggerService service, Guid trigger, TriggerCheckReport report)
    {
        var result = await service.ReportCheckAsync(Tenant, Director, trigger.ToString("D"), report, CancellationToken.None);
        Assert.Equal(TriggerReportRefusal.None, result.Refusal);
        var run = result.Starting is { } starting ? await starting : result.Run;
        return TriggerService.ToDto(run!);
    }

    private List<FactoryCheckStateEntity> StateRows(Guid trigger)
    {
        using var ctx = _db.CreateContext(Tenant);
        var owner = FactoryCheckStateStore.TriggerOwner(trigger);
        return ctx.FactoryCheckStates.Where(s => s.OwnerKey == owner).OrderBy(s => s.Sequence).ToList();
    }

    // ---------- the prompt ----------

    [Fact]
    public async Task ADefinitionCheckThatCountsWork_StartsOneSession_WithCountAndContextInThePrompt()
    {
        var service = Service();
        var t = AddDefinitionTrigger();

        var run = await Send(service, t, Report("""{"count": 2, "context": "from ada@example.com, bo@example.com"}"""));

        Assert.Equal(TriggerRunOutcome.Started, run.Outcome);
        var (machine, request) = Assert.Single(_starts);
        Assert.Equal("SOREN_NORTH", machine);
        Assert.Equal("New mail: 2. from ada@example.com, bo@example.com", request.PrePrompt);
        Assert.Equal(Director, request.Director);
        Assert.Equal(Factory, request.Factory);
        Assert.Equal(@"D:\ReposFred\warmforward", request.RepoPath);
    }

    [Fact]
    public async Task ADefinitionCheckWithNoContext_ReplacesTheContextWithNothing()
    {
        var service = Service();
        var t = AddDefinitionTrigger();

        await Send(service, t, Report("""{"count": 1}"""));

        Assert.Equal("New mail: 1. ", Assert.Single(_starts).request.PrePrompt);
    }

    [Fact]
    public async Task ACommandTrigger_KeepsContextAsWritten_InItsPrompt()
    {
        // Arrange: a command trigger whose prompt names {context}, and a check that prints one.
        var service = Service();
        var req = TriggerDefinitionTests.Valid();
        req.Prompt = "Mail: {count}. {context}";
        var (created, _) = service.Store.Create(Tenant, req, "session test", _now.AddMinutes(-1));

        // Act
        await Send(service, created!.Id, Report("""{"count": 3, "context": "ignored"}"""));

        // Assert: exactly today's reading - the count replaced, the context left as written.
        Assert.Equal("Mail: 3. {context}", Assert.Single(_starts).request.PrePrompt);
    }

    // ---------- the contract ----------

    [Fact]
    public async Task ADefinitionCheckWithAContextThatIsNotAString_IsFailed_AndStartsNothing()
    {
        var service = Service();
        var t = AddDefinitionTrigger();

        var run = await Send(service, t, Report("""{"count": 2, "context": 7}"""));

        Assert.Equal(TriggerRunOutcome.Failed, run.Outcome);
        Assert.Equal("the context is not a string: 7", run.Reason);
        Assert.Empty(_starts);
    }

    [Fact]
    public async Task ADefinitionCheckWhoseStateOutgrew64KB_IsFailed_AndItsStateIsNotStored()
    {
        var service = Service();
        var t = AddDefinitionTrigger();

        var run = await Send(service, t, Report("""{"count": 0}""", state: null, oversize: true));

        Assert.Equal(TriggerRunOutcome.Failed, run.Outcome);
        Assert.Equal(TriggerCheckContract.StateOversizeReason, run.Reason);
        Assert.Empty(StateRows(t));
    }

    // ---------- the state (design 1.4) ----------

    [Fact]
    public async Task TheState_IsStoredFromASuccess_OnlyWhenItChanged_WithTheRunThatWroteIt()
    {
        var service = Service();
        var t = AddDefinitionTrigger();

        var first = await Send(service, t, Report("""{"count": 0}""", state: """{"last":"m-1"}"""));
        _now = _now.AddMinutes(10);
        await Send(service, t, Report("""{"count": 0}""", state: """{"last":"m-1"}"""));
        _now = _now.AddMinutes(10);
        await Send(service, t, Report("""{"count": 0}""", state: """{"last":"m-2"}"""));

        var rows = StateRows(t);
        Assert.Equal(new[] { """{"last":"m-1"}""", """{"last":"m-2"}""" }, rows.Select(r => r.Content));
        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Sequence));
        Assert.Equal(first.Id, rows[0].RunRef);
        Assert.All(rows, r => Assert.Equal(Director, r.WrittenBy));
        Assert.All(rows, r => Assert.False(r.Reset));
    }

    [Fact]
    public async Task AFailedCheck_DiscardsItsState()
    {
        var service = Service();
        var t = AddDefinitionTrigger();
        await Send(service, t, Report("""{"count": 0}""", state: "kept"));

        _now = _now.AddMinutes(10);
        var failed = await Send(service, t, Report("half", state: "half-written", exitCode: 1));

        Assert.Equal(TriggerRunOutcome.Failed, failed.Outcome);
        Assert.Equal(new[] { "kept" }, StateRows(t).Select(r => r.Content));
    }

    [Fact]
    public async Task AStateFileThatIsGone_StoresNothing()
    {
        var service = Service();
        var t = AddDefinitionTrigger();

        await Send(service, t, Report("""{"count": 0}""", state: null));

        Assert.Empty(StateRows(t));
    }

    [Fact]
    public async Task ACheckThatStartsASession_KeepsItsState_WithTheStartedRun()
    {
        var service = Service();
        var t = AddDefinitionTrigger();

        var run = await Send(service, t, Report("""{"count": 1}""", state: "seen m-9"));

        Assert.Equal(TriggerRunOutcome.Started, run.Outcome);
        var row = Assert.Single(StateRows(t));
        Assert.Equal("seen m-9", row.Content);
        Assert.Equal(run.Id, row.RunRef);
    }

    [Fact]
    public async Task AStartThatFailed_KeepsNoState_SoTheNextCheckSeesTheWorkAgain()
    {
        var service = Service();
        var t = AddDefinitionTrigger();
        _attempt = TriggerStartAttempt.Failed("the machine refused the session");

        var run = await Send(service, t, Report("""{"count": 1}""", state: "seen m-9"));

        Assert.Equal(TriggerRunOutcome.Failed, run.Outcome);
        Assert.Empty(StateRows(t));
    }

    [Fact]
    public async Task AStartWhoseOutcomeIsUnknown_KeepsNoState()
    {
        var service = Service();
        var t = AddDefinitionTrigger();
        _attempt = TriggerStartAttempt.Unknown("the Director did not answer in time.");

        var run = await Send(service, t, Report("""{"count": 1}""", state: "seen m-9"));

        Assert.Equal(TriggerRunOutcome.Failed, run.Outcome);
        Assert.Empty(StateRows(t));
    }

    [Fact]
    public async Task AStateWriteThatFails_AfterASessionStarted_LeavesNoLockHeld()
    {
        // Arrange: the state table is gone, so the store throws on the write.
        var service = Service();
        var t = AddDefinitionTrigger();
        DropTheStateTable();

        // Act
        var result = await service.ReportCheckAsync(Tenant, Director, t.ToString("D"),
            Report("""{"count": 1}""", state: "seen m-9"), CancellationToken.None);
        var run = await result.Starting!;

        // Assert: the start's failure to keep the state is reported as no row handed back, and nothing holds the trigger.
        Assert.Null(run);
        Assert.Single(_starts);
        Assert.True(service.HoldsNoLock(Tenant, t));
        var next = await service.ReportCheckAsync(Tenant, Director, t.ToString("D"), Report("""{"count": 0}"""), CancellationToken.None);
        Assert.Equal(TriggerRunOutcome.NothingToDo, next.Run!.Outcome);
    }

    [Fact]
    public async Task AStateWriteThatFails_OnNothingToDo_ThrowsToTheRoute_AndLeavesNoLockHeld()
    {
        var service = Service();
        var t = AddDefinitionTrigger();
        DropTheStateTable();

        await Assert.ThrowsAnyAsync<Exception>(() => service.ReportCheckAsync(Tenant, Director, t.ToString("D"),
            Report("""{"count": 0}""", state: "seen m-9"), CancellationToken.None));

        Assert.True(service.HoldsNoLock(Tenant, t));
    }

    private void DropTheStateTable()
    {
        using var ctx = _db.CreateContext(Tenant);
        ctx.Database.ExecuteSqlRaw("DROP TABLE factory_check_state");
    }

    // ---------- pause, skipped-running and the lock behave as today ----------

    [Fact]
    public async Task APausedDefinitionTrigger_WritesPaused_StartsNothing_AndKeepsNoState()
    {
        var service = Service();
        var t = AddDefinitionTrigger(paused: true);

        var run = await Send(service, t, Report("""{"count": 3}""", state: "s1"));

        Assert.Equal(TriggerRunOutcome.Paused, run.Outcome);
        Assert.Empty(_starts);
        Assert.Empty(StateRows(t));
    }

    [Fact]
    public async Task ASecondDefinitionCheckWhileItsSessionLives_IsSkippedRunning_OneAtATime()
    {
        var service = Service();
        var t = AddDefinitionTrigger();
        var first = await Send(service, t, Report("""{"count": 2}""", state: "s1"));

        _now = _now.AddMinutes(10);
        var second = await Send(service, t, Report("""{"count": 5}""", state: "s2"));

        Assert.Equal(TriggerRunOutcome.SkippedRunning, second.Outcome);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Single(_starts);
        // The skipped check's state is not kept: no session took the work it counted.
        Assert.Equal(new[] { "s1" }, StateRows(t).Select(r => r.Content));
    }

    // ---------- what the Director is handed (design 2.1) ----------

    [Fact]
    public async Task TheAssignmentOfADefinitionTrigger_CarriesItsCheck_ItsSecrets_AndTheNewestState_AndNoFolder()
    {
        var service = Service();
        var t = AddDefinitionTrigger();
        Assert.Equal("", Assert.Single(service.AssignmentsFor(Tenant, Director, "SOREN_NORTH")).State);
        await Send(service, t, Report("""{"count": 0}""", state: "after the first run"));

        var a = Assert.Single(service.AssignmentsFor(Tenant, Director, "SOREN_NORTH"));

        Assert.Equal(TriggerKinds.Definition, a.Kind);
        Assert.Equal("frontdesk", a.Seat);
        Assert.Equal(Factory, a.Factory);
        Assert.Equal(4, a.DefinitionVersion);
        Assert.Equal("checks/new_mail.py", a.CheckPath);
        Assert.Equal(new[] { "mail-tenant", "mail-client" }, a.Secrets);
        Assert.Equal("after the first run", a.State);
        Assert.Null(a.DueRunId);
        Assert.Equal("", a.CheckCommand);
        Assert.Equal("", a.RepoPath);
        Assert.Equal(600, a.IntervalSeconds);
        Assert.Equal(TriggerDefinition.TimeoutSecondsFor(600), a.TimeoutSeconds);
    }

    // ---------- only a Director that says it runs definition checks gets one (design 2.6) ----------

    [Fact]
    public void AnOlderDirector_IsHandedNoDefinitionTrigger_AndClaimsNone_ButACapableOneGetsIt()
    {
        var service = Service();
        var t = AddDefinitionTrigger();

        Assert.Empty(service.AssignmentsFor(Tenant, "dir-older", "SOREN_NORTH"));
        Assert.True(string.IsNullOrEmpty(service.Store.Find(Tenant, t.ToString("D"))!.ClaimedByDirectorId));

        var a = Assert.Single(service.AssignmentsFor(Tenant, Director, "SOREN_NORTH"));
        Assert.Equal(TriggerKinds.Definition, a.Kind);
        Assert.Equal(Director, service.Store.Find(Tenant, t.ToString("D"))!.ClaimedByDirectorId);
    }

    [Fact]
    public void ACommandTrigger_ReachesAnOlderDirector_AsItAlwaysDid()
    {
        var service = Service();
        var command = AddCommandTrigger(service);
        AddDefinitionTrigger();

        var a = Assert.Single(service.AssignmentsFor(Tenant, "dir-older", "SOREN_NORTH"));

        Assert.Equal(command.ToString("D"), a.Id);
        Assert.Null(a.Kind);
    }

    [Fact]
    public async Task AnOlderDirectorsReportOfADefinitionCheck_IsRefused_AndWritesNoRunRow()
    {
        var service = Service();
        var t = AddDefinitionTrigger();

        var result = await service.ReportCheckAsync(Tenant, "dir-older", t.ToString("D"),
            Report("""{"count": 1}""", state: "s1"), CancellationToken.None);

        Assert.Equal(TriggerReportRefusal.CannotRunDefinitionChecks, result.Refusal);
        Assert.Empty(service.Store.ListRuns(Tenant, t, 10));
        Assert.Empty(_starts);
        Assert.Empty(StateRows(t));
    }

    [Fact]
    public void ADefinitionTrigger_OnAMachineWithNoCapableDirector_IsRedAndSaysSo_ACommandTriggerIsNot()
    {
        var service = Service();
        var t = AddDefinitionTrigger();
        var command = AddCommandTrigger(service);
        _definitionMachines.Clear();

        var definition = service.ToDto(service.Store.Find(Tenant, t.ToString("D"))!);
        var commandDto = service.ToDto(service.Store.Find(Tenant, command.ToString("D"))!);

        Assert.Equal((TriggerStatusKind.Red, "no Director on SOREN_NORTH can run checks from a definition yet"),
            (definition.Status, definition.StatusText));
        Assert.Equal((TriggerStatusKind.Ok, "waiting for the first check"), (commandDto.Status, commandDto.StatusText));
    }

    [Fact]
    public void TheAssignmentOfACommandTrigger_IsExactlyAsBefore_WithNoDefinitionField()
    {
        var service = Service();
        AddCommandTrigger(service);

        var a = Assert.Single(service.AssignmentsFor(Tenant, Director, "SOREN_NORTH"));

        Assert.Null(a.Kind);
        Assert.Null(a.Seat);
        Assert.Null(a.Factory);
        Assert.Null(a.DefinitionVersion);
        Assert.Null(a.CheckPath);
        Assert.Null(a.Secrets);
        Assert.Null(a.DueRunId);
        Assert.Null(a.State);
        Assert.Equal(TriggerDefinitionTests.Valid().CheckCommand, a.CheckCommand);
        Assert.Equal(TriggerDefinitionTests.Valid().RepoPath, a.RepoPath);

        // On the wire an older Director sees exactly the fields it always did: every new one is left out when null.
        var json = System.Text.Json.JsonSerializer.Serialize(a, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(new[] { "id", "name", "checkCommand", "repoPath", "intervalSeconds", "timeoutSeconds" },
            System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task ACommandTrigger_NeverStoresState_EvenWhenAReportCarriesOne()
    {
        var service = Service();
        var t = AddCommandTrigger(service);

        await Send(service, t, Report("""{"count": 0}""", state: "a command has no state"));

        using var ctx = _db.CreateContext(Tenant);
        Assert.Empty(ctx.FactoryCheckStates.ToList());
    }
}
