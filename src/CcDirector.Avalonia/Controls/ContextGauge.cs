using System;
using System.Globalization;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The colour band the context gauge bar shows as the window fills - pre-attentive escalation so
/// growth is noticeable before the number is read.
/// </summary>
public enum ContextUsageBand
{
    /// <summary>Below 70% used, or no percent known (the raw-number fallback).</summary>
    Neutral,

    /// <summary>From 70% up to and including 90% used.</summary>
    Amber,

    /// <summary>Above 90% used.</summary>
    Red,
}

/// <summary>
/// Pure presentation logic for the SessionActionBar context gauge (issue #799): the colour-band
/// selection and the compact label. Kept free of Avalonia types so it is unit-testable without a
/// window; the control maps the band to a brush and the label to a TextBlock.
/// </summary>
public static class ContextGauge
{
    /// <summary>The percent at or above which the bar turns amber (settled threshold).</summary>
    public const double AmberThresholdPercent = 70.0;

    /// <summary>The percent above which the bar turns red (settled threshold).</summary>
    public const double RedThresholdPercent = 90.0;

    /// <summary>
    /// The colour band for a used-percent: neutral below 70%, amber from 70% through 90%, red above
    /// 90%. A null percent (window size unknown - the raw-number fallback) stays neutral.
    /// </summary>
    public static ContextUsageBand SelectBand(double? percentUsed)
    {
        if (percentUsed is not { } pct)
            return ContextUsageBand.Neutral;
        if (pct > RedThresholdPercent)
            return ContextUsageBand.Red;
        if (pct >= AmberThresholdPercent)
            return ContextUsageBand.Amber;
        return ContextUsageBand.Neutral;
    }

    /// <summary>
    /// The compact gauge label: <c>ctx 42k / 200k (21%)</c> when the window is known, or
    /// <c>ctx 42k</c> (the raw-number fallback) when it is not.
    /// </summary>
    public static string FormatLabel(ContextUsageDto usage)
    {
        if (usage is null)
            throw new ArgumentNullException(nameof(usage));

        var used = FormatTokens(usage.UsedTokens);
        if (usage.WindowTokens is { } window && usage.PercentUsed is { } percent)
        {
            var pct = percent.ToString("0", CultureInfo.InvariantCulture);
            return $"ctx {used} / {FormatTokens(window)} ({pct}%)";
        }
        return $"ctx {used}";
    }

    /// <summary>Human-compact token count: <c>200000 -> 200k</c>, <c>1000000 -> 1M</c>, small counts
    /// verbatim.</summary>
    public static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000)
            return (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (tokens >= 1_000)
            return (tokens / 1_000).ToString(CultureInfo.InvariantCulture) + "k";
        return tokens.ToString(CultureInfo.InvariantCulture);
    }
}
