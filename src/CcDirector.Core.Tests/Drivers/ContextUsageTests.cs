using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Drivers;

// =====================================================================================
// ContextUsage capability (issue #799): the model -> window-size lookup, the ClaudeDriver
// ReadContextUsage mapping (percent + raw-number fallback), and the NotSupported guarantee
// on a driver that does not declare the flag.
// =====================================================================================
public sealed class ContextUsageTests
{
    // ---- ClaudeContextWindow: model id -> window size ----

    [Theory]
    [InlineData("claude-opus-4-8[1m]")]
    [InlineData("opus[1m]")]
    [InlineData("OPUS[1M]")] // suffix match is case-insensitive
    public void WindowTokensForModel_OneMillionSuffix_ReturnsOneMillion(string modelId)
    {
        Assert.Equal(1_000_000, ClaudeContextWindow.WindowTokensForModel(modelId));
    }

    [Theory]
    [InlineData("claude-opus-4-8")]
    [InlineData("opus")]
    [InlineData("claude-sonnet-4-5-20250929")]
    [InlineData("sonnet")]
    [InlineData("claude-haiku-4-5")]
    [InlineData("fable")]
    public void WindowTokensForModel_StandardClaudeModels_ReturnTwoHundredThousand(string modelId)
    {
        Assert.Equal(200_000, ClaudeContextWindow.WindowTokensForModel(modelId));
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gemini-2.5-pro")]
    [InlineData("some-unknown-model")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void WindowTokensForModel_UnmappedOrEmpty_ReturnsNull(string? modelId)
    {
        Assert.Null(ClaudeContextWindow.WindowTokensForModel(modelId));
    }

    // ---- ClaudeDriver.ReadContextUsage: mapping + fallback ----

    private static ClaudeDriver DriverReturning(SessionUsageDto? usage)
        => new(new StubTranscriptReader(usage));

    [Fact]
    public void ReadContextUsage_KnownModel_ComputesPercentAndWindow()
    {
        var usage = new SessionUsageDto
        {
            ContextTokens = 42_000,
            ContextModel = "claude-sonnet-4-5-20250929",
            AssistantMessageCount = 3,
            LastMessageUtc = new DateTime(2026, 6, 27, 8, 0, 0, DateTimeKind.Utc),
        };

        var ctx = DriverReturning(usage).ReadContextUsage("sid", "C:\\repo", null);

        Assert.NotNull(ctx);
        Assert.Equal(42_000, ctx.UsedTokens);
        Assert.Equal(200_000, ctx.WindowTokens);
        Assert.Equal(21.0, ctx.PercentUsed);
        Assert.Equal(usage.LastMessageUtc, ctx.AsOfUtc);
    }

    [Fact]
    public void ReadContextUsage_OneMillionModel_UsesMillionDenominator()
    {
        var usage = new SessionUsageDto
        {
            ContextTokens = 250_000,
            ContextModel = "claude-opus-4-8[1m]",
            AssistantMessageCount = 1,
        };

        var ctx = DriverReturning(usage).ReadContextUsage("sid", "C:\\repo", null);

        Assert.NotNull(ctx);
        Assert.Equal(1_000_000, ctx.WindowTokens);
        Assert.Equal(25.0, ctx.PercentUsed);
    }

    [Fact]
    public void ReadContextUsage_UnmappedModel_RawNumberFallback_NoWindowNoPercent()
    {
        var usage = new SessionUsageDto
        {
            ContextTokens = 12_345,
            ContextModel = "gpt-4o",
            AssistantMessageCount = 2,
        };

        var ctx = DriverReturning(usage).ReadContextUsage("sid", "C:\\repo", null);

        Assert.NotNull(ctx);
        Assert.Equal(12_345, ctx.UsedTokens);
        Assert.Null(ctx.WindowTokens);
        Assert.Null(ctx.PercentUsed);
    }

    [Fact]
    public void ReadContextUsage_NoTurnYet_ReturnsNull()
    {
        // Transcript exists but carries no usage-bearing assistant line.
        var usage = new SessionUsageDto { AssistantMessageCount = 0 };
        Assert.Null(DriverReturning(usage).ReadContextUsage("sid", "C:\\repo", null));
    }

    [Fact]
    public void ReadContextUsage_NoTranscript_ReturnsNull()
    {
        Assert.Null(DriverReturning(null).ReadContextUsage("sid", "C:\\repo", null));
    }

    // ---- Issue #803: the launch model id is the authoritative window signal ----

    [Fact]
    public void ReadContextUsage_LaunchModelOneMillion_TranscriptStripped_SizesAgainstMillion()
    {
        // The real-world #803 case: the session was launched as opus[1m] but Claude's transcript
        // records the model WITHOUT the [1m] suffix, so the transcript model alone would size it to
        // 200k and read 61%. The launch args carry the authoritative [1m] window.
        var usage = new SessionUsageDto
        {
            ContextTokens = 121_924,
            ContextModel = "claude-opus-4-8", // stripped, as the real transcript records it
            AssistantMessageCount = 5,
        };

        var ctx = DriverReturning(usage)
            .ReadContextUsage("sid", "C:\\repo", "--dangerously-skip-permissions --model opus[1m]");

        Assert.NotNull(ctx);
        Assert.Equal(121_924, ctx.UsedTokens);
        Assert.Equal(1_000_000, ctx.WindowTokens);
        Assert.InRange(ctx.PercentUsed!.Value, 11.0, 13.0); // ~12.2%, NOT ~61%
    }

    [Theory]
    [InlineData("--model claude-opus-4-8[1m]")] // equals-less, full transcript-style id
    [InlineData("--model=opus[1m]")]            // equals form
    public void ReadContextUsage_LaunchModelParsing_BothFlagForms_FindMillion(string launchArgs)
    {
        var usage = new SessionUsageDto
        {
            ContextTokens = 100_000,
            ContextModel = "claude-opus-4-8",
            AssistantMessageCount = 2,
        };

        var ctx = DriverReturning(usage).ReadContextUsage("sid", "C:\\repo", launchArgs);

        Assert.NotNull(ctx);
        Assert.Equal(1_000_000, ctx.WindowTokens);
        Assert.Equal(10.0, ctx.PercentUsed);
    }

    [Fact]
    public void ReadContextUsage_StandardLaunchModel_StaysTwoHundredThousand()
    {
        var usage = new SessionUsageDto
        {
            ContextTokens = 30_000,
            ContextModel = "claude-sonnet-4-5-20250929",
            AssistantMessageCount = 1,
        };

        var ctx = DriverReturning(usage).ReadContextUsage("sid", "C:\\repo", "--model sonnet");

        Assert.NotNull(ctx);
        Assert.Equal(200_000, ctx.WindowTokens);
        Assert.Equal(15.0, ctx.PercentUsed);
    }

    [Fact]
    public void ReadContextUsage_NoLaunchModel_FallsBackToTranscriptSelfCorrection()
    {
        // No --model in the launch args (provider default): fall back to the transcript model with
        // observed-size self-correction. 250k observed cannot fit a 200k window, so it promotes to 1M.
        var usage = new SessionUsageDto
        {
            ContextTokens = 250_000,
            ContextModel = "claude-opus-4-8", // stripped; no [1m] signal except the observed size
            AssistantMessageCount = 4,
        };

        var ctx = DriverReturning(usage).ReadContextUsage("sid", "C:\\repo", "--dangerously-skip-permissions");

        Assert.NotNull(ctx);
        Assert.Equal(1_000_000, ctx.WindowTokens);
        Assert.Equal(25.0, ctx.PercentUsed);
    }

    // ---- ClaudeContextWindow self-correcting (two-arg) fallback overload ----

    [Theory]
    [InlineData("claude-opus-4-8", 250_000, 1_000_000)] // over 200k -> promoted to 1M
    [InlineData("claude-opus-4-8", 50_000, 200_000)]    // under 200k -> stays 200k (honest under-report)
    [InlineData("sonnet", 50_000, 200_000)]             // standard family, low usage
    public void WindowTokensForModel_Observed_SelfCorrectsUpwardOnly(string modelId, long observed, long expected)
    {
        Assert.Equal(expected, ClaudeContextWindow.WindowTokensForModel(modelId, observed));
    }

    [Fact]
    public void WindowTokensForModel_Observed_UnmappedStaysNull()
    {
        Assert.Null(ClaudeContextWindow.WindowTokensForModel("gpt-4o", 999_999));
    }

    // ---- Issue #803 production path: the model comes from the configured DEFAULT, not per-session ----
    //
    // The real fleet bug: a session launched WITHOUT a per-session --model (so ClaudeArgs/userArgs is
    // null) still runs opus[1m] because the model is in AgentOptions.DefaultClaudeArgs. The gauge must
    // read the EFFECTIVE launch line (what SessionManager stores as Session.EffectiveLaunchArgs), which
    // is the result of BuildLaunchSpec merging the default in - NOT the null per-session args. These
    // tests prove that path end to end so a per-session-only fix can't masquerade as correct.

    [Fact]
    public void BuildLaunchSpec_ModelFromDefaultArgs_NoPerSessionArgs_EffectiveArgsCarryTheModel()
    {
        var agent = new ClaudeAgent(new AgentOptions { DefaultClaudeArgs = "--dangerously-skip-permissions --model opus[1m]" });

        // userArgs null: the production default-launch path (no per-session override).
        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false);

        Assert.Contains("--model opus[1m]", spec.Arguments);
    }

    [Fact]
    public void ReadContextUsage_EffectiveArgsFromDefault_SizesOpusOneMillion()
    {
        // Simulate the stored Session.EffectiveLaunchArgs for a default-launched opus[1m] session.
        var agent = new ClaudeAgent(new AgentOptions { DefaultClaudeArgs = "--dangerously-skip-permissions --model opus[1m]" });
        var effectiveArgs = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false).Arguments;

        var usage = new SessionUsageDto
        {
            ContextTokens = 121_924,
            ContextModel = "claude-opus-4-8", // transcript, [1m]-stripped
            AssistantMessageCount = 5,
        };

        var ctx = DriverReturning(usage).ReadContextUsage("sid", "C:\\repo", effectiveArgs);

        Assert.NotNull(ctx);
        Assert.Equal(1_000_000, ctx.WindowTokens);
        Assert.InRange(ctx.PercentUsed!.Value, 11.0, 13.0); // ~12.2%, the bug's correct reading
    }

