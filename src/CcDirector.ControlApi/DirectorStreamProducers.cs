using System.Runtime.CompilerServices;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (up-stream): the Director-side producers that yield the frames an
/// up-stream carries. Both are connection-agnostic (Architect ruling A): they take only a
/// <see cref="SessionManager"/> (or a path) plus a stream id and a CancellationToken, and <c>yield return</c>
/// <see cref="DirectorStreamFrame"/>s, so the connection concern never leaks into them and they are unit
/// testable in isolation. The 4 KB-to-64 KB frame cap (Architect ruling 2) is enforced here by chunking at
/// <see cref="DirectorStreamLimits.MaxBinaryFrameBytes"/>, so no single frame can monopolize the one shared
/// tunnel connection.
/// </summary>
internal static class DirectorStreamProducers
{
    private const int Max = DirectorStreamLimits.MaxBinaryFrameBytes;

    /// <summary>
    /// The live terminal producer: the exact cursor loop <c>TerminalStreamEndpoint.StreamSessionAsync</c>
    /// runs today, lifted to <c>yield return</c> frames instead of writing a WebSocket. Sends a Size frame and
    /// the self-contained screen SNAPSHOT first (so a fresh client terminal reconstructs correctly), then the
    /// live tail from one monotonic cursor via the session buffer's <c>GetWrittenSince</c>, a Size frame on a
    /// live PTY resize, and a Closed frame when the session exits. The one difference from the WebSocket
    /// version is the frame cap: a burst larger than <see cref="Max"/> is split into several bounded Binary
    /// frames (the WebSocket allowed one large frame; the shared tunnel does not).
    /// </summary>
    public static async IAsyncEnumerable<DirectorStreamFrame> ProduceTerminalAsync(
        SessionManager sessionManager, Guid sessionId, string streamId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var session0 = sessionManager.GetSession(sessionId);
        if (session0 is null)
        {
            yield return Closed(streamId, "session not found");
            yield break;
        }

        // The terminal the snapshot is of. An API-mode session's terminal comes and goes with the wheel, and each new one
        // is a different buffer counting its bytes from zero, so the cursor below belongs to this buffer and no other.
        var streamedBuffer = session0.Buffer;
        var (snapshot, reflected, snapCols, snapRows) = session0.GetTerminalSnapshot();
        yield return Size(streamId, snapCols, snapRows);
        foreach (var chunk in Chunk(snapshot))
            yield return Binary(streamId, chunk);

        long cursor = reflected;
        short lastCols = (short)snapCols;
        short lastRows = (short)snapRows;

        while (!cancellationToken.IsCancellationRequested)
        {
            var session = sessionManager.GetSession(sessionId);
            if (session is null)
            {
                yield return Closed(streamId, "session not found");
                yield break;
            }

            if (session.CurrentCols != lastCols || session.CurrentRows != lastRows)
            {
                lastCols = session.CurrentCols;
                lastRows = session.CurrentRows;
                yield return Size(streamId, lastCols, lastRows);
            }

            var buffer = session.Buffer;
            if (!ReferenceEquals(buffer, streamedBuffer))
            {
                // A DIFFERENT TERMINAL (phase 4 review, finding 1): the user handed back, or took the wheel again. The
                // client's screen is the old terminal's, so it is cleared; a new terminal is then streamed from its first
                // byte, which a cleared client replays exactly, and a terminal that went away leaves a sentence saying so.
                FileLog.Write($"[DirectorStreamProducers] ProduceTerminalAsync: stream={streamId}, session={sessionId}: " +
                              $"the terminal changed ({(buffer is null ? "none open now" : "a new one")}); clearing the client and restarting the cursor");
                streamedBuffer = buffer;
                cursor = 0;
                lastCols = session.CurrentCols;
                lastRows = session.CurrentRows;
                yield return Size(streamId, lastCols, lastRows);
                yield return Binary(streamId, buffer is null ? TerminalEndedScreen : ClearScreen);
                continue;
            }

            if (buffer is not null)
            {
                var (data, newCursor) = buffer.GetWrittenSince(cursor);
                if (data.Length > 0)
                {
                    foreach (var chunk in Chunk(data))
                        yield return Binary(streamId, chunk);
                    cursor = newCursor;
                    continue; // drain at full speed while bytes are flowing before sleeping
                }
            }

            if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
            {
                yield return Closed(streamId, "session exited");
                yield break;
            }

            if (!await DelayNoThrowAsync(40, cancellationToken))
                yield break; // cancelled (close-stream) - stop cleanly, no fault
        }
    }

