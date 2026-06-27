using CcDirector.Core.Codex;
using Xunit;

namespace CcDirector.Core.Tests.Codex;

// =====================================================================================
// CodexContextUsage: the gauge reads the LAST token_count event from a Codex rollout -
// used = last_token_usage.input_tokens, window = model_context_window (Codex gives us the
// window, so nothing is guessed).
// =====================================================================================
public sealed class CodexContextUsageTests
{
    private const string TokenCount1 =
        "{\"timestamp\":\"2026-06-27T08:00:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":50000,\"output_tokens\":10},\"model_context_window\":258400}}}";

    private const string TokenCount2 =
        "{\"timestamp\":\"2026-06-27T08:05:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":159314,\"output_tokens\":31},\"model_context_window\":258400}}}";

    [Fact]
    public void Compute_TakesLastTokenCount_ComputesPercentAndWindow()
    {
        var ctx = CodexContextUsage.Compute(new[] { TokenCount1, TokenCount2 });

        Assert.NotNull(ctx);
        Assert.Equal(159314, ctx.UsedTokens);            // the LATEST event wins
        Assert.Equal(258400, ctx.WindowTokens);
        Assert.Equal(61.7, ctx.PercentUsed);             // 159314 / 258400 * 100
        Assert.Equal(new DateTime(2026, 6, 27, 8, 5, 0, DateTimeKind.Utc), ctx.AsOfUtc);
    }

    [Fact]
    public void Compute_NoTokenCountEvent_ReturnsNull()
    {
        var lines = new[]
        {
            "{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"C:\\\\repo\"}}",
            "{\"type\":\"response_item\",\"payload\":{}}",
        };
        Assert.Null(CodexContextUsage.Compute(lines));
    }

    [Fact]
    public void Compute_WindowMissing_RawNumberFallback_NoPercent()
    {
        var line =
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":1234}}}}";
        var ctx = CodexContextUsage.Compute(new[] { line });

        Assert.NotNull(ctx);
        Assert.Equal(1234, ctx.UsedTokens);
        Assert.Null(ctx.WindowTokens);
        Assert.Null(ctx.PercentUsed);
    }

    [Fact]
    public void Compute_SkipsTornTailLine()
    {
        var ctx = CodexContextUsage.Compute(new[] { TokenCount1, "{ this is half-written" });
        Assert.NotNull(ctx);
        Assert.Equal(50000, ctx.UsedTokens);
    }
}
