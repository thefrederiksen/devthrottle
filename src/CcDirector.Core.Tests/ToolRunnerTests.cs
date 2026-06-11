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

    [Fact]
    public async Task RunStream_SlowOutput_ChunksArriveBeforeProcessExit()
    {
        if (!OperatingSystem.IsWindows()) return;

        // First line prints immediately; the process then stays alive ~2s before the second line.
        var chunks = await CollectAsync(new ToolRunner().RunStreamAsync(
            Cmd, new[] { "/c", "echo first & ping -n 3 127.0.0.1 >nul & echo second" },
            null, TimeSpan.FromSeconds(30)));

        var first = chunks.First(c =>
            c.Chunk.Stream == ToolRunChunk.StreamStdout &&
            c.Chunk.Data is not null && c.Chunk.Data.StartsWith("first"));
        var exit = chunks.Single(c => c.Chunk.Stream == ToolRunChunk.StreamExit);

        // Streaming, not buffer-then-dump: the first output line was observed well before exit.
        var leadMs = (exit.At - first.At).TotalMilliseconds;
        Assert.True(leadMs > 1000,
            $"first stdout chunk should arrive >1s before the exit chunk (was {leadMs:0}ms)");
    }

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
    public async Task RunStream_Timeout_KillsProcessTreeAndReportsDistinctTimeoutError()
    {
        if (!OperatingSystem.IsWindows()) return;

        // cmd spawns ping as a child: the tree (cmd + ping) must be gone after the kill.
        var chunks = await CollectAsync(new ToolRunner().RunStreamAsync(
            Cmd, new[] { "/c", "ping -n 60 127.0.0.1 >nul" }, null, TimeSpan.FromSeconds(2)));

        var start = chunks.First(c => c.Chunk.Stream == ToolRunChunk.StreamStart);
        Assert.NotNull(start.Chunk.Pid);

        var exit = chunks.Single(c => c.Chunk.Stream == ToolRunChunk.StreamExit);
        Assert.True(exit.Chunk.TimedOut);
        Assert.Null(exit.Chunk.ExitCode);
        Assert.NotNull(exit.Chunk.Error);
        Assert.Contains("timed out after 2s", exit.Chunk.Error);

        // PID absence: the launched cmd.exe is gone (Kill(entireProcessTree) took the tree down).
        // Short poll: Windows may keep the kernel process object findable for a few ms post-kill.
        var pid = start.Chunk.Pid.Value;
        var gone = false;
        for (var i = 0; i < 50 && !gone; i++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                gone = p.HasExited;
            }
            catch (ArgumentException)
            {
                gone = true;
            }
            if (!gone) await Task.Delay(100);
        }
        Assert.True(gone, $"pid {pid} still alive after the timeout kill");
    }

    [Fact]
    public async Task RunStream_Timeout_DurationReflectsBoundNotCommand()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sw = Stopwatch.StartNew();
        var chunks = await CollectAsync(new ToolRunner().RunStreamAsync(
            Cmd, new[] { "/c", "ping -n 60 127.0.0.1 >nul" }, null, TimeSpan.FromSeconds(2)));
        sw.Stop();

        Assert.Single(chunks, c => c.Chunk.Stream == ToolRunChunk.StreamExit);
        // The bound is real: a 60s command came back shortly after the 2s timeout, not after 60s.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"timed-out run should return promptly (took {sw.Elapsed.TotalSeconds:0.0}s)");
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
