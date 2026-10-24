using System.Text.Json;
using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

public sealed class ClaudeStateHookAdapterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cc-claude-state-hook-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureInstalled_RegistersOnlyPinnedPromptStopPermissionNotificationAndToolLifecycleHooks()
    {
        var settingsPath = ClaudeHookInstaller.EnsureInstalled(_directory, forWindows: false, ruleHook: null);

        Assert.NotNull(settingsPath);
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var hooks = document.RootElement.GetProperty("hooks");
        Assert.Equal(
            new[] { "Notification", "PermissionRequest", "PostToolUse", "PostToolUseFailure", "PreToolUse", "SessionStart", "Stop", "UserPromptSubmit" },
            hooks.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        AssertStateCommand(hooks, "UserPromptSubmit", "turn_started");
        AssertStateCommand(hooks, "Stop", "turn_ended");
        AssertStateCommand(hooks, "PermissionRequest", "permission_requested");
        AssertStateCommand(hooks, "PreToolUse", "tool_started");
        AssertStateCommand(hooks, "PostToolUse", "tool_finished");
        AssertStateCommand(hooks, "PostToolUseFailure", "tool_finished");

        var notifications = hooks.GetProperty("Notification");
        Assert.Single(notifications.EnumerateArray());
        Assert.False(notifications[0].TryGetProperty("matcher", out _));
        Assert.Contains("notification", ReadCommand(notifications[0]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "report-state.sh")]
    [InlineData(true, "report-state.ps1")]
    public void EnsureInstalled_StateScriptCarriesVersionedCapabilityProtectedAtomicContract(
        bool forWindows, string scriptName)
    {
        var settingsPath = ClaudeHookInstaller.EnsureInstalled(_directory, forWindows, ruleHook: null);
        Assert.NotNull(settingsPath);
        var script = File.ReadAllText(Path.Combine(_directory, scriptName));

        Assert.Contains("CC_STATE_EVENT_DIR", script, StringComparison.Ordinal);
        Assert.Contains("CC_STATE_EVENT_TOKEN", script, StringComparison.Ordinal);
        Assert.Contains("CC_SESSION_ID", script, StringComparison.Ordinal);
        Assert.Contains("version", script, StringComparison.Ordinal);
        Assert.Contains(".state.json", script, StringComparison.Ordinal);
        Assert.Contains(".claude-sequence.lock", script, StringComparison.Ordinal);
        Assert.Contains(".tmp", script, StringComparison.Ordinal);
        Assert.Contains("cliVersion", script, StringComparison.Ordinal);
        Assert.Contains("unsupported", script, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "report-state.sh")]
    [InlineData(true, "report-state.ps1")]
    public void EnsureInstalled_StateScriptSerializesSequenceBeforeWritingTheRecord(bool forWindows, string scriptName)
    {
        var settingsPath = ClaudeHookInstaller.EnsureInstalled(_directory, forWindows, ruleHook: null);
        Assert.NotNull(settingsPath);
        var script = File.ReadAllText(Path.Combine(_directory, scriptName));

        Assert.Contains(".claude-sequence.lock", script, StringComparison.Ordinal);
        Assert.Contains(".claude-sequence", script, StringComparison.Ordinal);
        Assert.Contains(".state.json", script, StringComparison.Ordinal);
        Assert.Contains(".tmp", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "report-state.sh", "$sequence.state.json", "$path.$$.tmp")]
    [InlineData(true, "report-state.ps1", "+ '.state.json'", "$path + '.' + $PID + '.tmp'")]
    public void EnsureInstalled_StateScript_UsesOneSharedCounterAndTemporaryNamesCannotMatchEventFiles(
        bool forWindows, string scriptName, string eventFileName, string temporaryFileName)
    {
        var settingsPath = ClaudeHookInstaller.EnsureInstalled(_directory, forWindows, ruleHook: null);
        Assert.NotNull(settingsPath);
        var script = File.ReadAllText(Path.Combine(_directory, scriptName));

        Assert.Contains(".claude-sequence", script, StringComparison.Ordinal);
        Assert.Contains(eventFileName, script, StringComparison.Ordinal);
        Assert.Contains(temporaryFileName, script, StringComparison.Ordinal);
        Assert.DoesNotContain(".state.json.tmp", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureInstalled_PowerShellStateScript_MalformedPayloadBecomesUnsupportedAndUsesInvariantTimestamp()
    {
        var settingsPath = ClaudeHookInstaller.EnsureInstalled(_directory, forWindows: true, ruleHook: null);
        Assert.NotNull(settingsPath);
        var script = File.ReadAllText(Path.Combine(_directory, "report-state.ps1"));

        Assert.Contains("catch { $eventName = 'unsupported'; $detail = $args[0] }", script, StringComparison.Ordinal);
        Assert.Contains("[Globalization.CultureInfo]::InvariantCulture", script, StringComparison.Ordinal);
    }

    private static void AssertStateCommand(JsonElement hooks, string hookName, string fact)
    {
        var entries = hooks.GetProperty(hookName);
        Assert.Contains(fact, ReadCommand(entries[0]), StringComparison.Ordinal);
    }

    private static string ReadCommand(JsonElement entry)
    {
        var command = entry.GetProperty("hooks")[0].GetProperty("command").GetString();
        Assert.NotNull(command);
        return command;
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { /* the temporary-file sweeper removes leftovers */ }
    }
}
