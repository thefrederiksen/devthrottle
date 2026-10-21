using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>The raw token counts of one report, or of the change between two.</summary>
public readonly record struct RawTokens(long Input, long Output, long CacheRead, long CacheCreation)
{
    public static RawTokens Of(TokenTotalsDto totals) =>
        new(totals.InputTokens, totals.OutputTokens, totals.CacheReadTokens, totals.CacheCreationTokens);

    public bool IsZero => Input == 0 && Output == 0 && CacheRead == 0 && CacheCreation == 0;
}

/// <summary>
/// THE ONE UNIT OF USE (the usage-by-session mandate). Tokens of different kinds cost an account very different amounts,
/// so they are WEIGHTED before they are added: an uncached input token 1, a cache write 1.25, a cache read 0.1 and an
/// output token 5 - the ratios of the provider's own prices. A weighted token is then turned into a share of the weekly
/// limit by each account's MEASURED calibration (<see cref="SessionUsageFold"/>), so the size of a model is measured,
/// never assumed here.
///
/// A REPORT IS A RUNNING SUM INSIDE ONE AGENT CONVERSATION. The change between two reports is their difference; a report
/// that is SMALLER in any count, or from another conversation, means the agent started counting again from zero (a
/// compaction into a new conversation, or a restart), so the whole of it is new use. That is what lets one session keep
/// one running total through every compaction.
/// </summary>
public static class SessionUsageMath
{
    public const double InputWeight = 1.0;
    public const double CacheCreationWeight = 1.25;
    public const double CacheReadWeight = 0.1;
    public const double OutputWeight = 5.0;

    /// <summary>The weighted tokens of <paramref name="tokens"/>.</summary>
    public static double Weighted(RawTokens tokens) =>
        tokens.Input * InputWeight
        + tokens.CacheCreation * CacheCreationWeight
        + tokens.CacheRead * CacheReadWeight
        + tokens.Output * OutputWeight;

    /// <summary>
    /// The use between the report <paramref name="previous"/> (null when there was none) and <paramref name="next"/>.
    /// <paramref name="sameConversation"/> is false when the two were counted from different agent conversations.
    /// An identical report is no use, whichever conversation it names: after a clear the Director names the new
    /// conversation at once but re-reads the counts only at its first turn end, so for a while it sends the OLD
    /// conversation's counts under the NEW name - counting those again would double the session's whole life.
    /// </summary>
    public static RawTokens Change(RawTokens? previous, RawTokens next, bool sameConversation)
    {
        if (previous is not { } before)
            return next;
        if (next == before)
            return default;
        var restarted = !sameConversation
            || next.Input < before.Input
            || next.Output < before.Output
            || next.CacheRead < before.CacheRead
            || next.CacheCreation < before.CacheCreation;
        if (restarted)
            return next;
        return new RawTokens(next.Input - before.Input, next.Output - before.Output,
            next.CacheRead - before.CacheRead, next.CacheCreation - before.CacheCreation);
    }
}
