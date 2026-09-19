using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;
using Microsoft.Win32.SafeHandles;
using static CcDirector.Core.ConPty.NativeMethods;

namespace CcDirector.Core.ConPty;

/// <summary>
/// Spawns a process attached to a PseudoConsole, manages async I/O loops
/// for reading output and monitoring process exit.
/// </summary>
public sealed class ProcessHost : IDisposable
{
    private readonly PseudoConsole _console;
    private readonly CancellationTokenSource _cts = new();
    private PROCESS_INFORMATION _processInfo;
    private FileStream? _inputStream;
    private FileStream? _outputStream;
    private Task? _drainTask;
    private Task? _exitMonitorTask;
    private bool _disposed;
    private bool _started;

    public event Action<int>? OnExited;

    public int ProcessId => _processInfo.dwProcessId;
    public IntPtr ProcessHandle => _processInfo.hProcess;

    public ProcessHost(PseudoConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    /// <summary>
    /// Spawn a process attached to the pseudo console.
    /// </summary>
    public void Start(string exePath, string args, string? workingDir, Dictionary<string, string>? environmentVars = null)
    {
        if (_started) throw new InvalidOperationException("ProcessHost already started.");
        _started = true;

        string commandLine = string.IsNullOrEmpty(args) ? $"\"{exePath}\"" : $"\"{exePath}\" {args}";

        // Say what is about to be launched, BEFORE trying it. A launch that fails leaves nothing else
        // behind: the only other line is the caller's failure message, and for a whole release the
        // Claude path printed no executable at all, so there was no way to see from the outside what
        // CreateProcess was actually handed (devthrottle_internal issue #1050).
        //
        // Only the NAMES of the injected variables are recorded, never their values: this dictionary
        // carries agent credentials (CURSOR_API_KEY, COPILOT_GITHUB_TOKEN), and a launch line is not
        // the place to write a token to disk.
        var envNames = environmentVars is null || environmentVars.Count == 0
            ? "(none)"
            : string.Join(",", environmentVars.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        FileLog.Write($"[ProcessHost] Start: exe={exePath}, args={(string.IsNullOrEmpty(args) ? "(none)" : args)}, " +
                      $"workingDir={workingDir ?? "(inherited)"}, injectedEnv={envNames}");

        // Two-call pattern for InitializeProcThreadAttributeList
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        // First call is expected to fail with ERROR_INSUFFICIENT_BUFFER - we just need the size

        var attributeList = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");

            // Store handle in unmanaged memory so we can pass a pointer to it
            var handlePtr = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(handlePtr, _console.Handle);

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _console.Handle,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                Marshal.FreeHGlobal(handlePtr);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
            }

            Marshal.FreeHGlobal(handlePtr);

            var startupInfo = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    // When Director is launched with redirected stdio, Windows may copy those
                    // redirected parent handles into a ConPTY child unless std handles are
                    // explicitly controlled. Null STARTF_USESTDHANDLES forces the child to bind to
                    // the pseudoconsole instead of seeing non-terminal stdin/stdout.
                    dwFlags = STARTF_USESTDHANDLES
                },
                lpAttributeList = attributeList
            };

            // Build a Unicode environment block with CLAUDECODE removed so that
            // Claude Code launched inside the terminal does not see itself as nested.
            var envBlock = IntPtr.Zero;
            try
            {
                envBlock = BuildEnvironmentBlock(environmentVars);

                if (!CreateProcessW(
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false, // bInheritHandles = false - ConPTY attribute list is the mechanism
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                        envBlock,
                        workingDir,
                        ref startupInfo,
                        out _processInfo))
                {
                    // THE ERROR CODE IS THE DIAGNOSIS - do not drop it. This threw
                    // "CreateProcess failed." for a whole release, and Win32Exception(code, message)
                    // returns the MESSAGE from Message, not the system text, so every caller that
                    // logged ex.Message logged those three words and nothing else. ERROR_FILE_NOT_FOUND,
                    // ERROR_DIRECTORY and ERROR_NOT_ENOUGH_MEMORY are three different bugs with three
                    // different fixes, and a clean-machine install failure (devthrottle_internal issue
                    // #1050) took four eliminations to place because the log distinguished none of them.
                    // The code, its system text, the command line and the working directory all go in
                    // the message, so they reach the Director log AND the caller's error response.
                    var error = Marshal.GetLastWin32Error();
                    var systemText = new Win32Exception(error).Message;
                    var detail = $"CreateProcess failed with Win32 error {error} ({systemText}). " +
                                 $"commandLine={commandLine}, workingDir={workingDir ?? "(inherited)"}";
                    FileLog.Write($"[ProcessHost] Start FAILED: {detail}");
                    throw new Win32Exception(error, detail);
                }
            }
            finally
            {
                if (envBlock != IntPtr.Zero)
                    Marshal.FreeHGlobal(envBlock);
            }

