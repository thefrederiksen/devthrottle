using System.Diagnostics;
using System.Text;
using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;
using static CcDirector.Core.UnixPty.UnixNativeMethods;

namespace CcDirector.Core.UnixPty;

/// <summary>
/// Spawns a process attached to a UnixPseudoConsole, manages async I/O loops
/// for reading output and monitoring process exit.
/// </summary>
public sealed class UnixProcessHost : IDisposable
{
    private readonly UnixPseudoConsole _console;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private Task? _drainTask;
    private Task? _exitMonitorTask;
    private bool _disposed;
    private bool _started;

    public event Action<int>? OnExited;

    public int ProcessId => _process?.Id ?? 0;

    public UnixProcessHost(UnixPseudoConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    /// <summary>
    /// Spawn a process attached to the pseudo console.
    /// Uses .NET Process class with redirected I/O connected to PTY.
    /// </summary>
    public void Start(string exePath, string args, string? workingDir, Dictionary<string, string>? environmentVars = null)
    {
        if (_started) throw new InvalidOperationException("UnixProcessHost already started.");
        _started = true;

        // Build command line
        // On Unix, we need to use the shell or exec directly
        // Using Process class for simpler management

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Set TERM environment variable for proper terminal behavior
            Environment = { ["TERM"] = "xterm-256color" }
        };

        // Inject extra environment variables (e.g. CC_SESSION_ID)
        if (environmentVars != null)
        {
            foreach (var kvp in environmentVars)
                startInfo.Environment[kvp.Key] = kvp.Value;
        }

        _process = new Process { StartInfo = startInfo };
        _process.EnableRaisingEvents = true;

        if (!_process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {exePath}");
        }

        System.Diagnostics.Debug.WriteLine($"[UnixProcessHost] Started PID {_process.Id}: {exePath} {args}");
    }

    /// <summary>
    /// Start the drain loop that reads output from the process into the buffer.
    /// Combines stdout and stderr into the buffer.
    /// </summary>
    public void StartDrainLoop(CircularTerminalBuffer buffer)
    {
        if (_process == null) throw new InvalidOperationException("Process not started.");

        _drainTask = Task.Run(async () =>
        {
            var readBuf = new byte[8192];
            var stdoutStream = _process.StandardOutput.BaseStream;
            var stderrStream = _process.StandardError.BaseStream;

            try
            {
                // Read from both stdout and stderr concurrently
                var stdoutTask = DrainStreamAsync(stdoutStream, buffer, "stdout");
                var stderrTask = DrainStreamAsync(stderrStream, buffer, "stderr");

                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UnixProcessHost] Drain error: {ex.Message}");
            }
        });
    }

    private async Task DrainStreamAsync(Stream stream, CircularTerminalBuffer buffer, string name)
    {
        var readBuf = new byte[8192];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(readBuf, 0, readBuf.Length, _cts.Token);
                if (bytesRead == 0) break; // EOF
                buffer.Write(readBuf.AsSpan(0, bytesRead));
            }
        }
        catch (IOException)
        {
            // Pipe broken - process exited
        }
        catch (ObjectDisposedException)
        {
            // Stream disposed during shutdown
        }
        catch (OperationCanceledException)
        {
            // Expected during cancellation
        }
    }

    /// <summary>
    /// Start monitoring for process exit.
    /// </summary>
    public void StartExitMonitor()
    {
        if (_process == null) throw new InvalidOperationException("Process not started.");

        _exitMonitorTask = Task.Run(async () =>
        {
            try
            {
                await _process.WaitForExitAsync(_cts.Token);
                OnExited?.Invoke(_process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                // OnExited runs subscriber code that may throw. Isolate it.
                FileLog.Write($"[UnixProcessHost] StartExitMonitor failed: {ex.Message}");
            }
        });
    }

    /// <summary>Write raw bytes to the process input.</summary>
    public void Write(byte[] data)
    {
        if (_disposed || _process == null) return;

        try
        {
            System.Diagnostics.Debug.WriteLine($"[UnixProcessHost Write] {data.Length} bytes");
            _process.StandardInput.BaseStream.Write(data, 0, data.Length);
            _process.StandardInput.BaseStream.Flush();
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UnixProcessHost Write] IOException: {ex.Message}");
        }
        catch (ObjectDisposedException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UnixProcessHost Write] ObjectDisposedException: {ex.Message}");
        }
    }

    /// <summary>Write raw bytes to the process input (async).</summary>
    public async Task WriteAsync(byte[] data)
    {
        if (_disposed || _process == null) return;

        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(data);
            await _process.StandardInput.BaseStream.FlushAsync();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Graceful shutdown: send Ctrl+C (SIGINT), wait, then terminate if needed.
    /// </summary>
    public async Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        if (_disposed || _process == null) return;

        // Send Ctrl+C (works for processes reading from terminal)
        try
        {
            Write(new byte[] { 0x03 }); // ETX = Ctrl+C
        }
        catch { }

        // Also try SIGTERM on Unix
        try
        {
            if (!_process.HasExited)
            {
                kill(_process.Id, SIGTERM);
            }
        }
        catch { }

        // Wait for process to exit
        var exitTask = _exitMonitorTask ?? Task.CompletedTask;
        var completed = await Task.WhenAny(exitTask, Task.Delay(timeoutMs));

        if (completed != exitTask && _process != null && !_process.HasExited)
        {
            // Process didn't exit in time - force kill
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        // Wait for tasks to finish (with timeout)
        try
        {
            var tasks = new List<Task>();
            if (_drainTask != null) tasks.Add(_drainTask);
            if (_exitMonitorTask != null) tasks.Add(_exitMonitorTask);
            if (tasks.Count > 0)
                Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(3));
        }
        catch (AggregateException) { }

        // Dispose process
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { }

            _process.Dispose();
            _process = null;
        }

        // Dispose the pseudo console
        _console.Dispose();

        _cts.Dispose();
    }
}
