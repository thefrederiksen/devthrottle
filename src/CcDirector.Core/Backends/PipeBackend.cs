using System.Diagnostics;
using System.Text;
using CcDirector.Core.Memory;

namespace CcDirector.Core.Backends;

/// <summary>
/// Pipe mode backend. Spawns a new 'claude -p' process for each prompt.
/// Output is captured to a CircularTerminalBuffer. The process is short-lived
/// and exits after responding to each prompt.
/// </summary>
public sealed class PipeBackend : ISessionBackend
{
    private string _executable = string.Empty;
    private string _baseArgs = string.Empty;
    private string _workingDir = string.Empty;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private CircularTerminalBuffer? _buffer;
    private Process? _currentProcess;
    private bool _disposed;
    private bool _initialized;
    private string _status = "Not Started";

    /// <summary>The Claude session ID for resuming conversations.</summary>
    public string? ClaudeSessionId { get; set; }

    public int ProcessId
    {
        get
        {
            try { return _currentProcess?.Id ?? 0; }
            catch { return 0; }
        }
    }

    public string Status => _status;
    public bool IsRunning => _currentProcess != null && !_currentProcess.HasExited;
    public bool HasExited => _disposed;
    public CircularTerminalBuffer? Buffer => _buffer;

    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;

    /// <summary>
    /// Create a PipeBackend with the specified buffer size.
    /// </summary>
    public PipeBackend(int bufferSizeBytes = 2 * 1024 * 1024)
    {
        _buffer = new CircularTerminalBuffer(bufferSizeBytes);
    }

    /// <summary>
    /// Initialize the backend. For pipe mode, this stores the configuration.
    /// No process is spawned until SendTextAsync is called.
    /// </summary>
    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null)
    {
        if (_initialized)
            throw new InvalidOperationException("Backend already initialized.");

        if (string.IsNullOrEmpty(executable))
            throw new ArgumentException("Executable path required", nameof(executable));
        if (!Directory.Exists(workingDir))
            throw new DirectoryNotFoundException($"Working directory not found: {workingDir}");

        _executable = executable;
        _baseArgs = args;
        _workingDir = workingDir;
        _initialized = true;

        SetStatus("Ready");
    }

    /// <summary>
    /// Write is not supported for pipe mode. Use SendTextAsync instead.
    /// </summary>
    public void Write(byte[] data)
    {
        // No-op for pipe mode - can't write to a process that may not exist
        System.Diagnostics.Debug.WriteLine("[PipeBackend] Write() called but pipe mode doesn't support direct writes");
    }

    /// <summary>
    /// Send a prompt to Claude. Spawns a new 'claude -p' process, writes the prompt,
    /// and drains the response to the buffer.
    /// </summary>
    public async Task SendTextAsync(string text)
    {
        if (_disposed || !_initialized) return;

        // Only one prompt at a time
        if (!await _busy.WaitAsync(0))
        {
            System.Diagnostics.Debug.WriteLine("[PipeBackend] Busy, ignoring prompt");
            return;
        }

        try
        {
            SetStatus("Working...");

            // Echo prompt to buffer for visual feedback
            var echoBytes = Encoding.UTF8.GetBytes($"> {text}\n\n");
            _buffer?.Write(echoBytes);

            // Build args: -p [baseArgs] [--resume sessionId]
            var args = BuildArgs();

            // Clear ClaudeSessionId so the new session can be re-mapped
            ClaudeSessionId = null;

            var psi = new ProcessStartInfo
            {
                FileName = _executable,
                Arguments = args,
                WorkingDirectory = _workingDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            System.Diagnostics.Debug.WriteLine($"[PipeBackend] Starting: {_executable} {args}");

            var process = new Process { StartInfo = psi };
            process.Start();
            _currentProcess = process;

            // Write prompt to stdin and close it
            await process.StandardInput.WriteAsync(text);
            process.StandardInput.Close();

            // Drain stdout to buffer
            var stdoutTask = DrainStreamToBufferAsync(process.StandardOutput.BaseStream);

            // Drain stderr for logging
            var stderrTask = DrainStderrAsync(process.StandardError);

            // Wait for process to exit
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);

            var exitCode = process.ExitCode;
            System.Diagnostics.Debug.WriteLine($"[PipeBackend] Process exited with code {exitCode}");

            // Add separator after response
            _buffer?.Write(Encoding.UTF8.GetBytes("\n"));

            _currentProcess = null;
            process.Dispose();

            ProcessExited?.Invoke(exitCode);
            SetStatus("Ready");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PipeBackend] Error: {ex.Message}");
            var errorBytes = Encoding.UTF8.GetBytes($"\n[Error: {ex.Message}]\n");
            _buffer?.Write(errorBytes);
            _currentProcess = null;
            SetStatus("Ready");
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>
    /// Resize is not supported for pipe mode.
    /// </summary>
    public void Resize(short cols, short rows)
    {
        // No-op - pipe mode doesn't have a persistent terminal
    }

    /// <summary>
    /// Kill the current process if running.
    /// </summary>
    public async Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        if (_disposed) return;

        try
        {
            if (_currentProcess is { HasExited: false } proc)
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PipeBackend] Shutdown error: {ex.Message}");
        }

        SetStatus("Stopped");
    }

    private string BuildArgs()
    {
        var sb = new StringBuilder("-p");

        if (!string.IsNullOrWhiteSpace(_baseArgs))
        {
            sb.Append(' ');
            sb.Append(_baseArgs);
        }

        if (ClaudeSessionId != null)
        {
            sb.Append(" --resume ");
            sb.Append(ClaudeSessionId);
        }

        return sb.ToString();
    }

    private async Task DrainStreamToBufferAsync(Stream stream)
    {
        var buf = new byte[4096];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            _buffer?.Write(buf.AsSpan(0, bytesRead));
        }
    }

    private async Task DrainStderrAsync(StreamReader stderr)
    {
        var content = await stderr.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(content))
        {
            System.Diagnostics.Debug.WriteLine($"[PipeBackend.stderr] {content}");
        }
    }

    private void SetStatus(string status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_currentProcess is { HasExited: false } proc)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch { /* best effort */ }

        _currentProcess?.Dispose();
        _busy.Dispose();
        _buffer?.Dispose();

        _currentProcess = null;
        _buffer = null;
    }
}
