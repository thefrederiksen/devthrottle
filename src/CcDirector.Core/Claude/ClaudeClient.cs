using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Client for the Claude Code CLI. Provides typed methods for headless/automation
/// usage via the -p (print mode) flag.
///
/// All methods are one-shot: they spawn a process, send a prompt, and return the result.
/// For long-lived interactive sessions, use ClaudeProcess directly.
/// </summary>
public sealed class ClaudeClient
{
    private readonly string _claudePath;
    private readonly string _workingDirectory;
    private readonly int _defaultTimeoutMs;

    /// <summary>
    /// Create a new ClaudeClient.
    /// </summary>
    /// <param name="claudePath">Absolute path to claude.exe.</param>
    /// <param name="workingDirectory">Working directory for Claude processes.</param>
    /// <param name="defaultTimeoutMs">Default timeout for all operations (default: 60s).</param>
    public ClaudeClient(string claudePath, string workingDirectory, int defaultTimeoutMs = 60_000)
    {
        if (string.IsNullOrWhiteSpace(claudePath))
            throw new ArgumentException("Claude path is required.", nameof(claudePath));
        if (!File.Exists(claudePath))
            throw new FileNotFoundException($"Claude executable not found: {claudePath}", claudePath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("Working directory is required.", nameof(workingDirectory));
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"Working directory not found: {workingDirectory}");

        _claudePath = claudePath;
        _workingDirectory = workingDirectory;
        _defaultTimeoutMs = defaultTimeoutMs;

        FileLog.Write($"[ClaudeClient] Created: claude={claudePath}, workDir={workingDirectory}, timeout={defaultTimeoutMs}ms");
    }

    /// <summary>
    /// Send a prompt to Claude and get a response with full metadata.
    /// Uses --output-format json internally to capture session ID, cost, and usage.
    /// </summary>
    public async Task<ClaudeResponse> ChatAsync(
        string prompt,
        ClaudeOptions? options = null,
        CancellationToken ct = default)
    {
        FileLog.Write($"[ClaudeClient] ChatAsync: promptLen={prompt.Length}, model={options?.Model}");

        var args = ClaudeArgBuilder.BuildChatArgs(options);
        var (stdout, stderr, exitCode) = await RunProcessAsync(args, prompt, options?.TimeoutMs, ct);

        if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException($"Claude exited with code {exitCode}: {stderr.Trim()}");

        var response = ClaudeResponseParser.ParseJsonResponse(stdout.Trim(), exitCode);

        FileLog.Write($"[ClaudeClient] ChatAsync completed: sessionId={response.SessionId}, cost=${response.TotalCostUsd}");
        return response;
    }

    /// <summary>
    /// Send a prompt to Claude with a JSON schema constraint, returning a typed result.
    /// The schema is derived automatically from type T using .NET JsonSchemaExporter.
    /// </summary>
    public async Task<ClaudeResponse<T>> ChatAsync<T>(
        string prompt,
        ClaudeOptions? options = null,
        CancellationToken ct = default)
    {
        FileLog.Write($"[ClaudeClient] ChatAsync<{typeof(T).Name}>: promptLen={prompt.Length}");

        var args = ClaudeArgBuilder.BuildStructuredArgs(typeof(T), options);
        var (stdout, stderr, exitCode) = await RunProcessAsync(args, prompt, options?.TimeoutMs, ct);

        if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException($"Claude exited with code {exitCode}: {stderr.Trim()}");

        var baseResponse = ClaudeResponseParser.ParseJsonResponse(stdout.Trim(), exitCode);
        var typedResult = ClaudeResponseParser.DeserializeResult<T>(baseResponse.Result);

        var response = new ClaudeResponse<T>
        {
            Result = typedResult,
            RawResult = baseResponse.Result,
            SessionId = baseResponse.SessionId,
            Subtype = baseResponse.Subtype,
            IsError = baseResponse.IsError,
            TotalCostUsd = baseResponse.TotalCostUsd,
            NumTurns = baseResponse.NumTurns,
            DurationMs = baseResponse.DurationMs,
            Usage = baseResponse.Usage,
            ExitCode = exitCode,
        };

        FileLog.Write($"[ClaudeClient] ChatAsync<{typeof(T).Name}> completed: sessionId={response.SessionId}");
        return response;
    }

