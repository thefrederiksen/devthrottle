using System.Text.Json;
using CcDirector.Core.Codex;
using Xunit;

namespace CcDirector.Core.Tests.Codex;

public class CodexHookInstallerTests
{
    private static (string ScriptDir, string HooksPath) TempPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-codex-hook-test-" + Guid.NewGuid().ToString("N"));
        return (Path.Combine(root, "scripts"), Path.Combine(root, ".codex", "hooks.json"));
    }

    [Fact]
    public void EnsureInstalled_WritesScript_AndAddsSessionStartHook()
    {
        var (scriptDir, hooksPath) = TempPaths();
        try
        {
            var ok = CodexHookInstaller.EnsureInstalled(scriptDir, hooksPath);

            Assert.True(ok);
            var scriptPath = Path.Combine(scriptDir, "report-preamble.ps1");
            Assert.True(File.Exists(scriptPath));
            var script = File.ReadAllText(scriptPath);
            Assert.Contains("fleet-preamble", script);
            Assert.Contains("additionalContext", script);
            Assert.Contains("CC_SESSION_ID", script);

            using var doc = JsonDocument.Parse(File.ReadAllText(hooksPath));
            var sessionStart = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
            var entry = Assert.Single(sessionStart.EnumerateArray());
            Assert.Equal("startup|resume|clear|compact", entry.GetProperty("matcher").GetString());
            var command = entry.GetProperty("hooks")[0].GetProperty("command").GetString();
            Assert.Contains("report-preamble.ps1", command);
        }
        finally { TryDeleteRoot(scriptDir); }
    }

    [Fact]
    public void EnsureInstalled_IsIdempotent_NoDuplicateEntry()
    {
        var (scriptDir, hooksPath) = TempPaths();
        try
        {
            CodexHookInstaller.EnsureInstalled(scriptDir, hooksPath);
            CodexHookInstaller.EnsureInstalled(scriptDir, hooksPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(hooksPath));
            var sessionStart = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
            Assert.Single(sessionStart.EnumerateArray());
        }
        finally { TryDeleteRoot(scriptDir); }
    }

    [Fact]
    public void EnsureInstalled_PreservesExistingUserHooks()
    {
        var (scriptDir, hooksPath) = TempPaths();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
            // A user who already has a PreToolUse hook AND their own SessionStart hook.
            File.WriteAllText(hooksPath, """
            {
              "hooks": {
                "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "my-policy.py" } ] } ],
                "SessionStart": [ { "matcher": "startup", "hooks": [ { "type": "command", "command": "my-greeting.py" } ] } ]
              }
            }
            """);

            var ok = CodexHookInstaller.EnsureInstalled(scriptDir, hooksPath);

            Assert.True(ok);
            using var doc = JsonDocument.Parse(File.ReadAllText(hooksPath));
            var hooks = doc.RootElement.GetProperty("hooks");
            // The user's PreToolUse hook survives untouched.
            Assert.Equal("my-policy.py",
                hooks.GetProperty("PreToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString());
            // SessionStart now has BOTH the user's entry and ours.
            var sessionStart = hooks.GetProperty("SessionStart").EnumerateArray().ToList();
            Assert.Equal(2, sessionStart.Count);
            var commands = sessionStart
                .Select(e => e.GetProperty("hooks")[0].GetProperty("command").GetString() ?? "")
                .ToList();
            Assert.Contains(commands, c => c.Contains("my-greeting.py"));
            Assert.Contains(commands, c => c.Contains("report-preamble.ps1"));
        }
        finally { TryDeleteRoot(scriptDir); }
    }

    [Fact]
    public void EnsureInstalled_MalformedHooksJson_ReturnsFalse_AndDoesNotClobber()
    {
        var (scriptDir, hooksPath) = TempPaths();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
            const string garbage = "this is not json {{{";
            File.WriteAllText(hooksPath, garbage);

            var ok = CodexHookInstaller.EnsureInstalled(scriptDir, hooksPath);

            Assert.False(ok);
            // The user's file is left exactly as it was - never overwritten.
            Assert.Equal(garbage, File.ReadAllText(hooksPath));
        }
        finally { TryDeleteRoot(scriptDir); }
    }

    private static void TryDeleteRoot(string scriptDir)
    {
        try
        {
            var root = Directory.GetParent(scriptDir)?.FullName;
            if (root is not null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch { /* best effort */ }
    }
}
