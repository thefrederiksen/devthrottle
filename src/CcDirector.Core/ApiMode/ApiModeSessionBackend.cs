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
/// TAKE THE WHEEL AND HAND BACK (phase 4). The user can open the same conversation in Pi's real terminal and later give it
/// back. While the protocol holds the conversation there is no terminal: <see cref="Buffer"/> is null, and keystrokes are
/// refused. While the user holds it, the terminal is a real one, made by the terminal path's own factory and launched with
/// the executable, the launch line (without <c>--mode rpc</c>), the folder and the environment this backend was started
/// with, through <see cref="CommandLineLauncher.Build"/> as terminal mode launches it; <see cref="Buffer"/>,
/// <see cref="Write"/>, <see cref="SendTextAsync"/> and <see cref="Resize"/> are that terminal's.
///
/// EXACTLY ONE PROCESS HOLDS THE CONVERSATION, AND THIS ENFORCES IT. One ownership lock per session is held across the
/// whole of every switch (take, hand back, shutdown, and the handling of the terminal exiting on its own), and each
/// switch waits for the old process to have exited before the new one starts. Pi's own restart (the turn-start bound)
/// runs inside the host only while the protocol holds the conversation: the host stops starting processes the moment a
/// take begins. A process-wide claim on the conversation (<see cref="ApiModeConversationClaims"/>) keeps a second
/// API-mode session off it.
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

    /// <summary>How long the user's terminal gets to exit on a hand back before it is killed.</summary>
    public const int TerminalShutdownMilliseconds = 5000;

    /// <summary>Why a take is refused while the user already holds the wheel.</summary>
    public const string AlreadyHeldReason =
        "You already have the wheel of this session: its terminal is open on the conversation. Hand it back first if you want the session to continue in API mode.";

    /// <summary>Why a hand back is refused while the protocol holds the conversation.</summary>
    public const string NotHeldReason =
        "You do not have the wheel of this session, so there is nothing to hand back: it is still running in API mode. Take the wheel first if you want to type into its terminal.";

    /// <summary>Why a hand back is refused while the terminal is in the middle of a turn.</summary>
    public const string TerminalMidTurnReason =
        "Pi is in the middle of a turn in the terminal, so the wheel was not handed back: two processes may never hold one conversation. Wait for the turn to finish, or stop it in the terminal, then hand back.";

    /// <summary>Why a hand back is refused while the user's terminal, asked to exit and then killed, has not exited yet.</summary>
    public const string TerminalStillRunningReason =
        "Pi's terminal was asked to exit and then killed, but it has not exited yet, so the wheel was not handed back: two processes may never hold one conversation. The terminal is still yours; hand back again once it has gone.";

    /// <summary>Why a Stop is refused while the user holds the wheel: the turn runs in Pi's terminal, out of the protocol's reach.</summary>
    public const string StopWhileUserHoldsReason =
        "You have the wheel of this session, so its turn runs in Pi's terminal, where Stop cannot reach it: nothing was stopped. Stop it in the terminal, then hand back if you want the session to continue in API mode.";

    /// <summary>Why a Stop is refused while no process holds the conversation (the user's terminal exited on its own).</summary>
    public const string StopWithNoHolderReason =
        "No process holds this session's conversation (its terminal exited), so no turn is running and nothing was stopped. Hand back to continue in API mode, or take the wheel to open the terminal again.";

    private readonly string _sessionId;
    private readonly string _agentSessionId;
    private readonly bool _resuming;
    private readonly SessionEventStream _stream;
    private readonly Func<IApiModeProcess> _processFactory;
    private readonly Func<ISessionBackend> _terminalFactory;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _ownership = new(1, 1);
    private ApiModeSessionHost? _host;
    private ISessionBackend? _terminal;
    private string? _claimKey;
    private string _status = "Not started";
    private string _workingDirectory = string.Empty;
    private string _executable = string.Empty;
    private string _launchLine = string.Empty;
    private Dictionary<string, string> _environment = [];
    private short _cols = 120;
    private short _rows = 30;
    private volatile bool _hasExited;
    private volatile SessionWheelHolder _wheelHolder = SessionWheelHolder.None;

    /// <param name="sessionId">The DevThrottle session.</param>
    /// <param name="agentSessionId">The id Pi is started with (<c>--session-id</c>), which is also how it resumes.</param>
    /// <param name="resuming">True when the session resumes an existing Pi conversation.</param>
    /// <param name="stream">The session's event stream.</param>
    /// <param name="processFactory">Makes the API-mode child process: the real one in the Director, a fake in tests.</param>
    /// <param name="terminalFactory">Makes the terminal backend the user's wheel runs in: the terminal path's own factory
    /// in the Director, a fake in tests.</param>
    /// <param name="clock">Where every event's time comes from.</param>
    public ApiModeSessionBackend(string sessionId, string agentSessionId, bool resuming, SessionEventStream stream,
        Func<IApiModeProcess> processFactory, Func<ISessionBackend> terminalFactory, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentSessionId);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(processFactory);
        ArgumentNullException.ThrowIfNull(terminalFactory);
        ArgumentNullException.ThrowIfNull(clock);
        _sessionId = sessionId;
        _agentSessionId = agentSessionId;
        _resuming = resuming;
        _stream = stream;
        _processFactory = processFactory;
        _terminalFactory = terminalFactory;
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

    /// <summary>Who holds the conversation now.</summary>
    public SessionWheelHolder WheelHolder => _wheelHolder;

    /// <summary>The user's terminal while they hold the wheel, or null.</summary>
    public ISessionBackend? Terminal => _terminal;

    public int ProcessId => _terminal?.ProcessId ?? _host?.ProcessId ?? 0;

    public string Status => _status;

    public bool IsRunning => _host is not null && !_hasExited;

    public bool HasExited => _hasExited;

    /// <summary>The user's terminal's buffer while they hold the wheel; null while the protocol holds it.</summary>
    public CircularTerminalBuffer? Buffer => _terminal?.Buffer;

    public string WorkingDirectory => _workingDirectory;

    public event Action<string>? StatusChanged;

    /// <summary>The session has ended: Pi's process ended for good, or the session was shut down. A switch of the wheel
    /// is not an exit.</summary>
    public event Action<int>? ProcessExited;

    /// <summary>Each batch of the session's events, in order, outside the host's lock (<see cref="ApiModeSessionHost.Published"/>).
    /// The session reads its activity from it.</summary>
    public event Action<IReadOnlyList<SessionEvent>>? EventsPublished;

    /// <summary>The user's terminal appeared or went away (a take, a hand back, the terminal exiting on its own). The
    /// session attaches its screen to <see cref="Buffer"/>, or drops it.</summary>
    public event Action? TerminalChanged;

    /// <summary>
    /// Start the agent in API mode: the executable and launch line the terminal path would use, with
    /// <see cref="PiApiModeArguments"/> ahead of them, in <paramref name="workingDir"/>, with the session's environment
    /// (every inherited <c>CC_</c> variable removed, then the session's own stamped set applied). <paramref name="cols"/>
    /// and <paramref name="rows"/> are kept for the user's terminal. Throws <see cref="ApiModeStartException"/>, naming the
    /// executable, when it cannot be started, and when another session holds the conversation.
    /// </summary>
    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDir);
        FileLog.Write($"[ApiModeSessionBackend] Start: session={_sessionId}, executable={executable}, args={args}, workingDir={workingDir}");
        if (_host is not null)
            throw new InvalidOperationException($"The API-mode session {_sessionId} was already started.");

        _executable = executable;
        _launchLine = args;
        _environment = new Dictionary<string, string>(environmentVars ?? []);
        _cols = cols;
        _rows = rows;
        var startInfo = new ApiModeProcessStartInfo
        {
            Executable = executable,
            Arguments = [.. PiApiModeArguments, .. ApiModeArguments.Split(args)],
            WorkingDirectory = workingDir,
            SetVariables = new Dictionary<string, string>(_environment),
            RemoveVariablePrefixes = [InheritedPrefixRemoved],
        };

        var claimKey = ApiModeConversationClaims.KeyOf(workingDir, _agentSessionId);
        ApiModeConversationClaims.Claim(claimKey, _sessionId);
        _claimKey = claimKey;
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
            ReleaseClaim();
            SetStatus("Failed to start");
            throw;
        }
        _wheelHolder = SessionWheelHolder.Protocol;
        SetStatus("Running");
    }

    /// <summary>
    /// Take the wheel: stop Pi's API-mode process cleanly (a running turn is stopped first by Pi's own abort; then its input
    /// is closed, and it is killed only if it has not exited in time), and only once it has exited, open the conversation
    /// in Pi's real terminal. Refused, with nothing changed, while the user already holds the wheel or once the session has
    /// ended. If the terminal cannot be started, the conversation goes back to the protocol and the take is refused with
    /// the reason.
    /// </summary>
    public async Task<WheelSwitchResult> TakeWheelAsync(CancellationToken cancellationToken)
    {
        FileLog.Write($"[ApiModeSessionBackend] TakeWheelAsync: session={_sessionId}, holder={_wheelHolder}");
        var host = Host;
        await _ownership.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_hasExited)
                return Refuse("take", $"Session {_sessionId} has ended, so there is no conversation to take.");
            if (_wheelHolder == SessionWheelHolder.User)
                return Refuse("take", AlreadyHeldReason);
            if (_wheelHolder == SessionWheelHolder.Protocol)
                await host.SuspendAsync(ApiModeSessionDefaults.ShutdownGrace, cancellationToken).ConfigureAwait(false);
            _wheelHolder = SessionWheelHolder.None;

            var terminal = _terminalFactory();
            try
            {
                terminal.ProcessExited += code => OnTerminalExited(terminal, code);
                var (launchExe, launchArgs) = CommandLineLauncher.Build(_executable, _launchLine);
                FileLog.Write($"[ApiModeSessionBackend] TakeWheelAsync: session={_sessionId}, Pi's API process has exited; starting its terminal: {launchExe} {launchArgs}");
                terminal.Start(launchExe, launchArgs, _workingDirectory, _cols, _rows, new Dictionary<string, string>(_environment));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ApiModeSessionBackend] TakeWheelAsync FAILED to start the terminal: session={_sessionId}: {ex.Message}");
                terminal.Dispose();
                await host.ResumeAsync(cancellationToken).ConfigureAwait(false);
                _wheelHolder = SessionWheelHolder.Protocol;
                return Refuse("take", $"Pi's terminal could not be started ({ex.Message}), so the conversation stays in API mode.");
            }

            _terminal = terminal;
            _wheelHolder = SessionWheelHolder.User;
            SetStatus("Running (the user has the wheel)");
            FileLog.Write($"[ApiModeSessionBackend] TakeWheelAsync: session={_sessionId}, the user has the wheel, terminal process {terminal.ProcessId}");
        }
        finally
        {
            _ownership.Release();
        }

        RaiseTerminalChanged();
        return WheelSwitchResult.Switched;
    }

    /// <summary>
    /// Hand the wheel back: refused while <paramref name="terminalMidTurn"/> says the terminal is in the middle of a turn
    /// (it is read under the ownership lock), and refused while the protocol already holds the conversation. Otherwise the
    /// user's terminal is ended cleanly (or found already gone), and only once it has exited, Pi's API-mode process starts
    /// again on the same conversation; prompts that waited are then written in order, each with its own receipt. A terminal
    /// that has not exited although it was asked to and then killed stays the user's, and the hand back is refused with
    /// <see cref="TerminalStillRunningReason"/>. <paramref name="beforeResume"/> runs under the ownership lock just before
    /// Pi is started, so whatever it sets cannot land after anything the new Pi reports.
    /// </summary>
    public async Task<WheelSwitchResult> HandBackWheelAsync(Func<bool> terminalMidTurn, Action beforeResume, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminalMidTurn);
        ArgumentNullException.ThrowIfNull(beforeResume);
        FileLog.Write($"[ApiModeSessionBackend] HandBackWheelAsync: session={_sessionId}, holder={_wheelHolder}");
        var host = Host;
        var changed = false;
        await _ownership.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_hasExited)
                return Refuse("hand back", $"Session {_sessionId} has ended, so there is no conversation to hand back.");
            if (_wheelHolder == SessionWheelHolder.Protocol)
                return Refuse("hand back", NotHeldReason);
            if (_terminal is { } terminal)
            {
                if (terminalMidTurn())
                    return Refuse("hand back", TerminalMidTurnReason);
                if (!await EndTerminalLocked(terminal).ConfigureAwait(false))
                    return Refuse("hand back", TerminalStillRunningReason);
                changed = true;
            }

            beforeResume();
            try
            {
                await host.ResumeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ApiModeSessionBackend] HandBackWheelAsync FAILED to start Pi again: session={_sessionId}: {ex.Message}");
                changed = true;
                _wheelHolder = SessionWheelHolder.None;
                SetStatus("No process holds the conversation");
                return Refuse("hand back", $"Pi could not be started again over the protocol ({ex.Message}). No process holds the conversation; hand back again, or take the wheel to open it in the terminal.");
            }
            _wheelHolder = SessionWheelHolder.Protocol;
            SetStatus("Running");
            FileLog.Write($"[ApiModeSessionBackend] HandBackWheelAsync: session={_sessionId}, the protocol holds the conversation again");
        }
        finally
        {
            _ownership.Release();
            if (changed)
                RaiseTerminalChanged();
        }
        return WheelSwitchResult.Switched;
    }

    /// <summary>
    /// Stop the running turn with Pi's own abort, and wait for Pi to confirm it. Null when Pi confirmed (the turn ended
    /// cancelled, or nothing was running); otherwise the true reason nothing was stopped: the user holds the wheel and the
    /// turn runs in the terminal (<see cref="StopWhileUserHoldsReason"/>), no process holds the conversation
    /// (<see cref="StopWithNoHolderReason"/>), or the session has ended.
    /// </summary>
    public async Task<string?> StopTurnAsync(CancellationToken cancellationToken)
    {
        FileLog.Write($"[ApiModeSessionBackend] StopTurnAsync: session={_sessionId}, holder={_wheelHolder}");
        var host = Host;
        // Read under the ownership lock, so a switch under way is seen finished (a take is bounded), never half done; the
        // lock is not held across Pi's answer, which comes only once Pi is idle.
        string? refusal;
        await _ownership.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            refusal = StopRefusal();
        }
        finally
        {
            _ownership.Release();
        }
        if (refusal is not null)
            return RefuseStop(refusal);
        var ended = await host.StopAsync(cancellationToken).ConfigureAwait(false);
        // Nothing answered because Pi was not running: a take began while the stop was on its way.
        if (ended is null && (host.Suspended || StopRefusal() is not null))
            return RefuseStop(StopRefusal() ?? StopWhileUserHoldsReason);
        FileLog.Write($"[ApiModeSessionBackend] StopTurnAsync: session={_sessionId}, Pi confirmed the stop, turn={ended?.TurnId ?? "(none running)"}");
        return null;
    }

    /// <summary>The user's terminal's keystrokes. Refused while the protocol holds the conversation: there is no terminal.</summary>
    public void Write(byte[] data)
    {
        if (_terminal is { } terminal)
        {
            terminal.Write(data);
            return;
        }
        throw NoTerminal("keystrokes");
    }

    /// <summary>The user's terminal's typed text. Refused while the protocol holds the conversation: there is no terminal.</summary>
    public Task SendTextAsync(string text) =>
        _terminal is { } terminal ? terminal.SendTextAsync(text) : throw NoTerminal("typed text");

    /// <summary>The user's terminal's Enter. Refused while the protocol holds the conversation: there is no terminal.</summary>
    public Task SendEnterAsync() =>
        _terminal is { } terminal ? terminal.SendEnterAsync() : throw NoTerminal("an Enter");

    /// <summary>The size the user's terminal has, or will have when the wheel is taken.</summary>
    public void Resize(short cols, short rows)
    {
        _cols = cols;
        _rows = rows;
        _terminal?.Resize(cols, rows);
    }

    /// <summary>End the session: the user's terminal if it is open, then Pi's process (or, with the wheel taken, the
    /// session's record of it). Up to <paramref name="timeoutMs"/> each, then killed.</summary>
    public async Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        FileLog.Write($"[ApiModeSessionBackend] GracefulShutdownAsync: session={_sessionId}, timeout={timeoutMs}ms, holder={_wheelHolder}");
        if (_host is null || _hasExited)
            return;
        var detached = false;
        await _ownership.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_terminal is { } terminal)
            {
                if (!await EndTerminalLocked(terminal).ConfigureAwait(false))
                {
                    // The session is ending, so no Pi will be started beside it: the terminal is let go and disposed, which
                    // kills it if it still runs.
                    FileLog.Write($"[ApiModeSessionBackend] GracefulShutdownAsync: session={_sessionId}, the user's terminal has not exited; disposing it as the session ends");
                    _terminal = null;
                    _wheelHolder = SessionWheelHolder.None;
                    terminal.Dispose();
                }
                detached = true;
            }
        }
        finally
        {
            _ownership.Release();
            if (detached)
                RaiseTerminalChanged();
        }
        await _host.ShutdownAsync(TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None).ConfigureAwait(false);
    }

    public void Dispose()
    {
        FileLog.Write($"[ApiModeSessionBackend] Dispose: session={_sessionId}");
        _terminal?.Dispose();
        _terminal = null;
        ReleaseClaim();
        if (_host is not { } host)
            return;
        host.Exited -= OnHostExited;
        host.Published -= OnHostPublished;
        // Disposing kills Pi if it still runs and waits for the event loop; never on the caller's thread, which may be
        // the user interface's.
        _ = DisposeHostAsync(host);
    }

    // ---------------------------------------------------------------- the terminal

    /// <summary>
    /// End the user's terminal: asked to exit, and killed after <see cref="TerminalShutdownMilliseconds"/>. Under the
    /// ownership lock. True once it has exited: it is then detached and disposed. False when it has not exited yet - the
    /// terminal hosts send the kill and return without waiting for the exit - and then nothing changed: it is still the
    /// user's terminal, and its exit, when it comes, is handled as its own (<see cref="HandleTerminalExitAsync"/>). Its
    /// exit event during the wait is handled only after the lock is released, by which time it is detached and ignored.
    /// </summary>
    private async Task<bool> EndTerminalLocked(ISessionBackend terminal)
    {
        FileLog.Write($"[ApiModeSessionBackend] ending the user's terminal: session={_sessionId}, process {terminal.ProcessId}");
        await terminal.GracefulShutdownAsync(TerminalShutdownMilliseconds).ConfigureAwait(false);
        if (!terminal.HasExited)
        {
            FileLog.Write($"[ApiModeSessionBackend] the user's terminal has NOT exited after its shutdown: session={_sessionId}, process {terminal.ProcessId}; it stays attached");
            return false;
        }
        _terminal = null;
        _wheelHolder = SessionWheelHolder.None;
        terminal.Dispose();
        return true;
    }

    /// <summary>
    /// The user's terminal exited. Expected on a hand back or a shutdown (it was detached first). On its own - the user
    /// quit Pi, or it crashed - the session stays: no process holds the conversation (<see cref="SessionWheelHolder.None"/>),
    /// prompts keep waiting in the queue, and the next take opens the terminal again while the next hand back starts Pi
    /// over the protocol, with nothing to stop.
    /// </summary>
    private void OnTerminalExited(ISessionBackend terminal, int exitCode)
    {
        FileLog.Write($"[ApiModeSessionBackend] the user's terminal exited: session={_sessionId}, exitCode={exitCode}");
        _ = HandleTerminalExitAsync(terminal, exitCode);
    }

    private async Task HandleTerminalExitAsync(ISessionBackend terminal, int exitCode)
    {
        var detached = false;
        try
        {
            await _ownership.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_terminal, terminal))
                    return;
                _terminal = null;
                _wheelHolder = SessionWheelHolder.None;
                detached = true;
                terminal.Dispose();
                SetStatus($"No process holds the conversation (the terminal exited with code {exitCode})");
                FileLog.Write($"[ApiModeSessionBackend] the user's terminal exited on its own: session={_sessionId}; no process holds the conversation until the next take or hand back");
            }
            finally
            {
                _ownership.Release();
            }
        }
        catch (Exception ex)
        {
            // An entry point: the terminal's exit event.
            FileLog.Write($"[ApiModeSessionBackend] HandleTerminalExitAsync FAILED: session={_sessionId}: {ex.Message}");
        }
        if (detached)
            RaiseTerminalChanged();
    }

    // ---------------------------------------------------------------- helpers

    private WheelSwitchResult Refuse(string what, string reason)
    {
        FileLog.Write($"[ApiModeSessionBackend] {what} REFUSED: session={_sessionId}: {reason}");
        return WheelSwitchResult.Refused(reason);
    }

    /// <summary>Why a Stop cannot reach a turn now, or null when the protocol holds the conversation.</summary>
    private string? StopRefusal() =>
        _hasExited ? $"Session {_sessionId} has ended, so there is no turn to stop."
        : _wheelHolder switch
        {
            SessionWheelHolder.User => StopWhileUserHoldsReason,
            SessionWheelHolder.None => StopWithNoHolderReason,
            _ => null,
        };

    private string RefuseStop(string reason)
    {
        FileLog.Write($"[ApiModeSessionBackend] stop REFUSED: session={_sessionId}: {reason}");
        return reason;
    }

    private void RaiseTerminalChanged()
    {
        try
        {
            TerminalChanged?.Invoke();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ApiModeSessionBackend] TerminalChanged handler FAILED: session={_sessionId}: {ex.Message}");
        }
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
        FileLog.Write($"[ApiModeSessionBackend] Exited: session={_sessionId}, exitCode={exitCode}");
        _hasExited = true;
        _wheelHolder = SessionWheelHolder.None;
        ReleaseClaim();
        SetStatus($"Exited ({exitCode})");
        ProcessExited?.Invoke(exitCode);
    }

    private void ReleaseClaim()
    {
        if (Interlocked.Exchange(ref _claimKey, null) is { } key)
            ApiModeConversationClaims.Release(key, _sessionId);
    }

    private void OnHostPublished(IReadOnlyList<SessionEvent> events) => EventsPublished?.Invoke(events);

    private void SetStatus(string status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    private InvalidOperationException NoTerminal(string what)
    {
        FileLog.Write($"[ApiModeSessionBackend] refused {what}: session={_sessionId} is in API mode and the protocol holds the conversation");
        return new InvalidOperationException(
            $"Session {_sessionId} is in API mode and its protocol holds the conversation: it has no terminal, so {what} cannot be sent. Prompts go through the session's send; take the wheel to type into its terminal.");
    }
}
