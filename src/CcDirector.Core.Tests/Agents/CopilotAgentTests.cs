using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using Xunit;

namespace CcDirector.Core.Tests.Agents;

/// <summary>
/// Unit tests for the GitHub Copilot (copilot) provider (issue #625): the AgentKind round-trip
/// (AC1), the launch-spec contract (AC2), executable/path resolution (AC3), and auth-token
/// resolution (AC10).
/// </summary>
public class CopilotAgentTests
{
    // ---------------------------------------------------------------- AC1

    [Fact]
    public void AgentKind_Copilot_RoundTripsThroughJsonSerialization()
    {
        // sessions.json persists AgentKind by name via JsonStringEnumConverter, so a restored
        // session relaunches with copilot. Assert the round-trip.
        var json = JsonSerializer.Serialize(AgentKind.Copilot);
        Assert.Equal("\"Copilot\"", json);

        var roundTripped = JsonSerializer.Deserialize<AgentKind>(json);
        Assert.Equal(AgentKind.Copilot, roundTripped);
    }

    [Fact]
    public void CopilotAgent_Kind_IsCopilot()
    {
        var agent = new CopilotAgent(new AgentOptions());
        Assert.Equal(AgentKind.Copilot, agent.Kind);
    }

    // ---------------------------------------------------------------- AC2

    [Fact]
    public void BuildLaunchSpec_NewSession_EmitsMintedSessionId()
    {
        // AC2(a): a new session emits --session-id <minted-uuid> and carries that id back as the
        // preassigned id (Copilot preassigns, like Claude).
        var agent = new CopilotAgent(new AgentOptions());

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false);

