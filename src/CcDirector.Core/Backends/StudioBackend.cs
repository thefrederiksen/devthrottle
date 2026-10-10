using System.Diagnostics;
using System.Text;
using CcDirector.Core.Claude;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Backends;

/// <summary>
/// Studio mode backend. Spawns 'claude -p --output-format stream-json' per prompt,
/// parses stdout JSONL into StreamMessage events for the card-based UI.
/// Multi-turn is achieved by using --resume with the Claude session ID on subsequent prompts.
/// Each prompt spawns a new process, closes stdin (EOF), drains the stream-json response,
/// then the process exits. The next prompt uses --resume to continue the conversation.
///
/// DISABLED (2026-03-10): Hidden from UI but code preserved for future use.
/// - Slash commands are uncertain in -p mode
/// - Hooks don't fire (no named pipe for lifecycle events)
/// - Terminal mode + Clean tab already provides the same card-based view via JSONL file polling
/// To re-enable: add Mode radio buttons back to NewSessionDialog.xaml and restore IsStudioMode logic.
/// </summary>
public sealed class StudioBackend : ISessionBackend
{
    private string _executable = string.Empty;
    private string _baseArgs = string.Empty;
    private string _workingDir = string.Empty;
    private Dictionary<string, string>? _environmentVars;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private Process? _currentProcess;
    private bool _disposed;
    private bool _initialized;
    private string _status = "Not Started";

    private readonly List<StreamMessage> _messages = new();
    private readonly object _messagesLock = new();
    private int _lineCount;
    private bool _firstPromptSent;

    /// <summary>The Claude session ID, extracted from stream-json init message or set externally.</summary>
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

    /// <summary>Studio mode has no terminal buffer -- rendering is done via StreamMessages.</summary>
    public CircularTerminalBuffer? Buffer => null;

    public event Action<string>? StatusChanged;
#pragma warning disable CS0067 // Required by ISessionBackend but not used in per-prompt model
    public event Action<int>? ProcessExited;
#pragma warning restore CS0067

    /// <summary>Fires for each parsed StreamMessage from stdout.</summary>
    public event Action<StreamMessage>? StreamMessageReceived;

    /// <summary>Fires when a prompt completes (process exits after responding).</summary>
    public event Action? PromptCompleted;

    /// <summary>All messages received so far (thread-safe copy).</summary>
    public List<StreamMessage> GetMessages()
    {
        lock (_messagesLock)
            return new List<StreamMessage>(_messages);
    }

