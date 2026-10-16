using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.ApiMode;

/// <summary>Tuning for an <see cref="ApiModeTransport"/>. The defaults suit every agent seen so far.</summary>
public sealed record ApiModeTransportOptions
{
    /// <summary>The property that carries a request's id, which the agent repeats on its response.</summary>
    public string IdPropertyName { get; init; } = "id";

    /// <summary>The largest record accepted. A longer line is a transport failure, and the process is killed.</summary>
    public int MaximumLineBytes { get; init; } = NewlineJsonFramer.DefaultMaximumLineBytes;

    /// <summary>How many of the most recent standard error lines are held.</summary>
    public int ErrorLinesHeld { get; init; } = 2000;

    /// <summary>
    /// After the process exits, how long to wait for its standard output and error to reach their end. A
    /// grandchild that inherited the pipes can hold them open after the agent itself is gone; the transport
    /// must still report the end.
    /// </summary>
    public TimeSpan DrainGrace { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The agent-neutral API-mode transport: one long-lived agent process per session, spoken to in
/// newline-delimited JSON over its standard input and output. It knows nothing about any one agent; a
/// dialect on top of it turns the records into session events.
///
/// What it does:
/// - Reads standard output continuously, so the agent never stalls on a full pipe, and cuts it into records
///   on line feed only (<see cref="NewlineJsonFramer"/>).
/// - Writes one JSON object per line, one write at a time.
/// - Matches a response to the request waiting for it by id (<see cref="SendRequestAsync"/>). A request still
///   waiting when the process ends fails with <see cref="ApiModeConnectionEndedException"/>.
/// - Delivers EVERY record, matched or not, to one consumer through <see cref="Events"/>, in wire order, and
///   then exactly one <see cref="ApiModeProcessEnded"/> as the last event.
/// - Holds standard error lines with a sequence number, so a failure reason can use only the lines written
///   since a marker (<see cref="MarkError"/>, <see cref="ErrorLinesSince"/>).
/// </summary>
public sealed class ApiModeTransport : IAsyncDisposable
{
    private readonly IApiModeProcess _process;
    private readonly TimeProvider _timeProvider;
    private readonly ApiModeTransportOptions _options;
    private readonly Channel<ApiModeTransportEvent> _events =
        Channel.CreateUnbounded<ApiModeTransportEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<ApiModeProcessEnded> _ended =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _readCancellation = new();

    // Guards _pending, _endedRecord, _recordSequence and every write to _events, so no record can follow the end.
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private ApiModeProcessEnded? _endedRecord;
    private long _recordSequence;

    private readonly object _errorGate = new();
    private readonly Queue<ApiModeErrorLine> _errorLines = new();
    private long _errorSequence;

    private long _nextRequestNumber;
    private int _started;
    private Task _outputLoop = Task.CompletedTask;
    private Task _errorLoop = Task.CompletedTask;
    private string? _outputFault;
    private string? _unterminatedOutput;

    public ApiModeTransport(IApiModeProcess process, TimeProvider? timeProvider = null, ApiModeTransportOptions? options = null)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new ApiModeTransportOptions();
        if (_options.ErrorLinesHeld <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), _options.ErrorLinesHeld, "At least one standard error line must be held.");
    }

    /// <summary>
    /// Every record in wire order, then one <see cref="ApiModeProcessEnded"/>, then the channel completes.
    /// For exactly one consumer.
    /// </summary>
    public ChannelReader<ApiModeTransportEvent> Events => _events.Reader;

    /// <summary>Completes when the process has ended and every record before the end has been delivered.</summary>
    public Task<ApiModeProcessEnded> Ended => _ended.Task;

    /// <summary>The agent process's id, or 0 before it starts.</summary>
    public int ProcessId => _process.ProcessId;

    /// <summary>
    /// Start the agent process and begin reading it. Throws <see cref="ApiModeStartException"/> when the process
    /// cannot be started, naming the executable and why. A transport is started once.
    /// </summary>
    public void Start(ApiModeProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        FileLog.Write($"[ApiModeTransport] Start: {startInfo.Describe()}");
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("This API-mode transport was already started; start a new one for a new process.");

        try
        {
            _process.Start(startInfo);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ApiModeTransport] Start FAILED: {ex.Message}");
            throw;
        }

