using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE SEAT THROUGH EVERY DOOR THAT DISPATCHES A CREATE (Factory Definition mission, phase 5). The Director door - the
/// one a person's New Session, the command line's spawn and the talk all take - is driven whole, in memory, with a
/// fake stream: what it sends, and what it refuses before anything is sent. The machine door's refusal and the
/// Interrupted continuation's create are each one function, read directly. The cron starter and the Fleet Manager's
/// placement share the machine door's spawner, whose check is proved in <see cref="FactoryKitCapabilityTests"/>.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactorySeatThroughTheDoorsTests : IDisposable
{
    private const string DirectorId = "dir-seat";
    private const string Machine = "SEAT-PC";
    private const string TheFactory = "website-factory";
    private static readonly Guid Caller = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-seat-doors-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly FactoryKitCapabilityRegistry _kit = new();
    private readonly List<NewSessionRequest> _sent = new();
    private SessionFactoryLookup _callerRow = SessionFactoryLookup.In(TheFactory, "outreach", 4);
    private SeatRulesRead _rules = SeatRulesRead.Carries(true);

    public FactorySeatThroughTheDoorsTests()
    {
        Directory.CreateDirectory(_dir);
        _registry = new DirectorRegistry(Path.Combine(_dir, "instances"));
        _registry.RegisterFromStream(DirectorId, Machine, "test", "2.22.0", pid: 1, startedAt: new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc), tenant: TenantId.Local);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private DirectorSpawnDoor Door() => new(
        new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false),
        _registry,
        id => id == Caller.ToString() ? _callerRow : SessionFactoryLookup.NotKnown,
        (directorId, command, ct) =>
        {
            if (command.Verb != "create") return Task.FromResult<DirectorCommandResult?>(null);
            _sent.Add(JsonSerializer.Deserialize<NewSessionRequest>(command.PayloadJson ?? "{}", Web)!);
            return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(
                JsonSerializer.Serialize(new SessionDto { SessionId = Guid.NewGuid().ToString() }, Web)));
        },
        Missions: null, WorkflowRuns: null, Workspaces: null,
        KitCapabilities: _kit,
        SeatRulesFor: _ => (_, _, _) => _rules);

    private static HttpContext AsSession()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Caller, TenantId.Local, DirectorId);
        return ctx;
    }

    private static HttpContext AsPerson()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        return ctx;
    }

    private static NewSessionRequest ChildSpawn(string agent = "ClaudeCode") => new()
    {
        RepoPath = Path.GetTempPath(),
        Agent = agent,
        ControllerSessionId = "none",
    };

    private static string? ErrorOf(IResult? result)
    {
        var value = (result as IValueHttpResult)?.Value;
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
    }

    // ---------- the Director door ----------

    [Fact]
    public async Task DirectorDoor_ChildOfASeat_ToADirectorThatBuildsTheKit_LeavesAsTheSeatWithARulesOnlyKit()
    {
        _kit.Record(TenantId.Local, DirectorId, buildsFactoryKit: true);

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(AsSession(), DirectorId, ChildSpawn(), Door());

        Assert.NotNull(outcome.Session);
        var sent = Assert.Single(_sent);
        Assert.Equal(TheFactory, sent.Factory);
        Assert.Equal("outreach", sent.FactorySeat);
        Assert.Equal(4, sent.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.RulesOnly, sent.FactoryKit);
    }

    [Fact]
    public async Task DirectorDoor_ChildOfASeat_ToAnOlderDirector_IsRefused409AndNothingLeaves()
    {
        _kit.Record(TenantId.Local, DirectorId, buildsFactoryKit: false);

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(AsSession(), DirectorId, ChildSpawn(), Door());

        Assert.Equal(StatusCodes.Status409Conflict, (outcome.Refusal as IStatusCodeHttpResult)?.StatusCode);
        Assert.Equal(
            $"The Director on {Machine} is version 2.22.0, which cannot start a seat of a defined factory (it cannot " +
            "build the seat's kit), so no session was started. Update this Director and try again.",
            ErrorOf(outcome.Refusal));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task DirectorDoor_UNDEFINED_FACTORY_ChildOfASessionWithNoSeat_GoesToAnOlderDirectorUnchanged()
    {
        _callerRow = SessionFactoryLookup.In(TheFactory);

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(AsSession(), DirectorId, ChildSpawn(), Door());

        Assert.NotNull(outcome.Session);
        var sent = Assert.Single(_sent);
        Assert.Equal(TheFactory, sent.Factory);
        Assert.False(sent.CarriesSeatStartFields());
    }

    [Fact]
    public async Task DirectorDoor_CodexChildOfASeatWithRules_IsRefusedWithTheTablesReasonAndNothingLeaves()
    {
        _kit.Record(TenantId.Local, DirectorId, buildsFactoryKit: true);

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(AsSession(), DirectorId, ChildSpawn("Codex"), Door());

        Assert.Equal(FactoryRulesSupport.For(AgentKind.Codex).Reason, ErrorOf(outcome.Refusal));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task DirectorDoor_CodexChildOfASeatWithNoRules_Leaves()
    {
        _kit.Record(TenantId.Local, DirectorId, buildsFactoryKit: true);
        _rules = SeatRulesRead.Carries(false);

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(AsSession(), DirectorId, ChildSpawn("Codex"), Door());

        Assert.NotNull(outcome.Session);
        Assert.Equal("outreach", Assert.Single(_sent).FactorySeat);
    }

    [Fact]
    public async Task DirectorDoor_APersonNamingASeat_IsRefusedAndNothingLeaves()
    {
        _kit.Record(TenantId.Local, DirectorId, buildsFactoryKit: true);
        var req = ChildSpawn();
        req.FactorySeat = "outreach";

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(AsPerson(), DirectorId, req, Door());

        Assert.Equal("A seat is started from its factory's page or its schedule, not named on a spawn.", ErrorOf(outcome.Refusal));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task DirectorDoor_APersonSendingAFolderlessSeatStartByHand_IsRefusedAsNamingASeat_AndNothingLeaves()
    {
        // Phase 5: a seat start carries no folder, so the door's "a folder is required" no longer answers it - the
        // person's own refusal does.
        _kit.Record(TenantId.Local, DirectorId, buildsFactoryKit: true);
        var req = ChildSpawn();
        req.RepoPath = "";
        req.Factory = TheFactory;
        req.FactorySeat = "outreach";
        req.FactoryDefinitionVersion = 4;
        req.FactoryKit = FactoryKits.Seat;
        req.FactoryHome = true;

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(AsPerson(), DirectorId, req, Door());

        Assert.Equal("A seat is started from its factory's page or its schedule, not named on a spawn.", ErrorOf(outcome.Refusal));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task DirectorDoor_TheGatewaysOwnFolderlessSeatStartForAPerson_Leaves()
    {
        _kit.Record(TenantId.Local, DirectorId, buildsFactoryKit: true);
        var ctx = AsPerson();
        SpawnFactory.MarkGatewaySeatStart(ctx);
        var req = ChildSpawn();
        req.RepoPath = "";
        req.Factory = TheFactory;
        req.FactorySeat = "outreach";
        req.FactoryDefinitionVersion = 4;
        req.FactoryKit = FactoryKits.Seat;
        req.FactoryWorktree = "devthrottle";

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(ctx, DirectorId, req, Door());

        Assert.NotNull(outcome.Session);
        var sent = Assert.Single(_sent);
        Assert.Equal("", sent.RepoPath);
        Assert.Equal("devthrottle", sent.FactoryWorktree);
    }

    [Fact]
    public async Task DirectorDoor_UNDEFINED_FACTORY_ACreateWithNoFolderAndNoSeat_IsStill400()
    {
        _kit.Record(TenantId.Local, DirectorId, buildsFactoryKit: true);
        var req = ChildSpawn();
        req.RepoPath = "";
        // A home or a repository name without a seat kit is no seat start: the folder is still required.
        req.FactoryHome = true;

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(AsPerson(), DirectorId, req, Door());

        Assert.Equal("repoPath is required", ErrorOf(outcome.Refusal));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task DirectorDoor_TheGatewaysOwnSeatStartForAPerson_ToAnOlderDirector_IsRefused()
    {
        // The talk takes this door: its seat start is let through the person's credential by the Gateway's own mark,
        // and still refused on a Director that cannot build the kit.
        var ctx = AsPerson();
        SpawnFactory.MarkGatewaySeatStart(ctx);
        var req = ChildSpawn();
        req.Factory = TheFactory;
        req.FactorySeat = "outreach";
        req.FactoryDefinitionVersion = 4;
        req.FactoryKit = FactoryKits.Seat;

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(ctx, DirectorId, req, Door());

        Assert.Contains("cannot start a seat of a defined factory", ErrorOf(outcome.Refusal));
        Assert.Empty(_sent);
    }

    // ---------- the machine door ----------

    private static readonly DirectorDto Older = new() { DirectorId = DirectorId, MachineName = Machine, Version = "2.22.0" };

    [Fact]
    public void RefuseAtMachineDoor_ASeatStartToAnOlderDirector_IsRefusedWithTheSpawnersAnswer()
    {
        var req = ChildSpawn();
        req.FactorySeat = "outreach";

        var refusal = SpawnFactory.RefuseAtMachineDoor(req, DirectorId, _ => Older,
            (r, id) => SpawnFactory.DirectorCannotBuildKit(r, Older, buildsFactoryKit: false));

        Assert.StartsWith($"The Director on {Machine} is version 2.22.0, which cannot start a seat", refusal);
    }

    [Fact]
    public void RefuseAtMachineDoor_ASeatStartWithNoSpawnerCheck_IsRefusedNotSent()
    {
        var req = ChildSpawn();
        req.Model = "claude-opus-5-5";

        Assert.Equal(CcDirector.Gateway.Running.MachineSessionSpawner.CannotTellKit, SpawnFactory.RefuseAtMachineDoor(req, DirectorId, _ => Older, null));
    }

    [Fact]
    public void RefuseAtMachineDoor_UNDEFINED_FACTORY_ACreateInAFactoryWithNoSeat_IsAskedOnlyAboutTheFactory()
    {
        var req = ChildSpawn();
        req.Factory = TheFactory;

        Assert.Null(SpawnFactory.RefuseAtMachineDoor(req, DirectorId, _ => Older,
            (_, _) => throw new InvalidOperationException("a create with no seat field must not be asked about the kit")));
    }

    [Fact]
    public void RefuseAtMachineDoor_AFactoryTooOldToCarry_IsTheFirstAnswer()
    {
        var req = ChildSpawn();
        req.Factory = TheFactory;
        req.FactorySeat = "outreach";
        var ancient = new DirectorDto { DirectorId = DirectorId, MachineName = Machine, Version = "2.12.0" };

        Assert.Contains("cannot put a session into a factory", SpawnFactory.RefuseAtMachineDoor(req, DirectorId, _ => ancient, null));
    }

    // ---------- the Interrupted continuation ----------

    private static CrashJournalSessionDto JournalRow() => new()
    {
        SessionId = Guid.NewGuid().ToString(),
        RepoPath = @"D:\repos\devthrottle",
        Agent = "ClaudeCode",
        Factory = TheFactory,
        FactorySeat = "outreach",
        FactoryDefinitionVersion = 4,
        FactoryKit = FactoryKits.Seat,
        LaunchModel = "claude-sonnet-5-5",
    };

    [Fact]
    public void ContinuationRequest_ASeatsJournalRow_ContinuesAsTheSameSeatOnTheSameModel()
    {
        var req = GatewayEndpoints.ContinuationRequest(JournalRow(), "context");

        Assert.Equal(TheFactory, req.Factory);
        Assert.Equal("outreach", req.FactorySeat);
        Assert.Equal(4, req.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, req.FactoryKit);
        Assert.Equal("claude-sonnet-5-5", req.Model);
        Assert.NotNull(SpawnFactory.DirectorCannotBuildKit(req, Older, buildsFactoryKit: false));
    }

    [Fact]
    public void ContinuationRequest_UNDEFINED_FACTORY_AJournalRowWithNoSeat_CarriesNoSeatField()
    {
        var row = JournalRow();
        row.FactorySeat = null;
        row.FactoryDefinitionVersion = null;
        row.FactoryKit = null;
        row.LaunchModel = null;

        var req = GatewayEndpoints.ContinuationRequest(row, "context");

        Assert.False(req.CarriesSeatStartFields());
        Assert.Null(SpawnFactory.DirectorCannotBuildKit(req, Older, buildsFactoryKit: false));
    }
}
