using System.ComponentModel;
using System.Runtime.InteropServices;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// What a launch that FAILS has to say for itself (devthrottle_internal issue #1050).
///
/// On the first clean-machine walk of v1.8.7 a session died with the whole of "CreateProcess failed."
/// - no Win32 error code, no executable, no command line, no working directory - and the person saw
/// only "HTTP 500 from the Director". It took a QA seat four experimental eliminations to place,
/// because ERROR_FILE_NOT_FOUND, ERROR_DIRECTORY and ERROR_NOT_ENOUGH_MEMORY are three different bugs
/// and the log distinguished none of them. These tests pin the evidence a failed launch must carry,
/// so the NEXT launch failure - whatever its cause - is readable from the log alone.
///
/// CC_DIRECTOR_ROOT is redirected so the Claude hook-settings file a launch installs is written to an
/// isolated temp folder, never the developer's own.
/// </summary>
[Trait("Gate", "Quarantine")]
[Collection("CcStorageRoot")]
public sealed class AgentLaunchDiagnosticsTests : IDisposable
{
    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _repo;

    public AgentLaunchDiagnosticsTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-launchdiag-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ===== An unresolvable agent command is refused by name, before CreateProcess =====

    [Fact]
    public void CreateSession_AgentCommandDoesNotResolve_ThrowsNamingTheAgentAndTheCommand()
    {
        // This is the clean-machine state exactly: the configured command is a bare name that is on
        // no PATH, so there is nothing to launch. Handing it to CreateProcess anyway is what produced
        // a bare "CreateProcess failed."; the launch now refuses it and says which agent, which
        // command, and what to do about it.
        var command = "claude-1050-not-installed";
        var manager = new SessionManager(new AgentOptions { ClaudePath = command });

        var ex = Assert.Throws<InvalidOperationException>(() => manager.CreateSession(_repo));

        Assert.Contains("Claude Code", ex.Message);
        Assert.Contains(command, ex.Message);
        Assert.Contains("PATH", ex.Message);
        Assert.Contains("Settings", ex.Message);
    }

    [Fact]
    public void CreateSession_AgentCommandDoesNotResolve_DoesNotReportOnlyCreateProcessFailed()
    {
        // The exact regression: three words that name no cause. The message may not be this again.
        var manager = new SessionManager(new AgentOptions { ClaudePath = "claude-1050-not-installed" });

        var ex = Assert.Throws<InvalidOperationException>(() => manager.CreateSession(_repo));

        Assert.NotEqual("CreateProcess failed.", ex.Message);
    }

    // ===== A launch that reaches CreateProcess and fails carries the Win32 error =====

    [Fact]
    public void ConPtyBackend_Start_MissingExecutable_MessageCarriesWin32ErrorCode()
    {
        if (!OnWindows) return; // ConPTY is a Windows pseudo-console; the Unix host reports errno already.

        // An absolute path to a file that is not there: CreateProcess answers ERROR_FILE_NOT_FOUND (2).
        // The point is not the number - it is that the number, its system text, the command line and
        // the working directory all reach the message a caller logs.
        var missing = Path.Combine(_root, "no-such-agent.exe");
        using var backend = new ConPtyBackend(bufferSizeBytes: 4096);

        var ex = Assert.Throws<Win32Exception>(
            () => backend.Start(missing, "--some-arg", _repo, 120, 30));

        Assert.Equal(2, ex.NativeErrorCode);
        Assert.Contains("Win32 error 2", ex.Message);
        Assert.Contains(missing, ex.Message);
        Assert.Contains(_repo, ex.Message);
    }

    // ===== A launch that succeeds records WHICH executable it launched =====

    [Fact]
    public void CreateSession_RecordsTheResolvedExecutableOnTheSession()
    {
        // The launch fact the log did not have and no test could pin: the resolved executable actually
        // handed to CreateProcess. A session that started can say what it started.
        var shell = OnWindows
            ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : "/bin/sh";
        var manager = new SessionManager(new AgentOptions { ClaudePath = shell });

        var session = manager.CreateSession(_repo);
        try
        {
            Assert.Equal(shell, session.LaunchExecutable);
        }
        finally
        {
            session.Dispose();
        }
    }
}
