using System.Text.Json;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Wingman training-data capture (issue #531 follow-up): when the setting is on, each wingman
/// summary appends one JSON-lines record with up to 20,000 chars of terminal + the response;
/// when off, nothing is written.
/// </summary>
public sealed class WingmanTrainingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wmts-" + Guid.NewGuid().ToString("N"));

    public WingmanTrainingStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string? FirstLine()
    {
        var f = Directory.GetFiles(_dir, "wingman-training-*.jsonl").FirstOrDefault();
        return f is null ? null : File.ReadAllLines(f).FirstOrDefault();
    }

    [Fact]
    public async Task WriteAsync_WhenDisabled_WritesNothing()
    {
        var store = new WingmanTrainingStore(isEnabled: () => false, dir: _dir);
        await store.WriteAsync("sid-1", "explain", "terminal text", "reply", "context", "spoken", 1.2);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task WriteAsync_WhenEnabled_AppendsOneRecordWithTheFields()
    {
        var store = new WingmanTrainingStore(isEnabled: () => true, dir: _dir);
        await store.WriteAsync("sid-1", "voice-turn", "TERMINAL", "the reply", "the context", "the spoken", 3.5);

        var line = FirstLine();
        Assert.NotNull(line);
        using var doc = JsonDocument.Parse(line!);
        var r = doc.RootElement;
        Assert.Equal("sid-1", r.GetProperty("sessionId").GetString());
        Assert.Equal("voice-turn", r.GetProperty("source").GetString());
        Assert.Equal("TERMINAL", r.GetProperty("terminal").GetString());
        Assert.Equal("the reply", r.GetProperty("reply").GetString());
        Assert.Equal("the context", r.GetProperty("recentContext").GetString());
        Assert.Equal("the spoken", r.GetProperty("spoken").GetString());
        Assert.False(r.GetProperty("terminalTruncated").GetBoolean());
        Assert.Equal("TERMINAL".Length, r.GetProperty("terminalChars").GetInt32());
    }

    [Fact]
    public async Task WriteAsync_TerminalOverLimit_KeepsTheMostRecent20000Chars()
    {
        var store = new WingmanTrainingStore(isEnabled: () => true, dir: _dir);
        // 25,000 chars: 'A' x5000 (oldest) then 'B' x20000 (most recent). Keep the tail.
        var terminal = new string('A', 5000) + new string('B', 20000);
        await store.WriteAsync("sid-1", "generate", terminal, "r", "c", "s", 0.0);

        using var doc = JsonDocument.Parse(FirstLine()!);
        var r = doc.RootElement;
        var kept = r.GetProperty("terminal").GetString()!;
        Assert.Equal(WingmanTrainingStore.MaxTerminalChars, kept.Length);
        Assert.True(r.GetProperty("terminalTruncated").GetBoolean());
        Assert.DoesNotContain('A', kept);                 // the oldest 5000 were dropped
        Assert.Equal(new string('B', 20000), kept);       // exactly the most-recent tail
    }

    [Fact]
    public async Task WriteAsync_TwoCaptures_AppendOnePerLine()
    {
        var store = new WingmanTrainingStore(isEnabled: () => true, dir: _dir);
        await store.WriteAsync("sid-1", "explain", "t1", "r1", "c1", "s1", 1.0);
        await store.WriteAsync("sid-2", "explain", "t2", "r2", "c2", "s2", 2.0);

        var f = Directory.GetFiles(_dir, "wingman-training-*.jsonl").Single();
        var lines = File.ReadAllLines(f).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Contains("sid-1", lines[0]);
        Assert.Contains("sid-2", lines[1]);
    }
}
