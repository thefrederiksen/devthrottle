namespace CcDirector.Core.Utilities;

/// <summary>
/// Where one of our own cc tools is on this machine. One lookup for every tool the Director runs, so the order
/// cannot drift between callers: an explicit path from an environment variable when the caller has one, then the
/// machine's installed tool directory, then PATH. Our own copy comes before PATH for the same reason the session
/// PATH is rewritten at launch: another install's copy in front of ours is shared state we do not control.
/// </summary>
public static class CcToolExecutable
{
    /// <summary>The full path to <paramref name="toolName"/>, or null when it is not installed.</summary>
    /// <param name="toolName">The command, as it is installed on PATH and in the machine's tool directory.</param>
    /// <param name="explicitPathEnvVar">An environment variable that may name the tool's path, read at every call;
    /// null when the tool has no such override.</param>
    public static string? Resolve(string toolName, string? explicitPathEnvVar)
    {
        if (string.IsNullOrWhiteSpace(toolName)) throw new ArgumentException("A tool name is required", nameof(toolName));

        if (explicitPathEnvVar is not null)
        {
            var explicitPath = Environment.GetEnvironmentVariable(explicitPathEnvVar);
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                var resolved = ExecutableResolver.Resolve(explicitPath);
                if (resolved is not null)
                    return resolved;
                FileLog.Write($"[CcToolExecutable] {explicitPathEnvVar}={explicitPath} does not resolve to a file; falling through to the normal search");
            }
        }

        try
        {
            var ownBin = Path.Combine(Storage.CcStorage.Bin(), toolName);
            var own = ExecutableResolver.Resolve(ownBin);
            if (own is not null)
                return own;
        }
        catch (Exception ex)
        {
            // The tool directory is not readable on this machine. That is not a reason to stop looking.
            FileLog.Write($"[CcToolExecutable] could not look in the machine's tool directory for {toolName} FAILED: {ex.Message}");
        }

        return ExecutableResolver.Resolve(toolName);
    }
}
