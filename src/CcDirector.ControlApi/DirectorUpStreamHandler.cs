using System.Collections.Concurrent;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (up-stream): the handler for the four CONNECTION-BOUND stream verbs
/// (Architect ruling A). These four are NOT in the unary verb-to-area dictionary - they branch earlier, in
/// <see cref="GatewayStreamClient"/>'s Command handler, because only they need the live SignalR connection
/// (to start an up-stream) and a per-stream <see cref="CancellationTokenSource"/> registry.
///
///  - <c>open-terminal-stream</c>: start the terminal producer streaming up under the Gateway's stream id.
///  - <c>read-file</c> / <c>screenshot-file</c>: start the finite file producer; returns the total size and
///    content type up front so the Gateway can set Content-Length (ruling 4).
///  - <c>close-stream</c>: cancel the producer for a stream id (idempotent - an unknown or already-finished id
///    is a safe no-op Ok, ruling 3).
///
/// On accepting an open the handler starts the producer in the background and returns Ok IMMEDIATELY (it does
/// not await the whole stream inside the unary call). If the tunnel connection drops, <see cref="CancelAll"/>
/// tears down EVERY live stream (ruling A): a dropped connection can no longer carry frames, so nothing is
/// left producing into a dead socket.
/// </summary>
internal sealed class DirectorUpStreamHandler
{
    private readonly SessionManager _sessionManager;
    private readonly Func<string, IAsyncEnumerable<DirectorStreamFrame>, Task> _sendUp;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _streams = new();

