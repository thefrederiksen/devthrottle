using System.Text.Json;
using CcDirector.Core.Codex;
using Xunit;

namespace CcDirector.Core.Tests.Codex;

public class CodexRolloutLocatorTests
{
    [Fact]
    public void Scan_ReturnsNewestRolloutForMatchingCwd_IgnoringOthers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codex-sessions-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(dir, "2026", "06", "19");
        Directory.CreateDirectory(sub);

        const string target = @"C:\target\repo";
        const string other = @"C:\other\repo";

        WriteRollout(sub, "older-target", target, mtimeSeconds: 100);
        var newestTarget = WriteRollout(sub, "newest-target", target, mtimeSeconds: 300);
        WriteRollout(sub, "newest-other", other, mtimeSeconds: 400); // newest overall, wrong cwd
        WriteRollout(sub, "old-other", other, mtimeSeconds: 50);

        try
        {
            var found = CodexRolloutLocator.Scan(target, dir);
            Assert.Equal(newestTarget, found, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Scan_NoMatchingCwd_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codex-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        WriteRollout(dir, "other", @"C:\some\other", mtimeSeconds: 100);
        try
        {
            Assert.Null(CodexRolloutLocator.Scan(@"C:\target\repo", dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Scan_WithNotBefore_IgnoresOlderSameRepoRollout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codex-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        const string target = @"C:\target\repo";
        var oldTimestamp = new DateTimeOffset(2026, 6, 19, 8, 0, 0, TimeSpan.Zero);
        var launchTime = new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);
        var newTimestamp = new DateTimeOffset(2026, 6, 19, 10, 1, 0, TimeSpan.Zero);

        WriteRollout(dir, "old-target", target, mtimeSeconds: 300, timestamp: oldTimestamp);
        var newTarget = WriteRollout(dir, "new-target", target, mtimeSeconds: 200, timestamp: newTimestamp);

        try
        {
            var found = CodexRolloutLocator.Scan(target, dir, launchTime);
            Assert.Equal(newTarget, found, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Scan_WithNotBefore_ReturnsNullUntilCurrentRolloutAppears()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codex-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        const string target = @"C:\target\repo";
        var oldTimestamp = new DateTimeOffset(2026, 6, 19, 8, 0, 0, TimeSpan.Zero);
        var launchTime = new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);

        WriteRollout(dir, "old-target", target, mtimeSeconds: 300, timestamp: oldTimestamp);

        try
        {
            Assert.Null(CodexRolloutLocator.Scan(target, dir, launchTime));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string WriteRollout(
        string dir,
        string name,
        string cwd,
        int mtimeSeconds,
        DateTimeOffset? timestamp = null)
    {
        var path = Path.Combine(dir, $"rollout-2026-06-19T13-00-00-{name}.jsonl");
        var ts = (timestamp ?? new DateTimeOffset(2026, 6, 19, 17, 29, 6, TimeSpan.Zero))
            .ToString("O");
        var meta = "{\"timestamp\":" + JsonSerializer.Serialize(ts)
                   + ",\"type\":\"session_meta\",\"payload\":{\"id\":\""
                   + name + "\",\"timestamp\":" + JsonSerializer.Serialize(ts)
                   + ",\"cwd\":" + JsonSerializer.Serialize(cwd) + "}}";
        File.WriteAllLines(path, new[]
        {
            meta,
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"hi\"}]}}",
        });
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc).AddSeconds(mtimeSeconds));
        return path;
    }
}
