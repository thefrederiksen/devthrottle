using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// Saving, starting, restarting and moving the Fleet Manager (the Fleet Manager mission, step 5), against a real
/// settings store and a fake world. The fake spawner records every call, so "no start was attempted" is asserted as
/// a count of zero calls, and it asks the start's own Director check exactly as the real spawner does.
/// </summary>
public sealed class FleetManagerPlacementServiceTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-fm-place");
    private static readonly DateTime Now = new(2026, 9, 16, 14, 30, 0, DateTimeKind.Utc);
    private const string OldId = "40000000-0000-4000-8000-000000000001";
    private const string NewId = "40000000-0000-4000-8000-000000000002";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TenantSettingsResolver _settings;
    private readonly FakePlacementWorld _world = new(Now, NewId);
    private readonly FleetManagerPlacementService _service;

    public FleetManagerPlacementServiceTests()
    {
        _settings = new TenantSettingsResolver(new TenantSettingsStore(_harness.Open()));
        _service = new FleetManagerPlacementService(_settings, _world, retirePoll: TimeSpan.Zero, retireGiveUp: TimeSpan.FromMinutes(5));
    }

    public void Dispose()
    {
        _service.Dispose();
        _harness.Dispose();
    }

    private static void NoStamp(NewSessionRequest _) { }

    private static FleetManagerMachineFacts Running(string machine, string directorId = "dir-a", DateTime? firstSeen = null) => new(
        machine, new LauncherDto { MachineName = machine, LastSeenAt = Now }, LauncherReach.Connected, true,
        new[] { new DirectorDto { DirectorId = directorId, MachineName = machine, LastSeen = Now } }, firstSeen, "ClaudeCode");

    private static FleetManagerMachineFacts LauncherOnly(string machine) => new(
        machine, new LauncherDto { MachineName = machine, LastSeenAt = Now }, LauncherReach.Connected, true, Array.Empty<DirectorDto>());

    private static FleetManagerMachineFacts Offline(string machine) => new(
        machine, null, LauncherReach.NotConnected, false, Array.Empty<DirectorDto>(), null, null, Now.AddHours(-3));

    private static SessionDto Live(string id, string state, string machine = "WORKSTATION-A") => new()
    {
        SessionId = id, ActivityState = state, MachineName = machine, Agent = "ClaudeCode", CreatedAt = Now.AddHours(-1),
    };

    private void MarkRunning(string state = "Idle")
    {
        _settings.SetFleetManagerSessionId(Tenant, OldId, Now);
        _world.Roster.Add(("dir-a", Live(OldId, state)));
    }

    // ---- setting key literals ---------------------------------------------------------------------------

    [Fact]
    public void SettingKeys_AreTheStoredLiterals()
    {
        Assert.Equal("fleet_manager_agent", TenantSettingKeys.FleetManagerAgent);
        Assert.Equal("fleet_manager_machine", TenantSettingKeys.FleetManagerMachine);
        Assert.Equal("fleet_manager_session_id", TenantSettingKeys.FleetManagerSessionId);
        Assert.Contains("fleet_manager_agent", TenantSettingKeys.All);
        Assert.Contains("fleet_manager_machine", TenantSettingKeys.All);
        Assert.Contains("fleet_manager_session_id", TenantSettingKeys.All);
    }

    // ---- read -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_NothingSaved_ShowsTheDefaultAndStoresNothing()
    {
        _world.Machines.Add(Running("WORKSTATION-A", firstSeen: Now.AddDays(-30)));

        var dto = await _service.ReadAsync(Tenant, default);

        Assert.True(dto.IsDefault);
        Assert.Equal("WORKSTATION-A", dto.Machine);
        Assert.Null(_settings.FleetManagerAgent(Tenant));
        Assert.Null(_settings.FleetManagerMachine(Tenant));
    }

    [Fact]
    public async Task ReadAsync_PlacementDirectorRunning_AsksItForItsAgents()
    {
        _world.Machines.Add(Running("WORKSTATION-A", "dir-a"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        _world.Agents = new List<AgentChoiceDto> { new() { Type = "ClaudeCode" } };

        var dto = await _service.ReadAsync(Tenant, default);

        Assert.Equal(new[] { "dir-a" }, _world.AgentListAskedOf);
        Assert.Equal("- Claude Code installed", dto.Machines.Single().Detail);
    }

    // ---- save refusals ----------------------------------------------------------------------------------

    public static IEnumerable<object?[]> IncompleteBodies() => new[]
    {
        new object?[] { null },
        new object?[] { new FleetManagerPlacementRequest { Agent = "ClaudeCode" } },
        new object?[] { new FleetManagerPlacementRequest { Machine = "WORKSTATION-A" } },
        new object?[] { new FleetManagerPlacementRequest { Agent = " ", Machine = "WORKSTATION-A" } },
    };

    [Theory]
    [MemberData(nameof(IncompleteBodies))]
    public async Task SaveAsync_MissingAgentOrMachine_IsRefusedAndNothingStored(FleetManagerPlacementRequest? body)
    {
        _world.Machines.Add(Running("WORKSTATION-A"));

        var result = await _service.SaveAsync(Tenant, body, default);

        Assert.Equal(400, result.Status);
        Assert.StartsWith("Both an agent and a computer are required", result.Error);
        Assert.Null(_settings.FleetManagerAgent(Tenant));
        Assert.Null(_settings.FleetManagerMachine(Tenant));
    }

    [Theory]
    [InlineData("RawCli")]
    [InlineData("NotAnAgent")]
    public async Task SaveAsync_UnknownAgent_IsRefused(string agent)
    {
        _world.Machines.Add(Running("WORKSTATION-A"));

        var result = await _service.SaveAsync(Tenant, new() { Agent = agent, Machine = "WORKSTATION-A" }, default);

        Assert.Equal(400, result.Status);
        Assert.Equal($"'{agent}' is not an agent the Fleet Manager can run on. Choose one of: ClaudeCode, Codex, Gemini, "
                     + "OpenCode, Pi, Grok, Copilot, Cursor. Nothing was saved.", result.Error);
        Assert.Null(_settings.FleetManagerAgent(Tenant));
    }

    [Fact]
    public async Task SaveAsync_UnknownMachine_IsRefused()
    {
        _world.Machines.Add(Running("WORKSTATION-A"));

        var result = await _service.SaveAsync(Tenant, new() { Agent = "Codex", Machine = "WORKSTATION-Z" }, default);

        Assert.Equal(400, result.Status);
        Assert.Equal("'WORKSTATION-Z' is not a computer on this account. Nothing was saved.", result.Error);
        Assert.Null(_settings.FleetManagerMachine(Tenant));
    }

    [Fact]
    public async Task SaveAsync_UnreachableMachine_IsRefused()
    {
        _world.Machines.Add(Offline("WORKSTATION-C"));

        var result = await _service.SaveAsync(Tenant, new() { Agent = "Codex", Machine = "WORKSTATION-C" }, default);

        Assert.Equal(400, result.Status);
        Assert.Equal("WORKSTATION-C cannot be reached now, so the Fleet Manager cannot be placed there. Choose a computer "
                     + "that is on. Nothing was saved.", result.Error);
        Assert.Null(_settings.FleetManagerMachine(Tenant));
    }

    [Fact]
    public async Task SaveAsync_Valid_StoresBothInCanonicalFormAndStartsNothing()
    {
        _world.Machines.Add(LauncherOnly("WORKSTATION-A"));

        var result = await _service.SaveAsync(Tenant, new() { Agent = "codex", Machine = "workstation-a" }, default);

        Assert.Equal(200, result.Status);
        Assert.Equal("Codex", _settings.FleetManagerAgent(Tenant));
        Assert.Equal("WORKSTATION-A", _settings.FleetManagerMachine(Tenant));
        Assert.False(result.Placement!.IsDefault);
        Assert.Empty(_world.Spawns);
    }

    // ---- start ------------------------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_Default_StartsTheFleetManagerMarksItAndStoresThePlacement()
    {
        _world.Machines.Add(Running("WORKSTATION-A", "dir-a", firstSeen: Now.AddDays(-2)));
        _world.NextSession = Live(NewId, "Starting");

        var result = await _service.StartAsync(Tenant, req => req.OriginSurface = "phone", default);

        Assert.Equal(200, result.Status);
        var spawn = Assert.Single(_world.Spawns);
        Assert.Equal("WORKSTATION-A", spawn.Machine);
        Assert.Equal("Fleet Manager", spawn.Request.Name);
        Assert.Equal("ClaudeCode", spawn.Request.Agent);
        Assert.True(spawn.Request.FleetManagerHome);
        Assert.Equal("", spawn.Request.RepoPath);
        Assert.Null(spawn.Request.ControllerSessionId);
        Assert.Equal("human", spawn.Request.Origin);
        Assert.Equal("phone", spawn.Request.OriginSurface); // the route's stamp ran on the request that was sent
        Assert.Equal("dir-a", spawn.Request.Director);
        Assert.Equal("You are this account's Fleet Manager. Run `cc-devthrottle workflow instructions fleet-manager` and "
                     + "follow it exactly, then run `cc-devthrottle fleet digest` to see where things stand.", spawn.Request.PrePrompt);

        Assert.Equal(NewId, _settings.FleetManagerSessionId(Tenant));
        Assert.Equal(new[] { NewId }, _world.MarksRecorded); // beside the mark, as the mark route records it
        Assert.Equal("ClaudeCode", _settings.FleetManagerAgent(Tenant));
        Assert.Equal("WORKSTATION-A", _settings.FleetManagerMachine(Tenant));
        Assert.Equal("running", result.Placement!.Status.State);
        Assert.Equal(NewId, result.Placement.Status.SessionId);
    }

    [Fact]
    public async Task StartAsync_NoDirectorRunning_LeavesTheDirectorUnpinnedSoTheLauncherStartsOne()
    {
        _world.Machines.Add(LauncherOnly("WORKSTATION-A"));
        _settings.SetFleetManagerPlacement(Tenant, "Gemini", "WORKSTATION-A", Now);

        var result = await _service.StartAsync(Tenant, NoStamp, default);

        Assert.Equal(200, result.Status);
        Assert.Null(Assert.Single(_world.Spawns).Request.Director);
    }

    [Fact]
    public async Task StartAsync_AlreadyRunning_IsRefusedAndNoStartIsAttempted()
    {
        _world.Machines.Add(Running("WORKSTATION-A"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        MarkRunning();

        var result = await _service.StartAsync(Tenant, NoStamp, default);

        Assert.Equal(409, result.Status);
        Assert.Equal($"The Fleet Manager is already running (session {OldId}). Restart it instead if you want a new one.", result.Error);
        Assert.Empty(_world.Spawns);
        Assert.Equal(OldId, _settings.FleetManagerSessionId(Tenant));
    }

    [Fact]
    public async Task StartAsync_ComputerUnreachable_IsRefusedAndNoStartIsAttempted()
    {
        _world.Machines.Add(LauncherOnly("WORKSTATION-A"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        _world.Machines[0] = Offline("WORKSTATION-A");
        _world.Machines.Add(Running("WORKSTATION-B", "dir-b"));

        var result = await _service.StartAsync(Tenant, NoStamp, default);

        Assert.Equal(409, result.Status);
        Assert.Equal("WORKSTATION-A cannot be reached now, so the Fleet Manager was not started. It never starts anywhere "
                     + "else: choose another computer and save, or start it when that one is back.", result.Error);
        Assert.Empty(_world.Spawns);
        Assert.Null(_settings.FleetManagerSessionId(Tenant));
    }

    [Fact]
    public async Task StartAsync_DirectorTooOldForTheFleetManagerFolder_IsRefusedBeforeTheCreate()
    {
        _world.Machines.Add(Running("WORKSTATION-A", "dir-old"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        _world.OldDirectors.Add("dir-old");

        var result = await _service.StartAsync(Tenant, NoStamp, default);

        Assert.Equal(502, result.Status);
        Assert.Equal("The Fleet Manager could not be started on WORKSTATION-A: The Director on WORKSTATION-A is older than "
                     + "the Fleet Manager and must be updated before the Fleet Manager can run there. Update DevThrottle on "
                     + "that computer, then start it again.", result.Error);
        Assert.Equal(0, _world.CreatesSent);
        Assert.Null(_settings.FleetManagerSessionId(Tenant));
    }

    [Fact]
    public async Task StartAsync_SpawnFails_ReturnsTheErrorAndMarksNothing()
    {
        _world.Machines.Add(LauncherOnly("WORKSTATION-A"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        _world.SpawnError = "launched a Director on 'WORKSTATION-A' but none registered within 90s";

        var result = await _service.StartAsync(Tenant, NoStamp, default);

        Assert.Equal(502, result.Status);
        Assert.Equal("The Fleet Manager could not be started on WORKSTATION-A: launched a Director on 'WORKSTATION-A' but "
                     + "none registered within 90s", result.Error);
        Assert.Null(_settings.FleetManagerSessionId(Tenant));
        Assert.Empty(_world.MarksRecorded);
    }

    // ---- restart ----------------------------------------------------------------------------------------

    [Fact]
    public async Task RestartAsync_NotRunning_IsRefused()
    {
        _world.Machines.Add(Running("WORKSTATION-A"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);

        var result = await _service.RestartAsync(Tenant, NoStamp, default);

        Assert.Equal(409, result.Status);
        Assert.Empty(_world.Spawns);
    }

    [Fact]
    public async Task RestartAsync_NewStartFails_LeavesTheOldOneRunningAndMarked()
    {
        _world.Machines.Add(Running("WORKSTATION-A"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        MarkRunning("Idle");
        _world.SpawnError = "the Director refused the create";

        var result = await _service.RestartAsync(Tenant, NoStamp, default);
        await _service.WhenIdleAsync();

        Assert.Equal(502, result.Status);
        Assert.Equal("The Fleet Manager could not be started on WORKSTATION-A: the Director refused the create", result.Error);
        Assert.Equal(OldId, _settings.FleetManagerSessionId(Tenant));
        Assert.Empty(_world.Closed);
    }

    [Fact]
    public async Task RestartAsync_OldOneWorking_IsClosedOnlyAfterItsTurnEnds()
    {
        _world.Machines.Add(Running("WORKSTATION-A"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        MarkRunning("Working");

        // The old one stays Working for three looks, then its turn ends.
        var looks = 0;
        _world.OnDelay = () =>
        {
            looks++;
            if (looks == 3) _world.SetState(OldId, "Idle");
            Assert.Empty(_world.Closed); // never closed while it was still working
        };

        var result = await _service.RestartAsync(Tenant, NoStamp, default);
        await _service.WhenIdleAsync();

        Assert.Equal(200, result.Status);
        Assert.Equal(NewId, _settings.FleetManagerSessionId(Tenant));
        Assert.Equal(3, looks);
        var closed = Assert.Single(_world.Closed);
        Assert.Equal(OldId, closed.SessionId);
        Assert.Equal("dir-a", closed.DirectorId);
        Assert.Equal("A new Fleet Manager replaced it (restarted or moved from Settings).", closed.Reason);
    }

    [Fact]
    public async Task RestartAsync_OldOneAskingAPermissionQuestion_IsNotClosed()
    {
        _world.Machines.Add(Running("WORKSTATION-A"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        MarkRunning("WaitingForPerm");
        _world.OnDelay = () => _world.Advance(TimeSpan.FromMinutes(1));

        var result = await _service.RestartAsync(Tenant, NoStamp, default);
        await _service.WhenIdleAsync();

        Assert.Equal(200, result.Status);
        Assert.Empty(_world.Closed); // the give-up window passed and it was left running, never force-closed
    }

    [Fact]
    public async Task RestartAsync_OldOneAlreadyExited_IsNotClosedAgain()
    {
        _world.Machines.Add(Running("WORKSTATION-A"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        MarkRunning("Working");
        _world.OnDelay = () => _world.SetState(OldId, "Exited");

        await _service.RestartAsync(Tenant, NoStamp, default);
        await _service.WhenIdleAsync();

        Assert.Empty(_world.Closed);
    }

    // ---- move -------------------------------------------------------------------------------------------

    [Fact]
    public async Task MoveAsync_Running_SavesThenStartsThereThenRetiresTheOldOne()
    {
        _world.Machines.Add(Running("WORKSTATION-A"));
        _world.Machines.Add(LauncherOnly("WORKSTATION-B"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        MarkRunning("Idle");

        var result = await _service.MoveAsync(Tenant, new() { Agent = "Codex", Machine = "WORKSTATION-B" }, NoStamp, default);
        await _service.WhenIdleAsync();

        Assert.Equal(200, result.Status);
        Assert.Equal("WORKSTATION-B", _settings.FleetManagerMachine(Tenant));
        Assert.Equal("Codex", _settings.FleetManagerAgent(Tenant));
        var spawn = Assert.Single(_world.Spawns);
        Assert.Equal("WORKSTATION-B", spawn.Machine);
        Assert.Equal("Codex", spawn.Request.Agent);
        Assert.Equal(NewId, _settings.FleetManagerSessionId(Tenant));
        Assert.Equal(OldId, Assert.Single(_world.Closed).SessionId);
    }

    [Fact]
    public async Task MoveAsync_NotRunning_SavesAndStarts()
    {
        _world.Machines.Add(LauncherOnly("WORKSTATION-B"));

        var result = await _service.MoveAsync(Tenant, new() { Agent = "Pi", Machine = "WORKSTATION-B" }, NoStamp, default);
        await _service.WhenIdleAsync();

        Assert.Equal(200, result.Status);
        Assert.Equal("Pi", Assert.Single(_world.Spawns).Request.Agent);
        Assert.Empty(_world.Closed);
    }

    [Fact]
    public async Task MoveAsync_UnreachableMachine_IsRefusedAndNothingChanges()
    {
        _world.Machines.Add(Running("WORKSTATION-A"));
        _world.Machines.Add(Offline("WORKSTATION-C"));
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        MarkRunning("Idle");

        var result = await _service.MoveAsync(Tenant, new() { Agent = "Codex", Machine = "WORKSTATION-C" }, NoStamp, default);
        await _service.WhenIdleAsync();

        Assert.Equal(400, result.Status);
        Assert.Empty(_world.Spawns);
        Assert.Empty(_world.Closed);
        Assert.Equal("WORKSTATION-A", _settings.FleetManagerMachine(Tenant));
        Assert.Equal(OldId, _settings.FleetManagerSessionId(Tenant));
    }
}
