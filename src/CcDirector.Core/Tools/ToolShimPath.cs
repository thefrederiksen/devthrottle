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
    /// Where the tool shims of the install at <paramref name="installRoot"/> are written. This is the ONE
    /// rule for it; the setup engine's layout and the browser harness installer both ask here.
    ///
    /// On Windows it is always the install's own <c>bin</c>. On macOS and Linux it is the user's
    /// <c>~/.local/bin</c> only when the install IS this machine's default root, and the install's own
    /// <c>bin</c> for any other root - a test Director on a scratch root, a wizard harness, an unpacked
    /// bundle.
    ///
    /// WHY. <c>~/.local/bin</c> is shared by everything the user runs. A Director on a non-default root
    /// used to re-point every <c>cc-*</c> link in it at its own root's virtual environment during the
    /// start-up self-heal, so deleting that throwaway root broke <c>cc-devthrottle</c> for every session
    /// on the machine - twice on devlinux during the phase 6 proof (mission #4070). A root that is not the
    /// machine's own keeps its shims inside itself, exactly as it always did on Windows, and deleting it
    /// takes nothing else with it.
    /// </summary>
    /// <param name="installRoot">The install root whose tools the shims run.</param>
    /// <param name="defaultRoot">This machine's default root (<c>CcStorage.DefaultRoot()</c>).</param>
    /// <param name="home">The user's home folder. Only read for the default root on macOS and Linux.</param>
    /// <param name="isWindows">True for the Windows layout.</param>
    /// <exception cref="InvalidOperationException">
    /// The default root on macOS or Linux with no home folder: the answer would be a relative path, which
    /// would write the links beside whatever the working directory happens to be.
    /// </exception>
    public static string ShimDirFor(string installRoot, string defaultRoot, string? home, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultRoot);

        var ownBin = Path.Combine(installRoot, "bin");
        if (isWindows || !IsSameDirectory(installRoot, defaultRoot)) return ownBin;

        if (string.IsNullOrWhiteSpace(home))
        {
            throw new InvalidOperationException(
                $"Cannot place the tool links for the default root {installRoot}: the user's home folder is "
                + "unknown, so ~/.local/bin cannot be resolved. Set HOME.");
        }

        return Path.Combine(home, ".local", "bin");
    }

    /// <summary>
    /// Do two directory paths name the same folder? Compared as full paths without a trailing separator,
    /// case-sensitively on Linux and case-insensitively on Windows and macOS, the way their file systems
    /// compare names by default.
    /// </summary>
    internal static bool IsSameDirectory(string a, string b)
    {
        static string Normalise(string p) =>
            Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(Normalise(a), Normalise(b), comparison);
    }

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
