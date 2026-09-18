namespace CcDirector.Core.Tests;

/// <summary>
/// A temporary directory whose path is the one every other tool will report back.
///
/// WHY A HELPER AND NOT Path.GetTempPath. On macOS <c>Path.GetTempPath()</c> answers
/// <c>/var/folders/...</c>, but <c>/var</c> is a symbolic link to <c>/private/var</c>. Anything that
/// resolves the path for real - git, the shell, and several of our own probes - reports the
/// <c>/private/var/...</c> form. A test that builds its fixture from the raw temp path and then
/// compares it against what git or the product returns is therefore comparing two spellings of the
/// same directory and failing on the spelling. That single difference accounted for a large share of
/// this suite's macOS failures, across the worktree reapers, the repository monitor and the
/// mutation-proof guard.
///
/// <c>ResolveLinkTarget</c> alone is not enough, because the link is an ANCESTOR (<c>/var</c>), not the
/// leaf - calling it on the temp directory itself answers "not a link". So this walks the path from the
/// root and resolves each component that turns out to be a link.
///
/// On Windows nothing here is a link and the answer is the path unchanged, so tests read the same on
/// both platforms.
/// </summary>
internal static class TestTempRoot
{
    /// <summary>
    /// <paramref name="prefix"/> plus a fresh identifier, under the canonical temporary directory.
    /// Does NOT create the directory - callers that need it create it, exactly as they did before.
    /// </summary>
    public static string For(string prefix) =>
        Path.Combine(Canonical(Path.GetTempPath()), prefix + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// <paramref name="path"/> with every symbolic link in it resolved, so it is the spelling other
    /// tools report. Never throws: a component that cannot be inspected is kept as written, because a
    /// test fixture is better off with the original path than with an exception.
    /// </summary>
    public static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) return Path.TrimEndingDirectorySeparator(full);

        var current = Path.DirectorySeparatorChar.ToString();
        foreach (var part in full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = target.FullName;
            }
            catch
            {
                // Keep the component as written - see the summary.
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }
}
