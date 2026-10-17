using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// The desktop New Session dialog's Connection row (API mode mission, issue #4070, phase 5): which connections an agent
/// is offered and which one a session starts with. It reads the same two facts the Gateway's agent list does - what the
/// agent's wrapper declares (<see cref="CatalogReadExecutor.DeclaredConnectionModes"/>) and the one switch
/// (<see cref="ConnectionModes.Offered(IEnumerable{string})"/>) - so the desktop offers exactly what the Cockpit and the
/// phone offer, and while API mode is not offered the row stays hidden here too.
/// </summary>
public static class NewSessionConnection
{
    /// <summary>The connections offered for <paramref name="kind"/>: Terminal, then API mode when offered for it.</summary>
    public static IReadOnlyList<string> OfferedFor(AgentKind kind) => OfferedFor(kind, ConnectionModes.ApiModeOffered);

    /// <inheritdoc cref="OfferedFor(AgentKind)"/>
    /// <param name="kind">The agent the session will start with.</param>
    /// <param name="apiModeOffered">Whether API mode is offered at all, passed so a test can read both positions.</param>
    public static IReadOnlyList<string> OfferedFor(AgentKind kind, bool apiModeOffered) =>
        ConnectionModes.Offered(CatalogReadExecutor.DeclaredConnectionModes(kind), apiModeOffered);

    /// <summary>Whether the dialog shows the Connection row for <paramref name="kind"/>: only when it is offered a choice.</summary>
    public static bool ShowsChoice(AgentKind kind, bool apiModeOffered) => OfferedFor(kind, apiModeOffered).Count > 1;

    /// <summary>
    /// The sentence shown under the Connection row: what the connection the session would start with means, in the
    /// words every surface shows (<see cref="ConnectionModes.DescriptionOf"/>), or null when that is the terminal or the
    /// row is not shown.
    /// </summary>
    public static string? DescriptionFor(AgentKind kind, string agentName, bool apiChosen, bool apiModeOffered)
    {
        if (!ShowsChoice(kind, apiModeOffered)) return null;
        var mode = ModeToStart(kind, apiChosen, apiModeOffered) == SessionConnectionMode.Api ? ConnectionModes.Api : ConnectionModes.Terminal;
        return ConnectionModes.DescriptionOf(mode, agentName);
    }

    /// <summary>
    /// The connection a session starts with: API mode only when the person chose it AND it is offered for this agent;
    /// otherwise the terminal, the default. A choice made for one agent never carries to another that lacks it.
    /// </summary>
    public static SessionConnectionMode ModeToStart(AgentKind kind, bool apiChosen, bool apiModeOffered)
    {
        var mode = apiChosen && OfferedFor(kind, apiModeOffered).Contains(ConnectionModes.Api)
            ? SessionConnectionMode.Api
            : SessionConnectionMode.Terminal;
        FileLog.Write($"[NewSessionConnection] ModeToStart: agent={kind}, apiChosen={apiChosen}, apiModeOffered={apiModeOffered}, mode={mode}");
        return mode;
    }
}
