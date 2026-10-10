using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Result of starting a Claude process and capturing its session ID.
/// </summary>
public sealed class ClaudeStartResult
{
    public required Process Process { get; init; }
    public required string SessionId { get; init; }
}

/// <summary>
/// Helper for starting Claude processes and extracting the session ID from
/// the stream-json init message that Claude emits before any API call.
///
/// Key insight: with --output-format stream-json --verbose, Claude emits
/// a {"type":"system","subtype":"init","session_id":"..."} message on stdout
/// as one of the very first lines, before any API call happens.
///
/// Usage:
///   var result = await ClaudeProcess.StartAndGetSessionIdAsync(claudePath, args, workDir);
///   // result.SessionId is immediately available
///   // result.Process is still running -- caller owns its lifecycle
/// </summary>
public static class ClaudeProcess
{
    /// <summary>
    /// Start a Claude process in stream-json verbose mode and extract the session ID
    /// from the init message emitted on stdout before any API call.
    ///
    /// The returned Process is still running. The caller is responsible for writing to
    /// stdin, reading remaining stdout, and disposing the process.
    /// </summary>
    /// <param name="claudePath">Path to claude.exe</param>
    /// <param name="arguments">CLI arguments (should NOT include -p, --output-format, or --verbose -- those are added automatically)</param>
    /// <param name="workingDirectory">Working directory for the process</param>
    /// <param name="timeoutMs">Max time to wait for the init message</param>
    /// <returns>The started process and its session ID</returns>
    public static async Task<ClaudeStartResult> StartAndGetSessionIdAsync(
        string claudePath,
        string arguments,
        string workingDirectory,
        int timeoutMs = 10_000)
    {
        FileLog.Write($"[ClaudeProcess] StartAndGetSessionIdAsync: args=\"{arguments}\", workDir={workingDirectory}");

        var fullArgs = $"-p --output-format stream-json --verbose {arguments}";

        var psi = new ProcessStartInfo
        {
            FileName = claudePath,
            Arguments = fullArgs,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        // Prevent nested-session detection if launched from within Claude Code
        psi.Environment.Remove("CLAUDECODE");

        var process = new Process { StartInfo = psi };
        process.Start();

        FileLog.Write($"[ClaudeProcess] Process started: PID={process.Id}, args=\"{fullArgs}\"");

        var sessionId = await ReadSessionIdFromStream(process.StandardOutput, timeoutMs);

        FileLog.Write($"[ClaudeProcess] Session ID captured: {sessionId}");

        return new ClaudeStartResult
        {
            Process = process,
            SessionId = sessionId
        };
    }

    /// <summary>
    /// Run a one-shot Claude command and return the session ID.
    /// The process is awaited to completion. Stdout/stderr are drained to
    /// prevent pipe deadlock.
    /// </summary>
    public static async Task<string> GetSessionIdAsync(
        string claudePath,
        string arguments,
        string workingDirectory,
        string stdinText,
        int timeoutMs = 30_000)
    {
        FileLog.Write($"[ClaudeProcess] GetSessionIdAsync: args=\"{arguments}\"");

        var fullArgs = $"-p --output-format stream-json --verbose {arguments}";

        var psi = new ProcessStartInfo
        {
            FileName = claudePath,
            Arguments = fullArgs,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        psi.Environment.Remove("CLAUDECODE");

        var process = new Process { StartInfo = psi };
        process.Start();

        FileLog.Write($"[ClaudeProcess] Process started: PID={process.Id}");

        // Write stdin and close
        await process.StandardInput.WriteAsync(stdinText);
        process.StandardInput.Close();

        // Read session ID from the stream
        var sessionId = await ReadSessionIdFromStream(process.StandardOutput, timeoutMs);

        FileLog.Write($"[ClaudeProcess] Session ID captured: {sessionId}");

        // Drain remaining stdout and stderr in background to prevent pipe deadlock
        var drainStdout = process.StandardOutput.ReadToEndAsync();
        var drainStderr = process.StandardError.ReadToEndAsync();

        // Wait for process to finish
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            FileLog.Write($"[ClaudeProcess] Process FAILED: timed out, killing PID={process.Id}");
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException ex)
            {
                // Process already exited between timeout check and kill
                // not-an-error: the process had already exited, which is what the kill wanted
                FileLog.Write($"[ClaudeProcess] Kill after timeout failed (already exited): {ex.Message}");
            }
        }

        await Task.WhenAll(drainStdout, drainStderr);
        process.Dispose();
        return sessionId;
    }

    private static async Task<string> ReadSessionIdFromStream(StreamReader stdout, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);

        while (!cts.Token.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await stdout.ReadLineAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line == null)
                throw new InvalidOperationException("Claude process closed stdout before emitting session ID.");

            if (!line.StartsWith('{'))
                continue;

            // Look for any JSON message with a session_id field.
            // The very first messages are hook_started with session_id,
            // followed by {"type":"system","subtype":"init","session_id":"..."}
            var sessionId = ExtractSessionId(line);
            if (sessionId != null)
                return sessionId;
        }

        throw new TimeoutException($"Timed out after {timeoutMs}ms waiting for session ID from Claude.");
    }

    private static string? ExtractSessionId(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            if (root.TryGetProperty("session_id", out var sessionIdProp))
            {
                var value = sessionIdProp.GetString();
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
        }
        catch (JsonException)
        {
            FileLog.Write($"[ClaudeProcess] ExtractSessionId FAILED: invalid JSON, line length={jsonLine.Length} (the agent's output is not logged in an error line)");
        }

        return null;
    }
}
