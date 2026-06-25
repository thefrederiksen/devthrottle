using System.Text.Json;
using CcDirector.Core.Pi;
using Xunit;

namespace CcDirector.Core.Tests.Pi;

public class PiSessionLocatorTests
{
    [Fact]
    public void Scan_ReturnsNewestSessionForMatchingCwd_IgnoringOthers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pi-sessions-" + Guid.NewGuid().ToString("N"));
        // Pi groups files in per-cwd subdirectories; the locator matches on the session record
        // cwd, so the subdirectory names are arbitrary here.
        var targetSub = Path.Combine(dir, "--C--target-repo--");
        var otherSub = Path.Combine(dir, "--C--other-repo--");
        Directory.CreateDirectory(targetSub);
        Directory.CreateDirectory(otherSub);

        const string target = @"C:\target\repo";
        const string other = @"C:\other\repo";

        WriteSession(targetSub, "older-target", target, mtimeSeconds: 100);
        var newestTarget = WriteSession(targetSub, "newest-target", target, mtimeSeconds: 300);
        WriteSession(otherSub, "newest-other", other, mtimeSeconds: 400); // newest overall, wrong cwd
        WriteSession(otherSub, "old-other", other, mtimeSeconds: 50);

        try
        {
            var found = PiSessionLocator.Scan(target, dir);
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
        var dir = Path.Combine(Path.GetTempPath(), "pi-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        WriteSession(dir, "other", @"C:\some\other", mtimeSeconds: 100);
        try
        {
            Assert.Null(PiSessionLocator.Scan(@"C:\target\repo", dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string WriteSession(string dir, string name, string cwd, int mtimeSeconds)
    {
        var path = Path.Combine(dir, $"2026-06-25T13-00-00-000Z_{name}.jsonl");
        var session = "{\"type\":\"session\",\"version\":3,\"id\":\"" + name
                      + "\",\"timestamp\":\"2026-06-25T13:00:00.000Z\",\"cwd\":" + JsonSerializer.Serialize(cwd) + "}";
        File.WriteAllLines(path, new[]
        {
            session,
            "{\"type\":\"message\",\"id\":\"u1\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}",
        });
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc).AddSeconds(mtimeSeconds));
        return path;
    }
}
