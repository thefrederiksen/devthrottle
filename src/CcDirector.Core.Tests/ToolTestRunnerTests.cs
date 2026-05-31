using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Covers the test runner: the OnPath file-existence check, the manifest-only safety guard (it
/// refuses any test not declared for the tool), honest skip when a binary is not built, and real
/// process launch outcomes (exit code, ExpectContains). Process-launching cases drive cmd.exe and
/// no-op on non-Windows since the product's tools are Windows binaries.
/// </summary>
public class ToolTestRunnerTests : IDisposable
{
    private readonly string _dir;

    public ToolTestRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ToolRunnerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* temp dir cleanup is best-effort */ }
    }

    private static string Cmd => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    // A descriptor whose binary is cmd.exe and whose declared test set is exactly the one passed in,
    // so the runner's "test must belong to the tool" guard is satisfied with the same instance.
    private static ToolDescriptor CmdTool(ToolTest test)
        => new("stub", "Test", "stub tool", null, Cmd, isBuilt: true, new[] { test });

    [Fact]
    public async Task RunTest_OnPathBinaryExists_Passes()
    {
        var onPath = new ToolTest(ToolTestKind.OnPath, Array.Empty<string>(), null);
        var tool = new ToolDescriptor("stub", "Test", "x", null, Cmd, isBuilt: true, new[] { onPath });

        var result = await new ToolTestRunner().RunTestAsync(tool, onPath);

        Assert.True(result.Passed);
        Assert.Null(result.ExitCode); // OnPath launches no process
    }

    [Fact]
    public async Task RunTest_OnPathBinaryMissing_Fails()
    {
        var missing = Path.Combine(_dir, "nope.exe");
        var onPath = new ToolTest(ToolTestKind.OnPath, Array.Empty<string>(), null);
        var tool = new ToolDescriptor("stub", "Test", "x", null, missing, isBuilt: false, new[] { onPath });

        var result = await new ToolTestRunner().RunTestAsync(tool, onPath);

        Assert.False(result.Passed);
    }

    [Fact]
    public async Task RunTest_TestNotDeclaredForTool_Throws()
    {
        var declared = new ToolTest(ToolTestKind.OnPath, Array.Empty<string>(), null);
        var foreign = new ToolTest(ToolTestKind.Smoke, new[] { "rm", "-rf" }, null);
        var tool = new ToolDescriptor("stub", "Test", "x", null, Cmd, isBuilt: true, new[] { declared });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ToolTestRunner().RunTestAsync(tool, foreign));
    }

    [Fact]
    public async Task RunTest_VersionBinaryNotBuilt_ReportsSkippedFail()
    {
        var missing = Path.Combine(_dir, "nope.exe");
        var version = new ToolTest(ToolTestKind.Version, new[] { "--version" }, null);
        var tool = new ToolDescriptor("stub", "Test", "x", null, missing, isBuilt: false, new[] { version });

        var result = await new ToolTestRunner().RunTestAsync(tool, version);

        Assert.False(result.Passed);
        Assert.Contains("not built", result.Message);
    }

    [Fact]
    public async Task RunTest_ProcessExitsZero_Passes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var smoke = new ToolTest(ToolTestKind.Smoke, new[] { "/c", "exit", "0" }, null);

        var result = await new ToolTestRunner().RunTestAsync(CmdTool(smoke), smoke);

        Assert.True(result.Passed);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunTest_ProcessExitsNonZero_FailsWithRealExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        var smoke = new ToolTest(ToolTestKind.Smoke, new[] { "/c", "exit", "2" }, null);

        var result = await new ToolTestRunner().RunTestAsync(CmdTool(smoke), smoke);

        Assert.False(result.Passed);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("exit 2", result.Message);
    }

    [Fact]
    public async Task RunTest_ExpectContainsPresent_Passes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var smoke = new ToolTest(ToolTestKind.Smoke, new[] { "/c", "echo", "HELLO-WORLD" }, "hello-world");

        var result = await new ToolTestRunner().RunTestAsync(CmdTool(smoke), smoke);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task RunTest_ExpectContainsMissing_Fails()
    {
        if (!OperatingSystem.IsWindows()) return;
        var smoke = new ToolTest(ToolTestKind.Smoke, new[] { "/c", "echo", "HELLO" }, "goodbye");

        var result = await new ToolTestRunner().RunTestAsync(CmdTool(smoke), smoke);

        Assert.False(result.Passed);
        Assert.Contains("missing", result.Message);
    }

    [Fact]
    public async Task RunAllForTool_RunsEveryDeclaredCheck()
    {
        if (!OperatingSystem.IsWindows()) return;
        var onPath = new ToolTest(ToolTestKind.OnPath, Array.Empty<string>(), null);
        var smoke = new ToolTest(ToolTestKind.Smoke, new[] { "/c", "exit", "0" }, null);
        var tool = new ToolDescriptor("stub", "Test", "x", null, Cmd, isBuilt: true, new[] { onPath, smoke });

        var results = await new ToolTestRunner().RunAllForToolAsync(tool);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Passed));
    }
}
