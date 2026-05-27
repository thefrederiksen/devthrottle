using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Tests.Wingman; // BufferOnlyBackend (internal test stub)
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// Tests for the per-turn review log (<see cref="TurnReviewLog"/>) and the trigger
/// (<see cref="TurnReviewLogger"/>, which writes one record per Working -> WaitingForInput
/// flip). All methods share an isolated CC_DIRECTOR_ROOT, set in the constructor; xUnit runs
/// the methods of one class sequentially so the shared env var is safe within the class.
/// </summary>
public sealed class TurnReviewLogTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public TurnReviewLogTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-turnreview-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static TurnReviewRecord? ReadLatestRecord()
    {
        var root = CcStorage.TurnReviewLogs();
        var files = Directory.Exists(root)
            ? Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            : Array.Empty<string>();
        var newest = files.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return newest is null ? null : JsonSerializer.Deserialize<TurnReviewRecord>(File.ReadAllText(newest));
    }

    [Fact]
    public void Write_persists_a_readable_record_under_date_and_session()
    {
        var record = new TurnReviewRecord
        {
            SessionId = "11111111-2222-3333-4444-555555555555",
            SessionName = "my session",
            Transcript = "agent did the thing",
            StatusColor = "red",
            StatusReason = "needs you",
            WingmanSaid = "It finished and is asking how to proceed.",
        };
        record.Screen.Add("> Continue?");
        record.WingmanActions.Add(new TurnReviewAction { At = DateTime.UtcNow, Action = "submit", Detail = "submit \"yes\"", Reason = "obvious" });

        TurnReviewLog.Write(record);

        var read = ReadLatestRecord();
        Assert.NotNull(read);
        Assert.Equal("my session", read.SessionName);
        Assert.Equal("agent did the thing", read.Transcript);
        Assert.Equal("red", read.StatusColor);
        Assert.Contains("> Continue?", read.Screen);
        Assert.Single(read.WingmanActions);
        Assert.Equal("submit", read.WingmanActions[0].Action);
    }

    [Fact]
    public void Write_purges_day_folders_older_than_retention()
    {
        // Seed a stale day-folder well past the retention window.
        var stale = DateTime.UtcNow.Date.AddDays(-(TurnReviewLog.RetentionDays + 1)).ToString("yyyy-MM-dd");
        var staleDir = Path.Combine(CcStorage.TurnReviewLogs(), stale, "sess");
        Directory.CreateDirectory(staleDir);
        File.WriteAllText(Path.Combine(staleDir, "old.json"), "{}");

        // A fresh write triggers the purge.
        TurnReviewLog.Write(new TurnReviewRecord { SessionId = "abc", Transcript = "x" });

        Assert.False(Directory.Exists(Path.Combine(CcStorage.TurnReviewLogs(), stale)),
            "stale day-folder should have been purged");
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        Assert.True(Directory.Exists(Path.Combine(CcStorage.TurnReviewLogs(), today)),
            "today's folder should exist");
    }

    [Fact]
    public async Task Logger_writes_a_record_on_the_working_to_waiting_flip()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var logger = new TurnReviewLogger(manager);
        try
        {
            var backend = new BufferOnlyBackend();
            var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
            var buf = session.Buffer ?? throw new InvalidOperationException("embedded session has no buffer");
            logger.Start(); // wires the existing session; cursor starts at the current buffer end

            // Produce some turn output, then flip Working -> needs-you (the trigger).
            buf.Write(Encoding.UTF8.GetBytes("TURN_OUTPUT_MARKER_42\r\n"));
            session.ApplyTerminalActivityState(ActivityState.Working);
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

            // The write happens off-thread; poll briefly.
            TurnReviewRecord? read = null;
            for (int i = 0; i < 20 && read is null; i++)
            {
                read = ReadLatestRecord();
                if (read is null) await Task.Delay(50);
            }

            Assert.NotNull(read);
            Assert.Equal(session.Id.ToString(), read.SessionId);
            Assert.Contains("TURN_OUTPUT_MARKER_42", read.Transcript);
        }
        finally { logger.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public async Task Logger_does_not_write_on_a_flip_to_working()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var logger = new TurnReviewLogger(manager);
        try
        {
            var backend = new BufferOnlyBackend();
            var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
            var buf = session.Buffer ?? throw new InvalidOperationException("embedded session has no buffer");
            logger.Start();

            buf.Write(Encoding.UTF8.GetBytes("working...\r\n"));
            session.ApplyTerminalActivityState(ActivityState.Working);

            await Task.Delay(200);
            Assert.Null(ReadLatestRecord()); // only the needs-you flip logs
        }
        finally { logger.Dispose(); manager.Dispose(); }
    }
}
