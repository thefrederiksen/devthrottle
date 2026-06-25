using System.Text.Json;
using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

public class ClaudeHookInstallerTests
{
    [Fact]
    public void EnsureInstalled_WritesScriptAndSettings_WithSessionStartMatchers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-hook-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settingsPath = ClaudeHookInstaller.EnsureInstalled(dir);

            Assert.NotNull(settingsPath);
            Assert.True(File.Exists(settingsPath));

            var scriptPath = Path.Combine(dir, "report-session.ps1");
            Assert.True(File.Exists(scriptPath));

            // The script posts to the claude-hook endpoint using the injected per-session env.
            var script = File.ReadAllText(scriptPath);
            Assert.Contains("claude-hook", script);
            Assert.Contains("CC_SESSION_ID", script);
            Assert.Contains("CC_DIRECTOR_API", script);

            // The settings register a SessionStart hook for each boundary source, in order.
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath!));
            var sessionStart = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
            var matchers = sessionStart.EnumerateArray()
                .Select(e => e.GetProperty("matcher").GetString())
                .ToArray();
            Assert.Equal(new[] { "startup", "resume", "clear", "compact" }, matchers);

            var command = sessionStart[0].GetProperty("hooks")[0].GetProperty("command").GetString();
            Assert.Contains("powershell", command);
            Assert.Contains(scriptPath, command); // the script path is embedded in the command
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void EnsureInstalled_IsIdempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-hook-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = ClaudeHookInstaller.EnsureInstalled(dir);
            var second = ClaudeHookInstaller.EnsureInstalled(dir);

            Assert.Equal(first, second);
            Assert.True(File.Exists(first!));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