    // ---- The NotSupported guarantee on drivers without the flag ----

    [Fact]
    public void ReadContextUsage_DriverWithoutFlag_Throws()
    {
        Assert.False(new CodexDriver().Capabilities.HasFlag(DriverCapabilities.ContextUsage));
        Assert.False(new PiDriver().Capabilities.HasFlag(DriverCapabilities.ContextUsage));

        Assert.Throws<NotSupportedException>(
            () => ((IAgentDriver)new CodexDriver()).ReadContextUsage("sid", "C:\\repo", null));
        Assert.Throws<NotSupportedException>(
            () => ((IAgentDriver)new PiDriver()).ReadContextUsage("sid", "C:\\repo", null));
    }

    [Fact]
    public void ClaudeDriver_DeclaresContextUsage()
    {
        Assert.True(new ClaudeDriver(new StubTranscriptReader(null))
            .Capabilities.HasFlag(DriverCapabilities.ContextUsage));
    }

    /// <summary>A transcript reader that returns a fixed usage object - keeps the driver tests off
    /// disk and the user profile.</summary>
    private sealed class StubTranscriptReader : ITranscriptReader
    {
        private readonly SessionUsageDto? _usage;
        public StubTranscriptReader(SessionUsageDto? usage) => _usage = usage;
        public List<TurnWidgetDto> ReadWidgets(string claudeSessionId, string repoPath) => new();
        public SessionUsageDto? ReadUsage(string claudeSessionId, string repoPath) => _usage;
        public List<(string ClaudeSessionId, DateTime LastWriteUtc)> ListTranscripts(string repoPath) => new();
    }
}
