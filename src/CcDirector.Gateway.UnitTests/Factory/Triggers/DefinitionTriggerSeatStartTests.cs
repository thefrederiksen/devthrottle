using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// A DEFINITION TRIGGER'S SESSION IS A SEAT START (Factory Definition, phase 5b, brief D item 8), end to end over a real
/// database file: the definition is saved and published into an account that never registered the factory, so the
/// publish registers it with no folder and writes the interval start's trigger with none. When that trigger's check
/// counts work, its session is built by FactorySeatStart from the published version - the seat's repository, model and
/// kit - read through the same FactorySeatStart.ReadPublished the Gateway wires. A command trigger in the same account
/// still builds its create from its own row.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class DefinitionTriggerSeatStartTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-seat-start");
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
    private const string Factory = DefinitionFixture.FactoryId;
    private const string Director = "dir-south-1";

    private readonly GatewayDbTestHarness _h = new();
    private readonly List<(string Machine, NewSessionRequest Request)> _starts = new();
    private readonly FactoryDefinitionStore _definitions;
    private readonly TriggerService _service;
    private int _nextSession;

    public DefinitionTriggerSeatStartTests()
    {
        var db = _h.Open();
        _definitions = new FactoryDefinitionStore(db);
        _service = new TriggerService(new TriggerStore(db), new FactoryActivityRecord(db),
            (machine, request, _) =>
            {
                lock (_starts) _starts.Add((machine, request));
                return Task.FromResult(TriggerStartAttempt.Started($"cccccccc-0000-4000-8000-{++_nextSession:D12}"));
            },
            findSession: (_, _) => null, findSessionByName: (_, _) => null, timeZone: _ => TimeZoneInfo.Utc,
            nowUtc: () => Now, startLifetime: CancellationToken.None,
            directorRunsDefinitionChecks: (_, _) => true, machineRunsDefinitionChecks: (_, _) => true,
            definitionOf: (tenant, factory) => FactorySeatStart.ReadPublished(_definitions, tenant, factory));

        // Saved and published as the owner, into an account where the factory was never registered.
        var content = DefinitionFixture.Valid();
        var draft = _definitions.SaveDraft(Tenant, Factory, content, 0, "person", "the owner", null, Now.AddMinutes(-5));
        Assert.True(draft.Ok, draft.Refusal);
        var check = FactoryDefinitionCheck.Check(content, Factory, DefinitionFixture.FleetSkills, DefinitionFixture.Machines,
            DefinitionCheckPurpose.Publish, DefinitionFixture.NotConnected(content));
        var published = new FactoryPublishApply(db).Publish(Tenant, Factory, draft.Version!.Version, check.Definition!, content,
            replaces: null, "the owner", Now.AddMinutes(-4));
        Assert.True(published.Write.Ok, published.Write.Refusal);
    }

    public void Dispose() => _h.Dispose();

    private async Task<TriggerRunDto> Report(string trigger, string output)
    {
        var result = await _service.ReportCheckAsync(Tenant, Director, trigger,
            new TriggerCheckReport { CheckedAtUtc = Now, ExitCode = 0, Output = output }, CancellationToken.None);
        Assert.Equal(TriggerReportRefusal.None, result.Refusal);
        return TriggerService.ToDto((result.Starting is { } starting ? await starting : result.Run)!);
    }

    [Fact]
    public async Task ReportCheckAsync_AFactoryRegisteredByAPublish_WhoseIntervalCheckFindsWork_StartsTheSeatFromThePublishedVersion()
    {
        var trigger = Assert.Single(_service.Store.List(Tenant), t => t.IsDefinitionCheck);
        Assert.Equal("", trigger.RepoPath);

        var run = await Report(trigger.Id.ToString("D"), """{"count": 2, "context": "from ada@example.com"}""");

        Assert.Equal(TriggerRunOutcome.Started, run.Outcome);
        var (machine, request) = Assert.Single(_starts);
        // The fixture's outreach seat: on SOREN_SOUTH, in the repository devthrottle, on claude-sonnet-5-5.
        Assert.Equal("SOREN_SOUTH", machine);
        Assert.Equal(("", "devthrottle", (bool?)null), (request.RepoPath, request.FactoryWorktree, request.FactoryHome));
        Assert.Equal(("outreach", 1, FactoryKits.Seat), (request.FactorySeat, request.FactoryDefinitionVersion, request.FactoryKit));
        Assert.Equal(("ClaudeCode", "claude-sonnet-5-5", Factory), (request.Agent, request.Model, request.Factory));
        Assert.Equal((Director, true), (request.Director, request.AutoDismiss));
        Assert.Equal((SessionOriginKinds.Schedule, SessionOriginSurfaces.Trigger), (request.Origin, request.OriginSurface));
        Assert.Equal("New mail: 2. from ada@example.com", request.PrePrompt);
        // What the check found reaches the agent as check.json: the outreach seat's first start, from version 1.
        var check = request.FactoryCheck!;
        Assert.Equal(("checks/new_mail.py", 2, "from ada@example.com"), (check.CheckPath, check.Count, check.Context));
        Assert.Equal((Now, 1, "outreach", 1), (check.CheckedUtc, check.DefinitionVersion, check.Seat, check.Start));
    }

    [Fact]
    public async Task ReportCheckAsync_ACommandTriggerBesideIt_StillBuildsItsCreateFromItsOwnRow()
    {
        // Goal 14: a trigger a person wrote with a command line keeps its folder, its agent and its prompt.
        var (command, error) = _service.Store.Create(Tenant, TriggerDefinitionTests.Valid(), "a person", Now.AddMinutes(-1));
        Assert.Null(error);

        await Report(command!.Id.ToString("D"), """{"count": 1}""");

        var request = Assert.Single(_starts).Request;
        Assert.Equal(TriggerDefinitionTests.Valid().RepoPath, request.RepoPath);
        Assert.Equal("ClaudeCode", request.Agent);
        Assert.Null(request.FactorySeat);
        Assert.Null(request.FactoryKit);
        Assert.Null(request.FactoryDefinitionVersion);
        Assert.Null(request.FactoryCheck);
    }
}
