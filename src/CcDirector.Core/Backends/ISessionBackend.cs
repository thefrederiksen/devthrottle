using CcDirector.Core.Memory;

namespace CcDirector.Core.Backends;

/// <summary>
/// Abstraction for different session backend implementations.
/// Each backend handles process lifecycle, I/O, and terminal management differently.
/// </summary>
public interface ISessionBackend : IDisposable
{
    /// <summary>The process ID of the running Claude process, or 0 if not running.</summary>
    int ProcessId { get; }

    /// <summary>Current status description (e.g., "Starting", "Running", "Exited (0)").</summary>
    string Status { get; }

    /// <summary>True if the backend has a running process.</summary>
    bool IsRunning { get; }

    /// <summary>True if the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>The terminal buffer for output. May be null for backends that don't buffer (Embedded).</summary>
    CircularTerminalBuffer? Buffer { get; }

    /// <summary>The process working directory, when the backend has one.</summary>
    string WorkingDirectory => string.Empty;

    /// <summary>Fires when the status changes.</summary>
    event Action<string>? StatusChanged;

    /// <summary>Fires when the process exits. Argument is exit code.</summary>
    event Action<int>? ProcessExited;

    /// <summary>
    /// Start the Claude process.
    /// </summary>
    /// <param name="executable">Path to claude executable.</param>
    /// <param name="args">Command line arguments.</param>
    /// <param name="workingDir">Working directory for the process.</param>
    /// <param name="cols">Terminal columns (ignored by some backends).</param>
    /// <param name="rows">Terminal rows (ignored by some backends).</param>
    void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null);

    /// <summary>
    /// Write raw bytes to the process input.
    /// For ConPty: writes to PTY input pipe.
    /// For Embedded: uses WriteConsoleInput or clipboard.
    /// For Pipe: not supported (use SendPromptAsync instead).
    /// </summary>
    void Write(byte[] data);

    /// <summary>
    /// Send text followed by Enter to the process.
    /// </summary>
    Task SendTextAsync(string text);

    /// <summary>Send just an Enter keystroke to the process for raw terminal-control scenarios.</summary>
    Task SendEnterAsync() => Task.CompletedTask;

    /// <summary>
    /// Resize the terminal. Only meaningful for ConPty backend.
    /// </summary>
    void Resize(short cols, short rows);

    /// <summary>
    /// Gracefully shutdown the process (send Ctrl+C, wait, then force kill if needed).
    /// </summary>
    Task GracefulShutdownAsync(int timeoutMs = 5000);

    /// <summary>
    /// What went wrong the last time <see cref="GracefulShutdownAsync"/> tried to shut this session down,
    /// in the backend's own words - or null when the last attempt raised no failure.
    ///
    /// WHY IT EXISTS (mission "Stop a session", inspection 1, finding I3). <see cref="GracefulShutdownAsync"/>
    /// must not throw - callers all over the desktop depend on a stop never blowing up in their hands - so
    /// a backend that cannot carry out its shutdown catches the failure and returns normally. That left the
    /// stop verb with no way to tell a shutdown that worked from one that was refused, and it reported the
    /// refused one as "no process was running" and cleared the row. This member is how the failure gets out
    /// without the exception getting out with it.
    ///
    /// Default null: a backend that has no shutdown failure to report says nothing, so no existing backend
    /// changes. Only <see cref="GitHubActionsBackend"/> sets it today - it is the one backend whose shutdown
    /// is a remote call that another party can refuse.
    /// </summary>
    string? LastShutdownFailure => null;
}

/// <summary>
/// Enum identifying the backend type.
/// </summary>
public enum SessionBackendType
{
    /// <summary>ConPTY-based terminal with WPF rendering.</summary>
    ConPty,

    /// <summary>Real console window embedded/overlaid on WPF.</summary>
    Embedded,

    /// <summary>Pipe mode - stateless, spawns process per prompt.</summary>
    Pipe,

    /// <summary>Studio mode - persistent stream-json backend with card-based UI.</summary>
    Studio,

    /// <summary>
    /// Remote backend - the session is a handle to a GitHub conversation thread
    /// (issue/PR) watched by the Claude GitHub App. Each turn is a @claude comment
    /// that triggers a workflow run on a GitHub-hosted runner. No local process;
    /// the buffer is fed from the run's status and the action's progress comment.
    /// </summary>
    GitHubActions
}