        Assert.Contains("--session-id", spec.Arguments);
        Assert.NotNull(spec.PreassignedSessionId);
        Assert.True(Guid.TryParse(spec.PreassignedSessionId, out _));
        Assert.Contains(spec.PreassignedSessionId, spec.Arguments);
    }

    [Fact]
    public void BuildLaunchSpec_YoloPresetArgs_IncludesAllowAll()
    {
        // AC2(b): the "Automatic (yolo)" preset contributes --allow-all; the agent passes it through.
        var agent = new CopilotAgent(new AgentOptions());

        var spec = agent.BuildLaunchSpec(
            userArgs: AgentToolCatalog.CopilotAllowAllArg, resumeSessionId: null, studioMode: false);

        Assert.Contains("--allow-all", spec.Arguments);
    }

    [Fact]
    public void BuildLaunchSpec_Resume_EmitsResumeFlag()
    {
        // AC2(c): a resume passes --resume <id> and does NOT mint a new session id.
        var agent = new CopilotAgent(new AgentOptions());

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: "abc-123", studioMode: false);

        Assert.Contains("--resume abc-123", spec.Arguments);
        Assert.DoesNotContain("--session-id", spec.Arguments);
        Assert.Null(spec.PreassignedSessionId);
    }

    [Fact]
    public void BuildLaunchSpec_NewSession_PassesUserArgsThrough()
    {
        var agent = new CopilotAgent(new AgentOptions());

        var spec = agent.BuildLaunchSpec(userArgs: "--model auto", resumeSessionId: null, studioMode: false);

        Assert.Contains("--model auto", spec.Arguments);
    }

    [Fact]
    public void SupportsPreassignedSessionId_IsTrue()
    {
        // AC2(d): Copilot preassigns via --session-id (verified live).
        var agent = new CopilotAgent(new AgentOptions());
        Assert.True(agent.SupportsPreassignedSessionId);
    }

    [Fact]
    public void SupportsStudioMode_IsFalse()
    {
        var agent = new CopilotAgent(new AgentOptions());
        Assert.False(agent.SupportsStudioMode);
    }

    // ---------------------------------------------------------------- AC3

    [Fact]
    public void ExecutablePath_DefaultsToNpmGlobalCopilotCmd()
    {
        // The default resolves the npm-global copilot.cmd on Windows (or a bare name if %APPDATA%
        // is unavailable). Either way the configured path is what the agent reports.
        var options = new AgentOptions();
        var agent = new CopilotAgent(options);

        Assert.Equal(options.CopilotPath, agent.ExecutablePath);
    }

    [Fact]
    public void ExecutablePath_UsesConfiguredCopilotPath()
    {
        var options = new AgentOptions { CopilotPath = @"D:\Tools\copilot\copilot.cmd" };
        var agent = new CopilotAgent(options);

        Assert.Equal(@"D:\Tools\copilot\copilot.cmd", agent.ExecutablePath);
    }

    [Fact]
    public void GetConfiguredPath_Copilot_ReturnsConfiguredCopilotPath()
    {
        var options = new AgentOptions { CopilotPath = "my-copilot" };

        var path = ToolDetectionService.GetConfiguredPath(AgentKind.Copilot, options);

        Assert.Equal("my-copilot", path);
    }

    [Fact]
    public void SetConfiguredPath_Copilot_UpdatesAgentOptions()
    {
        var options = new AgentOptions();

        ToolDetectionService.SetConfiguredPath(AgentKind.Copilot, options, "copilot-custom");

        Assert.Equal("copilot-custom", options.CopilotPath);
    }

    [Fact]
    public void DetectTool_ConfiguredExistingCopilotPath_ReturnsFound()
    {
        // AC3 (configured-path case, must pass even when the real binary is absent).
        var service = new ToolDetectionService();
        var options = new AgentOptions();
        var path = CreateExecutableStub("copilot-detect");

        var result = service.DetectTool(AgentKind.Copilot, options, path);

        Assert.True(result.Found);
        Assert.Equal(AgentKind.Copilot, result.Tool);
        Assert.Equal(Path.GetFullPath(path), result.ResolvedPath);
        Assert.Equal("configured path", result.Source);
    }

    [Fact]
    public void SupportedTools_IncludesCopilot()
    {
        // AC5: detection wizard includes copilot in the scanned tool list.
        Assert.Contains(AgentKind.Copilot, ToolDetectionService.SupportedTools);
    }

    [Fact]
    public void DisplayName_Copilot_IsGitHubCopilot()
    {
        Assert.Equal("GitHub Copilot", ToolDetectionService.DisplayName(AgentKind.Copilot));
    }

    // ---------------------------------------------------------------- AC10

    [Fact]
    public void ResolveCopilotToken_PrefersConfiguredValue()
    {
        var options = new AgentOptions { CopilotGitHubToken = "  configured-token  " };

        Assert.Equal("configured-token", options.ResolveCopilotToken());
    }

    [Fact]
    public void ResolveCopilotToken_PrefersCopilotEnvVarOverGhToken()
    {
        // Precedence (verified): COPILOT_GITHUB_TOKEN > GH_TOKEN > GITHUB_TOKEN.
        var options = new AgentOptions { CopilotGitHubToken = null };
        var saved = SnapshotTokenEnv();
        try
        {
            Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", "from-copilot");
            Environment.SetEnvironmentVariable("GH_TOKEN", "from-gh");
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", "from-github");

            Assert.Equal("from-copilot", options.ResolveCopilotToken());
        }
        finally
        {
            RestoreTokenEnv(saved);
        }
    }

    [Fact]
    public void ResolveCopilotToken_FallsBackToGhTokenThenGithubToken()
    {
        var options = new AgentOptions { CopilotGitHubToken = null };
        var saved = SnapshotTokenEnv();
        try
        {
            Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("GH_TOKEN", "from-gh");
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", "from-github");
            Assert.Equal("from-gh", options.ResolveCopilotToken());

            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Assert.Equal("from-github", options.ResolveCopilotToken());
        }
        finally
        {
            RestoreTokenEnv(saved);
        }
    }

    [Fact]
    public void ResolveCopilotToken_NoConfigNoEnv_ReturnsNull()
    {
        var options = new AgentOptions { CopilotGitHubToken = null };
        var saved = SnapshotTokenEnv();
        try
        {
            Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

            Assert.Null(options.ResolveCopilotToken());
        }
        finally
        {
            RestoreTokenEnv(saved);
        }
    }

    private static (string? Copilot, string? Gh, string? Github) SnapshotTokenEnv() =>
        (Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN"),
         Environment.GetEnvironmentVariable("GH_TOKEN"),
         Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

    private static void RestoreTokenEnv((string? Copilot, string? Gh, string? Github) saved)
    {
        Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", saved.Copilot);
        Environment.SetEnvironmentVariable("GH_TOKEN", saved.Gh);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", saved.Github);
    }

    private static string CreateExecutableStub(string baseName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-copilot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var ext = OperatingSystem.IsWindows() ? ".cmd" : "";
        var path = Path.Combine(dir, baseName + ext);
        File.WriteAllText(path, OperatingSystem.IsWindows() ? "@echo copilot 1.0\r\n" : "#!/bin/sh\necho copilot 1.0\n");
        return path;
    }
}
