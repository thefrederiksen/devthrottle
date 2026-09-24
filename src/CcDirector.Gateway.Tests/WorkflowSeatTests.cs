using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;
using CcDirector.Core.Tests;   // TestShell, linked into this project

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Workflows mission, phase 5b: seated sessions, at the Director's surviving seam.
///
/// Remove-the-network-port mission, phase 5: the /fleet/spawn route these tests originally drove -
/// including its own copy of the Gateway run-resolution - is gone with the Director's listener. On
/// the live path the GATEWAY resolves the seat (MachineEndpoints validates an explicit run id,
/// auto-seats a mission spawn onto the mission's run, and records the participant - covered by
/// <c>MachineSpawnWorkflowScopeTests</c>), and the CREATE it dispatches down the tunnel arrives with
/// run id + workflow id + PINNED version already resolved. What the DIRECTOR owes that create, and
/// what these tests pin at the real verb core:
///
///   * the seat is STAMPED on the session (run id + workflow id + pinned version, straight from the
///     request, never re-resolved locally);
///   * the seated session's maintained preamble file carries the seat paragraph - the pinned fetch
///     command and the fail-closed STOP rule - which is what the SessionStart hook prints;
///   * a seat whose workflow id is not a catalog slug is REFUSED, not guessed at;
///   * an unseated spawn is unaffected and carries no seat paragraph.
/// </summary>
[Collection("DirectorRoot")]
public sealed class WorkflowSeatTests : IAsyncLifetime
{
    private static readonly Guid KnownRunId = Guid.NewGuid();
    private const int PinnedVersion = 3;

    private readonly string _root;
    private readonly string? _prevRoot;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private string _repoDir = null!;

    public WorkflowSeatTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-seat-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "ccd-seat-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);

        _sm = new SessionManager(new AgentOptions());
        // The host is here for its session-state services: the preamble MAINTAINER is what writes the
        // hook file these tests read, exactly as it does in production. It binds nothing.
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(),
            instancesDirectory: Path.Combine(_root, "instances-isolated"));
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_repoDir)) Directory.Delete(_repoDir, true); } catch { /* best effort */ }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The preamble text the session's SessionStart hook would inject, read out of the file the Director
    /// maintains for it (remove-the-network-port mission, phase 3). The file holds the finished
    /// hookSpecificOutput envelope, so the text is its additionalContext field; an empty file means the
    /// hook injects nothing.
    /// </summary>
    private static string ReadMaintainedPreamble(string sessionId)
    {
        var path = SessionHookFiles.PreamblePathFor(Guid.Parse(sessionId));
        Assert.True(File.Exists(path), $"the Director did not maintain a preamble file at {path}");
        var body = File.ReadAllText(path);
        if (body.Length == 0)
            return "";
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString() ?? "";
    }

    /// <summary>A spawn body shaped like the create the Gateway dispatches, launching a harmless shell.</summary>
    private NewSessionRequest SpawnBody() => new()
    {
        RepoPath = _repoDir,
        Agent = "RawCli",
        Command = TestShell.Path,
        CommandArgs = TestShell.Args,
        Role = "Architect",
    };

    private DirectorCommandResult Spawn(NewSessionRequest body)
        => SessionCommandExecutor.Create(_sm, "dir-seat-test", new DirectorCommand
        {
            CommandId = "cmd-seat",
            Verb = "create",
            SessionId = "",
            PayloadJson = JsonSerializer.Serialize(body, Json),
        });

    private async Task CleanUpAsync(SessionDto dto)
    {
        if (dto.SessionId is not null && Guid.TryParse(dto.SessionId, out var id))
        {
            try { await _sm.KillSessionAsync(id); } catch { /* the shell may already be gone */ }
        }
    }

    [Fact]
    public async Task A_gateway_resolved_seat_isStamped_andBriefsTheAgent()
    {
        // What arrives down the tunnel after the Gateway resolved the seat: run id, catalog slug, and
        // the PINNED version, together.
        var body = SpawnBody();
        body.WorkflowRunId = KnownRunId;
        body.WorkflowId = "mission";
        body.WorkflowVersion = PinnedVersion;

        var result = Spawn(body);
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "{}", Json)!;
        try
        {
            // The seat: run id + workflow id + PINNED version, stamped from the request.
            Assert.Equal(KnownRunId, dto.WorkflowRunId);
            Assert.Equal("mission", dto.WorkflowId);
            Assert.Equal(PinnedVersion, dto.WorkflowVersion);

            // The briefing: the seated session's preamble tells the agent its seat, the PINNED fetch
            // command, and the fail-closed rule - regardless of agent kind, because it rides the same
            // preamble every agent family receives. Read from the FILE the Director maintains, which
            // is what the SessionStart hook prints.
            var preamble = ReadMaintainedPreamble(dto.SessionId!);
            Assert.Contains("[Workflow seat]", preamble);
            Assert.Contains("seated as Architect on the 'mission' workflow", preamble);
            Assert.Contains($"cc-devthrottle workflow instructions mission --version {PinnedVersion}", preamble);
            Assert.Contains("STOP and report", preamble);
        }
        finally { await CleanUpAsync(dto); }
    }

    [Fact]
    public void A_seat_whose_workflow_id_is_not_a_catalog_slug_isRefused()
    {
        // A forged or corrupted seat must never be stamped: the agent would fetch conduct that does
        // not exist and a governance record would name a workflow nobody published.
        var body = SpawnBody();
        body.WorkflowRunId = KnownRunId;
        body.WorkflowId = "Not A Slug!!";
        body.WorkflowVersion = PinnedVersion;

        var result = Spawn(body);

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Contains("invalid workflow seat", result.Error);
    }

    [Fact]
    public async Task An_unseated_spawn_isUnaffected_andCarriesNoSeatParagraph()
    {
        var result = Spawn(SpawnBody());
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "{}", Json)!;
        try
        {
            Assert.Null(dto.WorkflowRunId);
            Assert.Null(dto.WorkflowId);
            Assert.Null(dto.WorkflowVersion);

            var preamble = ReadMaintainedPreamble(dto.SessionId!);
            Assert.DoesNotContain("[Workflow seat]", preamble);
        }
        finally { await CleanUpAsync(dto); }
    }
}