    /// <summary>
    /// Initialize the backend. Stores config but does NOT spawn a process.
    /// The first process is spawned when SendTextAsync is called.
    /// </summary>
    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null)
    {
        FileLog.Write($"[StudioBackend] Start: executable={executable}, args={args}, workingDir={workingDir}");

        if (_initialized)
            throw new InvalidOperationException("Backend already initialized.");

        _executable = executable;
        _baseArgs = args;
        _workingDir = workingDir;
        _environmentVars = environmentVars;
        _initialized = true;

        // Extract session ID from args if present (--session-id <id>)
        var sessionIdFlag = "--session-id ";
        var idx = args.IndexOf(sessionIdFlag, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var start = idx + sessionIdFlag.Length;
            var end = args.IndexOf(' ', start);
            ClaudeSessionId = end >= 0 ? args[start..end] : args[start..];
            FileLog.Write($"[StudioBackend] Extracted ClaudeSessionId from args: {ClaudeSessionId}");
        }

        SetStatus("Ready");
    }

    /// <summary>Write is not meaningful for Studio mode per-prompt pattern.</summary>
    public void Write(byte[] data)
    {
        // No-op
    }

    /// <summary>
    /// Send a prompt to Claude. Spawns a new 'claude -p --output-format stream-json' process,
    /// writes the prompt to stdin, closes stdin (signals EOF), and drains the stream-json response.
    /// Subsequent prompts use --resume to continue the conversation.
    /// </summary>
    public async Task SendTextAsync(string text)
    {
        if (_disposed || !_initialized) return;

        if (!await _busy.WaitAsync(0))
        {
            FileLog.Write("[StudioBackend] SendTextAsync: busy, ignoring prompt");
            return;
        }

        try
        {
            SetStatus("Working...");
            FileLog.Write($"[StudioBackend] SendTextAsync: text=\"{(text.Length > 60 ? text[..60] + "..." : text)}\"");

            var args = BuildArgs();
            FileLog.Write($"[StudioBackend] Spawning: {_executable} {args}");

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

            // ProcessStartInfo inherits the Director's whole environment; take out what no session may
            // inherit (the parent agent's markers, the parent session's factory folder) before this
            // session's own variables go on top. One rule for every backend: InheritedSessionEnvironment.
            foreach (var inherited in psi.Environment.Keys.Where(InheritedSessionEnvironment.IsStripped).ToList())
                psi.Environment.Remove(inherited);

            if (_environmentVars != null)
            {
                foreach (var (key, value) in _environmentVars)
                    psi.Environment[key] = value;
            }

            var process = new Process { StartInfo = psi };
            process.Start();
            _currentProcess = process;

            FileLog.Write($"[StudioBackend] Process started, PID={process.Id}");

            // Write prompt to stdin and close it (signals EOF so -p mode starts processing)
            await process.StandardInput.WriteAsync(text);
            process.StandardInput.Close();

            // Drain stdout (stream-json lines) and stderr in parallel
            var stdoutTask = DrainStdoutAsync(process);
            var stderrTask = DrainStderrAsync(process);

            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);

            var exitCode = process.ExitCode;
            FileLog.Write($"[StudioBackend] Process exited with code {exitCode}");

            _currentProcess = null;
            process.Dispose();

            SetStatus("Ready");
            PromptCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[StudioBackend] SendTextAsync FAILED: {ex.Message}");
            _currentProcess = null;
            SetStatus("Ready");
        }
        finally
        {
            _busy.Release();
        }
    }

    public Task SendEnterAsync() => Task.CompletedTask;

    /// <summary>Resize is a no-op for Studio mode (no terminal).</summary>
    public void Resize(short cols, short rows) { }

    /// <summary>Kill the current process if running.</summary>
    public async Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        FileLog.Write($"[StudioBackend] GracefulShutdownAsync: timeoutMs={timeoutMs}");
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
            FileLog.Write($"[StudioBackend] GracefulShutdownAsync FAILED: {ex.Message}");
        }

        SetStatus("Stopped");
    }

    private string BuildArgs()
    {
        // First prompt: use --session-id to create the session (base args already have it)
        // Subsequent prompts: replace --session-id with --resume
        if (_firstPromptSent && ClaudeSessionId != null)
        {
            var sessionIdFlag = $"--session-id {ClaudeSessionId}";
            if (_baseArgs.Contains(sessionIdFlag))
                return _baseArgs.Replace(sessionIdFlag, $"--resume {ClaudeSessionId}");
        }

        _firstPromptSent = true;
        return _baseArgs;
    }

    private async Task DrainStdoutAsync(Process process)
    {
        try
        {
            var reader = process.StandardOutput;
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                FileLog.Write($"[StudioBackend.stdout] {(line.Length > 200 ? line[..200] + "..." : line)}");

                var msg = StreamMessageParser.ParseLine(line, _lineCount);
                _lineCount++;

                if (msg != null)
                {
                    lock (_messagesLock)
                        _messages.Add(msg);

                    StreamMessageReceived?.Invoke(msg);
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[StudioBackend] DrainStdoutAsync FAILED: {ex.Message}");
        }
    }

    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            var content = await process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(content))
                FileLog.Write($"[StudioBackend.stderr] {content}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[StudioBackend] DrainStderrAsync FAILED: {ex.Message}");
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
            if (_currentProcess is { HasExited: false })
            {
                _currentProcess.Kill(entireProcessTree: true);
            }
        }
        catch { /* best effort */ }

        _currentProcess?.Dispose();
        _currentProcess = null;
        _busy.Dispose();
    }
}