    /// <summary>
    /// Resume an existing Claude session with a new prompt.
    /// </summary>
    public async Task<ClaudeResponse> ResumeAsync(
        string sessionId,
        string prompt,
        ClaudeOptions? options = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID is required.", nameof(sessionId));

        FileLog.Write($"[ClaudeClient] ResumeAsync: sessionId={sessionId}, promptLen={prompt.Length}");

        var args = ClaudeArgBuilder.BuildResumeArgs(sessionId, options);
        var (stdout, stderr, exitCode) = await RunProcessAsync(args, prompt, options?.TimeoutMs, ct);

        if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException($"Claude exited with code {exitCode}: {stderr.Trim()}");

        var response = ClaudeResponseParser.ParseJsonResponse(stdout.Trim(), exitCode);

        FileLog.Write($"[ClaudeClient] ResumeAsync completed: sessionId={response.SessionId}, cost=${response.TotalCostUsd}");
        return response;
    }

    /// <summary>
    /// Send a prompt to Claude and stream response events as they arrive.
    /// Uses --output-format stream-json --verbose.
    /// The returned ClaudeStreamResult MUST be disposed to clean up the process.
    /// </summary>
    public ClaudeStreamResult StreamAsync(
        string prompt,
        ClaudeOptions? options = null,
        CancellationToken ct = default)
    {
        FileLog.Write($"[ClaudeClient] StreamAsync: promptLen={prompt.Length}");

        var args = ClaudeArgBuilder.BuildStreamArgs(options);
        var timeout = options?.TimeoutMs ?? _defaultTimeoutMs;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var process = StartProcess(args);

        // Write prompt and close stdin
        process.StandardInput.Write(prompt);
        process.StandardInput.Close();

        var events = ReadStreamEvents(process, cts.Token);

        var result = new ClaudeStreamResult(process, events, cts);
        return result;
    }

    /// <summary>
    /// Find claude.exe on PATH or in common npm install locations.
    /// Returns null if not found.
    /// </summary>
    public static string? FindClaudePath()
    {
        FileLog.Write("[ClaudeClient] FindClaudePath: searching...");

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir, "claude.exe");
            if (File.Exists(full))
            {
                FileLog.Write($"[ClaudeClient] FindClaudePath: found at {full}");
                return full;
            }
        }

        // Check common npm global install location
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var npmPath = Path.Combine(localAppData, "npm", "claude.exe");
        if (File.Exists(npmPath))
        {
            FileLog.Write($"[ClaudeClient] FindClaudePath: found at {npmPath}");
            return npmPath;
        }

        // Check user .local/bin (common on Windows for Claude Code)
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localBinPath = Path.Combine(userProfile, ".local", "bin", "claude.exe");
        if (File.Exists(localBinPath))
        {
            FileLog.Write($"[ClaudeClient] FindClaudePath: found at {localBinPath}");
            return localBinPath;
        }

        FileLog.Write("[ClaudeClient] FindClaudePath: NOT FOUND");
        return null;
    }

    private async Task<(string Stdout, string Stderr, int ExitCode)> RunProcessAsync(
        string arguments,
        string stdinText,
        int? timeoutMs,
        CancellationToken ct)
    {
        var effectiveTimeout = timeoutMs ?? _defaultTimeoutMs;

        FileLog.Write($"[ClaudeClient] RunProcessAsync: args=\"{arguments}\", timeout={effectiveTimeout}ms");

        var process = StartProcess(arguments);

        FileLog.Write($"[ClaudeClient] Process started: PID={process.Id}");

        // Write prompt and close stdin
        await process.StandardInput.WriteAsync(stdinText.AsMemory(), ct);
        process.StandardInput.Close();

        // Drain stdout and stderr concurrently to prevent pipe deadlock
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        // Wait for exit with combined timeout + cancellation
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (not user cancellation)
            FileLog.Write($"[ClaudeClient] Process FAILED: timed out after {effectiveTimeout}ms, killing PID={process.Id}");
            KillProcess(process);
            throw new TimeoutException($"Claude process timed out after {effectiveTimeout}ms.");
        }
        catch (OperationCanceledException)
        {
            // User cancellation
            // not-an-error: the caller cancelled the request
            FileLog.Write($"[ClaudeClient] Process cancelled, killing PID={process.Id}");
            KillProcess(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var exitCode = process.ExitCode;

        FileLog.Write($"[ClaudeClient] RunProcessAsync completed: exitCode={exitCode}, stdoutLen={stdout.Length}, stderrLen={stderr.Length}");

        process.Dispose();
        return (stdout, stderr, exitCode);
    }

    private Process StartProcess(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _claudePath,
            Arguments = arguments,
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        // Prevent nested-session detection when launched from within Claude Code
        psi.Environment.Remove("CLAUDECODE");

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    private static async IAsyncEnumerable<ClaudeStreamEvent> ReadStreamEvents(
        Process process,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await process.StandardOutput.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
                break; // EOF

            var evt = ClaudeResponseParser.ParseStreamLine(line);
            if (evt is not null)
                yield return evt;
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
    }
}
