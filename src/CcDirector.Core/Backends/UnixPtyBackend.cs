using System.Runtime.InteropServices;
using System.Text;
using CcDirector.Core.Input;
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

        // Strip a single trailing submit newline before evaluating "is this multi-line / large?".
        // Callers (MainWindow, REST API, Quick Actions) sometimes append "\n" as a submit
        // signal -- we don't want that to trip the multi-line heuristic and punt short
        // prompts through a temp file. The backend sends LF explicitly below.
        var textForCheck = text.TrimEnd('\r', '\n');

        string textToSend;
        if (LargeInputHandler.IsLargeInput(textForCheck) && !string.IsNullOrEmpty(_workingDir))
        {
            // Write to temp file and send @filepath
            var tempPath = LargeInputHandler.CreateTempFile(textForCheck, _workingDir);
            textToSend = $"@{tempPath}";
            FileLog.Write($"[UnixPtyBackend] Large input ({textForCheck.Length} chars), using temp file reference: {textToSend}");
        }
        else
        {
            textToSend = textForCheck;
        }

        var textBytes = Encoding.UTF8.GetBytes(textToSend);
        _processHost.Write(textBytes);

        // Brief delay so TUI processes text before Enter
        await Task.Delay(50);

        // Submit with CR (0x0D), NOT LF. Pressing Enter in a terminal sends CR, and
        // TUIs like Claude Code treat CR as "submit" but LF as "insert a newline".
        // This matches ConPtyBackend on Windows; sending LF here only added a blank
        // line in Claude's input box instead of submitting the prompt.
        _processHost.Write(new byte[] { 0x0D }); // CR = Enter/submit

        // Same unreliable @-reference Enter as ConPtyBackend (issue #212): watch for
        // submission evidence and keep nudging while the TUI stays dead.
        if (textToSend.StartsWith('@'))
            await AtReferenceSubmitVerifier.EnsureSubmittedAsync(Buffer, Write, textToSend);
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
