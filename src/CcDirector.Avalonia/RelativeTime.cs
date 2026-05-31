using System;

namespace CcDirector.Avalonia;

/// <summary>
/// Single source of truth for human-readable "X ago" labels. Previously this logic was
/// hand-copied (with subtly different tiers) into MainWindow.FormatAge, NewSessionDialog.TimeAgo
/// and RelinkSessionDialog.TimeAgo. Centralized here so the tiers and wording stay consistent
/// and a change (a new tier, a boundary fix) happens in one place.
/// </summary>
public static class RelativeTime
{
    /// <summary>
    /// Formats an elapsed duration as "just now" / "Xs ago" / "Xm ago" / "Xh ago" /
    /// "Xd ago" / "Xmo ago" / "Xy ago". Negative spans (clock skew) read as "just now".
    /// </summary>
    public static string Ago(TimeSpan d)
    {
        if (d.TotalSeconds < 1) return "just now";
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds}s ago";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d ago";
        if (d.TotalDays < 365) return $"{(int)(d.TotalDays / 30)}mo ago";
        return $"{(int)(d.TotalDays / 365)}y ago";
    }
}
