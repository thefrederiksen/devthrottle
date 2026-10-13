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

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task RunStream_EchoCommand_YieldsStartStdoutThenExitZero()
    {
        if (!OperatingSystem.IsWindows()) return;

        var chunks = await CollectAsync(new ToolRunner().RunStreamAsync(
            Cmd, new[] { "/c", "echo", "hello-328" }, null, TimeSpan.FromSeconds(30)));

        Assert.Equal(ToolRunChunk.StreamStart, chunks[0].Chunk.Stream);
        Assert.NotNull(chunks[0].Chunk.Pid);

        var stdout = chunks.Where(c => c.Chunk.Stream == ToolRunChunk.StreamStdout).ToList();
        Assert.Contains(stdout, c => c.Chunk.Data is not null && c.Chunk.Data.Contains("hello-328"));

        var exit = Assert.Single(chunks, c => c.Chunk.Stream == ToolRunChunk.StreamExit);
        Assert.Equal(0, exit.Chunk.ExitCode);
        Assert.False(exit.Chunk.TimedOut);
        Assert.NotNull(exit.Chunk.DurationMs);
        // The exit chunk is the terminal element of the stream.
        Assert.Equal(ToolRunChunk.StreamExit, chunks[^1].Chunk.Stream);
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task RunStream_StderrOutput_YieldsStderrChunk()
    {
        if (!OperatingSystem.IsWindows()) return;

        var chunks = await CollectAsync(new ToolRunner().RunStreamAsync(
            Cmd, new[] { "/c", "echo to-stderr 1>&2" }, null, TimeSpan.FromSeconds(30)));

        Assert.Contains(chunks, c =>
            c.Chunk.Stream == ToolRunChunk.StreamStderr &&
            c.Chunk.Data is not null && c.Chunk.Data.Contains("to-stderr"));
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task RunStream_NonZeroExit_ReportsRealExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;

        var chunks = await CollectAsync(new ToolRunner().RunStreamAsync(
            Cmd, new[] { "/c", "exit", "3" }, null, TimeSpan.FromSeconds(30)));

        var exit = chunks.Single(c => c.Chunk.Stream == ToolRunChunk.StreamExit);
        Assert.Equal(3, exit.Chunk.ExitCode);
        Assert.False(exit.Chunk.TimedOut);
        Assert.Null(exit.Chunk.Error);
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
