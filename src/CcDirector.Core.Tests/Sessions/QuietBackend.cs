using CcDirector.Core.Backends;
using CcDirector.Core.Memory;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// A session backend that starts nothing and says nothing, so a test can hold a real
/// <see cref="Core.Sessions.Session"/> without a process behind it. Shared by the two
/// pending-interaction test classes rather than copied into each, which is the same stub the rest of
/// this project writes per file as NullBackend.
/// </summary>
internal sealed class QuietBackend : ISessionBackend
{
    public CircularTerminalBuffer? Buffer => null;
    public int ProcessId => 1;
    public string Status => "Quiet";
    public bool IsRunning => true;
    public bool HasExited => false;

#pragma warning disable CS0067
    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;
#pragma warning restore CS0067

    public void Start(string executable, string args, string workingDir, short cols, short rows,
        Dictionary<string, string>? environmentVars = null) { }
    public void Write(byte[] data) { }
    public Task SendTextAsync(string text) => Task.CompletedTask;
    public Task SendEnterAsync() => Task.CompletedTask;
    public void Resize(short cols, short rows) { }
    public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
    public void Dispose() { }
}
