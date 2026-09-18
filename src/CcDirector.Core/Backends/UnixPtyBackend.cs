using System.Runtime.InteropServices;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.UnixPty;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Backends;

/// <summary>
/// Unix PTY-based session backend. Uses Unix pseudo-terminals for terminal emulation.
/// Process output is captured to a CircularTerminalBuffer for UI rendering.
/// Only used on macOS and Linux.
/// </summary>
public sealed class UnixPtyBackend : ISessionBackend
{
    private UnixPseudoConsole? _console;
    private UnixProcessHost? _processHost;
    private CircularTerminalBuffer? _buffer;
    private bool _disposed;
    private string _status = "Not Started";
    private string _workingDir = string.Empty;

    public int ProcessId => _processHost?.ProcessId ?? 0;

    /// <summary>
    /// The directory this session was started in. <see cref="ISessionBackend.WorkingDirectory"/>
    /// defaults to the empty string, and this backend never overrode it - so on macOS and Linux every
    /// session reported no working directory at all, while the Windows backend reported the real one.
    /// The value was already being stored here for the process start; only the accessor was missing.
    /// </summary>
    public string WorkingDirectory => _workingDir;

    public string Status => _status;
    public bool IsRunning => _processHost != null && !HasExited;
    public bool HasExited => _processHost == null || _status.StartsWith("Exited");
    public CircularTerminalBuffer? Buffer => _buffer;

    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;

    /// <summary>
    /// Create a UnixPtyBackend with the specified buffer size.
    /// </summary>
    /// <param name="bufferSizeBytes">Size of the circular terminal buffer in bytes.</param>
    public UnixPtyBackend(int bufferSizeBytes = 2 * 1024 * 1024)
    {
        // Verify we're on a Unix platform
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException(
                "UnixPtyBackend is only supported on macOS and Linux. Use ConPtyBackend on Windows.");
        }

        _buffer = new CircularTerminalBuffer(bufferSizeBytes);
    }

    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null)
    {
        if (_processHost != null)
            throw new InvalidOperationException("Backend already started.");

        _workingDir = workingDir;
        SetStatus("Starting...");

        // Create Unix PTY with terminal dimensions
        _console = UnixPseudoConsole.Create(cols, rows);

        // Record the spawn geometry before any output lands, so a replay knows the
        // width the very first bytes were emitted for (issue #1304).
        _buffer!.RecordResize(cols, rows);

        // Create process host
        _processHost = new UnixProcessHost(_console);
        _processHost.OnExited += OnProcessExited;

        // Start the process with optional extra environment variables
        _processHost.Start(executable, args, workingDir, environmentVars);

        // Start the drain loop to read output into buffer
        _processHost.StartDrainLoop(_buffer!);

        // Start monitoring for process exit
        _processHost.StartExitMonitor();

        SetStatus("Running");
    }

    public void Write(byte[] data)
    {
        if (_disposed || _processHost == null) return;
        _processHost.Write(data);
    }

    public async Task SendTextAsync(string text)
    {
        if (_disposed || _processHost == null) return;
        await TerminalSubmit.SharedSubmitAsync(this, text, "UnixPtyBackend");
    }

    public Task SendEnterAsync()
    {
        if (_disposed || _processHost == null) return Task.CompletedTask;
        _processHost.Write(new byte[] { 0x0D }); // CR = Enter/submit (matches Windows ConPty)
        return Task.CompletedTask;
    }

    public void Resize(short cols, short rows)
    {
        if (_disposed || _console == null) return;
        // Mark the geometry change at the current byte position BEFORE the console
        // resizes, so the repaint bytes the resize triggers fall after the mark and
        // are replayed at the new width (issue #1304).
        _buffer?.RecordResize(cols, rows);
        try
        {
            _console.Resize(cols, rows);
        }
        catch
        {
            // Resize may fail if console is disposed
        }
    }

    public async Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        if (_disposed || _processHost == null) return;

        SetStatus("Exiting...");
        await _processHost.GracefulShutdownAsync(timeoutMs);
    }

    private void OnProcessExited(int exitCode)
    {
        SetStatus($"Exited ({exitCode})");
        ProcessExited?.Invoke(exitCode);
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

        _processHost?.Dispose();
        _buffer?.Dispose();

        _processHost = null;
        _console = null;
        _buffer = null;
    }
}
