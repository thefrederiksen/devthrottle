using Xunit;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;

namespace CcDirector.Core.Tests.Settings;

public class ToolDetectionServiceTests
{
    [Fact]
    public void DetectTool_ConfiguredExistingPath_ReturnsFound()
    {
        var service = new ToolDetectionService();
        var options = new AgentOptions();
        var path = CreateVersionTool("detect-tool", "detect-version");

        var result = service.DetectTool(AgentKind.Pi, options, path);

        Assert.True(result.Found);
        Assert.Equal(AgentKind.Pi, result.Tool);
        Assert.Equal(Path.GetFullPath(path), result.ResolvedPath);
        Assert.Equal("configured path", result.Source);
    }

    [Fact]
    public void DetectTool_MissingPath_NeverResolvesToThatPath()
    {
        var service = new ToolDetectionService();
        var options = new AgentOptions();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-tool.exe");

        var result = service.DetectTool(AgentKind.Codex, options, missing);

        // A missing explicit path must never be reported as the resolved tool.
        // DetectTool may still fall back to a copy found on PATH or in a known
        // install location, so we can't assert "not found" here -- that would
        // depend on Codex being absent on the test/CI machine. We assert the
        // invariant that holds either way: the bad path itself is rejected.
        Assert.NotEqual(Path.GetFullPath(missing), result.ResolvedPath);
        if (!result.Found)
        {
            Assert.Null(result.ResolvedPath);
            Assert.Equal("not found", result.Source);
        }
    }

    [Fact]
    public async Task TestToolAsync_VersionCommandSucceeds_ReturnsOkWithVersion()
    {
        var service = new ToolDetectionService();
        var path = CreateVersionTool("test-tool", "1.2.3-test");

        var result = await service.TestToolAsync(AgentKind.ClaudeCode, path);

        Assert.True(result.Ok);
        Assert.Equal("1.2.3-test", result.Version);
        Assert.Contains("OK", result.Message);
    }

    [Fact]
    public void SetConfiguredPath_SupportedTools_UpdatesAgentOptions()
    {
        var options = new AgentOptions();

        ToolDetectionService.SetConfiguredPath(AgentKind.ClaudeCode, options, "claude-custom");
        ToolDetectionService.SetConfiguredPath(AgentKind.Pi, options, "pi-custom");
        ToolDetectionService.SetConfiguredPath(AgentKind.Codex, options, "codex-custom");
        ToolDetectionService.SetConfiguredPath(AgentKind.Gemini, options, "gemini-custom");
        ToolDetectionService.SetConfiguredPath(AgentKind.OpenCode, options, "opencode-custom");

        Assert.Equal("claude-custom", options.ClaudePath);
        Assert.Equal("pi-custom", options.PiPath);
        Assert.Equal("codex-custom", options.CodexPath);
        Assert.Equal("gemini-custom", options.GeminiPath);
        Assert.Equal("opencode-custom", options.OpenCodePath);
    }

    [Fact]
    public void GetConfiguredPath_SupportedTools_ReturnsAgentOptionsValues()
    {
        var options = new AgentOptions
        {
            ClaudePath = "claude-custom",
            PiPath = "pi-custom",
            CodexPath = "codex-custom",
            GeminiPath = "gemini-custom",
            OpenCodePath = "opencode-custom"
        };

        Assert.Equal("claude-custom", ToolDetectionService.GetConfiguredPath(AgentKind.ClaudeCode, options));
        Assert.Equal("pi-custom", ToolDetectionService.GetConfiguredPath(AgentKind.Pi, options));
        Assert.Equal("codex-custom", ToolDetectionService.GetConfiguredPath(AgentKind.Codex, options));
        Assert.Equal("gemini-custom", ToolDetectionService.GetConfiguredPath(AgentKind.Gemini, options));
        Assert.Equal("opencode-custom", ToolDetectionService.GetConfiguredPath(AgentKind.OpenCode, options));
    }

    [Fact]
    public async Task IsToolValidated_CurrentConfiguredPathWasTested_ReturnsTrue()
    {
        var oldRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-director-tool-validation-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            var service = new ToolDetectionService();
            var path = CreateVersionTool("validated-tool", "validated-version");
            var result = await service.TestToolAsync(AgentKind.Gemini, path);
            CcDirectorConfigService.MergePatch(ToolDetectionService.BuildValidationPatch(result));

            Assert.True(ToolDetectionService.IsToolValidated(AgentKind.Gemini, new AgentOptions { GeminiPath = path }));
            Assert.False(ToolDetectionService.IsToolValidated(AgentKind.Gemini, new AgentOptions { GeminiPath = path + "-other" }));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", oldRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateVersionTool(string prefix, string version)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-director-tool-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, prefix + ".cmd");
            File.WriteAllText(path, $"@echo off\r\necho {version}\r\nexit /b 0\r\n");
            return path;
        }
        else
        {
            var path = Path.Combine(dir, prefix);
            File.WriteAllText(path, $"#!/usr/bin/env sh\necho {version}\nexit 0\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }
    }
}
