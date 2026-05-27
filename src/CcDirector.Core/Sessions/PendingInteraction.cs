namespace CcDirector.Core.Sessions;

/// <summary>
/// The kind of structured interaction the agent is currently waiting on the
/// user to resolve. Each kind has different rendering and slightly different
/// response semantics; they all share the same <see cref="PendingInteraction"/>
/// container and the same dispatch path (the user's reply is sent as text to
/// the agent's stdin via /sessions/{sid}/prompt).
/// </summary>
public enum PendingInteractionKind
{
    /// <summary>
    /// Driven by the agent invoking Claude Code's <c>AskUserQuestion</c> tool.
    /// Payload is a single prompt plus a list of option labels.
    /// </summary>
    Question,

    /// <summary>
    /// Driven by the agent invoking Claude Code's <c>ExitPlanMode</c> tool.
    /// Payload is a markdown plan body the user must approve or reject.
    /// </summary>
    Plan,

    /// <summary>
    /// Driven by a <c>PermissionRequest</c> event or a <c>Notification</c>
    /// event with <c>notification_type=permission_prompt</c>. Payload is the
    /// tool name plus a truncated summary of the arguments the agent wants to
    /// run with.
    /// </summary>
    Permission,
}

/// <summary>
/// One choice the user can pick when responding to a
/// <see cref="PendingInteractionKind.Question"/>. The user's reply is the
/// <see cref="Label"/> sent as text to the session.
/// </summary>
public sealed class PendingInteractionOption
{
    /// <summary>The button text the user sees and (when clicked) what gets
    /// sent back to the session as the user's reply.</summary>
    public string Label { get; init; } = "";

    /// <summary>Optional secondary text shown below the label. May be null
    /// or empty when the agent did not provide a description.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Whatever structured question, plan, or permission ask the agent is
/// currently waiting on. At most one of these is set per <see cref="Session"/>
/// at any given time; the next one (if any) replaces it.
///
/// Its only source was the Claude Code hook path, which has been removed; terminal-driven
/// detection does not yet parse the structured ask off the screen, so this is currently
/// never populated. The type is kept for the wizard-detection work that will repopulate it
/// from the terminal grid.
///
/// This object is intentionally volatile state: it is NOT persisted by
/// <c>SessionStateStore</c>.
/// </summary>
public sealed class PendingInteraction
{
    /// <summary>Which family of interaction this is. Determines how the UI
    /// renders the body and which fields below are populated.</summary>
    public required PendingInteractionKind Kind { get; init; }

    /// <summary>UTC timestamp when this interaction was created. Used by the
    /// UI to display "waiting for Xm" durations.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The headline text the user is being asked to respond to.
    /// Populated for all kinds.
    /// - Question: the question text.
    /// - Plan: a short headline like "Plan ready - approve?".
    /// - Permission: a short headline like "Allow this tool to run?".
    /// </summary>
    public string Prompt { get; init; } = "";

    /// <summary>The option list for <see cref="PendingInteractionKind.Question"/>.
    /// Empty for other kinds.</summary>
    public List<PendingInteractionOption> Options { get; init; } = new();

    /// <summary>The plan body for <see cref="PendingInteractionKind.Plan"/>.
    /// Markdown. Null for other kinds.</summary>
    public string? PlanBody { get; init; }

    /// <summary>For <see cref="PendingInteractionKind.Permission"/>, the name
    /// of the tool the agent wants to run (e.g. "Bash", "Edit"). Null for
    /// other kinds.</summary>
    public string? ToolName { get; init; }

    /// <summary>For <see cref="PendingInteractionKind.Permission"/>, a
    /// truncated summary of the tool's arguments (e.g. the first line of a
    /// Bash command). Null for other kinds.</summary>
    public string? ToolInputSummary { get; init; }
}
