using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.Core.ApiMode;

/// <summary>
/// The backend of a session in API mode (issue #4070): the agent runs as one long-lived child process spoken to in its
/// own protocol (<see cref="ApiModeSessionHost"/>), not in a terminal. It is the seam that lets an API-mode session live
/// inside the Director's ordinary <see cref="Sessions.Session"/> - roster, status, exit and crash handling, stop and
/// disposal all go through this interface as they do for a terminal - while terminal sessions keep their own backends,
/// untouched.
///
/// There is no terminal: <see cref="Buffer"/> is null (as for the embedded backend), and anything that would type or
/// send keystrokes is refused, because a keystroke has nowhere to go. Prompts and Stop reach the agent through
/// <see cref="Host"/>, which the session calls directly.
/// </summary>
public sealed class ApiModeSessionBackend : ISessionBackend
{
    /// <summary>The arguments that put Pi in API mode, ahead of the session's own launch arguments.</summary>
    public static readonly IReadOnlyList<string> PiApiModeArguments = ["--mode", "rpc"];

    /// <summary>
    /// The prefix of every variable removed from the environment an API-mode Pi INHERITS from the Director. The session's
    /// own stamped variables (its CC_SESSION_ID, its Gateway address and key, the fleet text) are applied after the
    /// removal, exactly as terminal mode gives them, so the fleet commands in the preamble work (the Tech Lead's ruling,
    /// 16 October 2026).
    /// </summary>
    public const string InheritedPrefixRemoved = "CC_";

    private readonly string _sessionId;
    private readonly string _agentSessionId;
    private readonly bool _resuming;
    private readonly SessionEventStream _stream;
    private readonly Func<IApiModeProcess> _processFactory;
    private readonly TimeProvider _clock;
    private ApiModeSessionHost? _host;
    private string _status = "Not started";
    private string _workingDirectory = string.Empty;
    private volatile bool _hasExited;

