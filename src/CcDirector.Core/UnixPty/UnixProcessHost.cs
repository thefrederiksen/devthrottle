using System.Runtime.InteropServices;
using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;
using static CcDirector.Core.UnixPty.UnixNativeMethods;

namespace CcDirector.Core.UnixPty;

/// <summary>
/// Spawns a process attached to a <see cref="UnixPseudoConsole"/>, with the child's
/// stdin/stdout/stderr bound to the PTY subordinate and a controlling terminal established,
/// then manages async I/O loops for reading output and monitoring process exit.
///
/// The previous implementation launched the child via the .NET <see cref="System.Diagnostics.Process"/>
/// class with redirected pipes, which left the PTY subordinate completely unattached: the
/// child's stdin was an anonymous pipe, not a terminal. Modern Claude Code detects a
/// non-TTY stdin and switches to non-interactive (<c>--print</c>) mode, then exits with
/// "Input must be provided either through stdin or as a prompt argument". This host
/// instead binds the child directly to the PTY (the macOS/Linux analogue of the Windows
/// ConPty backend), so interactive TUIs run correctly.
/// </summary>
public sealed class UnixProcessHost : IDisposable
{
    private readonly UnixPseudoConsole _console;
    private readonly CancellationTokenSource _cts = new();
    private int _pid;
    private int _exitCode;
    private volatile bool _hasExited;
    private Task? _drainTask;
    private Task? _exitMonitorTask;
    private bool _disposed;
    private bool _started;
    private readonly object _exitLock = new();
    private bool _exitRaised;

    // Optional raw-PTY capture for diagnosing terminal-rendering issues. When the
    // environment variable CCD_PTY_CAPTURE is set, every byte read from the PTY
    // master (exactly what the child wrote, before any ANSI parsing) is appended
    // to that file. Set CCD_PTY_CAPTURE to a path, or to "1"/"auto" for a default
    // under the temp dir. Diagnostics only -- a no-op when the var is unset.
    private FileStream? _captureStream;

    public event Action<int>? OnExited;

    public int ProcessId => _pid;

