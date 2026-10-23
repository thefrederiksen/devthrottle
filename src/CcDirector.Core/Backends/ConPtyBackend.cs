using System.Text;
using CcDirector.Core.ConPty;
using CcDirector.Core.Drivers;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Backends;

/// <summary>
/// ConPTY-based session backend. Uses Windows Pseudo Console for terminal emulation.
/// Process output is captured to a CircularTerminalBuffer for WPF rendering.
/// </summary>
public sealed class ConPtyBackend : ISessionBackend
{
    private PseudoConsole? _console;
    private ProcessHost? _processHost;
    private CircularTerminalBuffer? _buffer;
    private bool _disposed;
    private string _status = "Not Started";
    private string _workingDir = string.Empty;

    public int ProcessId => _processHost?.ProcessId ?? 0;
    public string Status => _status;
    public bool IsRunning => _processHost != null && !HasExited;
    public bool HasExited => _processHost == null || _status.StartsWith("Exited");
    public CircularTerminalBuffer? Buffer => _buffer;
    public string WorkingDirectory => _workingDir;

    /// <inheritdoc />
    public string PromptFileDirectory { get; private set; } = string.Empty;

    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;

    /// <summary>
    /// Create a ConPtyBackend with the specified buffer size.
    /// </summary>
    /// <param name="bufferSizeBytes">Size of the circular terminal buffer in bytes.</param>
    public ConPtyBackend(int bufferSizeBytes = 2 * 1024 * 1024)
    {
        _buffer = new CircularTerminalBuffer(bufferSizeBytes);
    }

    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null)
    {
        if (_processHost != null)
            throw new InvalidOperationException("Backend already started.");

        _workingDir = workingDir;
        // The kit this session is started with decides where its prompt files go; no kit, today's .temp/.
        PromptFileDirectory = CcDirector.Core.Sessions.FactoryRunKit.PromptDirectoryFor(environmentVars);
        SetStatus("Starting...");

        // Create ConPTY with terminal dimensions
        _console = PseudoConsole.Create(cols, rows);

        // Record the spawn geometry before any output lands, so a replay knows the
        // width the very first bytes were emitted for (issue #1304).
        _buffer!.RecordResize(cols, rows);

        // Create process host
        _processHost = new ProcessHost(_console);
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
        if (_disposed || _processHost == null)
            throw TerminalWriteFailedException.Logged("ConPtyBackend", $"write {data.Length} byte(s)", NotRunningReason);
        _processHost.Write(data);
    }

    /// <summary>Why nothing can be written: the terminal was disposed, or its process was never started.</summary>
    private string NotRunningReason => _disposed ? "the terminal has been disposed" : "the terminal's process was never started";

    public async Task SendTextAsync(string text)
    {
        if (_disposed || _processHost == null)
            throw TerminalWriteFailedException.Logged("ConPtyBackend", $"submit a text of {text.Length} character(s)", NotRunningReason);
        await TerminalSubmit.SharedSubmitAsync(this, text, "ConPtyBackend");
    }

    public Task SendEnterAsync()
    {
        if (_disposed || _processHost == null)
            throw TerminalWriteFailedException.Logged("ConPtyBackend", "press Enter", NotRunningReason);
        _processHost.Write(new byte[] { 0x0D });
        return Task.CompletedTask;
    }

    public void Resize(short cols, short rows)
    {
        if (_disposed || _console == null)
            throw TerminalWriteFailedException.Logged("ConPtyBackend", $"resize to {cols}x{rows}", NotRunningReason);
        // Mark the geometry change at the current byte position BEFORE the console
        // resizes, so the repaint bytes the resize triggers fall after the mark and
        // are replayed at the new width (issue #1304).
        _buffer?.RecordResize(cols, rows);
        try
        {
            _console.Resize(cols, rows);
        }
        catch (Exception ex)
        {
            // A resize the console refused is logged and reaches the caller (issue #4380); it used to be swallowed here.
            throw TerminalWriteFailedException.Logged("ConPtyBackend", $"resize to {cols}x{rows}", "the console refused the resize", ex);
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
        FileLog.Write($"[ConPtyBackend] ProcessExited: pid={ProcessId}, exitCode={exitCode}");
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
