using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// THE VERIFIED PRIMITIVES - the entire set of checks a rule is allowed to run (owner ruling 15,
/// Architect ruling A3). These are ordinary reviewed static functions in the product, shipped like any
/// other feature and tested like any other feature. A rule never holds a program, an expression, a lambda
/// or a snippet: it holds the NAME of one of these plus argument values, validated against the signature
/// before it is ever stored. There is no interpreter, so there is no sandbox to get right.
///
/// Every argument is a VALUE. None of these takes a pattern, an expression or a format string - the
/// patterns below are ours, fixed in reviewed source, and no caller can reach them. A primitive whose
/// argument were effectively a program would be the interpreter coming back under another name.
///
/// Widening this set is a PRODUCT CHANGE - a new reviewed method, shipped in a release.
/// </summary>
public static class RulePrimitives
{
    // ---- is_path_inside ---------------------------------------------------------------------------

    /// <summary>
    /// Is <paramref name="target"/> the same place as <paramref name="root"/>, or somewhere underneath it?
    ///
    /// Answered on the RESOLVED paths, not the written ones: <c>..</c> and <c>.</c> are collapsed, and every
    /// segment that is a link is followed to what it really points at, so a link inside the repository that
    /// leads out of it answers false. The comparison is made segment-wise, so a sibling whose name merely
    /// starts with the root's name (<c>repo-other</c> beside <c>repo</c>) is NOT inside it - the prefix
    /// collision a naive string test gets wrong.
    ///
    /// A missing target or root answers false: nothing is inside nowhere. A link that cannot be resolved
    /// (a cycle, or too many levels) throws rather than quietly answering false - a check that cannot see
    /// where a path leads must say so, not guess.
    /// </summary>
    [RulePrimitive("Checks a path is inside a directory, resolving '..' and links first.")]
    public static bool IsPathInside(string target, string root)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(root)) return false;

        var resolvedTarget = ResolveFinalPath(target);
        var resolvedRoot = ResolveFinalPath(root);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(resolvedTarget, resolvedRoot, comparison)) return true;

        var rootWithSeparator = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;

        return resolvedTarget.StartsWith(rootWithSeparator, comparison);
    }

    /// <summary>How many times a link may lead to another link's ancestor before this gives up. A path that
    /// needs more than this is a cycle, and the documented contract is to say so rather than to guess.</summary>
    private const int MaximumLinkDepth = 40;

    /// <summary>
    /// The real, absolute location a path names: <c>..</c> and <c>.</c> collapsed, and every segment that
    /// exists and is a link followed to its final target. Walking segment by segment matters - resolving
    /// only the last one would miss a link ANCESTOR, which is exactly how a path inside the repository
    /// ends up outside it.
    ///
    /// A LINK'S TARGET HAS ANCESTORS OF ITS OWN, AND THEY GET RESOLVED TOO. This is not tidiness; without
    /// it the answer is wrong on macOS and on Linux for ordinary paths. On those systems the operating
    /// system call behind <c>ResolveLinkTarget</c> follows the chain of links but leaves the target's own
    /// directories exactly as they were written, so a link pointing at <c>/var/folders/x/repo/real</c>
    /// answers with that string even though <c>/var</c> is itself a link to <c>/private/var</c>. The root
    /// beside it, walked segment by segment, resolves to <c>/private/var/folders/x/repo</c> - and then one
    /// resolved path and one half-resolved path are compared, and a file plainly inside the repository is
    /// reported as outside it. Windows does not show this because its own call canonicalises the whole
    /// path; resolving the target here makes every system give the one answer.
    /// </summary>
    private static string ResolveFinalPath(string path) => ResolveFinalPath(path, 0);

    /// <param name="depth">How many link targets deep this already is. Guards the cycle that
    /// <c>a -> b/c</c> with <c>b -> a</c> would otherwise spin in.</param>
    private static string ResolveFinalPath(string path, int depth)
    {
        if (depth > MaximumLinkDepth)
            throw new IOException(
                $"'{path}' could not be resolved: it passes through more than {MaximumLinkDepth} links, " +
                "which means they lead in a circle. A containment check that cannot see where a path leads " +
                "must say so rather than answer.");

        var full = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(full) ?? "";
        var segments = full[pathRoot.Length..]
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

        var current = pathRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var linkTarget = ResolveLinkTarget(current);
            if (linkTarget is not null)
                current = ResolveFinalPath(linkTarget, depth + 1);
        }

        return TrimTrailingSeparator(current);
    }

    /// <summary>What a path finally points at when it is a link, or null when it exists and is not a link,
    /// or does not exist at all (an unwritten file is still located where it was named).</summary>
    private static string? ResolveLinkTarget(string path)
    {
        if (Directory.Exists(path)) return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        if (File.Exists(path)) return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        return null;
    }

    /// <summary>Drop a trailing separator, except on a path root ("D:\" and "/" are their own places).</summary>
    private static string TrimTrailingSeparator(string path)
    {
        var pathRoot = Path.GetPathRoot(path) ?? "";
        if (path.Length <= pathRoot.Length) return path;
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // ---- retry_delay_from -------------------------------------------------------------------------

    /// <summary>The relative form: "try again in 30 seconds", "retry after 5 minutes".</summary>
    private static readonly Regex RelativeWait = new(
        @"\b(?:in|after)\s+(\d+(?:\.\d+)?)\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The absolute form: "your limit will reset at 14:30".</summary>
    private static readonly Regex ClockWait = new(
        @"\bat\s+([01]?\d|2[0-3]):([0-5]\d)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// How many seconds the screen says to wait before trying again, or nothing when it does not say.
    ///
    /// Two forms are read: a relative wait ("try again in 5 minutes") and a clock time ("resets at 09:00"),
    /// which is measured against <paramref name="now"/> and rolls to tomorrow when it has already gone.
    /// A screen that says nothing about waiting answers nothing - never a guessed default, which a caller
    /// could not tell from a real reading.
    /// </summary>
    [RulePrimitive("Reads how long the screen says to wait before trying again.")]
    public static double? RetryDelayFrom(string screenText, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(screenText)) return null;

        var relative = RelativeWait.Match(screenText);
        if (relative.Success)
        {
            var amount = double.Parse(relative.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var unit = relative.Groups[2].Value.ToLowerInvariant();
            var seconds = unit[0] switch
            {
                's' => 1.0,
                'm' => 60.0,
                'h' => 3600.0,
                _ => throw new InvalidOperationException($"unreachable: unit '{unit}' is not one this pattern matches"),
            };
            return amount * seconds;
        }

        var clock = ClockWait.Match(screenText);
        if (clock.Success)
        {
            var nowUtc = now.ToUniversalTime();
            var hour = int.Parse(clock.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var minute = int.Parse(clock.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var when = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, hour, minute, 0, DateTimeKind.Utc);
            if (when <= nowUtc) when = when.AddDays(1);
            return (when - nowUtc).TotalSeconds;
        }

        return null;
    }

    // ---- elapsed_since ----------------------------------------------------------------------------

    /// <summary>
    /// How many seconds have passed since something first went wrong. Both moments are compared in UTC,
    /// whatever kind they arrive as. A <paramref name="firstFailure"/> in the future answers a NEGATIVE
    /// number rather than zero - clamping would hide a clock disagreement that the caller should see.
    /// </summary>
    [RulePrimitive("Measures how long it has been since something first went wrong.")]
    public static double ElapsedSince(DateTime firstFailure, DateTime now) =>
        (now.ToUniversalTime() - firstFailure.ToUniversalTime()).TotalSeconds;

    // ---- matches_any ------------------------------------------------------------------------------

    /// <summary>
    /// Does the text contain any of these terms, ignoring case? Terms are LITERAL text and are compared as
    /// written - ".*" is two characters, not "anything". An empty text or an empty list answers false.
    /// </summary>
    [RulePrimitive("Checks whether the text contains any of a list of words.")]
    public static bool MatchesAny(string text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text) || terms is null || terms.Count == 0) return false;

        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(term)) continue;
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---- extract_first ----------------------------------------------------------------------------

    /// <summary>A Windows drive path or a POSIX path, whichever appears first.</summary>
    private static readonly Regex FirstPath = new(
        @"[A-Za-z]:[\\/][^\s""'<>|?*]*|/(?:[^\s/""'<>|?*]+/)*[^\s/""'<>|?*]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A span of time written out, such as "5 minutes".</summary>
    private static readonly Regex FirstDuration = new(
        @"\b\d+(?:\.\d+)?\s*(?:seconds?|secs?|minutes?|mins?|hours?|hrs?|days?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A clock time, such as "09:44" or "09:44:12".</summary>
    private static readonly Regex FirstTimestamp = new(
        @"\b(?:[01]?\d|2[0-3]):[0-5]\d(?::[0-5]\d)?\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The first path, duration or clock time on the screen, or an empty string when there is none of that
    /// kind. The <paramref name="kind"/> is one of a closed set, never a pattern the caller supplies.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not a member of the closed set.</exception>
    [RulePrimitive("Pulls the first path, duration or clock time out of the screen.")]
    public static string ExtractFirst(string screenText, RuleExtractKind kind)
    {
        if (string.IsNullOrEmpty(screenText)) return "";

        var pattern = kind switch
        {
            RuleExtractKind.Path => FirstPath,
            RuleExtractKind.Duration => FirstDuration,
            RuleExtractKind.Timestamp => FirstTimestamp,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a member of the closed extract set"),
        };

        var match = pattern.Match(screenText);
        return match.Success ? match.Value.Trim() : "";
    }
}
