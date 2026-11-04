namespace CcDirector.Setup.Engine;

/// <summary>
/// Reads a macOS path (forward slashes, rooted at <c>/</c>) the same way on every machine.
///
/// The Mac decisions in this engine are pure functions of Mac paths, and their tests run on Windows, Linux and macOS.
/// <see cref="Path"/> follows the machine the code runs on: on Windows <c>Path.GetDirectoryName("/Applications/Director.app")</c>
/// answers <c>\Applications</c>, and <c>new Uri("/Applications/Director.app")</c> throws, because a path that starts
/// with a slash is not a file path there. So a decision that compared or printed a Mac path through <see cref="Path"/>
/// gave a different answer on Windows than on the Mac it is for. These read the path by its own rules instead.
/// </summary>
internal static class MacPath
{
    /// <summary>The folder holding <paramref name="path"/>: <c>/Applications</c> for <c>/Applications/Director.app</c>,
    /// <c>/</c> for <c>/Director.app</c>, and an empty string for a name with no folder - as <c>Path.GetDirectoryName</c>
    /// answers on a Mac.</summary>
    public static string DirectoryOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var slash = path.LastIndexOf('/');
        if (slash < 0) return string.Empty;
        return slash == 0 ? "/" : path[..slash];
    }

    /// <summary>The last part of <paramref name="path"/>: <c>Director.app</c> for <c>/Applications/Director.app</c>.</summary>
    public static string FileNameOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path[(path.LastIndexOf('/') + 1)..];
    }

    /// <summary>The <c>file://</c> address of <paramref name="path"/>, escaped as the Dock stores it:
    /// <c>file:///Users/some%20one/Applications/Director.app/</c>.</summary>
    public static string FileUrlOf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/'))
            throw new ArgumentException($"A Mac path starts at the root (/); '{path}' does not.", nameof(path));
        return new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = path }.Uri.AbsoluteUri;
    }
}
