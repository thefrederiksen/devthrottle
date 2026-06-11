using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Tools;

/// <summary>
/// Bounded, streaming process execution for <c>POST /tools/run</c> (issue #328).
///
/// Runs ONE already-resolved tool binary with an argument list (never a shell string) and yields
/// <see cref="ToolRunChunk"/>s as the process produces output: one <c>start</c> chunk (pid), a
/// <c>stdout</c>/<c>stderr</c> chunk per line AS IT ARRIVES (so a caller sees output before the
/// process exits), and exactly one terminal <c>exit</c> chunk (exitCode/timedOut/durationMs).
///
/// Safety: this class trusts its caller to have resolved the exe path through the tool catalog
/// allowlist (<see cref="ToolCatalogService"/>) - it validates the file exists and executes it
/// directly with <c>UseShellExecute=false</c>. On timeout the ENTIRE process tree is killed and
/// the exit chunk reports a distinct timeout error - the bound is real, never advisory.
/// </summary>
public sealed class ToolRunner
{
    /// <summary>
    /// Run the binary and stream its output. The returned sequence always ends with exactly one
    /// <c>exit</c> chunk, including on timeout (timedOut=true, process tree killed).
    /// </summary>
    /// <param name="exePath">Catalog-resolved binary path. Must exist.</param>
    /// <param name="args">Argument list passed verbatim (no shell).</param>
    /// <param name="workingDirectory">Working directory; defaults to the binary's directory. Must exist when provided.</param>
    /// <param name="timeout">Wall-clock bound; must be positive.</param>
    /// <param name="ct">Caller cancellation (e.g. the HTTP request aborting) - also kills the process tree.</param>
    public async IAsyncEnumerable<ToolRunChunk> RunStreamAsync(
        string exePath,
        IReadOnlyList<string> args,
        string? workingDirectory,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("Executable path is required", nameof(exePath));
        if (args is null)
            throw new ArgumentNullException(nameof(args));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive");
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Tool binary not found: {exePath}", exePath);
        if (workingDirectory is not null && !Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"Working directory not found: {workingDirectory}");

        FileLog.Write($"[ToolRunner] RunStream: exe={exePath}, args=[{string.Join(" ", args)}], cwd={workingDirectory ?? "(exe dir)"}, timeout={timeout.TotalSeconds:0}s");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            // The cc-* tools emit UTF-8 (rich/python and .NET alike); decoding with the default
            // ANSI codepage mangles box-drawing/non-ASCII output, so pin UTF-8 end-to-end.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        // Not `using`: if the consumer abandons the stream early the supervisor still owns the
        // process; the supervisor disposes it after the exit chunk is written.
        var process = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }
        var pid = process.Id;

        yield return new ToolRunChunk { Stream = ToolRunChunk.StreamStart, Pid = pid };

        // Unbounded is safe here: tool output is line-buffered text and the consumer drains
        // continuously; bounding would let a stalled HTTP client wedge the pumps mid-kill.
        var channel = Channel.CreateUnbounded<ToolRunChunk>();

        // Reading the redirected streams to EOF (instead of OutputDataReceived events) guarantees
        // the pumps complete only when the pipes close, so no output is lost at exit/kill time.
        var stdoutPump = PumpAsync(process.StandardOutput, ToolRunChunk.StreamStdout, channel.Writer);
        var stderrPump = PumpAsync(process.StandardError, ToolRunChunk.StreamStderr, channel.Writer);
        var supervisor = SuperviseAsync(process, exePath, timeout, sw, stdoutPump, stderrPump, channel.Writer, ct);

        await foreach (var chunk in channel.Reader.ReadAllAsync(CancellationToken.None))
            yield return chunk;

        await supervisor; // surfaces supervisor faults instead of swallowing them
    }

    /// <summary>Read one redirected stream line-by-line into the channel until the pipe closes.</summary>
    private static async Task PumpAsync(StreamReader reader, string streamName, ChannelWriter<ToolRunChunk> writer)
    {
        while (await reader.ReadLineAsync() is { } line)
            await writer.WriteAsync(new ToolRunChunk { Stream = streamName, Data = line });
    }

    /// <summary>
    /// Wait for exit within the bound, kill the whole tree on timeout/cancellation, drain the
    /// pumps, then emit the single terminal exit chunk and complete the channel.
    /// </summary>
    private static async Task SuperviseAsync(
        Process process,
        string exePath,
        TimeSpan timeout,
        Stopwatch sw,
        Task stdoutPump,
        Task stderrPump,
        ChannelWriter<ToolRunChunk> writer,
        CancellationToken ct)
    {
        try
        {
            await SuperviseCoreAsync(process, exePath, timeout, sw, stdoutPump, stderrPump, writer, ct);
        }
        catch (Exception ex)
        {
            // Surface the fault THROUGH the channel: ReadAllAsync rethrows it to the consumer, so a
            // supervisor failure can never silently hang the stream with an incomplete writer.
            FileLog.Write($"[ToolRunner] Supervise FAILED: exe={exePath}: {ex.Message}");
            writer.TryComplete(ex);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task SuperviseCoreAsync(
        Process process,
        string exePath,
        TimeSpan timeout,
        Stopwatch sw,
        Task stdoutPump,
        Task stderrPump,
        ChannelWriter<ToolRunChunk> writer,
        CancellationToken ct)
    {
        var timedOut = false;
        var cancelled = false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            cancelled = ct.IsCancellationRequested;
            KillTree(process, exePath);
        }

        // The pipes close when the process (tree) is gone, so the pumps always complete; awaiting
        // them guarantees every produced line was written to the channel before the exit chunk.
        await stdoutPump;
        await stderrPump;
        sw.Stop();

        ToolRunChunk exitChunk;
        if (timedOut)
        {
            FileLog.Write($"[ToolRunner] RunStream TIMEOUT: exe={exePath}, after={timeout.TotalSeconds:0}s, pid={SafePid(process)}");
            exitChunk = new ToolRunChunk
            {
                Stream = ToolRunChunk.StreamExit,
                TimedOut = true,
                DurationMs = sw.ElapsedMilliseconds,
                Error = $"timed out after {timeout.TotalSeconds:0}s; process tree killed",
            };
        }
        else if (cancelled)
        {
            FileLog.Write($"[ToolRunner] RunStream CANCELLED: exe={exePath}, after={sw.ElapsedMilliseconds}ms");
            exitChunk = new ToolRunChunk
            {
                Stream = ToolRunChunk.StreamExit,
                TimedOut = false,
                DurationMs = sw.ElapsedMilliseconds,
                Error = "cancelled by caller; process tree killed",
            };
        }
        else
        {
            var exitCode = process.ExitCode;
            FileLog.Write($"[ToolRunner] RunStream done: exe={exePath}, exitCode={exitCode}, durationMs={sw.ElapsedMilliseconds}");
            exitChunk = new ToolRunChunk
            {
                Stream = ToolRunChunk.StreamExit,
                ExitCode = exitCode,
                TimedOut = false,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }

        await writer.WriteAsync(exitChunk);
        writer.Complete();
    }

    private static void KillTree(Process process, string exePath)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // The tree raced us to exit - nothing left to kill. Logged so a real kill failure is visible.
            FileLog.Write($"[ToolRunner] KillTree: exe={exePath} already gone ({ex.Message})");
        }
    }

    private static string SafePid(Process process)
    {
        try { return process.Id.ToString(); }
        catch (InvalidOperationException) { return "?"; }
    }
}
