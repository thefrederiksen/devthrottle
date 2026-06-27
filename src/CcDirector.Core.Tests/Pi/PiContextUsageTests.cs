using CcDirector.Core.Pi;
using Xunit;

namespace CcDirector.Core.Tests.Pi;

// =====================================================================================
// PiContextUsage: the gauge reads the LAST assistant message's usage.input from a pi
// session file; pi does not record the window, so it is mapped from the model id
// (PiContextWindow). PiContextWindow itself maps gpt-5.5 and reuses the Claude table.
// =====================================================================================
public sealed class PiContextUsageTests
{
    private const string Assistant1 =
        "{\"type\":\"message\",\"timestamp\":\"2026-06-27T03:00:00.000Z\",\"message\":{\"role\":\"assistant\",\"provider\":\"openai-codex\",\"model\":\"gpt-5.5\",\"usage\":{\"input\":2000,\"output\":33,\"totalTokens\":2033}}}";

    private const string Assistant2 =
        "{\"type\":\"message\",\"timestamp\":\"2026-06-27T03:05:00.000Z\",\"message\":{\"role\":\"assistant\",\"provider\":\"openai-codex\",\"model\":\"gpt-5.5\",\"usage\":{\"input\":3876,\"output\":40,\"totalTokens\":3916}}}";

    [Fact]
    public void Compute_TakesLastAssistantUsage_MapsWindowFromModel()
    {
        var ctx = PiContextUsage.Compute(new[] { Assistant1, Assistant2 });

        Assert.NotNull(ctx);
        Assert.Equal(3876, ctx.UsedTokens);                 // the LATEST assistant message wins
        Assert.Equal(272_000, ctx.WindowTokens);            // gpt-5.5 -> 272000 (pi's observed limit)
        Assert.Equal(1.4, ctx.PercentUsed);                 // 3876 / 272000 * 100, rounded
        Assert.Equal(new DateTime(2026, 6, 27, 3, 5, 0, DateTimeKind.Utc), ctx.AsOfUtc);
    }

    [Fact]
    public void Compute_UnmappedModel_RawNumberFallback_NoPercent()
    {
        var line =
            "{\"type\":\"message\",\"message\":{\"role\":\"assistant\",\"model\":\"some-local-model\",\"usage\":{\"input\":500}}}";
        var ctx = PiContextUsage.Compute(new[] { line });

        Assert.NotNull(ctx);
        Assert.Equal(500, ctx.UsedTokens);
        Assert.Null(ctx.WindowTokens);
        Assert.Null(ctx.PercentUsed);
    }

    [Fact]
    public void Compute_IgnoresUserMessagesAndThinking_ReturnsNullWhenNoAssistantUsage()
    {
        var lines = new[]
        {
            "{\"type\":\"session\",\"cwd\":\"C:\\\\repo\"}",
            "{\"type\":\"message\",\"message\":{\"role\":\"user\",\"content\":[]}}",
            "{\"type\":\"thinking\"}",
        };
        Assert.Null(PiContextUsage.Compute(lines));
    }

    [Fact]
    public void PiContextWindow_MapsGpt55_AndReusesClaudeTable()
    {
        Assert.Equal(272_000, PiContextWindow.WindowTokensForModel("gpt-5.5"));
        Assert.Equal(200_000, PiContextWindow.WindowTokensForModel("claude-sonnet-4-5"));
        Assert.Equal(1_000_000, PiContextWindow.WindowTokensForModel("claude-opus-4-8[1m]"));
        Assert.Null(PiContextWindow.WindowTokensForModel("mystery-model"));
        Assert.Null(PiContextWindow.WindowTokensForModel(null));
    }
}
