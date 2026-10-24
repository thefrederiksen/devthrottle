using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

public sealed class ClaudeStateHookAdapterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cc-claude-state-hook-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureInstalled_RegistersPromptStopPermissionNotificationAndToolLifecycleHooks()
    {
        var settingsPath = ClaudeHookInstaller.EnsureInstalled(_directory, forWindows: false, ruleHook: null);

        Assert.NotNull(settingsPath);
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var hooks = document.RootElement.GetProperty("hooks");
        AssertStateCommand(hooks, "UserPromptSubmit", "turn_started");
        AssertStateCommand(hooks, "Stop", "turn_ended");
        AssertStateCommand(hooks, "PermissionRequest", "permission_requested");
        AssertStateCommand(hooks, "PreToolUse", "tool_started");
        AssertStateCommand(hooks, "PostToolUse", "tool_finished");
        AssertStateCommand(hooks, "PostToolUseFailure", "tool_finished");

        var notifications = hooks.GetProperty("Notification");
        Assert.Equal(2, notifications.GetArrayLength());
        Assert.Equal("permission_prompt", notifications[0].GetProperty("matcher").GetString());
        Assert.Contains("permission_requested", ReadCommand(notifications[0]), StringComparison.Ordinal);
        Assert.Equal("idle_prompt", notifications[1].GetProperty("matcher").GetString());
        Assert.Contains("turn_ended", ReadCommand(notifications[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void UnixStateHook_SequentialInvocations_EmitBoundAtomicContractEvents()
    {
        if (OperatingSystem.IsWindows()) return;
        var settingsPath = ClaudeHookInstaller.EnsureInstalled(_directory, forWindows: false, ruleHook: null);
        Assert.NotNull(settingsPath);
        var sessionId = Guid.NewGuid();
        var token = "0123456789abcdef0123456789abcdef";
        var stateDirectory = Path.Combine(_directory, sessionId + "." + token);
        var script = Path.Combine(_directory, "report-state.sh");

        RunUnixHook(script, "turn_started", sessionId, token, stateDirectory,
            "{\"hook_event_name\":\"UserPromptSubmit\"}");
        RunUnixHook(script, "tool_started", sessionId, token, stateDirectory,
            "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"AskUserQuestion\"}");

        var files = Directory.GetFiles(stateDirectory, "*.state.json").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, files.Length);
        Assert.EndsWith("1.state.json", files[0], StringComparison.Ordinal);
        Assert.EndsWith("2.state.json", files[1], StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(stateDirectory, "*.tmp"));
        AssertEvent(files[0], sessionId, 1, "turn_started");
        AssertEvent(files[1], sessionId, 2, "menu_opened");
    }

    [Fact]
    public void UnixStateHook_MismatchedSessionCapabilityDirectory_EmitsNothing()
    {
        if (OperatingSystem.IsWindows()) return;
        var settingsPath = ClaudeHookInstaller.EnsureInstalled(_directory, forWindows: false, ruleHook: null);
        Assert.NotNull(settingsPath);
        var sessionId = Guid.NewGuid();
        var otherSessionId = Guid.NewGuid();
        var token = "0123456789abcdef0123456789abcdef";
        var stateDirectory = Path.Combine(_directory, otherSessionId + "." + token);
        var script = Path.Combine(_directory, "report-state.sh");

        RunUnixHook(script, "turn_started", sessionId, token, stateDirectory, "{}");

        Assert.False(Directory.Exists(stateDirectory));
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

    private static void RunUnixHook(
        string script, string fact, Guid sessionId, string token, string stateDirectory, string standardInput)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This helper executes the Unix hook.");
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(fact);
        var scriptDirectory = Path.GetDirectoryName(script);
        Assert.NotNull(scriptDirectory);
        var fakeClaude = Path.Combine(scriptDirectory, "claude");
        if (!File.Exists(fakeClaude))
        {
            File.WriteAllText(fakeClaude, "#!/bin/sh\nprintf '2.1.302 (test fixture)\\n'\n");
            File.SetUnixFileMode(fakeClaude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        start.Environment["PATH"] = scriptDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        start.Environment["CC_SESSION_ID"] = sessionId.ToString();
        start.Environment["CC_STATE_EVENT_TOKEN"] = token;
        start.Environment["CC_STATE_EVENT_DIR"] = stateDirectory;
        using var process = Process.Start(start);
        Assert.NotNull(process);
        process.StandardInput.Write(standardInput);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
    }

    private static void AssertEvent(string path, Guid sessionId, long sequence, string eventName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.True(Guid.TryParse(root.GetProperty("eventId").GetString(), out _));
        Assert.Equal(sessionId, root.GetProperty("sessionId").GetGuid());
        Assert.Equal(sequence, root.GetProperty("sequence").GetInt64());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("occurredAtUtc").GetString(), out _));
        Assert.Equal(eventName, root.GetProperty("event").GetString());
        Assert.Equal("agent", root.GetProperty("source").GetString());
        Assert.Equal("claude", root.GetProperty("agent").GetString());
        Assert.Equal("cliVersion=2.1.302 (test fixture)", root.GetProperty("detail").GetString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { /* the temporary-file sweeper removes leftovers */ }
    }
}