    /// <summary>
    /// The finite file producer: read a file in <see cref="Max"/>-sized chunks, yielding one Binary frame per
    /// chunk, then a single Closed frame with reason "eof" at the natural end. Serves both a session file read
    /// (the Local Files viewer) and a screenshot read; the caller stats the size up front for Content-Length
    /// (Architect ruling 4). A cancel mid-read (browser disconnect via close-stream) stops cleanly with no eof
    /// frame and no fault.
    /// </summary>
    public static async IAsyncEnumerable<DirectorStreamFrame> ProduceFileAsync(
        string path, string streamId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: Max, useAsync: true);
        var buffer = new byte[Max];
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            var read = await ReadChunkAsync(stream, buffer, cancellationToken);
            if (read < 0)
                yield break; // cancelled mid-read - stop cleanly
            if (read == 0)
            {
                yield return Closed(streamId, "eof");
                yield break;
            }

            var chunk = new byte[read];
            Array.Copy(buffer, chunk, read);
            yield return Binary(streamId, chunk);
        }
    }

    /// <summary>A full reset of the client terminal, then a cleared screen and scrollback with the cursor home: the
    /// blank terminal a new terminal's first byte is written to.</summary>
    internal static readonly byte[] ClearScreen = System.Text.Encoding.ASCII.GetBytes("\u001bc\u001b[3J\u001b[2J\u001b[H");

    /// <summary>What the client shows once the terminal it was showing has gone: a cleared screen and the Director's
    /// sentence, so the dead terminal's last frame is never left looking typeable.</summary>
    internal static readonly byte[] TerminalEndedScreen = System.Text.Encoding.UTF8.GetBytes(
        "\u001bc\u001b[3J\u001b[2J\u001b[H\u001b[2m[The terminal has ended.]\u001b[0m\r\n");

    // Split a payload into bounded frames so no single Binary frame exceeds the cap. An empty payload yields
    // nothing (no empty frame), matching the old "if length > 0 send" behaviour.
    private static IEnumerable<byte[]> Chunk(byte[] data)
    {
        if (data.Length == 0)
            yield break;
        if (data.Length <= Max)
        {
            yield return data;
            yield break;
        }
        for (var offset = 0; offset < data.Length; offset += Max)
        {
            var length = Math.Min(Max, data.Length - offset);
            var chunk = new byte[length];
            Array.Copy(data, offset, chunk, 0, length);
            yield return chunk;
        }
    }

    // A cancellation-safe delay: returns true after the delay, false if cancelled (so the caller stops without
    // an exception escaping the async iterator).
    private static async Task<bool> DelayNoThrowAsync(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // A cancellation-safe read: returns the byte count (0 on end of file), or -1 if cancelled mid-read.
    private static async Task<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.ReadAsync(buffer, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
    }

    private static DirectorStreamFrame Size(string streamId, int cols, int rows) =>
        new() { StreamId = streamId, Kind = DirectorStreamFrameType.Size, Cols = cols, Rows = rows };

    private static DirectorStreamFrame Binary(string streamId, byte[] data) =>
        new() { StreamId = streamId, Kind = DirectorStreamFrameType.Binary, Data = data };

    private static DirectorStreamFrame Closed(string streamId, string reason) =>
        new() { StreamId = streamId, Kind = DirectorStreamFrameType.Closed, Reason = reason };
}
