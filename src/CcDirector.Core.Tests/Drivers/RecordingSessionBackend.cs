using CcDirector.Core.Backends;
using CcDirector.Core.Memory;

namespace CcDirector.Core.Tests.Drivers;

internal sealed class RecordingSessionBackend : ISessionBackend
{
    public int ProcessId => 1234;
    public string Status => "Running";
    public bool IsRunning => true;
    public bool HasExited => false;
    public CircularTerminalBuffer? Buffer { get; init; }

    public List<byte[]> WrittenBytes { get; } = new();
    public List<string> SentTexts { get; } = new();
    public List<string> Starts { get; } = new();
    public int EnterCount { get; private set; }
    public List<(short Columns, short Rows)> Resizes { get; } = new();
    public int ShutdownCount { get; private set; }

    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;

    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null)
    {
        Starts.Add($"{executable}|{args}|{workingDir}|{cols}|{rows}");
        StatusChanged?.Invoke(Status);
    }

    public void Write(byte[] data)
    {
        WrittenBytes.Add(data.ToArray());
    }

    public Task SendTextAsync(string text)
    {
        SentTexts.Add(text);
        return Task.CompletedTask;
    }

    public Task SendEnterAsync()
    {
        EnterCount++;
        return Task.CompletedTask;
    }

    public void Resize(short cols, short rows)
    {
        Resizes.Add((cols, rows));
    }

    public Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        ShutdownCount++;
        ProcessExited?.Invoke(0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
