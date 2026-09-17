using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.History;

/// <summary>One stored turn as <see cref="SessionTurnStore.ReadTail"/> answers it: the Chat row's role, parts and
/// time, plus the two flags a Chat row does not carry.</summary>
/// <param name="Role">"User" or "Assistant".</param>
/// <param name="Parts">The turn's content parts, in order.</param>
/// <param name="TimestampUtc">When the agent wrote the turn into its conversation, or null when the source carries no
/// time.</param>
/// <param name="IsMeta">True for a line the agent injected rather than the person or the model wrote.</param>
/// <param name="IsSidechain">True for a line of a nested subagent conversation.</param>
public sealed record StoredTurn(
    string Role, IReadOnlyList<HistoryPartDto> Parts, DateTime? TimestampUtc, bool IsMeta, bool IsSidechain);

/// <summary>The end of a session's stored conversation and the facts its head carries.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Agent">The agent the Director says runs it.</param>
/// <param name="IsSupported">False when the Director cannot read this agent's conversation.</param>
/// <param name="HistoryState">The Director's transcript state (Idle / Working / NeedsYou / BackgroundRunning), or
/// null when it derives none - today every agent other than Claude Code.</param>
/// <param name="Turns">The last turns of the current conversation, oldest first.</param>
public sealed record SessionTurnTail(
    string SessionId, string Agent, bool IsSupported, string? HistoryState, IReadOnlyList<StoredTurn> Turns);
