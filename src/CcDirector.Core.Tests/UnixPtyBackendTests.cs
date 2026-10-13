using System.Runtime.InteropServices;
using CcDirector.Core.Backends;
using CcDirector.Core.UnixPty;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Regression tests for the macOS/Linux PTY backend. The original implementation
/// spawned the child with redirected pipes and never attached the PTY subordinate, so the
/// child's stdin was not a terminal -- which made Claude Code drop into --print mode
/// and exit with "Input must be provided either through stdin or as a prompt argument".
/// The child MUST see a real TTY on stdin.
/// </summary>
[Collection("ProcessWideState")]
public class UnixPtyBackendTests
{
    private static bool OnUnix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task Start_ChildStdin_IsATty()
    {
        if (!OnUnix) return; // PTY backend only runs on macOS/Linux.

        using var backend = new UnixPtyBackend();
        // `test -t 0` succeeds only when stdin is a terminal.
        backend.Start("/bin/sh", "-c \"test -t 0 && echo TTY_YES || echo TTY_NO\"",
            Path.GetTempPath(), 120, 30);

        var output = await WaitForOutputAsync(backend, "TTY_", TimeSpan.FromSeconds(5));

        Assert.Contains("TTY_YES", output);
        Assert.DoesNotContain("TTY_NO", output);
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task Start_ChildSeesRequestedWindowSize()
    {
        if (!OnUnix) return; // PTY backend only runs on macOS/Linux.

        using var backend = new UnixPtyBackend();
        // `stty size` prints "<rows> <cols>" from the kernel winsize. Regression for the
        // arm64 macOS variadic-ioctl bug, where TIOCSWINSZ wrote garbage (e.g. 62608x28302),
        // making Claude Code draw a 28000-wide rule that wrapped into a wall of lines.
        backend.Start("/bin/sh", "-c \"stty size\"", Path.GetTempPath(), 120, 30);

        var output = await WaitForOutputAsync(backend, "30 120", TimeSpan.FromSeconds(5));

        Assert.Contains("30 120", output);
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task Start_ChildReceivesWorkingDirectory()
    {
        if (!OnUnix) return; // PTY backend only runs on macOS/Linux.

        var tempDir = Path.Combine(Path.GetTempPath(), "ccd-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using var backend = new UnixPtyBackend();
            backend.Start("/bin/sh", "-c pwd", tempDir, 120, 30);

            var output = await WaitForOutputAsync(backend, Path.GetFileName(tempDir), TimeSpan.FromSeconds(5));

            // macOS may resolve /var -> /private/var; compare on the unique leaf name.
            Assert.Contains(Path.GetFileName(tempDir), output);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Regression for the macOS garbled-terminal bug: the spawned child must ACQUIRE the
    /// pseudo-terminal as its controlling terminal. On macOS that requires the ccd-ptyshim
    /// (an explicit TIOCSCTTY ioctl in the child); on Linux the kernel grants it on open.
    /// Without a controlling terminal the terminal has no foreground process group, resizes
    /// deliver the window-change signal to nobody, and the agent paints at its spawn width
    /// forever - which every terminal view renders as overlapping garbage.
    /// </summary>
    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task Start_ChildAcquiresControllingTerminal()
    {
        if (!OnUnix) return; // PTY backend only runs on macOS/Linux.

        using var backend = new UnixPtyBackend();
        // `ps -o tty= -p $$` prints the controlling terminal ("ttys012" on macOS, "pts/0"
        // on Linux) or question marks when there is none.
        backend.Start("/bin/sh", "-c \"echo CTTY=$(ps -o tty= -p $$)\"", Path.GetTempPath(), 120, 30);

        var output = await WaitForOutputAsync(backend, "CTTY=", TimeSpan.FromSeconds(5));

        Assert.Matches("CTTY=(ttys|pts)", output);
        Assert.DoesNotContain("CTTY=?", output);
    }

    /// <summary>
    /// The end-to-end consequence of the controlling terminal: a PTY resize must reach the
    /// child as the window-change signal, so the agent repaints at the new geometry. This is
    /// what Windows ConPty does natively and what the macOS backend silently dropped.
    /// </summary>
    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task Resize_DeliversWindowChangeSignalToChild()
    {
        if (!OnUnix) return; // PTY backend only runs on macOS/Linux.

        using var backend = new UnixPtyBackend();
        backend.Start("/bin/sh",
            "-c \"trap 'echo GOT_WINCH' WINCH; echo TRAP_READY; while :; do sleep 0.1; done\"",
            Path.GetTempPath(), 120, 30);

        var ready = await WaitForOutputAsync(backend, "TRAP_READY", TimeSpan.FromSeconds(5));
        Assert.Contains("TRAP_READY", ready);

        backend.Resize(100, 40);

        var output = await WaitForOutputAsync(backend, "GOT_WINCH", TimeSpan.FromSeconds(5));
        Assert.Contains("GOT_WINCH", output);
    }

    private static async Task<string> WaitForOutputAsync(UnixPtyBackend backend, string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var text = backend.Buffer?.DumpAll() is { Length: > 0 } bytes
                ? System.Text.Encoding.UTF8.GetString(bytes)
                : string.Empty;
            if (text.Contains(marker))
                return text;
            await Task.Delay(50);
        }
        return backend.Buffer?.DumpAll() is { Length: > 0 } final
            ? System.Text.Encoding.UTF8.GetString(final)
            : string.Empty;
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("--session-id abc123", new[] { "--session-id", "abc123" })]
    [InlineData("  --resume   1234   ", new[] { "--resume", "1234" })]
    [InlineData("-c \"echo hello world\"", new[] { "-c", "echo hello world" })]
    [InlineData("--path '/some/dir with spaces/x'", new[] { "--path", "/some/dir with spaces/x" })]
    public void TokenizeArgs_SplitsCorrectly(string input, string[] expected)
    {
        var actual = UnixProcessHost.TokenizeArgs(input);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Regression for the macOS session-history loss: a Director launched from inside a
    /// Claude Code session inherits CLAUDE_CODE_CHILD_SESSION=1, and if that leaks into a
    /// spawned agent, interactive Claude Code treats itself as a subagent and silently
    /// never writes its session transcript. The child environment must strip the same
    /// parent-agent variables the Windows ProcessHost strips.
    /// </summary>
    [Fact]
    public void BuildEnvironment_StripsParentAgentVariables()
    {
        var poisoned = new Dictionary<string, string?>
        {
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_CHILD_SESSION"] = "1",
            ["CLAUDE_CODE_SESSION_ID"] = "11111111-2222-3333-4444-555555555555",
            ["CLAUDE_CODE_ENTRYPOINT"] = "cli",
            ["CODEX_THREAD_ID"] = "abc",
            ["GIT_EDITOR"] = "true",
        };
        var originals = new Dictionary<string, string?>();
        foreach (var kv in poisoned)
        {
            originals[kv.Key] = Environment.GetEnvironmentVariable(kv.Key);
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }
        Environment.SetEnvironmentVariable("CODEX_HOME", "/tmp/codex-home-test");
        try
        {
            var env = UnixProcessHost.BuildEnvironment(null)
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

            Assert.DoesNotContain(env, e => e.StartsWith("CLAUDECODE=", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(env, e => e.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(env, e => e.StartsWith("CODEX_THREAD_ID=", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(env, e => e.StartsWith("GIT_EDITOR=", StringComparison.OrdinalIgnoreCase));
            // CODEX_HOME is the deliberate exception, and TERM is forced.
            Assert.Contains(env, e => e.StartsWith("CODEX_HOME=", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("TERM=xterm-256color", env);
        }
        finally
        {
            foreach (var kv in originals)
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
        }
    }

    [Fact]
    public void BuildEnvironment_AppliesCallerOverrides()
    {
        var env = UnixProcessHost.BuildEnvironment(new Dictionary<string, string>
        {
            ["CC_SESSION_ID"] = "test-session-id",
        }).Where(e => e is not null).Select(e => e!).ToList();

        Assert.Contains("CC_SESSION_ID=test-session-id", env);
        Assert.Null(UnixProcessHost.BuildEnvironment(null)[^1]); // null-terminated
    }
}
