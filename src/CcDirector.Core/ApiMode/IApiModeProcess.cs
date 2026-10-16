namespace CcDirector.Core.ApiMode;

/// <summary>
/// The child process behind an API-mode transport, reduced to what the transport needs. The real one is
/// <see cref="SystemApiModeProcess"/>; tests use a fake that replays recorded bytes, so no test spawns a
/// process.
///
/// One instance is one process and is started once. The transport reads standard output and standard error
/// on its own loops and writes standard input under its own lock, so an implementation needs no locking of
/// its own beyond what its streams already have.
/// </summary>
public interface IApiModeProcess : IDisposable
{
    /// <summary>The operating system's id for the process, or 0 before it starts.</summary>
    int ProcessId { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>
    /// Start the process. A process that cannot be started throws <see cref="ApiModeStartException"/> naming
    /// the executable and why - never a silent substitute.
    /// </summary>
    void Start(ApiModeProcessStartInfo startInfo);

    /// <summary>Write one complete line (the bytes include the ending line feed) to standard input and flush it.</summary>
    Task WriteLineAsync(ReadOnlyMemory<byte> line, CancellationToken cancellationToken);

    /// <summary>Read the next bytes of standard output into <paramref name="buffer"/>. Returns 0 at the end of the stream.</summary>
    ValueTask<int> ReadOutputAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Read the next line of standard error, or null at the end of the stream.</summary>
    ValueTask<string?> ReadErrorLineAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Wait for the process to exit and return its exit code as the operating system reports it: on Linux and macOS
    /// 128 plus the signal for a process ended by a signal (137 for a kill), on Windows -1 for a killed process.
    /// </summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Close standard input: the polite way to ask the process to exit.</summary>
    void CloseInput();

    /// <summary>Kill the process and everything it started.</summary>
    void Kill();
}
