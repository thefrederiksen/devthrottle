using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The clean-machine install failure, at the seam it happened on (devthrottle_internal issue #1050).
///
/// On the first clean-machine walk of v1.8.7 the product could not start a session with the agent its
/// OWN wizard had just installed. The wizard runs the official installer, which drops the binary in
/// <c>~/.local\bin</c>; detection probes that location directly, so the wizard finds it and records
/// its ABSOLUTE path on the agent entry (<c>agent.entries[].executable_path</c>) and reports
/// "1 agent ready". But nothing writes the legacy per-type <c>agent.claude_path</c> key any more, so
/// <see cref="AgentOptions.ClaudePath"/> stays at its bare default "claude" - and the Control API
/// create path built the agent from <see cref="AgentOptions"/> alone. So the ARGUMENTS came from the
/// entry and the EXECUTABLE came from somewhere else: CreateProcess was handed a bare "claude" that
/// resolves on no clean machine, and the session died with "CreateProcess failed."
///
/// Every existing test passed because every test exercised the working seam - the desktop New Session
/// dialog passes the selected entry's path explicitly, which is every path a developer takes.
///
/// THIS TEST PINS THE PROPERTY, NOT THE SYMPTOM. It asserts the RESOLVED EXECUTABLE the create path
/// launched, and the expected value is a file inside the test's own temp folder, so no machine's PATH
/// can make it pass by accident - which is exactly how "the session starts" would go green again the
/// moment something put <c>.local\bin</c> on a PATH, while a clean machine stayed broken.
///
/// CC_DIRECTOR_ROOT is redirected to an isolated temp folder, so the seeded config, the agent entry
/// and the Claude hook-settings file never touch the real machine. In the "DirectorRoot" collection,
/// which serializes root-touching tests.
/// </summary>
[Collection("DirectorRoot")]
public sealed class CleanInstallSessionLaunchTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string DirectorId = "dir-1050-test";

    /// <summary>
    /// What the machine-level path is on a clean install: the bare default that nothing writes and
    /// nothing can resolve once the wizard has installed the agent off PATH.
    /// </summary>
    private const string UnresolvableBareCommand = "claude-1050-not-installed";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _repo;
    private readonly string _installedAgentExe;
    private readonly string _otherEntryExe;
    private readonly SessionManager _sm;

    public CleanInstallSessionLaunchTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-1050-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);

        // Stand in for the binary the wizard installed: a REAL executable at an absolute path in a
        // directory that is on no PATH - the shape of C:\Users\qa\.local\bin\claude.exe. A copy of the
        // platform shell is used because it is guaranteed to exist and to launch.
        var onWindows = OperatingSystem.IsWindows();
        var source = onWindows ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
        var exeName = onWindows ? "claude.exe" : "claude";
        _installedAgentExe = CopyShellTo(Path.Combine(_root, "local-bin"), exeName, source);
        _otherEntryExe = CopyShellTo(Path.Combine(_root, "other-bin"), exeName, source);

        // The config a clean machine has after the wizard's install-and-accept: an agent entry carrying
        // the installed binary's absolute path, and no legacy per-type path key at all.
        //
        // TWO entries of the same kind on purpose. The first is disabled and carries a DIFFERENT path
        // and a DIFFERENT preset; the enabled one is the entry every launch must read. With one entry
        // there is no way to tell "the launch read the entry" from "the launch read some other source
        // that happens to hold the same string" - and the defect was not a missing value, it was two
        // halves of one record being read from two places. Two distinguishable entries make the pairing
        // itself the assertion: the executable and the arguments have to name the SAME record.
        Directory.CreateDirectory(CcStorage.Config());
        File.WriteAllText(CcStorage.ConfigJson(), JsonSerializer.Serialize(new
        {
            agent = new
            {
                entries = new[]
                {
                    new
                    {
                        type = "ClaudeCode",
                        enabled = false,
                        executable_path = _otherEntryExe,
                        preset_id = "Skip permissions",
                        launch_mode = "Unattended",
                    },
                    new
                    {
                        type = "ClaudeCode",
                        enabled = true,
                        executable_path = _installedAgentExe,
                        preset_id = "Standard",
                        launch_mode = "Guided",
                    },
                },
            },
        }));

        _sm = new SessionManager(new AgentOptions { ClaudePath = UnresolvableBareCommand });
    }

    /// <summary>
    /// A real, launchable executable at an absolute path in a directory that is on no PATH - the shape
    /// of C:\Users\qa\.local\bin\claude.exe. A copy of the platform shell, because it is guaranteed to
    /// exist and to start.
    /// </summary>
    private static string CopyShellTo(string directory, string fileName, string shell)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, fileName);
        File.Copy(shell, target);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target,
                UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
        return target;
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

    private readonly List<Session> _created = new();

    private Session CreateThroughControlApi()
    {
        var result = SessionCommandExecutor.CreateAsync(_sm, DirectorId, new DirectorCommand
        {
            Verb = "create",
            PayloadJson = JsonSerializer.Serialize(new NewSessionRequest
            {
                RepoPath = _repo,
                Agent = "ClaudeCode",
                Name = "issue 1050 clean install launch",
            }, Web),
        }).GetAwaiter().GetResult();

        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson!, Web);
        Assert.NotNull(dto);
        var session = _sm.GetSession(Guid.Parse(dto!.SessionId));
        Assert.NotNull(session);
        _created.Add(session!);
        return session!;
    }

    [Fact]
    public void Create_LaunchesTheExecutableTheWizardRecordedOnTheAgentEntry()
    {
        // THE defect: the entry knows where the agent is, and the launch must use it. The bare
        // machine-level command cannot resolve, so before the fix this create failed outright.
        var session = CreateThroughControlApi();

        Assert.Equal(_installedAgentExe, session.LaunchExecutable);
    }

    [Fact]
    public void Create_DoesNotLaunchTheUnwritableBareMachineCommand()
    {
        // Stated on its own because the bare name is the failure. It must not reach the launcher when
        // an entry records a real path - not as a first choice and not as a fallback.
        var session = CreateThroughControlApi();

        Assert.NotEqual(UnresolvableBareCommand, session.LaunchExecutable);
        Assert.DoesNotContain(UnresolvableBareCommand, session.LaunchExecutable);
    }

    [Fact]
    public void Create_TakesArgumentsAndExecutableFromTheSameEntry()
    {
        // THE ACTUAL DEFECT, as one assertion: the arguments and the executable must name ONE record.
        // Two entries of this kind exist, each with its own path AND its own preset, so a launch that
        // read them from two sources would pair the enabled entry's arguments with the other entry's
        // binary (or with a machine-level path) and this would fail. Both halves must be the ENABLED
        // entry's: the Standard preset's automatic permission mode, and the path beside it.
        var session = CreateThroughControlApi();

        Assert.Equal(_installedAgentExe, session.LaunchExecutable);
        Assert.Contains("--permission-mode auto", session.ClaudeArgs ?? "");

        // And not the other entry's pairing, which is what "read from two sources" would produce.
        Assert.NotEqual(_otherEntryExe, session.LaunchExecutable);
        Assert.DoesNotContain("--dangerously-skip-permissions", session.ClaudeArgs ?? "");
    }
}
