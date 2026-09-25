// Extracted from ExecuteActionEndpointTests so BOTH halves of the split suite can use it: the
// host-bound tests that stay behind the machine-wide lock, and the pure ones that no longer do.
// It was defined inside a test file, which made that one file the only place either half could
// reach it - and a shared helper that lives inside a test file is a helper that pins its users to
// one assembly.
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// In-process stub backend for execute-action tests: provides a real CircularTerminalBuffer so
/// every byte the executor injects is observable, never spawns a process, and can simulate a
/// process exit on demand (drives Session.Status to Exited through the same backend event a real
/// ConPty exit uses).
/// </summary>
internal sealed class ExecuteActionTestBackend : ISessionBackend
{
    private bool _hasExited;

    public int ProcessId => 0;
    public string Status => "Buffer-only";
    public bool IsRunning => !_hasExited;
    public bool HasExited => _hasExited;
    public CircularTerminalBuffer? Buffer { get; } = new CircularTerminalBuffer(65536);

#pragma warning disable CS0067
    public event Action<string>? StatusChanged;
#pragma warning restore CS0067
    public event Action<int>? ProcessExited;

    /// <summary>Simulate the agent process dying (non-zero code so the manager keeps the row).</summary>
    public void RaiseProcessExited(int exitCode)
    {
        _hasExited = true;
        ProcessExited?.Invoke(exitCode);
    }

    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
    public void Write(byte[] data)
    {
        Interlocked.Increment(ref _textsSent);
        Buffer?.Write(data);
    }

    /// <summary>How many times anything was typed into this backend - text sent or bytes written - so a test can
    /// prove a refusal typed nothing (and, in its control case, that a delivery did type).</summary>
    public int TextsSent => Volatile.Read(ref _textsSent);
    private int _textsSent;

    public Task SendTextAsync(string text)
    {
        Interlocked.Increment(ref _textsSent);
        return Task.CompletedTask;
    }
    public Task SendEnterAsync() => Task.CompletedTask;
    public void Resize(short cols, short rows) { }

    /// <summary>The graceful-shutdown window the last kill passed, so tests can assert the fleet-stop path
    /// escalates on the shorter FleetKillGraceMs window instead of the standard one.</summary>
    public int? LastGracefulTimeoutMs { get; private set; }

    public Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        LastGracefulTimeoutMs = timeoutMs;
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
