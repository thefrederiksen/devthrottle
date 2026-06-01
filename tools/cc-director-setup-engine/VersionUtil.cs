namespace CcDirector.Setup.Engine;

/// <summary>
/// Version parsing and comparison shared across the engine. Mirrors the
/// normalization the Director's UpdateService already uses: collapse to
/// (Major, Minor, Build) so 4-part assembly versions and 3-part tags compare
/// cleanly, and tolerate a leading 'v' plus a "-prerelease" suffix.
/// </summary>
public static class VersionUtil
{
    /// <summary>Parse "v0.3.3", "0.3.3", "0.3.3-rc1", or "1.2.0.4" into a normalized Version, or null.</summary>
    public static Version? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var dash = t.IndexOf('-');
        if (dash >= 0) t = t[..dash];
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        return Version.TryParse(t, out var v) ? Normalize(v) : null;
    }

    /// <summary>Collapse to (Major, Minor, Build); a negative Build becomes 0.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// True when <paramref name="candidate"/> is strictly newer than
    /// <paramref name="installed"/>. Either side being unparseable returns false
    /// (we never "update" on the basis of a version we cannot read).
    /// </summary>
    public static bool IsNewer(string? candidate, string? installed)
    {
        var c = TryParse(candidate);
        var i = TryParse(installed);
        if (c is null || i is null) return false;
        return c > i;
    }
}
