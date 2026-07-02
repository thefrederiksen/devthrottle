namespace CcDirector.Core.Sessions;

/// <summary>
/// The single home for the rule that turns a repository path, an optional explicit name,
/// and an optional free-text purpose into a session display name (issue #800).
///
/// The problem this solves: on a fleet where many sessions run in the SAME checkout, a session
/// born with no name falls back to the bare repository folder name (e.g. "devthrottle"), so
/// every session in that checkout displays identically and they cannot be told apart. This
/// composer guarantees a created session is named AT BIRTH with something that ALWAYS contains
/// more than the bare folder name, and steers callers to describe what the session is FOR.
///
/// The rule (the "middle path" agreed in the review): an EXPLICIT name that is blank or equal
/// (case-insensitive) to the bare folder name is REJECTED by the caller via
/// <see cref="IsWeakExplicitName"/>; when no explicit name is given, <see cref="Compose"/>
/// AUTO-COMPOSES a meaningful name from folder + purpose (if any) + session type + a
/// disambiguator, so the bare folder name can never be produced.
/// </summary>
public static class SessionName
{
    /// <summary>How many characters of a free-text purpose are kept when building a name.</summary>
    public const int MaxPurposeLength = 60;

    /// <summary>How many hex characters of a session id form its disambiguator.</summary>
    public const int DisambiguatorLength = 4;

    /// <summary>
    /// The bare repository folder name for a path (trailing separators trimmed). This is the
    /// value the old fallbacks used as the WHOLE name and the value an explicit name may not
    /// equal. Empty string for a null/empty path - the caller validates the path separately.
    /// </summary>
    public static string FolderName(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            return string.Empty;
        return System.IO.Path.GetFileName(repoPath.TrimEnd('\\', '/'));
    }

    /// <summary>
    /// The short, stable disambiguator derived from a session id: its first
    /// <see cref="DisambiguatorLength"/> hex characters. Derived from the id so no new
    /// persisted counter state is introduced (issue #800 assumption).
    /// </summary>
    public static string Disambiguator(Guid sessionId)
    {
        var hex = sessionId.ToString("N");
        return hex[..DisambiguatorLength];
    }

    /// <summary>
    /// True when an EXPLICIT name is too weak to identify a session: blank/whitespace, or
    /// (case-insensitive, trimmed) equal to the bare repository folder name. The Control API
    /// rejects these with HTTP 400 so a caller that bothered to pass a name passes a useful one.
    /// Only call this when the caller actually supplied a name; an absent name is auto-composed.
    /// </summary>
    public static bool IsWeakExplicitName(string? explicitName, string repoFolderName)
    {
        if (string.IsNullOrWhiteSpace(explicitName))
            return true;
        return string.Equals(explicitName.Trim(), repoFolderName.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compose the final session display name. When <paramref name="explicitName"/> is non-blank
    /// it passes through verbatim (trimmed) - the caller is responsible for having rejected a
    /// weak explicit name via <see cref="IsWeakExplicitName"/> first. Otherwise the name is
    /// auto-composed so it ALWAYS contains more than the bare folder name:
    /// <list type="bullet">
    ///   <item>folder + purpose, when a purpose is given (e.g. "devthrottle: implement #799"); or</item>
    ///   <item>folder + disambiguator (e.g. "devthrottle / 1fb5").</item>
    /// </list>
    /// </summary>
    public static string Compose(string repoFolderName,
        string? explicitName, string? purpose, string disambiguator)
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
            return explicitName.Trim();

        if (!string.IsNullOrWhiteSpace(purpose))
            return $"{repoFolderName}: {CapPurpose(purpose)}";

        return $"{repoFolderName} / {disambiguator}";
    }

    /// <summary>
    /// The display name for an ALREADY-CREATED session: its custom name when it has one, else
    /// the same auto-composed folder + disambiguator (never the bare folder name). This is the
    /// single replacement for the former bare-folder display fallbacks.
    /// </summary>
    public static string DisplayName(string? customName, string repoFolderName,
        string disambiguator)
    {
        if (!string.IsNullOrWhiteSpace(customName))
            return customName.Trim();
        return Compose(repoFolderName, null, null, disambiguator);
    }

    /// <summary>Trim a free-text purpose and cap it to <see cref="MaxPurposeLength"/> characters.</summary>
    private static string CapPurpose(string purpose)
    {
        var trimmed = purpose.Trim();
        return trimmed.Length <= MaxPurposeLength ? trimmed : trimmed[..MaxPurposeLength].TrimEnd();
    }
}
