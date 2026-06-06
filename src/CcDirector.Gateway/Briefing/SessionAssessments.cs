using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Briefing;

/// <summary>
/// The Gateway-owned half of the issue #186 two-owner model: assessedState per session.
/// The Director's detector owns rawState (mechanical busy/quiet); after the brief agent
/// reads a turn, the brain's verdict refines what the quiet MEANS:
///
///   brief.NeedsYou != null  ->  "WaitingForInput"  (genuinely needs the user)
///   brief.NeedsYou == null  ->  "Idle"             (turn done, nothing needed - the
///                                                   refutation of a red quiet signal)
///
/// An assessment stands only for the turn it judged: any observation of the session
/// re-entering Working invalidates it (new PTY bytes = stale judgment). The Cockpit
/// displays AssessedState ?? ActivityState; the Gateway also pushes the assessment down
/// to the owning Director as a display annotation.
///
/// In-memory by design - assessments are derived (the briefs are the durable record) and
/// a Gateway restart simply re-derives them as new turns complete.
/// </summary>
public sealed class SessionAssessments
{
    private readonly ConcurrentDictionary<string, string> _assessed = new();

    /// <summary>Derive and record the assessment for a freshly stored brief. Degraded
    /// (stub) briefs assess nothing - an honest marker is not a judgment.</summary>
    public string? RecordBrief(string sessionId, TurnBriefDto brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        if (brief.Degraded) return null;

        var assessed = brief.NeedsYou is not null ? "WaitingForInput" : "Idle";
        _assessed[sessionId] = assessed;
        FileLog.Write($"[SessionAssessments] sid={sessionId} turn={brief.TurnNumber}: assessed={assessed} (needsYou={(brief.NeedsYou is null ? "null" : brief.NeedsYou.Urgency)})");
        return assessed;
    }

    /// <summary>New PTY activity: the standing assessment judged the PREVIOUS quiet.</summary>
    public void Invalidate(string sessionId)
    {
        if (_assessed.TryRemove(sessionId, out _))
            FileLog.Write($"[SessionAssessments] sid={sessionId}: assessment invalidated (session working again)");
    }

    /// <summary>The standing assessment, or null when none.</summary>
    public string? For(string sessionId)
        => _assessed.TryGetValue(sessionId, out var a) ? a : null;
}
