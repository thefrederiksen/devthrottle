using CcDirector.Core.Agents;
using Xunit;

namespace CcDirector.Core.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="RawCliAgent"/>: launch-spec building for the custom
/// arbitrary-CLI agent (issue #333).
/// </summary>
public class RawCliAgentTests
{
    // ===== Constructor validation =====

    [Fact]
    public void Constructor_NullExe_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new RawCliAgent(null!));
    }

    [Fact]
    public void Constructor_EmptyExe_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new RawCliAgent(""));
    }

    [Fact]
    public void Constructor_WhitespaceExe_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new RawCliAgent("   "));
    }

    // ===== Kind / capabilities =====

    [Fact]
    public void Kind_IsRawCli()
    {
        var agent = new RawCliAgent("pwsh");

        Assert.Equal(AgentKind.RawCli, agent.Kind);
    }

    [Fact]
    public void SupportsPreassignedSessionId_IsFalse()
    {
        var agent = new RawCliAgent("pwsh");

        Assert.False(agent.SupportsPreassignedSessionId);
    }

    [Fact]
    public void SupportsStudioMode_IsFalse()
    {
        var agent = new RawCliAgent("pwsh");

        Assert.False(agent.SupportsStudioMode);
    }

    // ===== ExecutablePath =====

    [Fact]
    public void ExecutablePath_ReturnsConstructorArg_BareCommand()
    {
        var agent = new RawCliAgent("pwsh");

        Assert.Equal("pwsh", agent.ExecutablePath);
    }

    [Fact]
    public void ExecutablePath_ReturnsConstructorArg_AbsolutePath()
    {
        var agent = new RawCliAgent(@"C:\Tools\aider.exe");

        Assert.Equal(@"C:\Tools\aider.exe", agent.ExecutablePath);
    }

    [Fact]
    public void ExecutablePath_TrimsConstructorArg()
    {
        var agent = new RawCliAgent("  pwsh  ");

        Assert.Equal("pwsh", agent.ExecutablePath);
    }

    // ===== BuildLaunchSpec - no extra args =====

    [Fact]
    public void BuildLaunchSpec_NoArgs_ReturnsEmptyArguments()
    {
        var agent = new RawCliAgent("pwsh");

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false);

        Assert.NotNull(spec);
        Assert.Equal(string.Empty, spec.Arguments);
    }

    [Fact]
    public void BuildLaunchSpec_NoArgs_PreassignedSessionId_IsNull()
    {
        var agent = new RawCliAgent("pwsh");

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false);

        Assert.Null(spec.PreassignedSessionId);
    }

    // ===== BuildLaunchSpec - user args =====

    [Fact]
    public void BuildLaunchSpec_UserArgsOnly_ReturnsUserArgs()
    {
        var agent = new RawCliAgent("aider");

        var spec = agent.BuildLaunchSpec(userArgs: "--no-auto-commits", resumeSessionId: null, studioMode: false);

        Assert.Equal("--no-auto-commits", spec.Arguments);
    }

    // ===== BuildLaunchSpec - construction-time extra args =====

    [Fact]
    public void BuildLaunchSpec_ConstructionTimeExtraArgs_ArePrependedToUserArgs()
    {
        var agent = new RawCliAgent("aider", extraArgs: "--dark-mode");

        var spec = agent.BuildLaunchSpec(userArgs: "--no-auto-commits", resumeSessionId: null, studioMode: false);

        Assert.Equal("--dark-mode --no-auto-commits", spec.Arguments);
    }

    [Fact]
    public void BuildLaunchSpec_ConstructionTimeExtraArgs_WithNoUserArgs_JustExtraArgs()
    {
        var agent = new RawCliAgent("aider", extraArgs: "--dark-mode");

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false);

        Assert.Equal("--dark-mode", spec.Arguments);
    }

    [Fact]
    public void BuildLaunchSpec_ConstructionTimeExtraArgs_NullUserArgs_NoPaddingSpaces()
    {
        var agent = new RawCliAgent("aider", extraArgs: "--flag");

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false);

        Assert.False(spec.Arguments.StartsWith(" "), "Arguments must not start with a space");
        Assert.False(spec.Arguments.EndsWith(" "), "Arguments must not end with a space");
    }

    // ===== BuildLaunchSpec - cmd routing via CommandLineLauncher =====
    // The routing happens inside CommandLineLauncher.Build, called by SessionManager.
    // These tests verify that BuildLaunchSpec itself does not pre-wrap the command;
    // CommandLineLauncher tests cover the .cmd/.bat wrapping path directly.

    [Fact]
    public void BuildLaunchSpec_DotCmdExecutable_DoesNotPreWrapInLaunchSpec()
    {
        // The exe is resolved and wrapped later by CommandLineLauncher.Build inside
        // SessionManager. BuildLaunchSpec must not touch the executable path.
        var agent = new RawCliAgent(@"C:\npm\aider.cmd");

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false);

        // Arguments should be empty (no user args); no cmd.exe wrapping here.
        Assert.Equal(string.Empty, spec.Arguments);
        Assert.Null(spec.PreassignedSessionId);
    }

    // ===== BuildLaunchSpec - ignored features =====

    [Fact]
    public void BuildLaunchSpec_ResumeSessionIdIgnored_NoPreassignedSessionId()
    {
        var agent = new RawCliAgent("pwsh");

        var spec = agent.BuildLaunchSpec(userArgs: "--flag", resumeSessionId: "abc-123-def", studioMode: false);

        // Resume is silently ignored; no preassigned ID.
        Assert.Null(spec.PreassignedSessionId);
    }

    [Fact]
    public void BuildLaunchSpec_StudioModeIgnored_NoStreamJsonPrepended()
    {
        var agent = new RawCliAgent("pwsh");

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: true);

        // Studio mode args must NOT appear in the arguments.
        Assert.DoesNotContain("stream-json", spec.Arguments);
        Assert.DoesNotContain("--output-format", spec.Arguments);
    }
}