    public UnixProcessHost(UnixPseudoConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    /// <summary>
    /// Spawn a process attached to the pseudo console via posix_spawn. The child
    /// runs as a new session leader with the PTY subordinate as its controlling terminal
    /// and stdin/stdout/stderr.
    /// </summary>
    public void Start(string exePath, string args, string? workingDir, Dictionary<string, string>? environmentVars = null)
    {
        if (_started) throw new InvalidOperationException("UnixProcessHost already started.");
        _started = true;

        FileLog.Write($"[UnixProcessHost] Start: exe={exePath}, args=\"{args}\", workDir={workingDir}");

        var resolvedExe = ResolveExecutable(exePath);

        // argv must start with the program name and be null-terminated.
        var argv = new List<string?> { resolvedExe };
        argv.AddRange(TokenizeArgs(args));
        argv.Add(null);

        var envp = BuildEnvironment(environmentVars);

        // The subordinate device path: the child opens this fresh (as session leader) so it
        // becomes the controlling terminal, then we dup it onto stdin/stdout/stderr.
        var ptsPtr = ptsname(_console.MasterFd);
        if (ptsPtr == IntPtr.Zero)
            throw new InvalidOperationException("ptsname returned null for the PTY master fd.");
        var ptsPath = Marshal.PtrToStringAnsi(ptsPtr)
            ?? throw new InvalidOperationException("Could not marshal PTY subordinate device path.");

        // Caller-allocated, zeroed buffers for the opaque spawn handles.
        IntPtr fileActions = AllocZeroed(1024);
        IntPtr attr = AllocZeroed(1024);
        try
        {
            Check(posix_spawnattr_init(attr), "posix_spawnattr_init");
            Check(posix_spawnattr_setflags(attr, POSIX_SPAWN_SETSID), "posix_spawnattr_setflags");

            Check(posix_spawn_file_actions_init(fileActions), "posix_spawn_file_actions_init");
            // stdin = freshly opened controlling terminal; stdout/stderr dup from it.
            Check(posix_spawn_file_actions_addopen(fileActions, STDIN_FILENO, ptsPath, O_RDWR, 0),
                "posix_spawn_file_actions_addopen");
            Check(posix_spawn_file_actions_adddup2(fileActions, STDIN_FILENO, STDOUT_FILENO),
                "posix_spawn_file_actions_adddup2(stdout)");
            Check(posix_spawn_file_actions_adddup2(fileActions, STDIN_FILENO, STDERR_FILENO),
                "posix_spawn_file_actions_adddup2(stderr)");
            // Close the inherited master/subordinate fds in the child.
            posix_spawn_file_actions_addclose(fileActions, _console.MasterFd);
            posix_spawn_file_actions_addclose(fileActions, _console.SubordinateFd);
            if (!string.IsNullOrEmpty(workingDir))
                Check(posix_spawn_file_actions_addchdir_np(fileActions, workingDir),
                    "posix_spawn_file_actions_addchdir_np");

            int rc = posix_spawnp(out _pid, resolvedExe, fileActions, attr, argv.ToArray(), envp);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"posix_spawnp failed for '{resolvedExe}' (rc={rc}, errno={Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            posix_spawn_file_actions_destroy(fileActions);
            posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(fileActions);
            Marshal.FreeHGlobal(attr);
        }

        // The child holds its own copy of the subordinate; the parent must release the
        // subordinate or reads on the master will never see EOF when the child exits.
        _console.CloseSubordinate();

        FileLog.Write($"[UnixProcessHost] Started PID {_pid}: {resolvedExe} {args}");
    }

    /// <summary>
    /// Start the drain loop that reads output from the PTY master into the buffer.
    /// </summary>
    public void StartDrainLoop(CircularTerminalBuffer buffer)
    {
        if (!_started) throw new InvalidOperationException("Process not started.");

        OpenCaptureIfRequested();

        _drainTask = Task.Run(() =>
        {
            var readBuf = new byte[8192];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int n = _console.Read(readBuf);
                    if (n > 0)
                    {
                        _captureStream?.Write(readBuf, 0, n);
                        _captureStream?.Flush();
                        buffer.Write(readBuf.AsSpan(0, n));
                    }
                    else
                    {
                        // n == 0 from a blocking master read means the subordinate side
                        // closed (child exited) -- EOF.
                        break;
                    }
                }
            }
            catch (ObjectDisposedException) { /* console disposed during shutdown */ }
            catch (IOException ex)
            {
                FileLog.Write($"[UnixProcessHost] Drain loop ended: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Start monitoring for process exit via waitpid.
    /// </summary>
    public void StartExitMonitor()
    {
        if (!_started) throw new InvalidOperationException("Process not started.");

        _exitMonitorTask = Task.Run(() =>
        {
            int rc = waitpid(_pid, out int status, 0);
            if (rc == -1)
            {
                // Already reaped or invalid -- treat as exit 0.
                FileLog.Write($"[UnixProcessHost] waitpid({_pid}) returned -1, errno={Marshal.GetLastWin32Error()}");
                RaiseExit(0);
                return;
            }

            int code = WIFEXITED(status) ? WEXITSTATUS(status)
                     : WIFSIGNALED(status) ? 128 + WTERMSIG(status)
                     : 0;
            FileLog.Write($"[UnixProcessHost] PID {_pid} exited: code={code} (status={status})");
            RaiseExit(code);
        });
    }

    private void RaiseExit(int code)
    {
        lock (_exitLock)
        {
            if (_exitRaised) return;
            _exitRaised = true;
            _exitCode = code;
            _hasExited = true;
        }

        try { OnExited?.Invoke(code); }
        catch (Exception ex)
        {
            // OnExited runs subscriber code that may throw. Isolate it.
            FileLog.Write($"[UnixProcessHost] OnExited handler threw: {ex.Message}");
        }
    }

    /// <summary>Write raw bytes to the process input (PTY master).</summary>
    public void Write(byte[] data)
    {
        if (_disposed || _hasExited) return;
        try
        {
            _console.Write(data);
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

    /// <summary>Write raw bytes to the process input (async wrapper).</summary>
    public Task WriteAsync(byte[] data)
    {
        Write(data);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Graceful shutdown: send Ctrl+C, then SIGTERM, then SIGKILL if needed.
    /// </summary>
    public async Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        if (_disposed || !_started || _hasExited) return;

        // Ctrl+C through the terminal (lets the TUI clean up).
        try { Write(new byte[] { 0x03 }); } catch { }

        // SIGTERM as a backstop.
        try { if (!_hasExited) kill(_pid, SIGTERM); } catch { }

        var exitTask = _exitMonitorTask ?? Task.CompletedTask;
        var completed = await Task.WhenAny(exitTask, Task.Delay(timeoutMs));

        if (completed != exitTask && !_hasExited)
        {
            try { kill(_pid, SIGKILL); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        // Force-kill a still-running child so waitpid returns and the subordinate closes,
        // which in turn unblocks the drain loop's master read.
        if (_started && !_hasExited && _pid > 0)
        {
            try { kill(_pid, SIGKILL); } catch { }
        }

        // Closing the master also unblocks any in-flight blocking read.
        _console.Dispose();

        try
        {
            var tasks = new List<Task>();
            if (_drainTask != null) tasks.Add(_drainTask);
            if (_exitMonitorTask != null) tasks.Add(_exitMonitorTask);
            if (tasks.Count > 0)
                Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(3));
        }
        catch (AggregateException) { }

        try { _captureStream?.Dispose(); } catch { }
        _captureStream = null;

        _cts.Dispose();
    }

    /// <summary>
    /// Open the raw-PTY capture file if CCD_PTY_CAPTURE is set. Diagnostics only:
    /// records the exact bytes the child writes to the PTY, before ANSI parsing,
    /// so terminal-rendering bugs can be reproduced from the real stream.
    /// </summary>
    private void OpenCaptureIfRequested()
    {
        var setting = Environment.GetEnvironmentVariable("CCD_PTY_CAPTURE");
        if (string.IsNullOrWhiteSpace(setting)) return;

        var path = setting is "1" or "auto"
            ? Path.Combine(Path.GetTempPath(), $"ccd-pty-capture-{_pid}.bin")
            : setting;

        _captureStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        FileLog.Write($"[UnixProcessHost] Raw PTY capture enabled -> {path}");
    }

    /// <summary>
    /// Resolve an executable name to an absolute path. If <paramref name="exe"/> already
    /// contains a path separator it is returned as-is. Otherwise PATH and common macOS
    /// install locations are searched, so the agent launches even when the Director was
    /// started from Finder/launchd (where the shell PATH is not inherited).
    /// </summary>
    private static string ResolveExecutable(string exe)
    {
        if (string.IsNullOrEmpty(exe) || exe.Contains('/'))
            return exe;

        var dirs = new List<string>();
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
            dirs.AddRange(pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, ".local", "bin"));
        dirs.Add("/opt/homebrew/bin");
        dirs.Add("/usr/local/bin");
        dirs.Add("/usr/bin");
        dirs.Add("/bin");

        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
            {
                FileLog.Write($"[UnixProcessHost] Resolved '{exe}' -> {candidate}");
                return candidate;
            }
        }

        // Fall back to the bare name; posix_spawnp will search PATH itself.
        return exe;
    }

    /// <summary>
    /// Build the child environment: inherit the parent's, force TERM, apply any
    /// caller overrides, and strip CLAUDECODE so Claude does not treat the session
    /// as a nested Claude Code invocation. Returns a null-terminated KEY=VALUE array.
    /// </summary>
    private static string?[] BuildEnvironment(Dictionary<string, string>? overrides)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
            env[(string)kv.Key] = kv.Value?.ToString() ?? string.Empty;

        env["TERM"] = "xterm-256color";
        env.Remove("CLAUDECODE");

        if (overrides != null)
            foreach (var kv in overrides)
                env[kv.Key] = kv.Value;

        var result = new string?[env.Count + 1];
        int i = 0;
        foreach (var kv in env)
            result[i++] = $"{kv.Key}={kv.Value}";
        result[i] = null;
        return result;
    }

    /// <summary>
    /// Split a command-line argument string into tokens, honoring single and double
    /// quotes and backslash escapes (matching common POSIX shell word-splitting for
    /// the simple argument strings the Director builds).
    /// </summary>
    internal static List<string> TokenizeArgs(string args)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return tokens;

        var current = new System.Text.StringBuilder();
        bool inToken = false;
        char quote = '\0';

        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];

            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; }
                else if (c == '\\' && quote == '"' && i + 1 < args.Length) { current.Append(args[++i]); }
                else { current.Append(c); }
                continue;
            }

            switch (c)
            {
                case ' ':
                case '\t':
                    if (inToken) { tokens.Add(current.ToString()); current.Clear(); inToken = false; }
                    break;
                case '"':
                case '\'':
                    quote = c; inToken = true;
                    break;
                case '\\':
                    if (i + 1 < args.Length) current.Append(args[++i]);
                    inToken = true;
                    break;
                default:
                    current.Append(c); inToken = true;
                    break;
            }
        }

        if (inToken) tokens.Add(current.ToString());
        return tokens;
    }

    private static IntPtr AllocZeroed(int size)
    {
        IntPtr p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i += 8)
            Marshal.WriteInt64(p, i, 0);
        return p;
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0)
            throw new InvalidOperationException($"{what} failed (rc={rc}, errno={Marshal.GetLastWin32Error()}).");
    }
}
