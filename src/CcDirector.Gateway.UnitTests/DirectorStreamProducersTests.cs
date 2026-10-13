using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (up-stream): tests for the Director-side producers and the four-verb
/// <see cref="DirectorUpStreamHandler"/>. The terminal producer is asserted against the SAME snapshot-then-tail
/// golden bytes the old TerminalStreamEndpoint would have sent; the file producer is asserted to chunk at the
/// frame cap and end with an eof frame; the handler is asserted to return Ok immediately on open (streaming in
/// the background), to be idempotent on close, and to fail loud on a missing session.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DirectorStreamProducersTests
{
    private static (SessionManager sm, Session session, ExecuteActionTestBackend backend) NewSession()
    {
        var sm = new SessionManager(new AgentOptions());
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        return (sm, session, backend);
    }

    private static byte[] Concat(IEnumerable<byte[]> parts)
    {
        using var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    private static async Task<List<DirectorStreamFrame>> CollectAsync(IAsyncEnumerable<DirectorStreamFrame> frames, CancellationToken ct)
    {
        var list = new List<DirectorStreamFrame>();
        await foreach (var f in frames.WithCancellation(ct))
            list.Add(f);
        return list;
    }

    [Fact]
    public async Task ProduceTerminal_YieldsSnapshotThenTailGoldenBytes_ThenClosedOnExit()
    {
        var (sm, session, backend) = NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            backend.Write(Encoding.UTF8.GetBytes("hello golden terminal world\r\n"));

            // The golden reference: exactly what the old StreamSessionAsync sent - the self-contained screen
            // snapshot first, then the live tail from the snapshot's cursor. Computed from the same buffer
            // primitives the producer uses, off the static (about-to-exit) buffer.
            var (snapshot, reflected, snapCols, snapRows) = session.GetTerminalSnapshot();
            var (tail, _) = session.Buffer!.GetWrittenSince(reflected);
            var expected = Concat(new[] { snapshot, tail });

            backend.RaiseProcessExited(0); // make the stream finite: the loop emits Closed once drained

            var frames = await CollectAsync(
                DirectorStreamProducers.ProduceTerminalAsync(sm, session.Id, "term-1", timeout.Token), timeout.Token);

            Assert.True(frames.Count >= 2);
            Assert.Equal(DirectorStreamFrameType.Size, frames[0].Kind);
            Assert.Equal(snapCols, frames[0].Cols);
            Assert.Equal(snapRows, frames[0].Rows);

            var last = frames[^1];
            Assert.Equal(DirectorStreamFrameType.Closed, last.Kind);
            Assert.Equal("session exited", last.Reason);

            var binaries = frames.Where(f => f.Kind == DirectorStreamFrameType.Binary).Select(f => f.Data!).ToList();
            Assert.All(binaries, b => Assert.True(b.Length <= DirectorStreamLimits.MaxBinaryFrameBytes));
            Assert.Equal(expected, Concat(binaries));
            Assert.All(frames, f => Assert.Equal("term-1", f.StreamId));
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task ProduceTerminal_MissingSession_YieldsOnlyAClosedFrame()
    {
        var sm = new SessionManager(new AgentOptions());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var frames = await CollectAsync(
                DirectorStreamProducers.ProduceTerminalAsync(sm, Guid.NewGuid(), "term-missing", timeout.Token), timeout.Token);

            var only = Assert.Single(frames);
            Assert.Equal(DirectorStreamFrameType.Closed, only.Kind);
            Assert.Equal("session not found", only.Reason);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task ProduceFile_ChunksAtTheFrameCap_ThenEofFrame_AndReconstructsTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccd-upstream-file-" + Guid.NewGuid().ToString("N") + ".bin");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            // Two full-cap chunks plus a partial third, so chunking is actually exercised.
            var content = new byte[(DirectorStreamLimits.MaxBinaryFrameBytes * 2) + 123];
            for (var i = 0; i < content.Length; i++) content[i] = (byte)(i % 251);
            await File.WriteAllBytesAsync(path, content, timeout.Token);

            var frames = await CollectAsync(DirectorStreamProducers.ProduceFileAsync(path, "file-1", timeout.Token), timeout.Token);

            var binaries = frames.Where(f => f.Kind == DirectorStreamFrameType.Binary).Select(f => f.Data!).ToList();
            Assert.Equal(3, binaries.Count);
            Assert.All(binaries, b => Assert.True(b.Length <= DirectorStreamLimits.MaxBinaryFrameBytes));
            Assert.Equal(content, Concat(binaries));

            var last = frames[^1];
            Assert.Equal(DirectorStreamFrameType.Closed, last.Kind);
            Assert.Equal("eof", last.Reason);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    // ---------- DirectorUpStreamHandler ----------

    // A sendUp that drains the producer into a list, so a test can assert what the handler streamed.
    private static (DirectorUpStreamHandler handler, List<DirectorStreamFrame> captured, Func<Task> drained) NewHandler(SessionManager sm)
    {
        var captured = new List<DirectorStreamFrame>();
        var done = new TaskCompletionSource();
        var handler = new DirectorUpStreamHandler(sm, async (streamId, frames) =>
        {
            try
            {
                await foreach (var f in frames)
                    lock (captured) captured.Add(f);
            }
            finally { done.TrySetResult(); }
        });
        return (handler, captured, () => done.Task);
    }

    private static DirectorCommand OpenTerminalCommand(Guid sid, string streamId) => new()
    {
        CommandId = "o1",
        Verb = "open-terminal-stream",
        SessionId = sid.ToString(),
        PayloadJson = SessionCommandExecutor.Serialize(new OpenStreamRequest { StreamId = streamId }),
    };

    [Fact]
    public void Handler_OpenTerminal_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new AgentOptions());
        var (handler, _, _) = NewHandler(sm);
        try
        {
            var result = handler.Handle(OpenTerminalCommand(Guid.NewGuid(), "x"));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public void Handler_CloseStream_UnknownId_IsIdempotentOk()
    {
        var sm = new SessionManager(new AgentOptions());
        var (handler, _, _) = NewHandler(sm);
        try
        {
            var result = handler.Handle(new DirectorCommand
            {
                Verb = "close-stream",
                PayloadJson = SessionCommandExecutor.Serialize(new CloseStreamRequest { StreamId = "never-started" }),
            });
            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // Tenant-boundary hardening (CR-4): the read-file verb is session-scoped now - the command carries the
    // session id and the path must live inside that session's working directory - so these two tests seat a
    // real session over a temp directory and read within it. The containment itself (out-of-root refusals,
    // the screenshot control) is pinned in DirectorUpStreamHandlerContainmentTests.

    [Fact]
    public async Task Handler_ReadFile_ReturnsOkWithTotalSize_AndStreamsTheFile()
    {
        var sm = new SessionManager(new AgentOptions());
        var (handler, captured, drained) = NewHandler(sm);
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ccd-upstream-read-" + Guid.NewGuid().ToString("N"))).FullName;
        var session = sm.CreateEmbeddedSession(dir, null, new ExecuteActionTestBackend());
        var path = Path.Combine(dir, "read-me.txt");
        try
        {
            var content = Encoding.UTF8.GetBytes("the quick brown fox");
            await File.WriteAllBytesAsync(path, content);

            var result = handler.Handle(new DirectorCommand
            {
                Verb = "read-file",
                SessionId = session.Id.ToString(),
                PayloadJson = SessionCommandExecutor.Serialize(new OpenStreamRequest { StreamId = "rf-1", Path = path }),
            });

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var body = SessionCommandExecutor.Deserialize<OpenReadResponse>(result.BodyJson);
            Assert.NotNull(body);
            Assert.Equal(content.Length, body!.TotalBytes);

            await drained().WaitAsync(TimeSpan.FromSeconds(5));
            List<DirectorStreamFrame> frames;
            lock (captured) frames = captured.ToList();
            var binaries = frames.Where(f => f.Kind == DirectorStreamFrameType.Binary).Select(f => f.Data!).ToList();
            Assert.Equal(content, Concat(binaries));
            Assert.Equal("eof", frames[^1].Reason);
        }
        finally
        {
            sm.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Handler_ReadFile_MissingFile_ReturnsNotFound()
    {
        var sm = new SessionManager(new AgentOptions());
        var (handler, _, _) = NewHandler(sm);
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ccd-upstream-missing-" + Guid.NewGuid().ToString("N"))).FullName;
        var session = sm.CreateEmbeddedSession(dir, null, new ExecuteActionTestBackend());
        try
        {
            var result = handler.Handle(new DirectorCommand
            {
                Verb = "read-file",
                SessionId = session.Id.ToString(),
                PayloadJson = SessionCommandExecutor.Serialize(new OpenStreamRequest { StreamId = "rf-x", Path = Path.Combine(dir, "does-not-exist.txt") }),
            });
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally
        {
            sm.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
