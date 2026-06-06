using System.Text;
using CcDirector.Core.ConPty;
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
        SetStatus("Starting...");

        // Create ConPTY with terminal dimensions
        _console = PseudoConsole.Create(cols, rows);

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
        if (_disposed || _processHost == null) return;
        _processHost.Write(data);
    }

    public async Task SendTextAsync(string text)
    {
        if (_disposed || _processHost == null) return;

        // Strip a single trailing submit newline before evaluating "is this multi-line / large?".
        // Callers (MainWindow, REST API, Quick Actions) sometimes append "\n" as a submit
        // signal -- we don't want that to trip the multi-line heuristic and punt short
        // prompts through a temp file. The backend sends CR explicitly below.
        var textForCheck = text.TrimEnd('\r', '\n');

        string textToSend;
        if (LargeInputHandler.IsLargeInput(textForCheck) && !string.IsNullOrEmpty(_workingDir))
        {
            // Write to temp file and send @relative/path forward-slash form.
            // Claude's @-reference parser treats backslashes as escapes, so a Windows
            // path with backslashes (D:\Repo\.temp\file.txt) was rejected silently and
            // the prompt never submitted. Make the path relative to the working dir
            // when possible and force forward slashes.
            var tempPath = LargeInputHandler.CreateTempFile(textForCheck, _workingDir);
            var relRef = MakeAtReference(tempPath, _workingDir);
            textToSend = $"@{relRef}";
            FileLog.Write($"[ConPtyBackend] Large input ({textForCheck.Length} chars), using temp file reference: {textToSend}");
        }
        else
        {
            textToSend = textForCheck;
        }

        var textBytes = Encoding.UTF8.GetBytes(textToSend);
        _processHost.Write(textBytes);

        // Brief delay so TUI processes text before Enter
        await Task.Delay(50);

        // Send Enter (carriage return)
        _processHost.Write(new byte[] { 0x0D });

        // @-reference Enters are unreliable (autocomplete popup / claude's startup window
        // drops them) and a parked prompt looks like an idle session (issue #212). Watch
        // for submission evidence (the TUI streams after a real submit) and keep nudging
        // while it stays dead; an extra Enter after a real submit is a no-op.
        if (textToSend.StartsWith('@'))
            await AtReferenceSubmitVerifier.EnsureSubmittedAsync(_buffer, Write, textToSend);
    }

    /// <summary>
    /// Build a Claude-friendly @-reference target. Uses a relative path (relative to
    /// the session's working directory) when the temp file is inside that subtree, and
    /// always forces forward slashes so claude's tokenizer doesn't escape backslashes.
    /// </summary>
    private static string MakeAtReference(string absoluteTempPath, string workingDir)
    {
        var p = absoluteTempPath;
        if (!string.IsNullOrEmpty(workingDir))
        {
            try
            {
                var rel = Path.GetRelativePath(workingDir, absoluteTempPath);
                if (!rel.StartsWith("..", StringComparison.Ordinal))
                    p = rel;
            }
            catch { /* fall through, use the absolute path */ }
        }
        return p.Replace('\\', '/');
    }

    public Task SendEnterAsync()
    {
        if (_disposed || _processHost == null) return Task.CompletedTask;
        _processHost.Write(new byte[] { 0x0D });
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
