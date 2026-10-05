using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Remove-the-network-port mission, phase 3: renders one session's fleet preamble and writes it as
/// READY-TO-PRINT SessionStart hook output, replacing the two Director routes that used to serve it
/// (<c>GET /sessions/{sid}/fleet-preamble</c> and its hook-output sibling).
///
/// ONE FILE, ONE SHAPE, EVERY HOOK. The two routes existed because the Windows PowerShell hook could
/// build the JSON envelope itself and the POSIX shell hook could not - so one route returned plain
/// text and the other returned the envelope. Writing the envelope means every hook, on every platform
/// and for every agent family, does the same thing: print the file. The two platforms cannot disagree
/// about the shape any more, which is the class of defect that made the macOS hook silently omit the
/// signed-in user for as long as it did (issue #1357).
///
/// EMPTY MEANS NOTHING, AND THAT IS THE POINT. When there is no preamble to inject - the user's own
/// text is empty, unreadable, or renders to nothing - the file is written EMPTY rather than left
/// missing or filled with an error. The hook prints the file verbatim into the agent's context, so an
/// empty file is the only thing that reliably means "inject nothing", and DevThrottle's own text is
/// deliberately NOT substituted: a user running their own text turned ours off, and a file error is
/// not consent to turn it back on.
/// </summary>
public static class SessionPreambleFile
{
    // Compact, because this is machine-to-machine and the hook prints it verbatim to the agent's
    // stdout. The serializer does the escaping - the whole reason the POSIX hook could never build
    // this itself.
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>Write the maintained hook output for one session under the real storage root.</summary>
    public static string WriteFor(Session session, string machine, SignedInUser? user)
        => WriteFor(session, machine, user, directory: null, store: null);

    /// <summary>
    /// Testable overload. <paramref name="directory"/> pins where the file goes;
    /// <paramref name="store"/> pins the injected-text store, and when it is supplied the workflow and
    /// skill indexes are omitted so a test stays hermetic - the same rule
    /// <see cref="Pi.PiPreambleWriter"/> follows.
    /// </summary>
    public static string WriteFor(
        Session session, string machine, SignedInUser? user, string? directory, InjectedTextStore? store)
    {
        ArgumentNullException.ThrowIfNull(session);

        var path = SessionHookFiles.PreamblePathFor(session.Id, directory);
        SessionHookFiles.WriteAtomic(path, Render(session, machine, user, store));
        return path;
    }

    /// <summary>Delete a session's maintained file. Called when the session is removed, so a reaped
    /// session leaves nothing behind that a later reader could mistake for a live one.</summary>
    public static void DeleteFor(Guid sessionId, string? directory = null)
    {
        var path = SessionHookFiles.PreamblePathFor(sessionId, directory);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionPreambleFile] could not delete {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// The exact bytes the hook will print: the SessionStart envelope carrying this session's
    /// preamble, or the empty string when there is nothing to inject. Exposed so a test can assert the
    /// content without touching the file system, and so the maintainer can compare a rewrite against
    /// what is already on disk.
    /// </summary>
    public static string Render(Session session, string machine, SignedInUser? user, InjectedTextStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Issue #800: the display name goes through the single composer, so a session never identifies
        // itself by the bare folder name.
        var name = SessionName.DisplayName(session.CustomName,
            SessionName.FolderName(session.RepoPath),
            SessionName.Disambiguator(session.Id));

        string text;
        try
        {
            // BuildForSession, not Build: this is a live delivery path, so it honours the user's choice
            // of whose text is injected and opts into the two live indexes.
            text = FleetPreamble.BuildForSession(
                session.Id.ToString(), name, machine, session.RepoPath, user, store,
                workflowIndex: store is null ? new WorkflowIndexStore() : null,
                skillIndex: store is null ? new SkillIndexStore() : null);
        }
        catch (Exception ex) when (ex is InjectedTextUnavailableException or FleetPreambleTemplateException)
        {
            // The user's text is live but unreadable, or was edited into something that cannot render.
            // Inject NOTHING - never our text, which they turned off. This is the same answer the two
            // deleted routes gave (an empty body) and the same one Pi's file gives.
            FileLog.Write(
                $"[SessionPreambleFile] the user's injected text is unavailable for {session.Id}, so NOTHING " +
                $"is injected (the DevThrottle text is deliberately not substituted): {ex.Message}");
            // Except the owner's lessons (issue #3559): they are not our text, and a file error must not cost a
            // Fleet Manager the corrections it was given.
            return string.IsNullOrEmpty(session.FleetManagerLessons) ? "" : Envelope(session.FleetManagerLessons);
        }

        // Workflows mission (phase 5b): a seated session's preamble carries its seat paragraph, built
        // by the one builder every delivery channel shares - it validates the workflow id, so a forged
        // seat renders nothing.
        var seat = WorkflowSeatParagraph.Build(
            session.WorkflowRunId, session.WorkflowId, session.WorkflowVersion, session.ExplicitRole);
        if (!string.IsNullOrEmpty(seat))
            text = string.IsNullOrEmpty(text) ? seat : text + "\n\n" + seat;

        // Factory Memory mission (phase 3a): a factory session whose notes are in place is told where they are.
        // Appended like the seat, because it is an operational fact about this session rather than our injectable
        // prose - and it is a courtesy: the environment variable is what makes the memory work.
        var memory = FactoryMemoryFiles.StartupLine(session);
        if (!string.IsNullOrEmpty(memory))
            text = string.IsNullOrEmpty(text) ? memory : text + "\n\n" + memory;

        // Issue #3559: the marked Fleet Manager's confirmed lessons, FIRST, exactly as the Gateway wrote them. This is
        // the path a compaction or a clear takes, which is when a Fleet Manager would otherwise lose them. Put in even
        // when the user's own text renders to nothing: an empty preamble turns OUR prose off, not the owner's lessons.
        if (!string.IsNullOrEmpty(session.FleetManagerLessons))
            text = string.IsNullOrEmpty(text) ? session.FleetManagerLessons : session.FleetManagerLessons + "\n" + text;

        // BuildForSession already collapses whitespace-only text to empty, so an empty envelope is
        // impossible by construction rather than by coincidence.
        if (string.IsNullOrEmpty(text))
            return "";

        return Envelope(text);
    }

    private static string Envelope(string text) => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new
        {
            hookEventName = "SessionStart",
            additionalContext = text,
        },
    }, JsonOpts);
}
