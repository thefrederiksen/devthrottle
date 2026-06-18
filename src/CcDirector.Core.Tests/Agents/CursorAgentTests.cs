using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using Xunit;

namespace CcDirector.Core.Tests.Agents;

/// <summary>
/// Unit tests for the Cursor (cursor-agent) provider (issue #517): the AgentKind
/// round-trip (AC1), the launch-spec contract (AC2), and executable/path resolution
/// (AC3).
/// </summary>
public class CursorAgentTests
{
    // ---------------------------------------------------------------- AC1

    [Fact]
    public void AgentKind_Cursor_RoundTripsThroughJsonSerialization()
    {
        // sessions.json persists AgentKind by name via JsonStringEnumConverter, so a
        // restored session relaunches with cursor-agent. Assert the round-trip.
        var json = JsonSerializer.Serialize(AgentKind.Cursor);
        Assert.Equal("\"Cursor\"", json);

        var roundTripped = JsonSerializer.Deserialize<AgentKind>(json);
        Assert.Equal(AgentKind.Cursor, roundTripped);
    }

    [Fact]
    public void CursorAgent_Kind_IsCursor()
    {
        var agent = new CursorAgent(new AgentOptions());
        Assert.Equal(AgentKind.Cursor, agent.Kind);
    }

    // ---------------------------------------------------------------- AC2

    [Fact]
    public void BuildLaunchSpec_NewSession_DoesNotEmitSessionId()
    {
        var agent = new CursorAgent(new AgentOptions());

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false);

        // Cursor mints its own id; there is no --session-id preassign flag.
        Assert.DoesNotContain("--session-id", spec.Arguments);
        Assert.Null(spec.PreassignedSessionId);
    }

    [Fact]
    public void BuildLaunchSpec_NewSession_PassesUserArgsThrough()
    {
        var agent = new CursorAgent(new AgentOptions());

        var spec = agent.BuildLaunchSpec(userArgs: "--model gpt-5", resumeSessionId: null, studioMode: false);

        Assert.Contains("--model gpt-5", spec.Arguments);
    }

    [Fact]
    public void BuildLaunchSpec_Resume_EmitsResumeFlagWithQuotedId()
    {
        var agent = new CursorAgent(new AgentOptions());

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: "chat_abc123", studioMode: false);

        Assert.Contains("--resume=\"chat_abc123\"", spec.Arguments);
        Assert.Null(spec.PreassignedSessionId);
    }

    [Fact]
    public void BuildLaunchSpec_YoloPresetArgs_IncludesForce()
    {
        // The "Automatic (yolo)" preset contributes --force; the agent passes it through.
        var agent = new CursorAgent(new AgentOptions());

        var spec = agent.BuildLaunchSpec(
            userArgs: AgentToolCatalog.CursorForceArg, resumeSessionId: null, studioMode: false);

        Assert.Contains("--force", spec.Arguments);
    }

    [Fact]
    public void BuildLaunchSpec_StudioMode_AddsStreamJsonFlags()
    {
        var agent = new CursorAgent(new AgentOptions());

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: true);

        Assert.Contains("--output-format stream-json", spec.Arguments);
    }

    [Fact]
    public void SupportsPreassignedSessionId_IsFalse()
    {
        var agent = new CursorAgent(new AgentOptions());
        Assert.False(agent.SupportsPreassignedSessionId);
    }

    // ---------------------------------------------------------------- AC3

    [Fact]
    public void ExecutablePath_DefaultsToCursorAgent()
    {
        var agent = new CursorAgent(new AgentOptions());
        Assert.Equal("cursor-agent", agent.ExecutablePath);
    }

    [Fact]
    public void ExecutablePath_UsesConfiguredCursorPath()
    {
        var options = new AgentOptions { CursorPath = @"D:\Tools\cursor\cursor-agent.exe" };
        var agent = new CursorAgent(options);

        Assert.Equal(@"D:\Tools\cursor\cursor-agent.exe", agent.ExecutablePath);
    }

    [Fact]
    public void GetConfiguredPath_Cursor_ReturnsConfiguredCursorPath()
    {
        var options = new AgentOptions { CursorPath = "my-cursor" };

        var path = ToolDetectionService.GetConfiguredPath(AgentKind.Cursor, options);

        Assert.Equal("my-cursor", path);
    }

    [Fact]
    public void SetConfiguredPath_Cursor_UpdatesAgentOptions()
    {
        var options = new AgentOptions();

        ToolDetectionService.SetConfiguredPath(AgentKind.Cursor, options, "cursor-custom");

        Assert.Equal("cursor-custom", options.CursorPath);
    }

    [Fact]
    public void DetectTool_ConfiguredExistingCursorPath_ReturnsFound()
    {
        // AC3 (configured-path case, must pass even when the real binary is absent).
        var service = new ToolDetectionService();
        var options = new AgentOptions();
        var path = CreateExecutableStub("cursor-detect");

        var result = service.DetectTool(AgentKind.Cursor, options, path);

        Assert.True(result.Found);
        Assert.Equal(AgentKind.Cursor, result.Tool);
        Assert.Equal(Path.GetFullPath(path), result.ResolvedPath);
        Assert.Equal("configured path", result.Source);
    }

    [Fact]
    public void ResolveCursorApiKey_PrefersConfiguredValue()
    {
        var options = new AgentOptions { CursorApiKey = "  configured-key  " };

        Assert.Equal("configured-key", options.ResolveCursorApiKey());
    }

    [Fact]
    public void ResolveCursorApiKey_NoConfigNoEnv_ReturnsNull()
    {
        var options = new AgentOptions { CursorApiKey = null };
        var previous = Environment.GetEnvironmentVariable("CURSOR_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("CURSOR_API_KEY", null);
            Assert.Null(options.ResolveCursorApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSOR_API_KEY", previous);
        }
    }

    private static string CreateExecutableStub(string baseName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-cursor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var ext = OperatingSystem.IsWindows() ? ".cmd" : "";
        var path = Path.Combine(dir, baseName + ext);
        File.WriteAllText(path, OperatingSystem.IsWindows() ? "@echo cursor-agent 1.0\r\n" : "#!/bin/sh\necho cursor-agent 1.0\n");
        return path;
    }
}
