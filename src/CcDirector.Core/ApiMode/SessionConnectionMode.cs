namespace CcDirector.Core.ApiMode;

/// <summary>
/// How the Director talks to a session's agent (API mode mission, issue #4070). Chosen once, when the session is
/// created; terminal is the default.
/// </summary>
public enum SessionConnectionMode
{
    /// <summary>The agent runs in a terminal and prompts are typed into it. Every agent supports it.</summary>
    Terminal,

    /// <summary>The agent runs as a long-lived child process spoken to in its own native protocol; a prompt is a
    /// message the agent acknowledges. Only an agent whose wrapper declares it (Pi today).</summary>
    Api,
}

/// <summary>The sets of connection modes a wrapper declares (<see cref="Drivers.IAgentDriver.ConnectionModes"/>).</summary>
public static class SessionConnectionModes
{
    /// <summary>Terminal only: every wrapper that has no API dialect.</summary>
    public static IReadOnlyList<SessionConnectionMode> TerminalOnly { get; } = [SessionConnectionMode.Terminal];

    /// <summary>Terminal and API mode: a wrapper that also speaks its agent's native protocol.</summary>
    public static IReadOnlyList<SessionConnectionMode> TerminalAndApi { get; } = [SessionConnectionMode.Terminal, SessionConnectionMode.Api];
}
