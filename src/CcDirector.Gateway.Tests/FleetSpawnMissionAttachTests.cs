using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;
using CcDirector.Core.Tests;   // TestShell, linked into this project

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1548's surviving seam. The original defect: `cc-devthrottle session spawn --mission <id>`
/// carried a mission ID and NOTHING ELSE, and the Director's spawn floor resolved it against the WRONG
/// store (its own local missions.json) instead of the Gateway's, rejecting a mission that existed.
///
/// Remove-the-network-port mission, phase 5: the Director's /fleet/spawn route - and the
/// Gateway-lookup leg these tests originally drove over loopback HTTP - is gone with the listener.
/// The ONE spawn path is now the <c>create</c> verb the Gateway dispatches down the tunnel, and the
/// GATEWAY resolves the mission name before dispatching (MachineEndpoints stamps
/// <c>req.MissionName</c> from its own store - the source of truth - before the create leaves).
/// What the DIRECTOR must get right is therefore the create-time contract, asserted here at the
/// real verb core:
///
///   * MissionId + MissionName both present (the Gateway path): stamp DIRECTLY, no local lookup -
///     the Director having no mission store of its own must not matter.
///   * MissionId alone: REFUSED loudly rather than silently dropped, and rather than resolved against
///     a store that cannot answer. Issue #2629: the temporary local-store bridge that used to sit here
///     is GONE. It resolved a bare id against a stale per-machine missions.json, so the second spawn
///     door - POST /directors/{id}/sessions, which forwarded the create verbatim and never stamped the
///     name - produced "unknown mission" for a mission that was real, active and listed.
///   * No mission: no attach, no lookup.
/// </summary>
[Collection("DirectorRoot")]
public sealed class FleetSpawnMissionAttachTests : IDisposable
{
    private static readonly Guid KnownMissionId = Guid.NewGuid();
    private const string KnownMissionName = "Stable Release";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly SessionManager _sm;
    private readonly string _repoDir;

    public FleetSpawnMissionAttachTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-spawnmission-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _repoDir = Path.Combine(Path.GetTempPath(), "ccd-spawnmission-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);

        _sm = new SessionManager(new AgentOptions());
    }

    public void Dispose()
    {
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_repoDir)) Directory.Delete(_repoDir, true); } catch { /* best effort */ }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A spawn body shaped like the create the Gateway dispatches, launching a harmless shell.</summary>
    private NewSessionRequest SpawnBody() => new()
    {
        RepoPath = _repoDir,
        Agent = "RawCli",
        Command = TestShell.Path,
        CommandArgs = TestShell.Args,
    };

    private DirectorCommandResult Spawn(NewSessionRequest body)
        => SessionCommandExecutor.Create(_sm, "dir-spawn-mission", new DirectorCommand
        {
            CommandId = "cmd-spawn-mission",
            Verb = "create",
            SessionId = "",
            PayloadJson = JsonSerializer.Serialize(body, Json),
        }, null);

    private async Task<SessionDto> SpawnOkAndCleanUpAsync(NewSessionRequest body)
    {
        var result = Spawn(body);
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "{}", Json)!;
        if (dto.SessionId is not null && Guid.TryParse(dto.SessionId, out var id))
        {
            try { await _sm.KillSessionAsync(id); } catch { /* the shell may already be gone */ }
        }
        return dto;
    }

    [Fact]
    public async Task Gateway_resolved_mission_attaches_directly_without_any_local_lookup()
    {
        // The Gateway path: id AND name arrive together because the Gateway already resolved the mission
        // against ITS store. The Director holds no mission store at all, and that must not matter -
        // re-resolving locally is the wrong-store defect #1548 and #2629 were both about.
        var body = SpawnBody();
        body.MissionId = KnownMissionId;
        body.MissionName = KnownMissionName;

        var dto = await SpawnOkAndCleanUpAsync(body);

        Assert.Equal(KnownMissionId, dto.MissionId);
        Assert.Equal(KnownMissionName, dto.MissionName);
    }

    [Fact]
    public void An_id_with_no_name_is_refused_loudly_and_names_the_real_problem()
    {
        // A create carrying a mission id and no name never passed through the Gateway's resolution, so
        // nothing on this machine can say what that mission is. Refusing is right - silently dropping the
        // attach would leave a session outside the pod that expected it, which is the quiet version of the
        // same defect. What must NOT happen is the old answer: consulting a stale per-machine store and
        // calling a real mission unknown, then advising the caller to create one that already exists.
        var body = SpawnBody();
        body.MissionId = Guid.NewGuid();

        var result = Spawn(body);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Contains("without its name", result.Error);
        Assert.Contains("Gateway", result.Error);
        Assert.DoesNotContain("Create it first", result.Error);
    }

    [Fact]
    public async Task No_mission_on_the_create_attaches_nothing()
    {
        var dto = await SpawnOkAndCleanUpAsync(SpawnBody());

        Assert.Null(dto.MissionId);
        Assert.Null(dto.MissionName);
    }
}
