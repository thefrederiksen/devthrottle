using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Gateway.Contracts;

namespace CcDirector.HostedAgent.Tests;

/// <summary>
/// In-memory stand-in for the ConPty: records raw writes, lets tests script the
/// terminal output clock (quiet/busy) and process death. No real process anywhere.
/// </summary>
public sealed class FakeBackend : ISessionBackend
{
    private readonly CircularTerminalBuffer _buffer = new(64 * 1024);

    public List<string> SentTexts { get; } = new();

    public List<byte[]> RawWrites { get; } = new();

    public string? StartedExecutable { get; private set; }

    public string? StartedArgs { get; private set; }

    public string? StartedWorkingDir { get; private set; }

    public int ProcessId { get; private set; }

    public string Status { get; private set; } = "NotStarted";

    public bool IsRunning => ProcessId != 0 && !HasExited;

    public bool HasExited { get; private set; }

    public CircularTerminalBuffer? Buffer => _buffer;

    public event Action<string>? StatusChanged;

    public event Action<int>? ProcessExited;

    public void Start(string executable, string args, string workingDir, short cols, short rows,
        Dictionary<string, string>? environmentVars = null)
    {
        StartedExecutable = executable;
        StartedArgs = args;
        StartedWorkingDir = workingDir;
        ProcessId = 4242;
        Status = "Running";
        StatusChanged?.Invoke(Status);
        EmitOutput("agent banner");
    }

    /// <summary>Simulate terminal bytes arriving (resets the quiet clock).</summary>
    public void EmitOutput(string text) =>
        _buffer.Write(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Simulate the CLI dying (the crash-recovery scenario).</summary>
    public void SimulateExit(int code = 1)
    {
        HasExited = true;
        Status = $"Exited ({code})";
        ProcessExited?.Invoke(code);
    }

    /// <summary>Invoked on every raw Write so tests can simulate the TUI echoing
    /// typed characters back into the terminal stream.</summary>
    public Action<byte[]>? OnRawWrite { get; set; }

    public void Write(byte[] data)
    {
        RawWrites.Add(data);
        OnRawWrite?.Invoke(data);
    }

    public Task SendTextAsync(string text)
    {
        SentTexts.Add(text);
        return Task.CompletedTask;
    }

    public void Resize(short cols, short rows)
    {
    }

    public Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        HasExited = true;
        Status = "Exited (0)";
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Scriptable driver for HostedAgent tests: records every verb, serves transcripts
/// from in-memory dictionaries, and lets tests react to a submit the way a real CLI
/// writes its transcript.
/// </summary>
public sealed class FakeDriver : IAgentDriver
{
    public List<string> Submits { get; } = new();

    public int CancelCount { get; private set; }

    public int InterruptCount { get; private set; }

    public int HistoryCount { get; private set; }

    public int ClearCount { get; private set; }

    /// <summary>Session ids handed out by BuildLaunchSpec, in order.</summary>
    public List<string> IssuedSessionIds { get; } = new();

    /// <summary>Invoked on every SubmitAsync so tests can mutate transcripts.</summary>
    public Action<string>? OnSubmit { get; set; }

    /// <summary>Invoked on ClearContextAsync (e.g. to add the new transcript file).</summary>
    public Action? OnClear { get; set; }

    public Dictionary<string, List<TurnWidgetDto>> Widgets { get; } = new();

    public Dictionary<string, SessionUsageDto> Usage { get; } = new();

    public List<(string AgentSessionId, DateTime LastWriteUtc)> Transcripts { get; } = new();

    public AgentKind Kind => AgentKind.ClaudeCode;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.ClearContext | DriverCapabilities.Cancel
        | DriverCapabilities.TranscriptRead | DriverCapabilities.PreassignedSessionId;

    public IReadOnlyList<AgentSlashCommand> SlashCommands => [];

    public void AddTextReply(string agentSessionId, string userPrompt, string replyText)
    {
        var list = Widgets.TryGetValue(agentSessionId, out var w) ? w : Widgets[agentSessionId] = new();
        list.Add(new TurnWidgetDto { Kind = "UserMessage", Content = userPrompt });
        list.Add(new TurnWidgetDto { Kind = "Text", Content = replyText });
    }

    public string ResolveExecutable(string? configuredPath) => configuredPath ?? "fake-agent.exe";

    public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId)
    {
        var id = Guid.NewGuid().ToString();
        IssuedSessionIds.Add(id);
        return new AgentLaunchSpec($"{baseArgs ?? "--fake"} --session-id {id}", id);
    }

    public Task SubmitAsync(ISessionBackend backend, string text)
    {
        Submits.Add(text);
        OnSubmit?.Invoke(text);
        return Task.CompletedTask;
    }

    public Task CancelAsync(ISessionBackend backend)
    {
        CancelCount++;
        return Task.CompletedTask;
    }

    public Task InterruptAsync(ISessionBackend backend)
    {
        InterruptCount++;
        return Task.CompletedTask;
    }

    public Task ShowHistoryAsync(ISessionBackend backend)
    {
        HistoryCount++;
        return Task.CompletedTask;
    }

    public Task ClearContextAsync(ISessionBackend backend)
    {
        ClearCount++;
        OnClear?.Invoke();
        return Task.CompletedTask;
    }

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) =>
        Widgets.TryGetValue(agentSessionId, out var w) ? new List<TurnWidgetDto>(w) : new List<TurnWidgetDto>();

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) =>
        Usage.TryGetValue(agentSessionId, out var u) ? u : null;

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) =>
        new(Transcripts);
}

/// <summary>In-memory transcript store for ClaudeDriver delegation tests.</summary>
public sealed class FakeTranscriptReader : ITranscriptReader
{
    public Dictionary<string, List<TurnWidgetDto>> Widgets { get; } = new();

    public Dictionary<string, SessionUsageDto> Usage { get; } = new();

    public List<(string ClaudeSessionId, DateTime LastWriteUtc)> Transcripts { get; } = new();

    public List<TurnWidgetDto> ReadWidgets(string claudeSessionId, string repoPath) =>
        Widgets.TryGetValue(claudeSessionId, out var w) ? new List<TurnWidgetDto>(w) : new List<TurnWidgetDto>();

    public SessionUsageDto? ReadUsage(string claudeSessionId, string repoPath) =>
        Usage.TryGetValue(claudeSessionId, out var u) ? u : null;

    public List<(string ClaudeSessionId, DateTime LastWriteUtc)> ListTranscripts(string repoPath) =>
        new(Transcripts);
}
