using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Director-side detector for the scheduled-run auto-dismiss verdict (issue #1200). It watches an
/// auto-dismiss session's turn-ends and, from the agent's OWN transcript (not the terminal buffer, so it is
/// robust to the alternate-screen full-screen mode), parses the <c>CC-DISMISS</c> sentinel the run prints
/// as its final message and stamps <see cref="Session.DismissVerdict"/>. That verdict then flows UP to the
/// Gateway on the session DTO, where the auto-dismiss sweep closes the session over the stream on
/// <c>done</c>. Reading the transcript (via the same <see cref="ITranscriptReader"/> the Director already
/// uses for briefs) rather than scraping the terminal is deliberate: the sentinel is the agent's real
/// message text, unaffected by ANSI/alt-screen rendering.
///
/// Only Claude sessions carry a transcript today, so a non-Claude auto-dismiss run simply never stamps a
/// verdict and is never auto-closed - the conservative default.
/// </summary>
public sealed class AutoDismissVerdictWatcher
{
    private readonly ITranscriptReader _transcripts;

    /// <param name="transcripts">Transcript reader (a test seam); production passes null for the disk-backed reader.</param>
    public AutoDismissVerdictWatcher(ITranscriptReader? transcripts = null)
    {
        _transcripts = transcripts ?? new ClaudeTranscriptReader();
    }

    /// <summary>
    /// Subscribe to <paramref name="session"/>'s turn-ends so its verdict is (re)parsed each time the agent
    /// settles. A no-op for a session that is not auto-dismiss (so a normal human session is never scanned).
    /// The scan runs off the state-change callback thread (fire-and-forget) so it never blocks the detector.
    /// </summary>
    public void Attach(Session session)
    {
        if (session is null || !session.AutoDismiss)
            return;

        session.OnActivityStateChanged += (oldState, newState) =>
        {
            _ = oldState; // unused: we only care about the state we transitioned INTO
            // A turn is over only when the session settles to WaitingForInput (the dumb quiet timer). At that
            // point the final assistant message - which carries any CC-DISMISS block - is in the transcript.
            if (!ActivityStateFacts.IsTurnEnd(newState))
                return;
            _ = Task.Run(() => ScanAndStamp(session));
        };
        FileLog.Write($"[AutoDismissVerdictWatcher] attached: session={session.Id}");
    }

    /// <summary>
    /// Read the session's latest assistant text, parse the last <c>CC-DISMISS</c> block, and stamp the
    /// verdict when one is present. A boundary (fire-and-forget target): it owns its try/catch so a
    /// transcript read fault never escapes onto a background thread.
    /// </summary>
    internal void ScanAndStamp(Session session)
    {
        try
        {
            var text = ReadAssistantText(session);
            var signal = DismissVerdictSignal.ParseLatest(text);
            if (signal is not null)
                session.SetDismissVerdict(signal.Wire);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AutoDismissVerdictWatcher] scan FAILED: session={session.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Concatenate the session's assistant text widgets in chronological order. Parsing the whole
    /// (rather than only the last widget) keeps "last verdict wins" correct even when the block is embedded
    /// in a longer final message or a later turn supersedes an earlier verdict. Returns null when the
    /// transcript is unavailable (no ClaudeSessionId yet, or a non-Claude agent).
    /// </summary>
    internal string? ReadAssistantText(Session session)
    {
        if (string.IsNullOrEmpty(session.ClaudeSessionId))
            return null;

        var widgets = _transcripts.ReadWidgets(session.ClaudeSessionId, session.RepoPath);
        if (widgets.Count == 0)
            return null;

        var texts = widgets
            .Where(w => string.Equals(w.Kind, "Text", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(w.Content))
            .Select(w => w.Content);
        return string.Join("\n", texts);
    }
}
