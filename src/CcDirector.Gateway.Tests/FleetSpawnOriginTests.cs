using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;
using CcDirector.Core.Tests;   // TestShell, linked into this project

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Session origin and lineage at the Director's spawn floor (devthrottle_internal issue #982): a session
/// records WHO asked for it, from WHERE, and WHICH session made the call, and the record is stamped
/// before the agent launches.
///
/// Remove-the-network-port mission, phase 5: the Director's /fleet/spawn HTTP route is gone with the
/// listener, and the ONE spawn path every caller rides is the <c>create</c> verb - the Gateway
/// dispatches it down the tunnel and <see cref="SessionCommandExecutor.Create"/> is its whole body:
/// validation, create funnel, pre-launch stamp, DTO mapper. These tests drive that real verb core, so
/// the strength of the original wire tests is preserved over the surviving code. The sessions are
/// `cmd /k`, the same harmless launch the sibling spawn tests use.
/// </summary>
[Collection("DirectorRoot")]
public sealed class FleetSpawnOriginTests : IDisposable
{
    private const string DirectorId = "dir-spawn-origin";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly SessionManager _sm;
    private readonly string _repoDir;

    public FleetSpawnOriginTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-spawnorigin-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _repoDir = Path.Combine(Path.GetTempPath(), "ccd-spawnorigin-repo-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>A spawn body shaped like the CLI's, launching a harmless shell.</summary>
    private NewSessionRequest SpawnBody() => new()
    {
        RepoPath = _repoDir,
        Agent = "RawCli",
        Command = TestShell.Path,
        CommandArgs = TestShell.Args,
        Name = "origin test session",
    };

    private DirectorCommandResult Spawn(NewSessionRequest body)
        => SessionCommandExecutor.Create(_sm, DirectorId, new DirectorCommand
        {
            CommandId = "cmd-spawn-origin",
            Verb = "create",
            SessionId = "",
            PayloadJson = JsonSerializer.Serialize(body, Json),
        });

    private SessionDto SpawnOk(NewSessionRequest body)
    {
        var result = Spawn(body);
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "{}", Json);
        Assert.NotNull(dto);
        return dto!;
    }

    private async Task CleanUpAsync(SessionDto dto)
    {
        if (dto.SessionId is not null && Guid.TryParse(dto.SessionId, out var id))
        {
            try { await _sm.KillSessionAsync(id); } catch { /* the shell may already be gone */ }
        }
    }

    [Fact]
    public async Task An_agent_spawn_records_who_asked_and_which_session_asked()
    {
        // What `cc-devthrottle session spawn` sends from inside a session: CC_SESSION_ID present, so the
        // CLI states agent + itself as the parent. This is the whole claim the issue is about - the
        // fleet's sessions are largely started by other sessions, and until now nothing recorded it.
        var parent = Guid.NewGuid();
        var body = SpawnBody();
        body.Origin = SessionOriginKinds.Agent;
        body.OriginSurface = SessionOriginSurfaces.Cli;
        body.ParentSessionId = parent.ToString();

        var dto = SpawnOk(body);
        try
        {
            Assert.Equal(SessionOriginKinds.Agent, dto.OriginKind);
            Assert.Equal(SessionOriginSurfaces.Cli, dto.OriginSurface);
            Assert.Equal(parent.ToString(), dto.ParentSessionId);

            // And it is on the SESSION, not just the response - which is what the roster push carries
            // up to the Gateway and what the durable history row is written from.
            var session = _sm.GetSession(Guid.Parse(dto.SessionId!));
            Assert.NotNull(session);
            Assert.Equal(SessionOriginKinds.Agent, session!.OriginKind);
            Assert.Equal(parent, session.ParentSessionId);
        }
        finally { await CleanUpAsync(dto); }
    }

    [Fact]
    public async Task A_spawn_that_names_no_surface_is_recorded_with_unknown_kind_and_no_parent()
    {
        // Only the caller can tell a person running the command from a session running it; inventing a
        // kind here would fabricate exactly the number the field exists to produce. (The CLI itself
        // always states its surface; a create that names none is an older or hand-rolled caller.)
        var dto = SpawnOk(SpawnBody());
        try
        {
            Assert.Equal(SessionOriginKinds.Unknown, dto.OriginKind);
            Assert.Null(dto.ParentSessionId);
        }
        finally { await CleanUpAsync(dto); }
    }

    [Fact]
    public async Task A_parent_named_on_a_human_origin_is_dropped()
    {
        // The two statements contradict each other; the stated kind is what the caller meant. Keeping
        // both would leave a lineage edge hanging off a session nobody claims started it.
        var body = SpawnBody();
        body.Origin = SessionOriginKinds.Human;
        body.OriginSurface = SessionOriginSurfaces.Cli;
        body.ParentSessionId = Guid.NewGuid().ToString();

        var dto = SpawnOk(body);
        try
        {
            Assert.Equal(SessionOriginKinds.Human, dto.OriginKind);
            Assert.Null(dto.ParentSessionId);
        }
        finally { await CleanUpAsync(dto); }
    }

    [Fact]
    public void A_mistyped_origin_is_refused_rather_than_recorded_as_unknown()
    {
        // The --type lesson, applied here. If a typo landed in the "unknown" bucket it would be
        // indistinguishable from an honest older caller, and the share of sessions agents start would
        // quietly absorb every caller's mistakes with nothing to show for it.
        var body = SpawnBody();
        body.Origin = "robot";

        var result = Spawn(body);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Contains("unknown origin 'robot'", result.Error);
        Assert.Contains(SessionOriginKinds.Agent, result.Error);
    }

    [Fact]
    public void A_mistyped_origin_surface_is_refused()
    {
        var body = SpawnBody();
        body.OriginSurface = "smoke-signal";

        var result = Spawn(body);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Contains("unknown origin surface", result.Error);
    }

    [Fact]
    public void A_parent_session_id_that_is_not_an_id_is_refused()
    {
        // A broken lineage edge must never be silently dropped: the session would then look like a root,
        // and a root session is a meaningful thing in the tree this field exists to build.
        var body = SpawnBody();
        body.Origin = SessionOriginKinds.Agent;
        body.ParentSessionId = "the session next to me";

        var result = Spawn(body);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Contains("is not a session id", result.Error);
    }

    [Fact]
    public async Task Lineage_is_recorded_even_when_the_new_session_has_no_controller()
    {
        // The case that separates lineage from supervision. `session spawn --standalone` deliberately
        // creates a human-facing PEER with no controller - so nothing recedes it to slate, and nothing
        // about the running session says an agent made it. It is still an agent-started session, and
        // that is precisely what is being counted.
        var parent = Guid.NewGuid();
        var body = SpawnBody();
        body.Origin = SessionOriginKinds.Agent;
        body.OriginSurface = SessionOriginSurfaces.Cli;
        body.ParentSessionId = parent.ToString();
        body.ControllerSessionId = null; // --standalone

        var dto = SpawnOk(body);
        try
        {
            Assert.False(dto.IsControlled);
            Assert.Null(dto.ControllerSessionId);
            Assert.Equal(parent.ToString(), dto.ParentSessionId);
        }
        finally { await CleanUpAsync(dto); }
    }
}
