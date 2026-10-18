namespace CcDirector.Core.Configuration;

/// <summary>
/// What one call to an included model costs, per million tokens, in US dollars - kept beside
/// <see cref="IncludedModelId"/> so a feature that must hold its own spend to a limit (the Mail Desk, whose
/// mission stops calling the model at a spend limit) reads the price from one named place instead of
/// carrying its own copy of the numbers.
///
/// One constant pair per model, named for the model. A price that changes is changed here, once.
/// </summary>
public static class IncludedModelPrice
{
    /// <summary>The input (prompt) price of <see cref="IncludedModelId.WingmanFast"/>: $0.36 per million tokens
    /// (Mail Desk phase 3 contract, section 5; phase 0 measured the model at this price).</summary>
    public const decimal WingmanFastInputUsdPerMillionTokens = 0.36m;

    /// <summary>The output (completion) price of <see cref="IncludedModelId.WingmanFast"/>: $0.40 per million tokens.</summary>
    public const decimal WingmanFastOutputUsdPerMillionTokens = 0.40m;

    /// <summary>The cost of one <see cref="IncludedModelId.WingmanFast"/> call from its token counts.</summary>
    public static decimal WingmanFastCostUsd(int inputTokens, int outputTokens)
    {
        if (inputTokens < 0) throw new ArgumentOutOfRangeException(nameof(inputTokens), inputTokens, "A token count cannot be negative.");
        if (outputTokens < 0) throw new ArgumentOutOfRangeException(nameof(outputTokens), outputTokens, "A token count cannot be negative.");
        return (inputTokens * WingmanFastInputUsdPerMillionTokens + outputTokens * WingmanFastOutputUsdPerMillionTokens) / 1_000_000m;
    }
}
