using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CcDirector.Core.ApiMode.Pi;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.Core.ApiMode;

/// <summary>What became of one prompt sent to an API-mode session: the agent's receipt, or why there is none.</summary>
/// <param name="PromptId">The Director's id for the prompt.</param>
/// <param name="Delivered">True when the agent acknowledged it (<see cref="PromptDelivered"/>).</param>
/// <param name="RefusalReason">Why it was refused (<see cref="PromptRefused.Reason"/>), or null when delivered.</param>
/// <param name="RefusedBy">Who refused it, or null when delivered.</param>
public sealed record ApiModePromptReceipt(string PromptId, bool Delivered, string? RefusalReason, PromptRefusedBy? RefusedBy);

/// <summary>The defaults an API-mode session runs with.</summary>
public static class ApiModeSessionDefaults
{
    /// <summary>How long Pi gets to exit after its standard input closes before it is killed.</summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a prompt Pi acknowledged as <c>started</c> may wait for its turn to begin (<c>agent_start</c>) before the
    /// Director gives that turn up as failed and restarts Pi (the Delivery Lead's ruling of 16 October 2026 on review
    /// finding 1 of pull request #4076). Pi promises no turn after its acknowledgement.
    /// </summary>
    public static readonly TimeSpan TurnStartBound = TimeSpan.FromSeconds(60);
}

/// <summary>
/// One API-mode session's agent (API mode mission, issue #4070, phase 1): a long-lived Pi process
/// (<c>pi --mode rpc</c>) whose every record is pumped into <see cref="PiDialect"/>, every command the dialect asks for
/// written, and every session event handed to the session's <see cref="SessionEventStream"/>.
///
/// It owns the Director's prompt queue. A prompt is written only when the dialect says one may be
/// (<see cref="PiDialect.CanWritePrompt"/>: Pi has named its conversation and no prompt is in flight); otherwise it is
/// held and reported <see cref="PromptQueued"/>. Its receipt is Pi's own acknowledgement - <see cref="PromptDelivered"/>
/// or <see cref="PromptRefused"/> - never an echo on a screen.
///
/// One Pi process at a time is the session's connection. When Pi acknowledges a prompt and its turn does not begin
/// within <see cref="ApiModeSessionDefaults.TurnStartBound"/> on the injected clock, the dialect ends that turn failed
/// (<see cref="PiDialect.OnTurnStartOverdue"/>) and the host restarts Pi on the same conversation, in the same folder,
/// with the same arguments; the prompt is never sent again on its own. Only an end that is not such a restart ends the
/// session (<see cref="Ended"/>, <see cref="Exited"/>).
///
/// Everything that touches a dialect, the queue or the stream runs under one lock, from one place at a time: the event
/// loop, a send, a stop, the timer. A command's line is written and reported to the dialect inside that lock, so Pi's
/// response to it can never reach the dialect first.
/// </summary>
public sealed class ApiModeSessionHost : IAsyncDisposable
{
    /// <summary>The reason a prompt still queued when Pi's process ended is refused (it was never written).</summary>
    public const string ExitedBeforeWritingReason = "Pi exited before the prompt could be sent";

    private readonly string _sessionId;
    private readonly string _agentSessionId;
    private readonly bool _resuming;
    private readonly Func<IApiModeProcess> _processFactory;
    private readonly SessionEventStream _stream;
    private readonly TimeProvider _clock;
    private readonly ApiModeTransportOptions? _transportOptions;
    private readonly TimeSpan _turnStartBound;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<QueuedPrompt> _queue = [];
    private readonly Dictionary<string, TaskCompletionSource<ApiModePromptReceipt>> _awaitingReceipt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StopWaiter> _stops = new(StringComparer.Ordinal);

    // Every prompt id this session has used, so none is used twice (a Gateway id and a minted one included).
    private readonly HashSet<string> _usedPromptIds = new(StringComparer.Ordinal);
    private readonly List<string> _defects = [];
    private readonly List<Task> _loops = [];
    private readonly TaskCompletionSource<ConnectionEnded> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Every published batch, in order, for Published's handlers - run on their own loop, outside the lock, so a
    // handler that calls back into the host can never wait on the lock it was called under. The session's end is
    // reported from that loop too, after the last batch.
    private readonly Channel<IReadOnlyList<SessionEvent>> _observed =
        Channel.CreateUnbounded<IReadOnlyList<SessionEvent>>(new UnboundedChannelOptions { SingleReader = true });
    private Task _observer = Task.CompletedTask;
    private ApiModeProcessStartInfo? _startInfo;
    private Connection? _connection;
    private ConnectionEnded? _sessionEnded;
    private bool _conversationHasMessages;
    private int _restarts;

