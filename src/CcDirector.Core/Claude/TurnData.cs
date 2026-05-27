namespace CcDirector.Core.Claude;

/// <summary>
/// Snapshot of data accumulated during a single turn (a user prompt through the agent's
/// stop). Consumed by the session summarizers and the Wingman's code-review-discipline
/// check. Formerly populated by the Claude Code hook path; that path has been removed, so
/// this is now produced only by the summarization callers that still build it explicitly.
/// </summary>
public sealed record TurnData(
    string UserPrompt,
    List<string> ToolsUsed,
    List<string> FilesTouched,
    List<string> BashCommands,
    DateTimeOffset Timestamp);
