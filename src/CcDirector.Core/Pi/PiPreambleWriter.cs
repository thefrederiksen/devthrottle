using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Pi;

/// <summary>
/// Writes the fleet preamble to a per-session file for Pi's <c>--append-system-prompt &lt;file&gt;</c>
/// flag. Pi has no Claude/Codex-style SessionStart hook, but it accepts a system-prompt append at
/// launch and keeps that system prompt across <c>/new</c> and <c>/compact</c> (those reset the
/// conversation, not the launch system prompt). So a single file written at launch gives Pi the same
/// "knows the fleet, and still knows it after a reset" behaviour without any in-process extension.
///
/// The Director already knows the session's identity at launch, so the preamble is built locally
/// (no endpoint fetch needed) and is correct for this exact session.
/// </summary>
public static class PiPreambleWriter
{
    /// <summary>Write the preamble for one session under the default per-user directory; returns the path.</summary>
    public static string WriteForSession(string sessionId, string? name, string machine, string repoPath)
        => WriteForSession(sessionId, name, machine, repoPath, DefaultDirectory());

    /// <summary>Testable overload that writes under an explicit directory.</summary>
    public static string WriteForSession(string sessionId, string? name, string machine, string repoPath, string directory)
    {
        Directory.CreateDirectory(directory);
        var text = FleetPreamble.Build(sessionId, name, machine, repoPath);
        var path = Path.Combine(directory, $"{sessionId}.txt");
        File.WriteAllText(path, text);
        FileLog.Write($"[PiPreambleWriter] wrote fleet preamble for {sessionId} to {path}");
        return path;
    }

    private static string DefaultDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cc-director", "pi-preamble");
}
