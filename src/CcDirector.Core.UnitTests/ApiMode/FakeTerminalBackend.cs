using CcDirector.Core.Backends;
using CcDirector.Core.Memory;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>
/// A terminal backend that runs nothing (API mode, phase 4: the user's wheel). It records how it was started and what was
/// typed into it, writes to a real <see cref="CircularTerminalBuffer"/> when the test says so, and exits when it is shut
/// down or when the test makes it exit on its own. Nothing is spawned and nothing waits on a clock.
/// </summary>
internal sealed class FakeTerminalBackend : ISessionBackend
{
    private readonly Action<string>? _timeline;
    private int _exited;

    public FakeTerminalBackend(Action<string>? timeline = null) => _timeline = timeline;

    public string? StartedExecutable { get; private set; }
    public string? StartedArguments { get; private set; }
    public string? StartedWorkingDirectory { get; private set; }
    public IReadOnlyDictionary<string, string>? StartedEnvironment { get; private set; }
    public (short Cols, short Rows) StartedSize { get; private set; }
    public List<byte[]> Written { get; } = [];
    public bool Disposed { get; private set; }

    /// <summary>When set, <see cref="Start"/> throws it, as a terminal that cannot be started does.</summary>
    public Exception? StartFailure { get; init; }

    public int ProcessId => StartedExecutable is null ? 0 : 5151;
    public string Status => HasExited ? "Exited" : StartedExecutable is null ? "Not started" : "Running";
    public bool IsRunning => StartedExecutable is not null && !HasExited;
    public bool HasExited => Volatile.Read(ref _exited) == 1;
    public CircularTerminalBuffer? Buffer { get; } = new(64 * 1024);

#pragma warning disable CS0067
    public event Action<string>? StatusChanged;
#pragma warning restore CS0067
    public event Action<int>? ProcessExited;

    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null)
    {
        if (StartFailure is not null)
            throw StartFailure;
        StartedExecutable = executable;
        StartedArguments = args;
        StartedWorkingDirectory = workingDir;
        StartedEnvironment = new Dictionary<string, string>(environmentVars ?? []);
        StartedSize = (cols, rows);
        _timeline?.Invoke("terminal started");
    }

    public void Write(byte[] data) => Written.Add(data);

    public Task SendTextAsync(string text)
    {
        Written.Add(System.Text.Encoding.UTF8.GetBytes(text + "\r"));
        return Task.CompletedTask;
    }

    public void Resize(short cols, short rows) => StartedSize = (cols, rows);

    /// <summary>Pi in the terminal draws something: bytes on the session's screen.</summary>
    public void Draw(string text) => Buffer!.Write(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>False for a terminal that is still running after its shutdown returns, as the real terminal hosts leave one
    /// whose exit did not come within the timeout: they send the kill and return without waiting for the exit.</summary>
    public bool ExitsOnShutdown { get; set; } = true;

    /// <summary>How many times the terminal was asked to shut down.</summary>
    public int ShutdownsAsked { get; private set; }

    /// <summary>A clean shutdown: the terminal exits with code 0 (unless <see cref="ExitsOnShutdown"/> is false).</summary>
    public Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        ShutdownsAsked++;
        if (ExitsOnShutdown)
            Exit(0);
        return Task.CompletedTask;
    }

    /// <summary>The terminal exits by itself (the user quit Pi, or it crashed).</summary>
    public void Exit(int code)
    {
        if (Interlocked.Exchange(ref _exited, 1) == 1)
            return;
        _timeline?.Invoke("terminal exited");
        ProcessExited?.Invoke(code);
    }

    public void Dispose() => Disposed = true;
}
