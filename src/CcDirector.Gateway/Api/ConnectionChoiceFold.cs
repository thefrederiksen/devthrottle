using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// CHOOSING HOW A SESSION IS CONNECTED, ruled once on the Gateway (API mode mission, issue #4070, phase 5). Two rulings:
/// which connections the New Session row offers for each agent (<see cref="Offer"/>), and whether a create may ask for
/// the one it names (<see cref="RefusalOf"/>). Both read <see cref="ConnectionModes.ApiModeOffered"/>, the one switch, so
/// while API mode is not offered the row is absent on every client and a create that asks for it is refused - no surface
/// offers a mode it cannot finish (mission section 8).
/// </summary>
internal static class ConnectionChoiceFold
{
    /// <summary>
    /// Replace each Director-reported agent's declared connections with the OFFERED ones, labelled (Terminal always, API
    /// mode only for a wrapper that declares it while <paramref name="apiModeOffered"/> is on), each with the sentence the
    /// row shows while it is selected. An older Director that reports no connections gets Terminal alone. Mutates and
    /// returns <paramref name="agents"/>.
    /// </summary>
    public static List<AgentChoiceDto> Offer(List<AgentChoiceDto> agents, bool apiModeOffered)
    {
        foreach (var agent in agents)
        {
            var declared = agent.Connections.Select(c => c.Mode).ToList();
            agent.Connections = ConnectionChoiceDto.For(ConnectionModes.Offered(declared, apiModeOffered), agent.DisplayName);
        }
        FileLog.Write($"[ConnectionChoiceFold] Offer: {agents.Count} agent(s), apiModeOffered={apiModeOffered}, " +
                      $"with API mode={agents.Count(a => a.Connections.Count > 1)}");
        return agents;
    }

    /// <summary>
    /// Why the Gateway refuses this create's <see cref="NewSessionRequest.ConnectionMode"/>, or null when it goes on to the
    /// Director. Absent and <c>terminal</c> always go on. A word that is not a connection mode is refused, never guessed.
    /// <c>api</c> is refused while <paramref name="apiModeOffered"/> is off - except on a restore's create, which brings
    /// back a session the way it already ran. <paramref name="validatedRestore"/> says the create IS a restore, and only
    /// the door that has checked its <see cref="NewSessionRequest.RestoreClaim"/> may say so: a claim in the body proves
    /// nothing until the Gateway has checked who sent it (the phase 5 review, finding 1 - the machine door never checks
    /// one, so a forged claim there skipped the switch). Whether the AGENT has API mode is the Director's to answer, from
    /// the agent's own wrapper, and it refuses in words.
    /// </summary>
    public static string? RefusalOf(NewSessionRequest req, bool apiModeOffered, bool validatedRestore)
    {
        var mode = req.ConnectionMode;
        if (string.IsNullOrEmpty(mode) || mode == ConnectionModes.Terminal)
            return null;
        if (mode != ConnectionModes.Api)
        {
            FileLog.Write($"[ConnectionChoiceFold] RefusalOf: unknown connection mode \"{mode}\"");
            return $"\"{mode}\" is not a connection mode: it is \"{ConnectionModes.Terminal}\" or \"{ConnectionModes.Api}\".";
        }
        if (apiModeOffered || validatedRestore)
            return null;
        FileLog.Write("[ConnectionChoiceFold] RefusalOf: API mode asked for while it is not offered");
        return "API mode is not offered yet: it opens once taking the wheel is built (mission #4070, phase 4). " +
               "Start the session in terminal mode.";
    }
}