    /// <param name="sendUp">
    /// Sends the producer's frames UP the tunnel under a stream id - in production this wraps
    /// <c>connection.SendAsync("StreamUp", streamId, frames)</c>. Injected (not the connection itself) so the
    /// handler and its producers stay unit-testable without a live SignalR connection.
    /// </param>
    public DirectorUpStreamHandler(SessionManager sessionManager, Func<string, IAsyncEnumerable<DirectorStreamFrame>, Task> sendUp)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _sendUp = sendUp ?? throw new ArgumentNullException(nameof(sendUp));
    }

    /// <summary>The number of streams this handler currently has producing. For diagnostics and tests.</summary>
    public int ActiveStreamCount => _streams.Count;

    /// <summary>True for exactly the four connection-bound stream verbs; everything else is a unary verb.</summary>
    public static bool IsStreamVerb(string verb) =>
        verb is "open-terminal-stream" or "read-file" or "screenshot-file" or "close-stream";

    /// <summary>Handle one of the four stream verbs. Never throws for a normal failure; returns a typed result.</summary>
    public DirectorCommandResult Handle(DirectorCommand command)
    {
        return command.Verb switch
        {
            "open-terminal-stream" => OpenTerminal(command),
            "read-file" => OpenFile(command),
            "screenshot-file" => OpenScreenshot(command),
            "close-stream" => Close(command),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not a stream verb"),
        };
    }

    /// <summary>Cancel and drop EVERY live stream. Called when the tunnel connection drops (Architect ruling A).</summary>
    public void CancelAll()
    {
        foreach (var streamId in _streams.Keys)
            CancelStream(streamId);
        FileLog.Write("[DirectorUpStreamHandler] CancelAll: all live streams torn down (tunnel dropped)");
    }

    private DirectorCommandResult OpenTerminal(DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<OpenStreamRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrEmpty(request.StreamId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "streamId is required");

        if (_sessionManager.GetSession(guid) is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var streamId = request.StreamId;
        StartProducer(streamId, ct => DirectorStreamProducers.ProduceTerminalAsync(_sessionManager, guid, streamId, ct));
        return DirectorCommandResult.Success(); // open-ended terminal: no body
    }

    private DirectorCommandResult OpenFile(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<OpenStreamRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrEmpty(request.StreamId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "streamId is required");
        if (string.IsNullOrEmpty(request.Path))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "path is required");

        // The file read is SESSION-SCOPED: resolve the session exactly as OpenTerminal does, and refuse any
        // path outside its working directory through ResolveSessionFile (ResolveScreenshot's twin - resolve
        // fully, then refuse anything outside the allowed root). The pre-hardening version served ANY path
        // after only File.Exists, so on a hosted Gateway any active device key could read gateway-token.txt,
        // .ssh keys, or .env files on any machine in the account.
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");
        var session = _sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var path = ControlEndpoints.ResolveSessionFile(session.WorkingDirectory, request.Path);
        if (path is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "path is outside the session's working directory");
        if (!File.Exists(path))
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "file not found");

        var streamId = request.StreamId;
        StartProducer(streamId, ct => DirectorStreamProducers.ProduceFileAsync(path, streamId, ct));
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(BuildReadResponse(path, ControlEndpoints.FileContentType(path))));
    }

    private DirectorCommandResult OpenScreenshot(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<OpenStreamRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrEmpty(request.StreamId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "streamId is required");

        // Resolve + traversal-check through the SAME helper the REST /screenshots/file route uses, so the two
        // paths cannot drift. A missing / bad / non-image name resolves to null -> NotFound.
        var path = ControlEndpoints.ResolveScreenshot(request.ScreenshotId);
        if (path is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "screenshot not found");

        var streamId = request.StreamId;
        StartProducer(streamId, ct => DirectorStreamProducers.ProduceFileAsync(path, streamId, ct));
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(BuildReadResponse(path, ControlEndpoints.ScreenshotContentType(path))));
    }

    private DirectorCommandResult Close(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<CloseStreamRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrEmpty(request.StreamId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "streamId is required");

        // Idempotent (ruling 3): closing a stream that already finished or never started is a safe no-op Ok.
        CancelStream(request.StreamId);
        return DirectorCommandResult.Success();
    }

    private static OpenReadResponse BuildReadResponse(string path, string? contentType)
    {
        long? total = null;
        try { total = new FileInfo(path).Length; }
        catch (Exception ex) { FileLog.Write($"[DirectorUpStreamHandler] could not stat {path} for total size FAILED: {ex.Message}"); }
        return new OpenReadResponse { TotalBytes = total, ContentType = contentType };
    }

    private void StartProducer(string streamId, Func<CancellationToken, IAsyncEnumerable<DirectorStreamFrame>> makeProducer)
    {
        var cts = new CancellationTokenSource();
        if (!_streams.TryAdd(streamId, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException($"stream id already active (ids must be fresh): {streamId}");
        }
        FileLog.Write($"[DirectorUpStreamHandler] starting producer for stream {streamId} (active={_streams.Count})");
        _ = PumpAsync(streamId, makeProducer(cts.Token), cts);
    }

    // Fire-and-forget: stream the producer's frames up the tunnel until it completes (Closed/eof) or is
    // cancelled (close-stream / tunnel drop). Best-effort; a send fault ends the stream and is logged, never
    // thrown into the caller (the open command already returned Ok). PumpAsync is the SOLE disposer of the
    // stream's CancellationTokenSource, and it disposes only here in the finally - after the producer has
    // fully unwound - so a concurrent close-stream/CancelAll only ever CANCELS the source, never disposes it
    // while a Task.Delay/ReadAsync still holds its token (which would throw ObjectDisposedException).
    private async Task PumpAsync(string streamId, IAsyncEnumerable<DirectorStreamFrame> frames, CancellationTokenSource cts)
    {
        try
        {
            await _sendUp(streamId, frames);
        }
        catch (OperationCanceledException)
        {
            // close-stream / tunnel drop cancelled us. Normal.
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorUpStreamHandler] stream {streamId} pump FAILED: {ex.Message}");
        }
        finally
        {
            _streams.TryRemove(streamId, out _); // no-op if close-stream/CancelAll already removed it
            cts.Dispose();
        }
    }

    // Cancel (never dispose) a live stream's token so its producer stops; PumpAsync's finally disposes the
    // source once the producer has unwound. Idempotent: an already-removed stream is a no-op.
    private void CancelStream(string streamId)
    {
        if (_streams.TryRemove(streamId, out var cts))
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* pump already disposed it - nothing to do */ }
        }
    }
}
