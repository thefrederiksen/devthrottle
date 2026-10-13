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
