namespace CcDirector.Core.Utilities;

/// <summary>
/// READING A REPOSITORY PATH THAT CAME FROM SOMEWHERE ELSE.
///
/// A repository path in this product is rarely about the machine reading it. A Director on macOS opens a
/// workspace saved on a Windows desktop; the Gateway runs in a Linux container and is handed paths pushed
/// up by every Director in the fleet; the Cockpit and the phone show what the Gateway sends them. So the
/// question "what is this repository's folder called" must be answered from the PATH, never from the
/// operating system that happens to be asking.
///
/// <c>Path.GetFileName</c> cannot answer it, because it honours only the separator of the host it runs on.
/// Handed <c>D:\ReposFred\devthrottle_internal</c> on macOS or Linux it finds no separator at all and
/// returns the whole path, so a seat that should read "devthrottle_internal" reads
/// "D:\ReposFred\devthrottle_internal" instead. That is not a display blemish: the same string is what a
/// person scans a list of repositories by.
/// </summary>
public static class RepositoryPaths
{
    private static readonly char[] EitherSeparator = { '/', '\\' };

    /// <summary>
    /// The last segment of a repository path - its folder name - whichever machine wrote the path and
    /// whichever machine is reading it. Both separators are understood, and a trailing one is ignored so
    /// <c>D:\repos\x</c> and <c>D:\repos\x\</c> answer alike.
    ///
    /// A path that is nothing but separators, or empty, answers an empty string: there is no folder name in
    /// it to find, and the caller decides what that means rather than being handed a guess.
    /// </summary>
    public static string FolderName(string? repositoryPath)
    {
        var trimmed = (repositoryPath ?? "").Trim().TrimEnd(EitherSeparator);
        if (trimmed.Length == 0) return "";

        var lastSeparator = trimmed.LastIndexOfAny(EitherSeparator);
        return lastSeparator < 0 ? trimmed : trimmed[(lastSeparator + 1)..];
    }
}
