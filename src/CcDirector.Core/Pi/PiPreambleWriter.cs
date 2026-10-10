using CcDirector.Core.Account;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
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
/// (no endpoint fetch needed) and is correct for this exact session. The signed-in user (issue #1357)
/// is passed in from the Director's cached snapshot so the preamble names the human too.
/// </summary>
public static class PiPreambleWriter
{
    /// <summary>Write the preamble for one session under the default per-user directory; returns the path.</summary>
    public static string WriteForSession(string sessionId, string? name, string machine, string repoPath)
        => WriteForSession(sessionId, name, machine, repoPath, DefaultDirectory(), user: null);

    /// <summary>
    /// Write the preamble for one session under the default per-user directory, naming the signed-in
    /// DevThrottle user (issue #1357) and carrying the session's workflow-seat paragraph when it is
    /// seated (Workflows mission, phase 5b); returns the path.
    /// </summary>
    public static string WriteForSession(string sessionId, string? name, string machine, string repoPath, SignedInUser? user, string? seatParagraph = null)
        => WriteForSession(sessionId, name, machine, repoPath, DefaultDirectory(), user, store: null, seatParagraph);

    /// <summary>Testable overload that writes under an explicit directory.</summary>
    public static string WriteForSession(string sessionId, string? name, string machine, string repoPath, string directory)
        => WriteForSession(sessionId, name, machine, repoPath, directory, user: null);

    /// <summary>Testable overload that writes under an explicit directory and names the signed-in user.</summary>
    public static string WriteForSession(string sessionId, string? name, string machine, string repoPath, string directory, SignedInUser? user)
        => WriteForSession(sessionId, name, machine, repoPath, directory, user, store: null);

    /// <summary>Testable overload that also pins the injected-text store.
    /// <paramref name="seatParagraph"/> (Workflows mission, phase 5b) is the pre-built workflow-seat
    /// paragraph from <see cref="WorkflowSeatParagraph"/>, appended after the preamble so a seated Pi
    /// session learns its conduct fetch at launch exactly like the hook-fed agents; null for an
    /// unseated session. It is appended even when the preamble itself is empty - the seat is the
    /// operational fact the session was spawned for, not our injectable prose.</summary>
    public static string WriteForSession(
        string sessionId, string? name, string machine, string repoPath, string directory,
        SignedInUser? user, InjectedTextStore? store, string? seatParagraph = null)
    {
        Directory.CreateDirectory(directory);

        // BuildForSession, not Build: Pi is a live delivery path, so it injects the user's own text
        // when they are running one - and, being a live path, it opts into the workflow index
        // (Workflows mission, phase 5) exactly like the hook endpoints. When the injected-text store
        // is pinned (tests), the index store follows the same hermetic rule and is omitted.
        string text;
        try
        {
            text = FleetPreamble.BuildForSession(sessionId, name, machine, repoPath, user, store,
                workflowIndex: store is null ? new WorkflowIndexStore() : null,
                skillIndex: store is null ? new SkillIndexStore() : null);
        }
        catch (Exception ex) when (ex is InjectedTextUnavailableException or FleetPreambleTemplateException)
        {
            // The user's text is live but unreadable or unrenderable. Write an EMPTY file: Pi is
            // launched with --append-system-prompt pointing at it, so empty means nothing is injected.
            //
            // This mirrors what the hook endpoints do for Claude and Codex, and it is the behaviour the
            // documentation promises: DevThrottle injects nothing and says so. Letting the exception
            // escape here would abort the Pi session's launch instead - a different, undocumented, and
            // much ruder answer to the same situation. Note what is NOT done: substituting our text.
            // They turned ours off, and a file error is not consent to turn it back on.
            FileLog.Write(
                $"[PiPreambleWriter] injected text read FAILED: the user's injected text is unavailable for {sessionId}, so NOTHING " +
                $"is injected (the DevThrottle text is deliberately not substituted): {ex.Message}");
            text = "";
        }

        if (!string.IsNullOrEmpty(seatParagraph))
            text = string.IsNullOrEmpty(text) ? seatParagraph : text + "\n\n" + seatParagraph;

        var path = Path.Combine(directory, $"{sessionId}.txt");
        File.WriteAllText(path, text);
        FileLog.Write($"[PiPreambleWriter] wrote fleet preamble for {sessionId} to {path} ({text.Length} characters)");
        return path;
    }

    private static string DefaultDirectory() => CcStorage.PiPreamble();
}