    /// <param name="sessionId">The DevThrottle session.</param>
    /// <param name="agentSessionId">The id Pi is started with (<c>--session-id</c>), which is also how it resumes.</param>
    /// <param name="resuming">True when the session resumes an existing Pi conversation.</param>
    /// <param name="stream">The session's event stream.</param>
    /// <param name="processFactory">Makes the child process: the real one in the Director, a fake in tests.</param>
    /// <param name="clock">Where every event's time comes from.</param>
    public ApiModeSessionBackend(string sessionId, string agentSessionId, bool resuming, SessionEventStream stream,
        Func<IApiModeProcess> processFactory, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentSessionId);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(processFactory);
        ArgumentNullException.ThrowIfNull(clock);
        _sessionId = sessionId;
        _agentSessionId = agentSessionId;
        _resuming = resuming;
        _stream = stream;
        _processFactory = processFactory;
        _clock = clock;
    }

    /// <summary>The agent process and its dialect. Throws before <see cref="Start"/>.</summary>
    public ApiModeSessionHost Host =>
        _host ?? throw new InvalidOperationException($"The API-mode session {_sessionId} has not been started.");

    /// <summary>The id Pi was started with: the conversation take the wheel and a resume open.</summary>
    public string AgentSessionId => _agentSessionId;

    /// <summary>The session's event stream.</summary>
    public SessionEventStream EventStream => _stream;

    /// <summary>What the process was started with, or null before <see cref="Start"/>.</summary>
    public ApiModeProcessStartInfo? StartedWith { get; private set; }

    public int ProcessId => _host?.ProcessId ?? 0;

    public string Status => _status;

    public bool IsRunning => _host is not null && !_hasExited;

    public bool HasExited => _hasExited;

    /// <summary>Why the session's Pi ended, from its last <see cref="ConnectionEnded"/>; null until it has. Set before
    /// <see cref="ProcessExited"/> is raised.</summary>
    public string? EndReason { get; private set; }

    /// <summary>Whether that end was a failure as the stream tells it (<see cref="ApiModeSessionHost.EndedInFailure"/>),
    /// whatever the exit code. Set before <see cref="ProcessExited"/> is raised.</summary>
    public bool EndedInFailure { get; private set; }

    /// <summary>Null: an API-mode session has no terminal.</summary>
    public CircularTerminalBuffer? Buffer => null;

    public string WorkingDirectory => _workingDirectory;

    public event Action<string>? StatusChanged;

    public event Action<int>? ProcessExited;

    /// <summary>Each batch of the session's events, in order, outside the host's lock (<see cref="ApiModeSessionHost.Published"/>).
    /// The session reads its activity from it.</summary>
    public event Action<IReadOnlyList<SessionEvent>>? EventsPublished;

    /// <summary>
    /// Start the agent in API mode: the executable and launch line the terminal path would use, with
    /// <see cref="PiApiModeArguments"/> ahead of them, in <paramref name="workingDir"/>, with the session's environment
    /// (every inherited <c>CC_</c> variable removed, then the session's own stamped set applied). <paramref name="cols"/>
    /// and <paramref name="rows"/> are ignored: there is no terminal. Throws <see cref="ApiModeStartException"/>, naming the executable, when it cannot be started.
    /// </summary>
    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDir);
        FileLog.Write($"[ApiModeSessionBackend] Start: session={_sessionId}, executable={executable}, args={args}, workingDir={workingDir}");
        if (_host is not null)
            throw new InvalidOperationException($"The API-mode session {_sessionId} was already started.");

        var startInfo = new ApiModeProcessStartInfo
        {
            Executable = executable,
            Arguments = [.. PiApiModeArguments, .. ApiModeArguments.Split(args)],
            WorkingDirectory = workingDir,
            SetVariables = new Dictionary<string, string>(environmentVars ?? []),
            RemoveVariablePrefixes = [InheritedPrefixRemoved],
        };

        var host = new ApiModeSessionHost(_sessionId, _agentSessionId, _resuming, _processFactory, _stream, _clock);
        host.Exited += OnHostExited;
        host.Published += OnHostPublished;
        _host = host;
        _workingDirectory = workingDir;
        StartedWith = startInfo;
        try
        {
            host.Start(startInfo);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ApiModeSessionBackend] Start FAILED: session={_sessionId}: {ex.Message}");
            SetStatus("Failed to start");
            throw;
        }
        SetStatus("Running");
    }

    /// <summary>Refused: there is no terminal to type into. Send prompts through the session.</summary>
    public void Write(byte[] data) => throw NoTerminal("keystrokes");

    /// <summary>Refused: there is no terminal to type into. Send prompts through the session.</summary>
    public Task SendTextAsync(string text) => throw NoTerminal("typed text");

    /// <summary>Nothing to size: an API-mode session has no terminal.</summary>
    public void Resize(short cols, short rows)
    {
    }

    /// <summary>Close Pi's standard input, wait up to <paramref name="timeoutMs"/>, then kill it.</summary>
    public async Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        FileLog.Write($"[ApiModeSessionBackend] GracefulShutdownAsync: session={_sessionId}, timeout={timeoutMs}ms");
        if (_host is null || _hasExited)
            return;
        await _host.ShutdownAsync(TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None).ConfigureAwait(false);
    }

    public void Dispose()
    {
        FileLog.Write($"[ApiModeSessionBackend] Dispose: session={_sessionId}");
        if (_host is not { } host)
            return;
        host.Exited -= OnHostExited;
        host.Published -= OnHostPublished;
        // Disposing kills Pi if it still runs and waits for the event loop; never on the caller's thread, which may be
        // the user interface's.
        _ = DisposeHostAsync(host);
    }

    private async Task DisposeHostAsync(ApiModeSessionHost host)
    {
        try
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An entry point: nobody awaits it.
            FileLog.Write($"[ApiModeSessionBackend] DisposeHostAsync FAILED: session={_sessionId}: {ex.Message}");
        }
    }

    private void OnHostExited(int exitCode)
    {
        // The host completes Ended before it raises Exited, so the reason is there to read.
        var host = _host ?? throw new InvalidOperationException($"Session {_sessionId}: Pi exited with no host.");
        if (!host.Ended.IsCompletedSuccessfully)
            throw new InvalidOperationException($"Session {_sessionId}: the host raised Exited before Ended completed.");
        EndReason = host.Ended.Result.Reason;
        EndedInFailure = host.EndedInFailure;
        FileLog.Write($"[ApiModeSessionBackend] Exited: session={_sessionId}, exitCode={exitCode}, failure={EndedInFailure}, reason={EndReason}");
        _hasExited = true;
        SetStatus($"Exited ({exitCode})");
        ProcessExited?.Invoke(exitCode);
    }

    private void OnHostPublished(IReadOnlyList<SessionEvent> events) => EventsPublished?.Invoke(events);

    private void SetStatus(string status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    private InvalidOperationException NoTerminal(string what)
    {
        FileLog.Write($"[ApiModeSessionBackend] refused {what}: session={_sessionId} is in API mode and has no terminal");
        return new InvalidOperationException(
            $"Session {_sessionId} is in API mode: it has no terminal, so {what} cannot be sent. Prompts go through the session's send.");
    }
}
