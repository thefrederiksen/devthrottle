using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// Phase 5: tests for <see cref="SessionLogWriter"/>. Covers the roundtrip contract
/// (write -> read JSONL back -> parse), restart safety (append vs truncate), and
/// the disposal lifecycle. The hot-path back-pressure test is deliberately omitted -
/// the bounded-channel drop policy is the relevant Channels API behavior, not ours.
/// </summary>
public sealed class SessionLogWriterTests : IDisposable
{
    // We write to the real production path (%LOCALAPPDATA%\cc-director\session-logs\<sid>)
    // and clean up the per-session dir at the end. Using a CC_DIRECTOR_ROOT env-var
    // override would race against other test classes that read CcStorage.Root().
    private readonly List<Guid> _createdSessionDirs = new();
    private readonly SessionManager _manager;

    public SessionLogWriterTests()
    {
        _manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
    }

    public void Dispose()
    {
        _manager.Dispose();
        foreach (var sid in _createdSessionDirs)
        {
            try
            {
                var dir = SessionLogPaths.SessionDir(sid);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { }
        }
    }

    private Session CreateTrackedSession()
    {
        var s = _manager.CreateSession(Path.GetTempPath());
        _createdSessionDirs.Add(s.Id);
        return s;
    }

    [Fact]
    public void Writer_creates_session_directory_and_meta_json()
    {
        var session = CreateTrackedSession();
        using var writer = new SessionLogWriter(session);
        writer.Start();

        var dir = SessionLogPaths.SessionDir(session.Id);
        Assert.True(Directory.Exists(dir));
        var metaPath = SessionLogPaths.MetaJson(session.Id);
        Assert.True(File.Exists(metaPath));

        var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        Assert.Equal(session.Id.ToString(), meta.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(1, meta.RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public async Task Writer_persists_wingman_color_changes()
    {
        var session = CreateTrackedSession();
        using var writer = new SessionLogWriter(session);
        writer.Start();

        session.SetStatusColor("blue", "working");
        session.SetStatusColor("red", "waiting for input");

        // Give the background writer a moment to drain. 200 ms is generous; the channel
        // flushes within microseconds in practice.
        await Task.Delay(200);
        // Dispose flushes synchronously.
        writer.Dispose();

        var lines = File.ReadAllLines(SessionLogPaths.WingmanEventsJsonl(session.Id));
        Assert.True(lines.Length >= 2, $"expected >= 2 wingman events, got {lines.Length}");
        var first = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("blue", first.GetProperty("newColor").GetString());
        var second = JsonDocument.Parse(lines[1]).RootElement;
        Assert.Equal("red", second.GetProperty("newColor").GetString());
    }

    [Fact]
    public async Task Writer_persists_turn_summaries_via_helper()
    {
        var session = CreateTrackedSession();
        using var writer = new SessionLogWriter(session);
        writer.Start();

        writer.WriteTurnSummary(new TurnSummary
        {
            Headline = "added a unit test",
            NeedsUser = "no",
            Decisions = new List<string> { "use xUnit", "stub the backend" },
        });
        writer.WriteTurnSummary(new TurnSummary
        {
            Headline = "ran the test suite",
            NeedsUser = "no",
        });

        await Task.Delay(200);
        writer.Dispose();

        var lines = File.ReadAllLines(SessionLogPaths.TurnsJsonl(session.Id));
        Assert.Equal(2, lines.Length);
        var t0 = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("added a unit test", t0.GetProperty("headline").GetString());
    }

    [Fact]
    public async Task Writer_restart_appends_rather_than_truncating()
    {
        var session = CreateTrackedSession();

        // First "boot"
        var writer1 = new SessionLogWriter(session);
        writer1.Start();
        session.SetStatusColor("blue", "working");
        await Task.Delay(200);
        writer1.Dispose();

        // Second "boot" - simulate a Director restart by constructing a new writer for
        // the same session. Should append, not truncate.
        var writer2 = new SessionLogWriter(session);
        writer2.Start();
        session.SetStatusColor("green", "done");
        await Task.Delay(200);
        writer2.Dispose();

        var lines = File.ReadAllLines(SessionLogPaths.WingmanEventsJsonl(session.Id));
        Assert.True(lines.Length >= 2);
        var last = JsonDocument.Parse(lines[^1]).RootElement;
        Assert.Equal("green", last.GetProperty("newColor").GetString());
    }
}
