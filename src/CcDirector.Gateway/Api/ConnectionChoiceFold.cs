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
    /// mode only for a wrapper that declares it while <paramref name="apiModeOffered"/> is on and the Director is new
    /// enough to carry it), each with the sentence the row shows while it is selected, and the one New Session starts on
    /// marked as the default (<see cref="ConnectionModes.DefaultOf"/> on what is offered: Terminal for every agent today,
    /// Pi included - the owner's decision, 20 October 2026). An older Director that reports no
    /// connections gets Terminal alone, and so does a Director below <see cref="FirstDirectorThatCarriesApiMode"/> whatever
    /// it declares: v2.24.0 declares API mode for Pi, and offering it there would offer a create
    /// <see cref="DirectorCannotCarry(NewSessionRequest, DirectorDto)"/> then refuses (the #4257 review, finding 1). So
    /// Pi's default is Terminal there too. Mutates and returns <paramref name="agents"/>.
    /// </summary>
    public static List<AgentChoiceDto> Offer(List<AgentChoiceDto> agents, bool apiModeOffered, DirectorDto director)
    {
        var carries = CarriesApiMode(director);
        foreach (var agent in agents)
        {
            var declared = agent.Connections.Select(c => c.Mode).ToList();
            var offered = ConnectionModes.Offered(declared, apiModeOffered && carries);
            agent.Connections = ConnectionChoiceDto.For(offered, agent.DisplayName, ConnectionModes.DefaultOf(agent.Type, offered));
        }
        FileLog.Write($"[ConnectionChoiceFold] Offer: {agents.Count} agent(s), apiModeOffered={apiModeOffered}, " +
                      $"director={director?.DirectorId}, version={director?.Version}, carriesApiMode={carries}, " +
                      $"with API mode={agents.Count(a => a.Connections.Count > 1)}, " +
                      $"API mode by default={agents.Count(a => a.Connections.Any(c => c.IsDefault && c.Mode == ConnectionModes.Api))}");
        return agents;
    }

    /// <summary>
    /// Settle a create that asks for the default connection (<see cref="NewSessionRequest.DefaultConnection"/>) and names
    /// none: its <see cref="NewSessionRequest.ConnectionMode"/> becomes <c>api</c> when <see cref="ConnectionModes.DefaultOf"/>
    /// rules API mode for this agent and <paramref name="director"/> carries it (<see cref="CarriesApiMode"/>, the one
    /// version rule the offer reads), and stays absent - terminal - otherwise, so an older Director gets the body it
    /// always got. API mode is taken as offered for every agent while <paramref name="apiModeOffered"/> is on: whether
    /// this agent's wrapper has it is the Director's to answer, and it refuses in words. A create that names a mode, or
    /// does not ask, is left as it is. Mutates <paramref name="req"/>.
    /// </summary>
    public static void SettleDefault(NewSessionRequest req, bool apiModeOffered, DirectorDto? director)
    {
        if (req.DefaultConnection != true || !string.IsNullOrEmpty(req.ConnectionMode)) return;
        var offered = apiModeOffered && CarriesApiMode(director)
            ? new[] { ConnectionModes.Terminal, ConnectionModes.Api }
            : new[] { ConnectionModes.Terminal };
        var mode = ConnectionModes.DefaultOf(req.Agent, offered);
        if (mode == ConnectionModes.Api) req.ConnectionMode = ConnectionModes.Api;
        FileLog.Write($"[ConnectionChoiceFold] SettleDefault: agent={req.Agent}, director={director?.DirectorId ?? "(unknown)"}, " +
                      $"version={director?.Version ?? "(none)"}, mode={mode}");
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

    /// <summary>The first Director release that carries the whole of API mode. The create field and the API-mode code first
    /// shipped in v2.23.0, but v2.23.0 has no event push (#4090), and v2.24.0 does not have the terminal following the
    /// wheel (#4234), so API mode only half works on either. v2.25.0 is the first with all of it.</summary>
    internal static readonly Version FirstDirectorThatCarriesApiMode = new(2, 25, 0);

    /// <summary>Whether <paramref name="director"/> is a release that carries API mode. A version that cannot be read is
    /// not: whether it carries the mode cannot be told. The one version rule, read by the offer and by every guard.</summary>
    internal static bool CarriesApiMode(DirectorDto? director)
        => director is not null && SpawnFactory.ReleaseOf(director.Version) is { } version && version >= FirstDirectorThatCarriesApiMode;

    /// <summary>
    /// The refusal for an API-mode create going to a Director too old to honour it, or null when the create may go. An
    /// older Director has no such field, drops it without a word, and starts the agent in its terminal - a session
    /// started as something nobody asked for, and the caller is never told (the phase 6 review, finding 1). So the
    /// create is refused before it leaves, with a sentence naming the version needed. A Director whose version cannot be
    /// read is refused too: whether it carries the mode cannot be told. The same shape as the factory field's guard
    /// (<see cref="SpawnFactory.DirectorCannotCarry(NewSessionRequest, DirectorDto)"/>).
    /// </summary>
    internal static string? DirectorCannotCarry(NewSessionRequest req, DirectorDto director)
    {
        if (req?.ConnectionMode != ConnectionModes.Api || director is null) return null;
        if (CarriesApiMode(director)) return null;

        var name = string.IsNullOrWhiteSpace(director.MachineName) ? director.DirectorId : director.MachineName;
        var shown = string.IsNullOrWhiteSpace(director.Version) ? "a version it does not report" : $"version {director.Version.Trim()}";
        FileLog.Write($"[ConnectionChoiceFold] REFUSED an API-mode create to Director {director.DirectorId} on {name}: {shown} is older than {FirstDirectorThatCarriesApiMode}");
        return $"The Director on {name} is {shown}, which cannot start a session in API mode, so no session was started. " +
               $"Update this Director to {FirstDirectorThatCarriesApiMode.ToString(3)} or later, or start the session in terminal mode.";
    }

    /// <summary>
    /// <see cref="DirectorCannotCarry(NewSessionRequest, DirectorDto)"/> for a door that knows the Director only by id
    /// (the machine door, which learns it from the resolver). A door with no way to read the Director, or an id the
    /// account does not hold, is refused for an API-mode create: what it cannot read it cannot vouch for.
    /// </summary>
    internal static string? DirectorCannotCarry(NewSessionRequest req, string directorId, Func<string, DirectorDto?>? directorOf)
    {
        if (req?.ConnectionMode != ConnectionModes.Api) return null;
        if (directorOf?.Invoke(directorId) is { } director) return DirectorCannotCarry(req, director);
        FileLog.Write($"[ConnectionChoiceFold] REFUSED an API-mode create to Director {directorId}: its version cannot be read here");
        return $"The Gateway cannot read which version Director {directorId} is, so it cannot tell whether that Director can " +
               "start a session in API mode, and no session was started.";
    }
}
