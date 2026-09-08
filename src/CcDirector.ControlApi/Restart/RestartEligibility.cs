using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Restart;

/// <summary>
/// Is THIS Director the one its launcher supervises - and so the one a launcher restart would restart?
/// Issue #2725 (restart epic, Phase 6).
///
/// WHY THE QUESTION EXISTS. A launcher supervises exactly one Director - the installed executable in the
/// default instance home - and its restart verb restarts that one whatever path the command names
/// (issue #2743). A restart cycle that drained a NAMED instance or a development slot and then asked the
/// launcher would close one fleet and restart a different Director, and every check on the way would
/// have passed. Only the Director can answer this, from its own instance slug and its own executable
/// path, so it is asked before the owner ever sees the request and again before it drains itself.
///
/// FOUR ANSWERS, NOT TWO. Eligible is true only when both facts match. It is false when either fact is
/// present and contradicts. It is NULL when the process path cannot be read at all, and a null travels
/// to the caller as "could not tell", which every caller treats as a refusal - never folded into false,
/// because a false has a specific reason and this does not, and never into true.
/// </summary>
public static class RestartEligibility
{
    /// <summary>Judge from the facts.</summary>
    /// <param name="instanceSlug">The slug this Director runs as.</param>
    /// <param name="isDefaultInstance">Whether that slug is the default instance the launcher supervises.</param>
    /// <param name="processPath">This process's executable, or null when the runtime cannot say.</param>
    /// <param name="supervisedPath">The executable (or application bundle) the launcher starts.</param>
    public static DirectorRestartEligibilityDto Judge(string instanceSlug, bool isDefaultInstance,
        string? processPath, string supervisedPath)
    {
        var dto = new DirectorRestartEligibilityDto
        {
            InstanceSlug = instanceSlug,
            ProcessPath = processPath,
            SupervisedPath = supervisedPath,
        };

        if (!isDefaultInstance)
        {
            dto.Eligible = false;
            dto.Reason = $"this Director runs as the named instance '{instanceSlug}', and a launcher restarts only "
                       + "the default instance it supervises. Restarting the launcher's Director would leave this "
                       + "one untouched and drain the wrong fleet, so it refuses.";
            return dto;
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            dto.Eligible = null;
            dto.Reason = "this Director could not read its own executable path, so it cannot tell whether it is "
                       + "the executable its launcher supervises. Not knowing is not a yes: it refuses.";
            return dto;
        }

        if (!SamePathOrInsideBundle(processPath, supervisedPath))
        {
            dto.Eligible = false;
            dto.Reason = $"this Director is running from '{processPath}', and its launcher supervises "
                       + $"'{supervisedPath}'. A launcher restart would start that executable, not this one, so "
                       + "this Director refuses to drain itself for it.";
            return dto;
        }

        dto.Eligible = true;
        dto.Reason = $"this Director is the default instance running from '{processPath}', which is the executable "
                   + "its launcher supervises, so a launcher restart restarts it.";
        return dto;
    }

    /// <summary>Equal paths, or - for the macOS application bundle - the process inside the bundle the
    /// launcher starts. Case-insensitive on Windows, where the file system is.</summary>
    private static bool SamePathOrInsideBundle(string processPath, string supervisedPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var process = Normalize(processPath);
        var supervised = Normalize(supervisedPath);
        if (string.Equals(process, supervised, comparison)) return true;
        return process.StartsWith(supervised + Path.DirectorySeparatorChar, comparison)
               || process.StartsWith(supervised + Path.AltDirectorySeparatorChar, comparison);
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception) { return path.Trim(); }
    }
}