    /// <param name="sessionId">The DevThrottle session.</param>
    /// <param name="agentSessionId">The id Pi is started with (<c>--session-id</c>).</param>
    /// <param name="resuming">True when the session resumes an existing Pi conversation: a Pi that finds none fails.</param>
    /// <param name="processFactory">Makes each Pi process (the real one, or a test's fake): the first, and one per restart.</param>
    /// <param name="stream">The session's event stream, shared by every Pi process of the session.</param>
    /// <param name="clock">Where every event's time and the turn-start bound come from.</param>
    /// <param name="transportOptions">Transport tuning, or null for the defaults.</param>
    /// <param name="turnStartBound">How long an acknowledged prompt may wait for its turn; null for
    /// <see cref="ApiModeSessionDefaults.TurnStartBound"/>.</param>
    public ApiModeSessionHost(string sessionId, string agentSessionId, bool resuming, Func<IApiModeProcess> processFactory,
        SessionEventStream stream, TimeProvider clock, ApiModeTransportOptions? transportOptions = null, TimeSpan? turnStartBound = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentSessionId);
        ArgumentNullException.ThrowIfNull(processFactory);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(clock);
        _sessionId = sessionId;
        _agentSessionId = agentSessionId;
        _resuming = resuming;
        _conversationHasMessages = resuming;
        _processFactory = processFactory;
        _stream = stream;
        _clock = clock;
        _transportOptions = transportOptions;
        _turnStartBound = turnStartBound ?? ApiModeSessionDefaults.TurnStartBound;
        FileLog.Write($"[ApiModeSessionHost] Created: session={sessionId}, agentSession={agentSessionId}, resuming={resuming}, generation={stream.Generation}");
    }

    /// <summary>The current Pi process's id, or 0 before it starts.</summary>
    public int ProcessId => _connection?.Transport.ProcessId ?? 0;

    /// <summary>How many times Pi was restarted because a turn never began.</summary>
    public int Restarts => Volatile.Read(ref _restarts);

    /// <summary>Completes once the session's Pi has ended for good (not for a restart) and every event before the end
    /// was published. Carries the last <see cref="ConnectionEnded"/>, whose reason says why.</summary>
    public Task<ConnectionEnded> Ended => _ended.Task;

    /// <summary>Raised once, after <see cref="Ended"/> and after the last <see cref="Published"/>, with the exit code the
    /// last process reported.</summary>
    public event Action<int>? Exited;

    /// <summary>Raised with each batch of events the stream published, in order, on one loop outside the host's lock.
    /// The session reads its activity from it (a turn running or not).</summary>
    public event Action<IReadOnlyList<SessionEvent>>? Published;

    /// <summary>Dialect defects seen so far (each already logged): our own code or Pi's record shape is wrong.</summary>
    public IReadOnlyList<string> Defects
    {
        get { lock (_defects) return _defects.ToList(); }
    }

    /// <summary>
    /// Start Pi and begin pumping its records; then write <c>get_state</c>, the first command, so Pi names its
    /// conversation before any prompt goes. Throws <see cref="ApiModeStartException"/>, naming the executable, when
    /// the process cannot be started. A host is started once.
    /// </summary>
    public void Start(ApiModeProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        FileLog.Write($"[ApiModeSessionHost] Start: session={_sessionId}, {startInfo.Describe()}");
        if (Interlocked.CompareExchange(ref _startInfo, startInfo, null) is not null)
            throw new InvalidOperationException($"The API-mode host of session {_sessionId} was already started.");
        StartConnection(_resuming);
        _observer = Task.Run(ObserveAsync);
    }

    /// <summary>
    /// Send one prompt only if it can be written to Pi now - Pi has named its conversation, no prompt is in flight and
    /// none is queued - and return its receipt; return null, having sent and queued nothing, when it cannot. For a send
    /// that must never wait behind other work (a guarded send).
    /// </summary>
    public async Task<ApiModePromptReceipt?> TrySendPromptNowAsync(string text, CancellationToken cancellationToken) =>
        await TrySendPromptNowAsync(text, gatewayPromptId: null, cancellationToken).ConfigureAwait(false);

    /// <summary>As <see cref="TrySendPromptNowAsync(string, CancellationToken)"/>, carrying the Gateway's id for the
    /// prompt when it has one.</summary>
    public async Task<ApiModePromptReceipt?> TrySendPromptNowAsync(string text, string? gatewayPromptId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        FileLog.Write($"[ApiModeSessionHost] TrySendPromptNowAsync: session={_sessionId}, length={text.Length}");
        TaskCompletionSource<ApiModePromptReceipt> receipt;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionEnded is not null || _connection is not { Ended: false } connection || _queue.Count > 0 || !connection.CanWritePrompt)
            {
                FileLog.Write($"[ApiModeSessionHost] TrySendPromptNowAsync: session={_sessionId}, Pi cannot take a prompt now; nothing sent");
                return null;
            }
            var promptId = ResolvePromptIdLocked(gatewayPromptId);
            receipt = new TaskCompletionSource<ApiModePromptReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            _awaitingReceipt[promptId] = receipt;
            await WritePromptLocked(connection, new QueuedPrompt(promptId, text)).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        var result = await receipt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileLog.Write($"[ApiModeSessionHost] TrySendPromptNowAsync: session={_sessionId}, prompt={result.PromptId}, " +
                      (result.Delivered ? "delivered" : $"REFUSED by {result.RefusedBy}: {result.RefusalReason}"));
        return result;
    }

    /// <summary>
    /// Send one prompt. It is written now when Pi may take it, or held in the Director's queue (<see cref="PromptQueued"/>)
    /// until the prompt in flight settles or Pi names its conversation. Completes with Pi's receipt: delivered, or
    /// refused with the reason (Pi's own words, or the Director's when Pi never answered).
    /// </summary>
    public Task<ApiModePromptReceipt> SendPromptAsync(string text, CancellationToken cancellationToken) =>
        SendPromptAsync(text, gatewayPromptId: null, cancellationToken);

    /// <summary>
    /// Send one prompt, as <see cref="SendPromptAsync(string, CancellationToken)"/>, carrying the Gateway's own id for it
    /// (its delivery id) when it has one: that id is the prompt id on every event about the prompt, so its receipt joins
    /// to the prompt the user sent (<see cref="ResolvePromptIdLocked"/>).
    /// </summary>
    public async Task<ApiModePromptReceipt> SendPromptAsync(string text, string? gatewayPromptId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        FileLog.Write($"[ApiModeSessionHost] SendPromptAsync: session={_sessionId}, length={text.Length}, gatewayPromptId={gatewayPromptId ?? "(none)"}");
        TaskCompletionSource<ApiModePromptReceipt> receipt;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var promptId = ResolvePromptIdLocked(gatewayPromptId);
            receipt = new TaskCompletionSource<ApiModePromptReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            _awaitingReceipt[promptId] = receipt;

            if (_sessionEnded is not null || _connection is null)
            {
                // Nothing can take it: say so in a refusal, so the prompt still has its one receipt.
                var why = _sessionEnded?.Reason ?? "it was never started";
                TakeEventsLocked([RefusedByDirector(promptId, $"Pi is not running: {why}")]);
            }
            else if (_queue.Count == 0 && !_connection.Ended && _connection.CanWritePrompt)
            {
                await WritePromptLocked(_connection, new QueuedPrompt(promptId, text)).ConfigureAwait(false);
            }
            else
            {
                _queue.Add(new QueuedPrompt(promptId, text));
                var reason = _connection.Ready && !_connection.Ended ? PromptQueuedReason.TurnRunning : PromptQueuedReason.AgentStarting;
                FileLog.Write($"[ApiModeSessionHost] Queued: session={_sessionId}, prompt={promptId}, reason={reason}, position={_queue.Count}");
                TakeEventsLocked([new PromptQueued
                {
                    SessionId = _sessionId, Sequence = _stream.Numbering.NextSequence(), At = Now,
                    PromptId = promptId, Reason = reason, Position = _queue.Count,
                }]);
            }
        }
        finally
        {
            _lock.Release();
        }

        var result = await receipt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileLog.Write($"[ApiModeSessionHost] SendPromptAsync: session={_sessionId}, prompt={result.PromptId}, " +
                      (result.Delivered ? "delivered" : $"REFUSED by {result.RefusedBy}: {result.RefusalReason}"));
        return result;
    }

    /// <summary>
    /// Stop: write Pi's <c>abort</c> and wait for Pi to answer it, which Pi does only once it is idle. Returns the turn
    /// that ended meanwhile (<see cref="TurnOutcome.Cancelled"/> when Pi cut it short), or null when nothing was
    /// running - an abort while idle is harmless and emits nothing. Returns at once with null when Pi is not running.
    /// </summary>
    public async Task<TurnEnded?> StopAsync(CancellationToken cancellationToken)
    {
        FileLog.Write($"[ApiModeSessionHost] StopAsync: session={_sessionId}");
        StopWaiter waiter;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionEnded is not null || _connection is not { Ended: false } connection)
            {
                FileLog.Write($"[ApiModeSessionHost] StopAsync: session={_sessionId}, Pi is not running; nothing to stop");
                return null;
            }
            var abort = connection.Dialect.BuildAbort();
            waiter = new StopWaiter();
            _stops[abort.Id] = waiter;
            if (!await WriteCommandLocked(connection, abort).ConfigureAwait(false))
            {
                _stops.Remove(abort.Id);
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }

        var ended = await waiter.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileLog.Write($"[ApiModeSessionHost] StopAsync: session={_sessionId}, turn={ended?.TurnId ?? "(none running)"}, outcome={ended?.Outcome.ToString() ?? "(none)"}");
        return ended;
    }

    /// <summary>
    /// Ask Pi to exit by closing its standard input, kill it if it has not ended within <paramref name="grace"/>, and
    /// wait until every event up to the last <see cref="ConnectionEnded"/> was published. No restart follows.
    /// </summary>
    public async Task<ConnectionEnded> ShutdownAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        FileLog.Write($"[ApiModeSessionHost] ShutdownAsync: session={_sessionId}, grace={grace.TotalMilliseconds}ms");
        Connection connection;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            connection = _connection
                ?? throw new InvalidOperationException($"The API-mode host of session {_sessionId} was never started.");
            connection.ShuttingDown = true;
        }
        finally
        {
            _lock.Release();
        }

        await connection.Transport.ShutdownAsync(grace, cancellationToken).ConfigureAwait(false);
        var ended = await _ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileLog.Write($"[ApiModeSessionHost] ShutdownAsync: session={_sessionId}, ended: {ended.Reason}");
        return ended;
    }

    /// <summary>Kill Pi if it still runs, wait for the event loops to finish, and release the processes.</summary>
    public async ValueTask DisposeAsync()
    {
        FileLog.Write($"[ApiModeSessionHost] DisposeAsync: session={_sessionId}");
        Connection? connection;
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            connection = _connection;
            if (connection is not null)
                connection.ShuttingDown = true;
        }
        finally
        {
            _lock.Release();
        }

        if (connection is not null)
            await connection.Transport.DisposeAsync().ConfigureAwait(false);
        Task[] loops;
        lock (_loops)
            loops = _loops.ToArray();
        await Task.WhenAll(loops).ConfigureAwait(false);
        await _observer.ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- connections

    private void StartConnection(bool resuming)
    {
        var dialect = new PiDialect(_sessionId, _agentSessionId, resuming, _clock, _stream.Numbering);
        var transport = new ApiModeTransport(_processFactory(), _clock, _transportOptions);
        var connection = new Connection(transport, dialect);
        transport.Start(_startInfo!);
        _connection = connection;
        lock (_loops)
        {
            _loops.Add(Task.Run(() => PumpAsync(connection)));
            _loops.Add(Task.Run(() => ConnectAsync(connection)));
        }
        FileLog.Write($"[ApiModeSessionHost] StartConnection: session={_sessionId}, Pi running as process {transport.ProcessId}, resuming={resuming}");
    }

    private async Task ConnectAsync(Connection connection)
    {
        try
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!connection.Ended)
                    await WriteCommandLocked(connection, connection.Dialect.BuildGetState()).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            // An entry point: nobody awaits it. A failure leaves Pi unnamed, so no prompt is ever written; the end of
            // the process still refuses every one.
            FileLog.Write($"[ApiModeSessionHost] ConnectAsync FAILED: session={_sessionId}: {ex}");
            AddDefect($"get_state could not be written: {ex.Message}");
        }
    }

    private async Task PumpAsync(Connection connection)
    {
        try
        {
            await foreach (var item in connection.Transport.Events.ReadAllAsync().ConfigureAwait(false))
            {
                await _lock.WaitAsync().ConfigureAwait(false);
                try
                {
                    switch (item)
                    {
                        case ApiModeRecord record:
                            await OnRecordLocked(connection, record).ConfigureAwait(false);
                            break;
                        case ApiModeProcessEnded end:
                            OnProcessEndedLocked(connection, end);
                            break;
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }
        }
        catch (Exception ex)
        {
            // The loop is an entry point: a failure here is a defect in this host. Pi is stopped so the session does
            // not run on with nobody reading it, and its end is still reported, so every prompt gets its receipt and
            // the session learns that the process is gone.
            FileLog.Write($"[ApiModeSessionHost] PumpAsync FAILED: session={_sessionId}: {ex}");
            AddDefect($"the event loop failed: {ex.Message}");
            await ReportEndAfterLoopFailureAsync(connection).ConfigureAwait(false);
        }

        ConnectionEnded? sessionEnded;
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!connection.Ended)
                throw new InvalidOperationException($"The transport of session {_sessionId} ended without reporting the end of the process.");
            // A process that ended for a restart is not the session's end: the next one carries on.
            sessionEnded = ReferenceEquals(connection, _connection) ? _sessionEnded : null;
        }
        finally
        {
            _lock.Release();
        }

        // The observer loop reports the end once every batch before it has reached Published's handlers.
        if (sessionEnded is not null)
            _observed.Writer.TryComplete();
    }

    private async Task ObserveAsync()
    {
        await foreach (var batch in _observed.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Published?.Invoke(batch);
            }
            catch (Exception ex)
            {
                // An entry point for each handler's work: one that fails must not stop the session's own reporting.
                FileLog.Write($"[ApiModeSessionHost] Published handler FAILED: session={_sessionId}: {ex.Message}");
            }
        }

        var ended = _sessionEnded
            ?? throw new InvalidOperationException($"The observer of session {_sessionId} finished before the session ended.");
        _ended.TrySetResult(ended);
        try
        {
            Exited?.Invoke(ended.ExitCode);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ApiModeSessionHost] Exited handler FAILED: session={_sessionId}: {ex.Message}");
        }
    }

    private async Task ReportEndAfterLoopFailureAsync(Connection connection)
    {
        connection.ShuttingDown = true;
        var end = await connection.Transport.ShutdownAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!connection.Ended)
                OnProcessEndedLocked(connection, end);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task OnRecordLocked(Connection connection, ApiModeRecord record)
    {
        if (record.Json is not { } json)
        {
            AddDefect($"Pi wrote a line that is not JSON ({record.ParseError}): {Truncate(record.RawLine)}");
            return;
        }

        // Standard error is read on its own pipe; the lines read so far go to the dialect before this record, so a
        // failure reason holds the lines of its own turn. Skipped for the stream of reply deltas, the bulk of the wire.
        if (!IsMessageUpdate(json))
            FeedErrorLinesLocked(connection);

        PiDialectStep step;
        try
        {
            step = connection.Dialect.OnStdoutRecord(json);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // A record missing a field the dialect must read: Pi's shape changed. Stop this Pi process; its end still
            // refuses every prompt without a receipt and ends any running turn (the dialect's contract).
            FileLog.Write($"[ApiModeSessionHost] OnRecord FAILED: session={_sessionId}, record {record.Sequence}: {ex.Message}");
            AddDefect($"the dialect could not read record {record.Sequence}: {ex.Message}");
            connection.ShuttingDown = true;
            _ = StopProcessAsync(connection, $"the Director could not read Pi's record: {ex.Message}");
            return;
        }

        // A turn Pi itself began means the conversation now holds messages, so a restart resumes it.
        if (step.Events.Any(e => e is TurnStarted))
            _conversationHasMessages = true;

        await TakeStepLocked(connection, step).ConfigureAwait(false);
        await WriteQueuedPromptsLocked(connection).ConfigureAwait(false);
    }

    private void OnProcessEndedLocked(Connection connection, ApiModeProcessEnded end)
    {
        FeedErrorLinesLocked(connection);
        connection.DisarmTurnStartTimer();
        var restart = connection.RestartAfterEnd && !connection.ShuttingDown;

        // A prompt still queued was never written, so the dialect knows nothing of it. When Pi is restarted it waits for
        // the new process; otherwise it is refused here, first, with why the process ended (for a resume that found
        // nothing, that). The dialect then refuses every prompt it had written and ends any running turn, and its
        // ConnectionEnded is the last event of this process.
        if (!restart)
            RefuseQueuedLocked(connection.Dialect.StopReason ?? $"Pi exited ({end.Describe()})");

        if (end.ExitCode is null)
            FileLog.Write($"[ApiModeSessionHost] OnProcessEnded: session={_sessionId}, the exit code could not be read ({end.TransportFault}); reported as -1");
        var step = connection.Dialect.OnProcessExited(end.ExitCode ?? -1);
        TakeEventsLocked(step.Events);
        AddDefects(step.Defects);
        var connectionEnded = (ConnectionEnded)step.Events[^1];
        connection.Ended = true;

        foreach (var stop in _stops.Values)
            stop.Completion.TrySetResult(stop.TurnEnded);
        _stops.Clear();
        var queued = _queue.Select(q => q.PromptId).ToHashSet(StringComparer.Ordinal);
        foreach (var (promptId, waiting) in _awaitingReceipt.Where(a => !queued.Contains(a.Key)).ToList())
        {
            // Every written prompt has had its receipt by now (rule 1). One that has not is a defect, and still gets an answer.
            AddDefect($"prompt {promptId} reached the end of Pi's process with no receipt");
            _awaitingReceipt.Remove(promptId);
            waiting.TrySetResult(new ApiModePromptReceipt(promptId, false, connectionEnded.Reason, PromptRefusedBy.Director));
        }
        FileLog.Write($"[ApiModeSessionHost] OnProcessEnded: session={_sessionId}, {connectionEnded.Reason}, restart={restart}");

        if (restart)
        {
            try
            {
                FileLog.Write($"[ApiModeSessionHost] Restarting Pi: session={_sessionId}, conversation={_agentSessionId}, resuming={_conversationHasMessages}, queued={_queue.Count}; the overdue prompt is not sent again");
                StartConnection(_conversationHasMessages);
                Interlocked.Increment(ref _restarts);
                return;
            }
            catch (Exception ex)
            {
                // The restart could not start Pi: the session ends here, with both reasons.
                FileLog.Write($"[ApiModeSessionHost] Restarting Pi FAILED: session={_sessionId}: {ex.Message}");
                connectionEnded = connectionEnded with { Reason = $"{connectionEnded.Reason}. Restarting Pi failed: {ex.Message}" };
                RefuseQueuedLocked(connectionEnded.Reason);
            }
        }

        _sessionEnded = connectionEnded;
    }

    private void RefuseQueuedLocked(string why)
    {
        var refusals = _queue.Select(q => (SessionEvent)RefusedByDirector(q.PromptId, $"{ExitedBeforeWritingReason}: {why}")).ToList();
        _queue.Clear();
        TakeEventsLocked(refusals);
    }

    // ---------------------------------------------------------------- steps and writes

    private async Task TakeStepLocked(Connection connection, PiDialectStep step)
    {
        TakeEventsLocked(step.Events);
        AddDefects(step.Defects);

        foreach (var command in step.CommandsToWrite)
            await WriteCommandLocked(connection, command).ConfigureAwait(false);

        if (step.Result is { } result && _stops.Remove(result.CommandId, out var stop))
            stop.Completion.TrySetResult(stop.TurnEnded);

        if (step.StopReason is { } stopReason)
            _ = StopProcessAsync(connection, stopReason);

        ReconcileTurnStartTimerLocked(connection);
    }

    private void TakeEventsLocked(IReadOnlyList<SessionEvent> events)
    {
        _stream.Publish(events);
        if (events.Count > 0)
            _observed.Writer.TryWrite(events);
        foreach (var e in events)
        {
            switch (e)
            {
                case ConnectionReady when _connection is { } current:
                    current.Ready = true;
                    break;
                case PromptDelivered delivered when _awaitingReceipt.Remove(delivered.PromptId, out var waiting):
                    waiting.TrySetResult(new ApiModePromptReceipt(delivered.PromptId, true, null, null));
                    break;
                case PromptRefused refused when _awaitingReceipt.Remove(refused.PromptId, out var waiting):
                    waiting.TrySetResult(new ApiModePromptReceipt(refused.PromptId, false, refused.Reason, refused.RefusedBy));
                    break;
                case TurnEnded turnEnded:
                    foreach (var stop in _stops.Values)
                        stop.TurnEnded = turnEnded;
                    break;
            }
        }
    }

    private async Task WriteQueuedPromptsLocked(Connection connection)
    {
        while (_queue.Count > 0 && !connection.Ended && connection.CanWritePrompt)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            if (!await WritePromptLocked(connection, next).ConfigureAwait(false))
                return;
        }
    }

    private async Task<bool> WritePromptLocked(Connection connection, QueuedPrompt prompt)
    {
        var command = connection.Dialect.BuildPrompt(prompt.PromptId, prompt.Text);
        if (await WriteCommandLocked(connection, command).ConfigureAwait(false))
            return true;

        // Not written: the process is gone. It goes back to the head of the queue, and the end settles it.
        _queue.Insert(0, prompt);
        return false;
    }

    /// <summary>Write one command and tell the dialect. False when Pi's process had already ended, so nothing was written.</summary>
    private async Task<bool> WriteCommandLocked(Connection connection, PiCommand command)
    {
        var line = JsonNode.Parse(command.Line) as JsonObject
            ?? throw new InvalidOperationException($"The dialect built a command that is not a JSON object: {command.Type}");
        try
        {
            await connection.Transport.WriteRecordAsync(line, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ApiModeConnectionEndedException ex)
        {
            // not-an-error: Pi ended first; the end of the process, which follows, settles everything waiting
            FileLog.Write($"[ApiModeSessionHost] WriteCommand: session={_sessionId}, {command.Type} {command.Id} not written: {ex.Message}");
            return false;
        }
        await TakeStepLocked(connection, connection.Dialect.OnCommandWritten(command)).ConfigureAwait(false);
        return true;
    }

    // ---------------------------------------------------------------- prompt ids

    /// <summary>
    /// The prompt id every event about this prompt carries (the Delivery Lead's ruling, 16 October 2026: a receipt joins
    /// to the prompt the user sent). A prompt that came from the Gateway with its delivery id carries THAT id. A prompt
    /// with none - typed in the desktop, a typed Gateway prompt with no delivery id, a fleet message, the framework's own
    /// text - gets a Director id, <c>prompt-1</c>, <c>prompt-2</c>, ... Ids are unique within the session: a minted id
    /// skips any already used, and a Gateway id sent a second time (a retry of a delivery the Director refused) carries
    /// <c>&lt;id&gt;:attempt-2</c>, <c>:attempt-3</c>, ... so each attempt has its own receipt and still names its delivery.
    /// </summary>
    private string ResolvePromptIdLocked(string? gatewayPromptId)
    {
        string promptId;
        if (string.IsNullOrWhiteSpace(gatewayPromptId))
        {
            do promptId = _stream.NextPromptId(); while (_usedPromptIds.Contains(promptId));
        }
        else
        {
            promptId = gatewayPromptId;
            for (var attempt = 2; _usedPromptIds.Contains(promptId); attempt++)
                promptId = $"{gatewayPromptId}:attempt-{attempt}";
            if (promptId != gatewayPromptId)
                FileLog.Write($"[ApiModeSessionHost] ResolvePromptId: session={_sessionId}, Gateway id {gatewayPromptId} was already used in this session; this attempt is {promptId}");
        }
        _usedPromptIds.Add(promptId);
        return promptId;
    }

    // ---------------------------------------------------------------- the turn-start bound

    /// <summary>Arm the bound when Pi has acknowledged a prompt whose turn has not begun; disarm it once none waits.</summary>
    private void ReconcileTurnStartTimerLocked(Connection connection)
    {
        if (connection.Ended)
            return;
        var waiting = connection.Dialect.PromptsAwaitingTurn.Count > 0;
        if (waiting && connection.TurnStartTimer is null)
        {
            FileLog.Write($"[ApiModeSessionHost] Turn-start bound armed: session={_sessionId}, prompts={string.Join(",", connection.Dialect.PromptsAwaitingTurn)}, bound={_turnStartBound.TotalSeconds}s");
            connection.TurnStartTimer = _clock.CreateTimer(_ => _ = OnTurnStartOverdueAsync(connection), null, _turnStartBound, Timeout.InfiniteTimeSpan);
        }
        else if (!waiting)
        {
            connection.DisarmTurnStartTimer();
        }
    }

    private async Task OnTurnStartOverdueAsync(Connection connection)
    {
        try
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                connection.DisarmTurnStartTimer();
                if (connection.Ended || connection.ShuttingDown || connection.Dialect.PromptsAwaitingTurn.Count == 0)
                    return;
                FileLog.Write($"[ApiModeSessionHost] Turn-start bound ran out: session={_sessionId}, prompts={string.Join(",", connection.Dialect.PromptsAwaitingTurn)}; Pi is restarted on the same conversation");
                // The dialect ends the waiting prompts' turn failed and asks for this process to stop; the end of the
                // process then starts a new one on the same conversation.
                connection.RestartAfterEnd = true;
                await TakeStepLocked(connection, connection.Dialect.OnTurnStartOverdue()).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            // An entry point: the timer's callback. Nobody awaits it.
            FileLog.Write($"[ApiModeSessionHost] OnTurnStartOverdueAsync FAILED: session={_sessionId}: {ex}");
            AddDefect($"the turn-start bound could not be acted on: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task StopProcessAsync(Connection connection, string reason)
    {
        try
        {
            FileLog.Write($"[ApiModeSessionHost] StopProcessAsync: session={_sessionId}, reason={reason}");
            await connection.Transport.ShutdownAsync(ApiModeSessionDefaults.ShutdownGrace, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An entry point: nobody awaits it. The transport's own disposal still kills the process.
            FileLog.Write($"[ApiModeSessionHost] StopProcessAsync FAILED: session={_sessionId}: {ex.Message}");
        }
    }

    private void FeedErrorLinesLocked(Connection connection)
    {
        var since = connection.Transport.ErrorLinesSince(connection.ErrorCursor);
        foreach (var line in since.Lines)
        {
            connection.ErrorCursor = line.Sequence + 1;
            var step = connection.Dialect.OnStderrLine(line.Text);
            TakeEventsLocked(step.Events);
            AddDefects(step.Defects);
            if (step.StopReason is { } stopReason)
                _ = StopProcessAsync(connection, stopReason);
        }
    }

    private PromptRefused RefusedByDirector(string promptId, string reason)
    {
        FileLog.Write($"[ApiModeSessionHost] Refused: session={_sessionId}, prompt={promptId}, reason={reason}");
        return new PromptRefused
        {
            SessionId = _sessionId, Sequence = _stream.Numbering.NextSequence(), At = Now,
            PromptId = promptId, Reason = reason, RefusedBy = PromptRefusedBy.Director,
        };
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    private void AddDefect(string defect)
    {
        FileLog.Write($"[ApiModeSessionHost] DEFECT: session={_sessionId}, {defect}");
        lock (_defects)
            _defects.Add(defect);
    }

    private void AddDefects(IReadOnlyList<string> defects)
    {
        // Already logged by the dialect.
        if (defects.Count == 0)
            return;
        lock (_defects)
            _defects.AddRange(defects);
    }

    private static bool IsMessageUpdate(JsonElement json) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && type.ValueEquals("message_update");

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "...";

    private sealed record QueuedPrompt(string PromptId, string Text);

    private sealed class StopWaiter
    {
        public TaskCompletionSource<TurnEnded?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TurnEnded? TurnEnded { get; set; }
    }

    /// <summary>One Pi process of the session and its dialect. Touched only under the host's lock.</summary>
    private sealed class Connection(ApiModeTransport transport, PiDialect dialect)
    {
        public ApiModeTransport Transport { get; } = transport;
        public PiDialect Dialect { get; } = dialect;
        public long ErrorCursor { get; set; } = 1;
        public bool Ready { get; set; }
        public bool Ended { get; set; }

        /// <summary>The host is ending this process on purpose (shutdown, disposal, a record it could not read): no
        /// restart, whatever else asked for one.</summary>
        public bool ShuttingDown { get; set; }

        /// <summary>The turn-start bound ran out: when this process has ended, start a new one on the same conversation.</summary>
        public bool RestartAfterEnd { get; set; }

        public ITimer? TurnStartTimer { get; set; }

        public bool CanWritePrompt => Ready && Dialect.CanWritePrompt;

        public void DisarmTurnStartTimer()
        {
            TurnStartTimer?.Dispose();
            TurnStartTimer = null;
        }
    }
}
