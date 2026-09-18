using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director's half of starting a Fleet Manager (the Fleet Manager mission, step 5): a create that carries
/// <see cref="NewSessionRequest.FleetManagerHome"/> runs in <c>&lt;data root&gt;/fleet-manager</c>, makes that folder
/// when it is missing, ignores whatever repository path came with it, and is decided before the blank-path refusal.
///
/// CC_DIRECTOR_ROOT is redirected to a temp folder, and the agent is a copy of the platform shell at an absolute path,
/// the way <see cref="CleanInstallSessionLaunchTests"/> does it, so nothing touches the real machine.
/// </summary>
[Collection("DirectorRoot")]
public sealed class FleetManagerHomeCreateTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string DirectorId = "dir-fm-home-test";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly SessionManager _sm;
    private readonly List<Session> _created = new();

    public FleetManagerHomeCreateTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-fm-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        var onWindows = OperatingSystem.IsWindows();
        var shell = onWindows ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var agent = Path.Combine(bin, onWindows ? "claude.exe" : "claude");
        File.Copy(shell, agent);
        if (!onWindows)
            File.SetUnixFileMode(agent, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);

        Directory.CreateDirectory(CcStorage.Config());
        File.WriteAllText(CcStorage.ConfigJson(), JsonSerializer.Serialize(new
        {
            agent = new { entries = new[] { new { type = "ClaudeCode", enabled = true, executable_path = agent } } },
        }));
        _sm = new SessionManager(new AgentOptions { ClaudePath = agent });
    }

    public void Dispose()
    {
        foreach (var session in _created)
        {
            try { session.Dispose(); } catch { /* best effort */ }
        }
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private DirectorCommandResult Create(NewSessionRequest request)
        => SessionCommandExecutor.Create(_sm, DirectorId, new DirectorCommand
        {
            Verb = "create",
            PayloadJson = JsonSerializer.Serialize(request, Web),
        });

    private Session SessionOf(DirectorCommandResult result)
    {
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson!, Web)!;
        var session = _sm.GetSession(Guid.Parse(dto.SessionId))!;
        _created.Add(session);
        return session;
    }

    [Fact]
    public void Create_FleetManagerHomeWithBlankRepoPath_CreatesAndUsesTheFolder()
    {
        var home = Path.Combine(_root, "fleet-manager");
        Assert.False(Directory.Exists(home));

        var session = SessionOf(Create(new NewSessionRequest
        {
            RepoPath = "", FleetManagerHome = true, Name = "Fleet Manager", Agent = "ClaudeCode",
        }));

        Assert.True(Directory.Exists(home));
        Assert.Equal(home, CcStorage.FleetManagerHome());
        Assert.Equal(home, session.RepoPath);
    }

    [Fact]
    public void Create_FleetManagerHome_IgnoresTheRepoPathSentWithIt()
    {
        var session = SessionOf(Create(new NewSessionRequest
        {
            RepoPath = Path.Combine(_root, "does-not-exist"), FleetManagerHome = true, Name = "Fleet Manager", Agent = "ClaudeCode",
        }));

        Assert.Equal(Path.Combine(_root, "fleet-manager"), session.RepoPath);
    }

    [Fact]
    public void Create_BlankRepoPathWithoutTheFlag_IsStillRefused()
    {
        var result = Create(new NewSessionRequest { RepoPath = "", Name = "Fleet Manager", Agent = "ClaudeCode" });

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Equal("repoPath is required", result.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "fleet-manager")));
    }
}
