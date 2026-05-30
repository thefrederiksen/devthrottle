namespace CcDirector.Core.Utilities;

/// <summary>
/// Resolves a command name (e.g. "opencode") or path to a concrete executable file,
/// mirroring the search rules CreateProcess/cmd use: an explicit path is checked
/// directly; a bare name is searched across each PATH directory, trying each PATHEXT
/// extension (Windows). Returns null when nothing matches.
///
/// Callers use this to fail fast with a clear, actionable message ("OpenCode is not
/// installed") instead of letting CreateProcess fail with a cryptic Win32 error that
/// then gets swallowed and leaves the user with no feedback.
/// </summary>
public static class ExecutableResolver
{
    /// <summary>
    /// Resolve <paramref name="command"/> using the current process PATH and PATHEXT.
    /// </summary>
    /// <returns>The full path to the executable, or null if it could not be found.</returns>
    public static string? Resolve(string command)
        => Resolve(command,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"));

    /// <summary>
    /// Resolve <paramref name="command"/> against an explicit search path and PATHEXT list.
    /// Exposed for deterministic testing without mutating process-global environment.
    /// </summary>
    public static string? Resolve(string command, string? searchPath, string? pathExt)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var extensions = GetPathExtensions(pathExt);

        // Explicit path (rooted or containing a directory separator): check it directly,
        // never against PATH. This is how CreateProcess treats such commands too.
        if (Path.IsPathRooted(command)
            || command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return ResolveWithExtensions(command, extensions);
        }

        // Bare command name: search each PATH directory in order.
        if (string.IsNullOrEmpty(searchPath))
            return null;

        foreach (var dir in searchPath.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim(), command);
            }
            catch
            {
                // A malformed PATH entry should not abort the whole search.
                continue;
            }

            var resolved = ResolveWithExtensions(candidate, extensions);
            if (resolved != null)
                return resolved;
        }

        return null;
    }

    private static string? ResolveWithExtensions(string basePath, IReadOnlyList<string> extensions)
    {
        // Exact-name match is honored only when the name already carries an extension
        // (e.g. "opencode.cmd", "tool.exe"), or on POSIX where executables have no implicit
        // extension. On Windows a bare "opencode" must NOT match an extensionless "opencode"
        // file (npm drops a Unix bash shim there next to "opencode.cmd") - CreateProcess
        // cannot run it, and cmd/PATHEXT resolution would skip it too.
        var allowExactMatch = !OperatingSystem.IsWindows() || Path.HasExtension(basePath);
        if (allowExactMatch && File.Exists(basePath))
            return Path.GetFullPath(basePath);

        // Then try appending each executable extension (Windows resolves "opencode" -> "opencode.cmd").
        foreach (var ext in extensions)
        {
            if (ext.Length == 0)
                continue;
            var withExt = basePath + ext;
            if (File.Exists(withExt))
                return Path.GetFullPath(withExt);
        }

        return null;
    }

    private static IReadOnlyList<string> GetPathExtensions(string? pathExt)
    {
        // POSIX has no implicit executable extensions; the file must be named exactly.
        if (!OperatingSystem.IsWindows())
            return Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(pathExt))
            pathExt = ".COM;.EXE;.BAT;.CMD";

        return pathExt
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToArray();
    }
}
