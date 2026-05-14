namespace CcDirector.Core.Agents;

/// <summary>
/// Output of <see cref="IAgent.BuildLaunchSpec"/>. Tells the SessionManager exactly what to spawn.
/// </summary>
/// <param name="Arguments">Fully-formed command-line args to pass to the agent executable.</param>
/// <param name="PreassignedSessionId">
/// If non-null, the agent was launched with a session ID known up front (e.g. Claude's <c>--session-id</c>).
/// Director can link the Session record to this ID immediately without waiting for hooks or scanning files.
/// Null for agents that don't support preassignment (e.g. Pi).
/// </param>
public sealed record AgentLaunchSpec(string Arguments, string? PreassignedSessionId);