        _outputLoop = Task.Run(ReadOutputLoopAsync);
        _errorLoop = Task.Run(ReadErrorLoopAsync);
        _ = Task.Run(WatchForExitAsync);
        FileLog.Write($"[ApiModeTransport] Start: process {_process.ProcessId} running");
    }

    /// <summary>
    /// Write <paramref name="command"/> and wait for the record whose id matches. When the command has no id, one is
    /// given (on a copy; the caller's object is not changed). Cancelling stops the wait only; the line may already
    /// be written. Throws <see cref="ApiModeConnectionEndedException"/> when the process ends first.
    /// </summary>
    public async Task<ApiModeRecord> SendRequestAsync(JsonObject command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = (JsonObject)command.DeepClone();
        var id = ReadOrAssignId(request);
        var label = LabelOf(request);
        FileLog.Write($"[ApiModeTransport] SendRequestAsync: id={id}, command={label}");

        var pending = new PendingRequest(id, label);
        lock (_gate)
        {
            ThrowIfNotRunning();
            if (_endedRecord is not null)
                throw new ApiModeConnectionEndedException(
                    $"Request {id} ({label}) was not sent: the agent process already ended ({_endedRecord.Describe()}).", _endedRecord);
            if (!_pending.TryAdd(id, pending))
                throw new InvalidOperationException($"A request with id {id} is already waiting for its response.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            lock (_gate)
                _pending.Remove(id);
            pending.Completion.TrySetCanceled(cancellationToken);
        });

        try
        {
            await WriteLineAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
                _pending.Remove(id);
            FileLog.Write($"[ApiModeTransport] SendRequestAsync FAILED writing id={id}: {ex.Message}");
            throw;
        }

        var response = await pending.Completion.Task.ConfigureAwait(false);
        FileLog.Write($"[ApiModeTransport] SendRequestAsync: id={id} answered by record {response.Sequence}");
        return response;
    }

    /// <summary>Write one record that expects no response (a notification, or an answer to the agent's own request).</summary>
    public async Task WriteRecordAsync(JsonObject record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        FileLog.Write($"[ApiModeTransport] WriteRecordAsync: record={LabelOf(record)}");
        lock (_gate)
        {
            ThrowIfNotRunning();
            if (_endedRecord is not null)
                throw new ApiModeConnectionEndedException(
                    $"Record {LabelOf(record)} was not sent: the agent process already ended ({_endedRecord.Describe()}).", _endedRecord);
        }

        await WriteLineAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A marker for "now" in standard error: <see cref="ErrorLinesSince"/> with it returns only lines written after
    /// this call. A failure reason uses the lines since the turn started, never older ones.
    /// </summary>
    public long MarkError()
    {
        lock (_errorGate)
        {
            var marker = _errorSequence + 1;
            FileLog.Write($"[ApiModeTransport] MarkError: marker={marker}");
            return marker;
        }
    }

    /// <summary>The standard error lines written at or after <paramref name="marker"/>, oldest first.</summary>
    public ApiModeErrorLinesSince ErrorLinesSince(long marker)
    {
        lock (_errorGate)
        {
            var lines = _errorLines.Where(line => line.Sequence >= marker).ToList();
            var firstHeld = _errorLines.Count == 0 ? _errorSequence + 1 : _errorLines.Peek().Sequence;
            var noLongerHeld = Math.Max(0, firstHeld - Math.Max(marker, 1));
            FileLog.Write($"[ApiModeTransport] ErrorLinesSince: marker={marker}, lines={lines.Count}, noLongerHeld={noLongerHeld}");
            return new ApiModeErrorLinesSince(lines, noLongerHeld);
        }
    }

    /// <summary>
    /// Ask the process to exit by closing its standard input; if it has not ended within <paramref name="grace"/>,
    /// kill it. Returns how it ended. Safe to call after the process already ended.
    /// </summary>
    public async Task<ApiModeProcessEnded> ShutdownAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        FileLog.Write($"[ApiModeTransport] ShutdownAsync: process {_process.ProcessId}, grace={grace.TotalMilliseconds}ms");
        ThrowIfNotRunning();

        if (!_ended.Task.IsCompleted)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _process.CloseInput();
            }
            finally
            {
                _writeLock.Release();
            }

            using var graceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var graceElapsed = Task.Delay(grace, _timeProvider, graceCancellation.Token);
            var first = await Task.WhenAny(_ended.Task, graceElapsed).ConfigureAwait(false);
            graceCancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (first != _ended.Task)
            {
                FileLog.Write($"[ApiModeTransport] ShutdownAsync: process {_process.ProcessId} did not exit within {grace.TotalMilliseconds}ms of closing its input; killing it");
                _process.Kill();
            }
        }

        var ended = await _ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileLog.Write($"[ApiModeTransport] ShutdownAsync: ended, {ended.Describe()}");
        return ended;
    }

    /// <summary>Kill the process if it is still running, wait for the end to be reported, and release the process.</summary>
    public async ValueTask DisposeAsync()
    {
        FileLog.Write($"[ApiModeTransport] DisposeAsync: process {_process.ProcessId}");
        if (Volatile.Read(ref _started) == 1)
        {
            if (!_ended.Task.IsCompleted)
                _process.Kill();
            await _ended.Task.ConfigureAwait(false);
        }

        _process.Dispose();
        _readCancellation.Dispose();
        _writeLock.Dispose();
    }

    private async Task WriteLineAsync(JsonObject record, CancellationToken cancellationToken)
    {
        // The serialiser never writes a raw line feed (it escapes one inside a string), so this is one line.
        var json = record.ToJsonString();
        var bytes = new byte[Encoding.UTF8.GetByteCount(json) + 1];
        Encoding.UTF8.GetBytes(json, bytes);
        bytes[^1] = (byte)'\n';

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.WriteLineAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadOutputLoopAsync()
    {
        var framer = new NewlineJsonFramer(_options.MaximumLineBytes);
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await _process.ReadOutputAsync(buffer, _readCancellation.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                foreach (var line in framer.Append(buffer.AsSpan(0, read)))
                    Deliver(line);
            }

            var tail = framer.TakeUnterminated();
            if (tail is not null)
                _unterminatedOutput = Encoding.UTF8.GetString(tail);
            FileLog.Write($"[ApiModeTransport] ReadOutputLoopAsync: standard output ended after {Interlocked.Read(ref _recordSequence)} records");
        }
        catch (OperationCanceledException) when (_readCancellation.IsCancellationRequested)
        {
            // not-an-error: the exit watcher stopped the read after the drain grace, and it reports the end itself
            FileLog.Write("[ApiModeTransport] ReadOutputLoopAsync: stopped reading; the process ended but its output was still open");
        }
        catch (Exception ex)
        {
            _outputFault = ex.Message;
            FileLog.Write($"[ApiModeTransport] ReadOutputLoopAsync FAILED: {ex.Message}; killing process {_process.ProcessId}");
            _process.Kill();
        }
    }

    private async Task ReadErrorLoopAsync()
    {
        try
        {
            while (await _process.ReadErrorLineAsync(_readCancellation.Token).ConfigureAwait(false) is { } text)
            {
                lock (_errorGate)
                {
                    _errorSequence++;
                    _errorLines.Enqueue(new ApiModeErrorLine(_errorSequence, text));
                    while (_errorLines.Count > _options.ErrorLinesHeld)
                        _errorLines.Dequeue();
                }
                FileLog.Write($"[ApiModeTransport] process {_process.ProcessId} standard error: {text}");
            }
        }
        catch (OperationCanceledException) when (_readCancellation.IsCancellationRequested)
        {
            // not-an-error: the exit watcher stopped the read after the drain grace, and it reports the end itself
            FileLog.Write("[ApiModeTransport] ReadErrorLoopAsync: stopped reading; the process ended but its standard error was still open");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ApiModeTransport] ReadErrorLoopAsync FAILED: {ex.Message}");
        }
    }

    private async Task WatchForExitAsync()
    {
        int exitCode;
        try
        {
            exitCode = await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            FileLog.Write($"[ApiModeTransport] WatchForExitAsync: process {_process.ProcessId} exited with code {exitCode}");

            using var graceCancellation = new CancellationTokenSource();
            var drained = Task.WhenAll(_outputLoop, _errorLoop);
            var first = await Task.WhenAny(drained, Task.Delay(_options.DrainGrace, _timeProvider, graceCancellation.Token)).ConfigureAwait(false);
            graceCancellation.Cancel();
            if (first != drained)
            {
                FileLog.Write($"[ApiModeTransport] WatchForExitAsync: output still open {_options.DrainGrace.TotalMilliseconds}ms after the exit; something the process started holds it; ending anyway");
                _readCancellation.Cancel();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ApiModeTransport] WatchForExitAsync FAILED: {ex.Message}");
            _outputFault ??= $"the exit code could not be read: {ex.Message}";
            exitCode = -1;
        }

        Complete(exitCode);
    }

    private void Deliver(byte[] line)
    {
        if (line.Length == 0)
            return;

        var rawLine = Encoding.UTF8.GetString(line);
        JsonElement? json = null;
        string? parseError = null;
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            json = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
            // not-an-error: the line is still delivered, carrying its ParseError, and the dialect decides what it means
            FileLog.Write($"[ApiModeTransport] Deliver: a line of {line.Length} bytes is not JSON: {ex.Message}");
        }

        if (json is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(_options.IdPropertyName, out var idElement))
        {
            id = idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString(),
                JsonValueKind.Number => idElement.GetRawText(),
                _ => null,
            };
        }

        PendingRequest? answered = null;
        ApiModeRecord record;
        lock (_gate)
        {
            if (_endedRecord is not null)
            {
                FileLog.Write($"[ApiModeTransport] Deliver: a record arrived after the end was reported and is not delivered: {Truncate(rawLine)}");
                return;
            }

            if (id is not null && _pending.Remove(id, out var pending))
                answered = pending;
            record = new ApiModeRecord
            {
                Sequence = ++_recordSequence,
                RawLine = rawLine,
                Json = json,
                ParseError = parseError,
                Id = id,
                AnsweredRequest = answered is not null,
            };
            _events.Writer.TryWrite(record);
        }

        answered?.Completion.TrySetResult(record);
    }

    private void Complete(int exitCode)
    {
        ApiModeProcessEnded ended;
        List<PendingRequest> orphaned;
        lock (_gate)
        {
            if (_endedRecord is not null)
                return;
            List<ApiModeErrorLine> errorLines;
            lock (_errorGate)
                errorLines = _errorLines.ToList();
            ended = new ApiModeProcessEnded
            {
                ExitCode = exitCode,
                ErrorLines = errorLines,
                UnterminatedOutput = _unterminatedOutput,
                TransportFault = _outputFault,
            };
            _endedRecord = ended;
            orphaned = _pending.Values.ToList();
            _pending.Clear();
            _events.Writer.TryWrite(ended);
            _events.Writer.TryComplete();
        }

        FileLog.Write($"[ApiModeTransport] Complete: process {_process.ProcessId} ended, {ended.Describe()}, {orphaned.Count} requests unanswered");
        foreach (var request in orphaned)
        {
            request.Completion.TrySetException(new ApiModeConnectionEndedException(
                $"The agent process ended ({ended.Describe()}) before answering request {request.Id} ({request.Label}).", ended));
        }

        _ended.TrySetResult(ended);
    }

    private string ReadOrAssignId(JsonObject request)
    {
        if (request[_options.IdPropertyName] is JsonValue existing)
        {
            if (existing.TryGetValue<string>(out var text))
                return text;
            return existing.ToJsonString();
        }

        if (request.ContainsKey(_options.IdPropertyName))
            throw new ArgumentException($"The command's {_options.IdPropertyName} must be a string or a number.", nameof(request));

        var id = $"req-{Interlocked.Increment(ref _nextRequestNumber)}";
        request[_options.IdPropertyName] = id;
        return id;
    }

    private static string LabelOf(JsonObject record)
    {
        foreach (var name in new[] { "type", "method" })
        {
            if (record[name] is JsonValue value && value.TryGetValue<string>(out var text))
                return text;
        }
        return "(no type)";
    }

    private void ThrowIfNotRunning()
    {
        if (Volatile.Read(ref _started) == 0)
            throw new InvalidOperationException("The API-mode transport has not been started.");
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "...";

    private sealed class PendingRequest(string id, string label)
    {
        public string Id { get; } = id;
        public string Label { get; } = label;
        public TaskCompletionSource<ApiModeRecord> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