            // Close the thread handle immediately - we only need the process handle
            CloseHandle(_processInfo.hThread);
            _processInfo.hThread = IntPtr.Zero;

            // Create streams for I/O
            _inputStream = new FileStream(_console.InputWriteSide, FileAccess.Write, bufferSize: 256, isAsync: false);
            _outputStream = new FileStream(_console.OutputReadSide, FileAccess.Read, bufferSize: 8192, isAsync: false);
        }
        finally
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    /// <summary>
    /// Build a Unicode environment block from the current process environment,
    /// stripping variables that would cause child processes to malfunction
    /// (e.g. CLAUDECODE which prevents Claude Code from starting).
    /// The returned IntPtr must be freed with Marshal.FreeHGlobal.
    /// </summary>
    private static IntPtr BuildEnvironmentBlock(Dictionary<string, string>? extraVars = null)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key as string;
            var value = entry.Value as string;
            if (key is null || value is null)
                continue;
            // Strip parent-agent environment variables to prevent nested
            // detection and session/thread ID conflicts when Director is
            // itself being driven from a terminal agent.
            if (key.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("CODEX_HOME", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.Equals("GIT_EDITOR", StringComparison.OrdinalIgnoreCase))
                continue;
            vars[key] = value;
        }

        // Enable 24-bit color output from CLI tools
        vars["COLORTERM"] = "truecolor";

        // Identify as a modern xterm-compatible terminal so CLI tools (notably
        // Claude Code) take their full-featured rendering path rather than a
        // minimal fallback that emits token-boundary artifacts into the byte stream.
        vars["TERM"] = "xterm-256color";
        vars["TERM_PROGRAM"] = "cc-director";

        // Inject extra variables (e.g. CC_SESSION_ID)
        if (extraVars != null)
        {
            foreach (var kvp in extraVars)
                vars[kvp.Key] = kvp.Value;
        }

        // Format: KEY=VALUE\0KEY=VALUE\0\0  (double-null terminated)
        var sb = new StringBuilder();
        foreach (var kvp in vars)
        {
            sb.Append(kvp.Key).Append('=').Append(kvp.Value).Append('\0');
        }
        sb.Append('\0'); // trailing null to double-terminate

        var block = sb.ToString();
        var byteCount = block.Length * sizeof(char);
        var ptr = Marshal.AllocHGlobal(byteCount);
        Marshal.Copy(block.ToCharArray(), 0, ptr, block.Length);
        return ptr;
    }

    /// <summary>
    /// Start the drain loop that reads output from the process into the buffer.
    /// </summary>
    public void StartDrainLoop(CircularTerminalBuffer buffer)
    {
        _drainTask = Task.Run(() =>
        {
            var readBuf = new byte[8192];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int bytesRead = _outputStream!.Read(readBuf, 0, readBuf.Length);
                    if (bytesRead == 0) break; // EOF - pipe closed
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
        });
    }

    /// <summary>
    /// Start monitoring for process exit.
    /// </summary>
    public void StartExitMonitor()
    {
        _exitMonitorTask = Task.Run(() =>
        {
            try
            {
                WaitForSingleObject(_processInfo.hProcess, INFINITE);
                GetExitCodeProcess(_processInfo.hProcess, out uint exitCode);
                OnExited?.Invoke((int)exitCode);
            }
            catch (Exception ex)
            {
                // OnExited runs subscriber code that may throw. Isolate it so a
                // faulting subscriber cannot fault this background task.
                FileLog.Write($"[ProcessHost] StartExitMonitor failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Write raw bytes to the process input - the ONLY door bytes take from the Director into the
    /// agent's terminal.
    ///
    /// EVERY exit from this method is logged, and that is the whole point (issue #1551). On
    /// 2026-07-14 a 1031-character phone dictation never reached Claude Code's composer. The
    /// terminal's OUTPUT was being recorded byte-for-byte at the time (session-logs/&lt;id&gt;/raw.jsonl,
    /// then on for every session, now off unless switched on), so we could
    /// prove the text was absent from the composer - but this direction recorded NOTHING, so we
    /// could not tell whether the bytes failed to leave here or left and were dropped by the TUI.
    /// Those two causes need opposite fixes and the evidence to separate them did not exist.
    ///
    /// The failure paths used <see cref="System.Diagnostics.Debug.WriteLine"/>, which is
    /// [Conditional("DEBUG")] and therefore compiled out of the shipped build entirely: a write that
    /// threw was silent on disk, in the exact place a lost prompt would go missing.
    ///
    /// SILENCE HERE NOW MEANS SUCCESS. FileStream.Write either writes every byte or throws, so a
    /// send with no line from this method is proof the bytes left the Director - which points the
    /// next investigation at the TUI instead of at us. That inference only holds while every
    /// non-success path below stays loud; do not quiet one.
    ///
    /// Success is deliberately NOT logged: every keystroke goes through here, and per-write lines
    /// would bury the failures this exists to surface.
    /// </summary>
    public void Write(byte[] data)
    {
        if (_disposed || _inputStream == null)
        {
            FileLog.Write($"[ProcessHost] Write SKIPPED: pid={ProcessId}, bytes={data.Length}, " +
                          $"disposed={_disposed}, streamNull={_inputStream == null} - the bytes never left the Director");
            return;
        }
        try
        {
            _inputStream.Write(data, 0, data.Length);
            _inputStream.Flush();
        }
        catch (IOException ex)
        {
            FileLog.Write($"[ProcessHost] Write FAILED: pid={ProcessId}, bytes={data.Length}: " +
                          $"{ex.GetType().Name}: {ex.Message} - the bytes never left the Director");
        }
        catch (ObjectDisposedException ex)
        {
            FileLog.Write($"[ProcessHost] Write FAILED: pid={ProcessId}, bytes={data.Length}: " +
                          $"{ex.GetType().Name}: {ex.Message} - the bytes never left the Director");
        }
    }

    /// <summary>Write raw bytes to the process input (async).</summary>
    public async Task WriteAsync(byte[] data)
    {
        if (_disposed || _inputStream == null) return;
        try
        {
            await _inputStream.WriteAsync(data);
            await _inputStream.FlushAsync();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Graceful shutdown: send Ctrl+C, wait, then terminate if needed.
    /// </summary>
    public async Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        if (_disposed) return;

        // Send Ctrl+C
        Write(new byte[] { 0x03 });

        // Wait for process to exit
        var exitTask = _exitMonitorTask ?? Task.CompletedTask;
        var completed = await Task.WhenAny(exitTask, Task.Delay(timeoutMs));

        if (completed != exitTask)
        {
            // Process didn't exit in time - kill the entire process tree
            KillProcessTree();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        // Kill the entire process tree (not just the root process) so child
        // processes like Node.js don't keep ConPTY pipes open.
        KillProcessTree();

        // Dispose the pseudo console FIRST -- this closes ConPTY pipes and causes
        // EOF on the output stream, which unblocks the drain loop immediately.
        // Previously this was done AFTER the task wait, causing a guaranteed 3-second
        // stall while the drain loop sat blocked on a synchronous Read().
        _console.Dispose();

        // Dispose streams to unblock any remaining I/O
        _inputStream?.Dispose();
        _outputStream?.Dispose();

        // Now wait for tasks -- they should complete quickly since pipes are closed
        try
        {
            var tasks = new List<Task>();
            if (_drainTask != null) tasks.Add(_drainTask);
            if (_exitMonitorTask != null) tasks.Add(_exitMonitorTask);
            if (tasks.Count > 0)
                Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(3));
        }
        catch (AggregateException) { }

        // Close process handle
        if (_processInfo.hProcess != IntPtr.Zero)
        {
            CloseHandle(_processInfo.hProcess);
            _processInfo.hProcess = IntPtr.Zero;
        }

        _cts.Dispose();
    }

    /// <summary>
    /// Kill the entire process tree rooted at the spawned process.
    /// TerminateProcess only kills the root -- child processes (Node.js, bash, etc.)
    /// survive and keep ConPTY pipes open, blocking shutdown.
    /// </summary>
    private void KillProcessTree()
    {
        if (_processInfo.dwProcessId == 0) return;
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(_processInfo.dwProcessId);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { } // Process already exited
        catch (InvalidOperationException) { } // Process already exited
    }
}
