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
        Assert.Contains(".tmp", script, StringComparison.Ordinal);
        Assert.Contains("cliVersion", script, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", script, StringComparison.OrdinalIgnoreCase);
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
    public void UnixStateHook_UnknownNotification_EmitsUnsupportedDiagnostic()
    {
        if (OperatingSystem.IsWindows()) return;
        var settingsPath = ClaudeHookInstaller.EnsureInstalled(_directory, forWindows: false, ruleHook: null);
        Assert.NotNull(settingsPath);
        var sessionId = Guid.NewGuid();
        var token = "0123456789abcdef0123456789abcdef";
        var stateDirectory = Path.Combine(_directory, sessionId + "." + token);
        var script = Path.Combine(_directory, "report-state.sh");

        RunUnixHook(script, "notification", sessionId, token, stateDirectory,
            "{\"hook_event_name\":\"Notification\",\"notification_type\":\"auth_success\"}");

        var file = Assert.Single(Directory.GetFiles(stateDirectory, "*.state.json"));
        AssertEvent(file, sessionId, 1, "unsupported", "auth_success");
    }

    [Fact]
    public void UnixStateHook_ConcurrentInvocations_EmitEverySequence()
    {
        if (OperatingSystem.IsWindows()) return;
        var settingsPath = ClaudeHookInstaller.EnsureInstalled(_directory, forWindows: false, ruleHook: null);
        Assert.NotNull(settingsPath);
        var sessionId = Guid.NewGuid();
        var token = "0123456789abcdef0123456789abcdef";
        var stateDirectory = Path.Combine(_directory, sessionId + "." + token);
        var script = Path.Combine(_directory, "report-state.sh");
        EnsureFakeClaude(script);

        var processes = Enumerable.Range(0, 8)
            .Select(_ => StartUnixHook(script, "tool_started", sessionId, token, stateDirectory, "{\"tool_name\":\"Bash\"}"))
            .Append(StartUnixHook(script, "permission_requested", sessionId, token, stateDirectory, "{}"))
            .ToArray();
        foreach (var process in processes)
        {
            Assert.True(process.WaitForExit(10_000));
            Assert.Equal(0, process.ExitCode);
            process.Dispose();
        }

        var sequences = Directory.GetFiles(stateDirectory, "*.state.json")
            .Select(path => long.Parse(Path.GetFileNameWithoutExtension(path).Split('.')[0]))
            .OrderBy(sequence => sequence)
            .ToArray();
        Assert.Equal(Enumerable.Range(1, 9).Select(value => (long)value), sequences);
        Assert.Contains(Directory.GetFiles(stateDirectory, "*.state.json"), path =>
            JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("event").GetString() == "permission_requested");
    }

    [Fact]
    public void PowerShellStateHook_MalformedPayload_EmitsUnsupportedDiagnostic()
    {
        var powerShell = FindPowerShell();
        if (powerShell is null) return;
        var settingsPath = ClaudeHookInstaller.EnsureInstalled(_directory, forWindows: true, ruleHook: null);
        Assert.NotNull(settingsPath);
        var sessionId = Guid.NewGuid();
        var token = "0123456789abcdef0123456789abcdef";
        var stateDirectory = Path.Combine(_directory, sessionId + "." + token);

        RunPowerShellHook(powerShell, Path.Combine(_directory, "report-state.ps1"), "notification", sessionId, token, stateDirectory,
            "{\"notification_type\":\"permission");

        var file = Assert.Single(Directory.GetFiles(stateDirectory, "*.state.json"));
        AssertEvent(file, sessionId, 1, "unsupported", "notification");
    }

    [Fact]
    public void PowerShellStateHook_FinnishCulture_EmitsIsoTimestamp()
    {
        var powerShell = FindPowerShell();
        if (powerShell is null) return;
        var settingsPath = ClaudeHookInstaller.EnsureInstalled(_directory, forWindows: true, ruleHook: null);
        Assert.NotNull(settingsPath);
        var sessionId = Guid.NewGuid();
        var token = "0123456789abcdef0123456789abcdef";
        var stateDirectory = Path.Combine(_directory, sessionId + "." + token);

        RunPowerShellHook(powerShell, Path.Combine(_directory, "report-state.ps1"), "turn_started", sessionId, token, stateDirectory, "{}", "fi-FI");

        var file = Assert.Single(Directory.GetFiles(stateDirectory, "*.state.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$", document.RootElement.GetProperty("occurredAtUtc").GetString());
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
        using var process = StartUnixHook(script, fact, sessionId, token, stateDirectory, standardInput);
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
    }

    private static Process StartUnixHook(
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
        EnsureFakeClaude(script);
        start.Environment["PATH"] = scriptDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        start.Environment["CC_SESSION_ID"] = sessionId.ToString();
        start.Environment["CC_STATE_EVENT_TOKEN"] = token;
        start.Environment["CC_STATE_EVENT_DIR"] = stateDirectory;
        var process = Process.Start(start);
        Assert.NotNull(process);
        process.StandardInput.Write(standardInput);
        process.StandardInput.Close();
        return process;
    }

    private static void EnsureFakeClaude(string script)
    {
        var scriptDirectory = Path.GetDirectoryName(script);
        Assert.NotNull(scriptDirectory);
        if (OperatingSystem.IsWindows())
        {
            var fakeClaude = Path.Combine(scriptDirectory, "claude.cmd");
            if (!File.Exists(fakeClaude))
                File.WriteAllText(fakeClaude, "@echo 2.1.302 (test fixture)\r\n");
            return;
        }

        var unixFakeClaude = Path.Combine(scriptDirectory, "claude");
        if (!File.Exists(unixFakeClaude))
        {
            File.WriteAllText(unixFakeClaude, "#!/bin/sh\nprintf '2.1.302 (test fixture)\\n'\n");
            File.SetUnixFileMode(unixFakeClaude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string? FindPowerShell()
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        return path.Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh"))
            .FirstOrDefault(File.Exists);
    }

    private static void RunPowerShellHook(string powerShell, string script, string fact, Guid sessionId, string token, string stateDirectory, string standardInput, string? culture = null)
    {
        var start = new ProcessStartInfo(powerShell)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-NoProfile");
        if (culture is null)
        {
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add(fact);
        }
        else
        {
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"[Threading.Thread]::CurrentThread.CurrentCulture=[Globalization.CultureInfo]::GetCultureInfo('{culture}'); & '{script.Replace("'", "''", StringComparison.Ordinal)}' '{fact}'");
        }
        var scriptDirectory = Path.GetDirectoryName(script);
        Assert.NotNull(scriptDirectory);
        EnsureFakeClaude(script);
        start.Environment["PATH"] = scriptDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        start.Environment["CC_SESSION_ID"] = sessionId.ToString();
        start.Environment["CC_STATE_EVENT_TOKEN"] = token;
        start.Environment["CC_STATE_EVENT_DIR"] = stateDirectory;
        using var process = Process.Start(start);
        Assert.NotNull(process);
        process.StandardInput.Write(standardInput);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(10_000), process.StandardError.ReadToEnd());
        Assert.Equal(0, process.ExitCode);
    }

    private static void AssertEvent(string path, Guid sessionId, long sequence, string eventName, string? detail = null)
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
        if (detail is null)
            Assert.Equal(JsonValueKind.Null, root.GetProperty("detail").ValueKind);
        else
            Assert.Equal(detail, root.GetProperty("detail").GetString());
        Assert.Equal("2.1.302 (test fixture)", root.GetProperty("cliVersion").GetString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { /* the temporary-file sweeper removes leftovers */ }
    }
}
