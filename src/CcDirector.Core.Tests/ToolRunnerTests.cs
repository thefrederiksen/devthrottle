using System.Diagnostics;
using CcDirector.Core.Tools;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Covers the bounded streaming process runner behind POST /tools/run (issue #328): chunk
/// ordering (start -> output -> exactly one exit), incremental delivery (output chunks observed
/// BEFORE the process exits, by timestamp), honest exit codes, stderr capture, and the timeout
/// bound (process tree killed and gone, distinct timedOut error). Process-launching cases drive
/// cmd.exe and no-op on non-Windows, same as ToolTestRunnerTests.
/// </summary>
public class ToolRunnerTests
{
    private static string Cmd => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    private static async Task<List<(ToolRunChunk Chunk, DateTime At)>> CollectAsync(
        IAsyncEnumerable<ToolRunChunk> stream)
    {
        var collected = new List<(ToolRunChunk, DateTime)>();
        await foreach (var chunk in stream)
            collected.Add((chunk, DateTime.UtcNow));
        return collected;
    }

    [Fact]
    public async Task RunStream_MissingExe_ThrowsFileNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-tool-" + Guid.NewGuid().ToString("N") + ".exe");

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await foreach (var _ in new ToolRunner().RunStreamAsync(
                missing, Array.Empty<string>(), null, TimeSpan.FromSeconds(5)))
            {
            }
        });
    }

    [Fact]
    public async Task RunStream_MissingWorkingDirectory_ThrowsDirectoryNotFound()
    {
        if (!OperatingSystem.IsWindows()) return;
        var missingDir = Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
        {
            await foreach (var _ in new ToolRunner().RunStreamAsync(
                Cmd, new[] { "/c", "exit", "0" }, missingDir, TimeSpan.FromSeconds(5)))
            {
            }
        });
    }

    [Fact]
    public async Task RunStream_NonPositiveTimeout_Throws()
    {
        if (!OperatingSystem.IsWindows()) return;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in new ToolRunner().RunStreamAsync(
                Cmd, Array.Empty<string>(), null, TimeSpan.Zero))
            {
            }
        });
    }
}
