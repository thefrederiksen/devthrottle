using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Supervisor;

/// <summary>
/// Phase 5: snapshot of one session's state that gets piped into the supervisor's
/// "ask" prompt. Pure data; the caller (typically the Director's endpoint handler)
/// fills it from <c>Session.RecentSupervisorEvents</c>, <c>TurnSummaryCache</c>, the
/// terminal buffer tail, and a git snapshot. Keeping this as a plain shape lets
/// <see cref="SupervisorService.AskAboutSessionAsync"/> stay a pure function and
/// makes the prompt builder snapshot-testable.
/// </summary>
public sealed class SupervisorAskContext
{
    public string SessionId { get; init; } = "";
    public string RepoPath { get; init; } = "";
    public string AgentKind { get; init; } = "";
    public string ActivityState { get; init; } = "";
    public string CurrentColor { get; init; } = "";
    public string CurrentReason { get; init; } = "";
    public bool GitDirty { get; init; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<SupervisorAskEvent> RecentSupervisorEvents { get; init; } = Array.Empty<SupervisorAskEvent>();

    /// <summary>Oldest first; caller may pass up to 5 or so.</summary>
    public IReadOnlyList<TurnSummary> RecentTurnSummaries { get; init; } = Array.Empty<TurnSummary>();

    /// <summary>
    /// Tail of the terminal buffer, ANSI escapes stripped and length-capped. The
    /// caller is responsible for the strip/cap; this class doesn't reach into the
    /// session.
    /// </summary>
    public string BufferTailText { get; init; } = "";

    /// <summary>
    /// One-line description of what's actually in this context, used as the UI's
    /// "context the supervisor sees" footer AND echoed back in <see cref="SupervisorAskResult.ContextDigest"/>.
    /// </summary>
    public string ToDigest()
    {
        return string.Join(", ",
            $"events:{RecentSupervisorEvents.Count}",
            $"turns:{RecentTurnSummaries.Count}",
            $"buffer:{BufferTailText?.Length ?? 0}ch",
            $"repo:{System.IO.Path.GetFileName((RepoPath ?? "").TrimEnd('\\', '/'))}",
            $"color:{CurrentColor}");
    }
}

/// <summary>Per-event row inside <see cref="SupervisorAskContext.RecentSupervisorEvents"/>.</summary>
public sealed record SupervisorAskEvent(DateTime At, string OldColor, string NewColor, string Reason);
