using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Terminal.Core;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// End-to-end tests for the live-attach terminal snapshot at the <see cref="Session"/> level: bytes
/// go through the real backend buffer -> OnBytesWritten -> the session's PTY-sized parser, exactly
/// as in production. Proves the snapshot reconstructs the CURRENT screen even after the raw ring
/// buffer has wrapped past the bytes that drew part of it - the case that made an incrementally
/// repainting agent (Codex) tear on the old mid-stream byte replay.
/// </summary>
[Collection("ProcessWideState")]
public sealed class SessionTerminalSnapshotTests
{
    private static Session NewSession(ISessionBackend backend)
    {
        var s = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: "claude-test",
            activityState: ActivityState.Working,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);
        s.MarkRunning();
        return s;
    }

    private static (AnsiParser Parser, string[] Rows) ReplayIntoClient(byte[] snapshot, int cols, int rows)
    {
        var cells = new TerminalCell[cols, rows];
        var scrollback = new List<TerminalCell[]>();
        var client = new AnsiParser(cells, cols, rows, scrollback, 5000);
        client.Parse(snapshot);
        return (client, client.SnapshotActiveRows().Rows);
    }

    [Fact]
    public void Snapshot_ReconstructsScreen_AfterRingBufferWrappedPastEarlyContent()
    {
        // 8KB ring: small enough to wrap many times over the churn below, dropping the header draw.
        var backend = new BufferBackend(8 * 1024);
        using var s = NewSession(backend);
        s.Resize(20, 5); // size the live-attach parser to the content geometry

        // Draw a header ONCE at the top, then churn the bottom row in place far past the ring size.
        // This is the shape that tears a mid-stream replay: the header bytes scroll out of the ring,
        // but the authoritative parser still holds the header cell.
        backend.Buffer!.Write(Encoding.ASCII.GetBytes("\x1b[1;1HHEADER-KEEP-ME"));
        for (int i = 0; i < 4000; i++)
            backend.Buffer!.Write(Encoding.ASCII.GetBytes($"\x1b[5;1Hupdate {i,6}    "));

        long totalWritten = backend.Buffer!.TotalBytesWritten;
        Assert.True(totalWritten > 8 * 1024, "precondition: churn must exceed the ring so it wraps");

        // NEW behavior: the snapshot reconstructs the full current screen.
        var (snapshot, cursor, cols, rows) = s.GetTerminalSnapshot();
        Assert.Equal(20, cols);
        Assert.Equal(5, rows);
        var (_, clientRows) = ReplayIntoClient(snapshot, cols, rows);
        Assert.Contains("HEADER-KEEP-ME", clientRows[0]);
        Assert.Contains("update   3999", clientRows[4]);

        // The snapshot cursor points at (or past) the churn, so live output resumes without replaying it.
        Assert.True(cursor >= totalWritten - backend.Buffer!.DumpAll().Length,
            "snapshot cursor should reflect the bytes already consumed");

        // OLD behavior, for contrast: replay only what the ring still holds, from a blank terminal.
        // The header was drawn once and has since scrolled out of the ring, so it is GONE.
        var ring = backend.Buffer!.DumpAll();
        var blankCells = new TerminalCell[20, 5];
        var blank = new AnsiParser(blankCells, 20, 5, new List<TerminalCell[]>(), 5000);
        blank.Parse(ring);
        Assert.DoesNotContain("HEADER-KEEP-ME", blank.SnapshotActiveRows().Rows[0]);
    }

    [Fact]
    public void Snapshot_TracksLivePtyResize_AndKeepsContent()
    {
        var backend = new BufferBackend(64 * 1024);
        using var s = NewSession(backend);
        s.Resize(30, 6);
        backend.Buffer!.Write(Encoding.ASCII.GetBytes("\x1b[1;1Htop-left-content"));

        var before = s.GetTerminalSnapshot();
        Assert.Equal(30, before.Cols);
        Assert.Equal(6, before.Rows);

        // Grow the PTY: the snapshot must now report the new geometry and keep the copied content.
        s.Resize(50, 10);
        var after = s.GetTerminalSnapshot();
        Assert.Equal(50, after.Cols);
        Assert.Equal(10, after.Rows);

        var (_, rows) = ReplayIntoClient(after.Snapshot, after.Cols, after.Rows);
        Assert.Contains("top-left-content", rows[0]);
    }

    [Fact]
    public void Snapshot_PreservesAlternateScreen()
    {
        var backend = new BufferBackend(64 * 1024);
        using var s = NewSession(backend);
        s.Resize(24, 4);
        // Enter the alternate screen and draw a full-screen TUI frame.
        backend.Buffer!.Write(Encoding.ASCII.GetBytes("\x1b[?1049h\x1b[2J\x1b[1;1HALT-SCREEN-APP\x1b[3;1Hstatus row"));
        Assert.True(s.IsAlternateScreen);

        var (snapshot, _, cols, rows) = s.GetTerminalSnapshot();
        var (client, clientRows) = ReplayIntoClient(snapshot, cols, rows);
        Assert.True(client.IsAlternateScreen, "snapshot should restore the client into the alternate screen");
        Assert.Contains("ALT-SCREEN-APP", clientRows[0]);
        Assert.Contains("status row", clientRows[2]);
    }

    /// <summary>Backend exposing a real terminal buffer of the given capacity so the session's
    /// server-side parsers initialize and are fed via OnBytesWritten, exactly like production.</summary>
    private sealed class BufferBackend : ISessionBackend
    {
        public CircularTerminalBuffer? Buffer { get; }
        public BufferBackend(int capacity) => Buffer = new CircularTerminalBuffer(capacity);

        public int ProcessId => 1234;
        public string Status => "Buffered";
        public bool IsRunning => true;
        public bool HasExited => false;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() => Buffer?.Dispose();
    }
}
