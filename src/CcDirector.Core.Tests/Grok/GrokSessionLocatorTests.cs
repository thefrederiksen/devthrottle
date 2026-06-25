using CcDirector.Core.Grok;
using Xunit;

namespace CcDirector.Core.Tests.Grok;

/// <summary>
/// Validates that GrokSessionLocator resolves the newest chat_history.jsonl for the per-cwd
/// directory whose percent-encoded name decodes to the repo path, ignoring other cwd directories
/// and older sessions. The encoded directory names match what Grok writes (for example
/// <c>D:\target\repo</c> becomes <c>D%3A%5Ctarget%5Crepo</c>), so this also exercises that the
/// cwd-encoding round-trips through the decode-based match.
/// </summary>
public class GrokSessionLocatorTests
{
    [Fact]
    public void Scan_ReturnsNewestSessionForMatchingEncodedCwd_IgnoringOthers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grok-sessions-" + Guid.NewGuid().ToString("N"));

        const string target = @"D:\target\repo";

        // Grok percent-encodes the absolute cwd as the per-cwd directory name
        // (D%3A%5Cother%5Crepo decodes to D:\other\repo).
        var targetCwd = Path.Combine(dir, "D%3A%5Ctarget%5Crepo");
        var otherCwd = Path.Combine(dir, "D%3A%5Cother%5Crepo");

        WriteSession(targetCwd, "019ee034-older", mtimeSeconds: 100);
        var newestTarget = WriteSession(targetCwd, "019ee034-newest", mtimeSeconds: 300);
        WriteSession(otherCwd, "019ee034-other-newest", mtimeSeconds: 400); // newest overall, wrong cwd

        try
        {
            var found = GrokSessionLocator.Scan(target, dir);
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
        var dir = Path.Combine(Path.GetTempPath(), "grok-sessions-" + Guid.NewGuid().ToString("N"));
        WriteSession(Path.Combine(dir, "D%3A%5Csome%5Cother"), "019ee034-x", mtimeSeconds: 100);
        try
        {
            Assert.Null(GrokSessionLocator.Scan(@"D:\target\repo", dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Writes <cwdDir>/<sessionId>/chat_history.jsonl with a minimal user line and returns its path.
    private static string WriteSession(string cwdDir, string sessionId, int mtimeSeconds)
    {
        var sessionDir = Path.Combine(cwdDir, sessionId);
        Directory.CreateDirectory(sessionDir);
        var path = Path.Combine(sessionDir, "chat_history.jsonl");
        File.WriteAllLines(path, new[]
        {
            """{"type":"user","content":[{"type":"text","text":"hi"}]}""",
        });
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc).AddSeconds(mtimeSeconds));
        return path;
    }
}
