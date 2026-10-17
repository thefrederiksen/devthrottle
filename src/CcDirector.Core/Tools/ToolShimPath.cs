using CcDirector.Core.Utilities;

namespace CcDirector.Core.Tools;

/// <summary>
/// The directory that puts the cc-* tools on PATH on macOS and Linux - <c>~/.local/bin</c>, where the
/// installer links each tool to its console script in <c>pyenv/bin</c> - and the one rule for making sure
/// the Director's own PATH holds it (issue devthrottle_internal#2474).
///
/// WHY THE DIRECTOR FIXES ITS OWN PATH. A Director started over SSH, or by anything else that does not read
/// a login profile, inherits a PATH without <c>~/.local/bin</c>: non-interactive bash returns from
/// <c>~/.bashrc</c> before any PATH setup and never reads <c>~/.profile</c>. Every session the Director
/// starts inherits that PATH, so no session could run <c>cc-devthrottle</c>, and a Smart Restart carried the
/// same PATH into the new build. Prepending the directory at start-up mends this process, every session it
/// starts afterwards, and every build it relaunches, whatever started it.
/// </summary>
public static class ToolShimPath
{
    /// <summary><c>~/.local/bin</c>: where the macOS and Linux installers link the cc-* tools.</summary>
    public static string UnixShimDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

    /// <summary>
    /// <paramref name="searchPath"/> with <paramref name="dir"/> in front of it, or unchanged when it already
    /// lists the directory. Pure, so the rule is testable without touching this process's PATH.
    /// </summary>
    public static string WithDirectory(string? searchPath, string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        if (Contains(searchPath, dir)) return searchPath!;
        return string.IsNullOrEmpty(searchPath) ? dir : dir + Path.PathSeparator + searchPath;
    }

    /// <summary>True when <paramref name="searchPath"/> lists <paramref name="dir"/>, comparing the way the
    /// platform does (case-insensitively on Windows) and ignoring a trailing separator.</summary>
    public static bool Contains(string? searchPath, string dir)
    {
        if (string.IsNullOrEmpty(searchPath) || string.IsNullOrWhiteSpace(dir)) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var target = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return searchPath
            .Split(Path.PathSeparator)
            .Select(p => p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Any(p => p.Length > 0 && string.Equals(p, target, comparison));
    }

    /// <summary>
    /// On macOS and Linux, put <see cref="UnixShimDir"/> at the front of this process's PATH when it is
    /// missing. Returns true when PATH was changed. Does nothing on Windows, where the installer puts the
    /// managed bin directory on the user's PATH in the registry and every start reads it.
    /// </summary>
    public static bool EnsureOnProcessPath()
    {
        if (OperatingSystem.IsWindows()) return false;

        var current = Environment.GetEnvironmentVariable("PATH");
        var dir = UnixShimDir;
        var updated = WithDirectory(current, dir);
        if (updated == current)
        {
            FileLog.Write($"[ToolShimPath] EnsureOnProcessPath: {dir} already on PATH");
            return false;
        }

        Environment.SetEnvironmentVariable("PATH", updated);
        FileLog.Write($"[ToolShimPath] EnsureOnProcessPath: added {dir} to this process's PATH (was: {current ?? "(unset)"})");
        return true;
    }
}
