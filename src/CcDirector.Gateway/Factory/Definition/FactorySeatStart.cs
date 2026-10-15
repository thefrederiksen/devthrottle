using CcDirector.Core.Agents;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>The published definition of a defined factory, read once: the version and what it says.</summary>
public sealed record PublishedFactoryDefinition(int Version, FactoryDefinition Definition)
{
    /// <summary>The seat of this version with this id, compared exactly, or null when the version has none.</summary>
    public SeatDefinition? Seat(string? seat)
        => Definition.Seats.FirstOrDefault(s => string.Equals(s.Seat, seat?.Trim(), StringComparison.Ordinal));

    /// <summary>The seat's brief as a talk or a run is given it: this version's text, recorded as a definition version.</summary>
    public Talk.FactoryGivenBrief BriefOf(SeatDefinition seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        return new Talk.FactoryGivenBrief(Version, seat.Brief, FactoryBriefVersionKinds.Definition);
    }
}

/// <summary>
/// A person's override of a talk with a seat (Factory Definition mission, phase 5): the agent, model and machine the
/// person chose instead of the definition's. Each is null when the person left it as the definition says. A scheduled
/// or triggered start never carries one.
/// </summary>
public sealed record FactorySeatStartOverride(string? Agent, string? Model, string? Machine)
{
    /// <summary>True when the person chose nothing.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Agent) && string.IsNullOrWhiteSpace(Model) && string.IsNullOrWhiteSpace(Machine);

    /// <summary>The override in words, as the history row records it: "agent Codex, model x, machine Y, chosen by the
    /// person", naming only what was chosen.</summary>
    public string Words()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Agent)) parts.Add("agent " + Agent.Trim());
        if (!string.IsNullOrWhiteSpace(Model)) parts.Add("model " + Model.Trim());
        if (!string.IsNullOrWhiteSpace(Machine)) parts.Add("machine " + Machine.Trim());
        return string.Join(", ", parts) + ", chosen by the person";
    }
}

/// <summary>What <see cref="FactorySeatStart.Build"/> returns: the create, and the machine it goes to.</summary>
public sealed record FactorySeatStartBuilt(NewSessionRequest Request, string Machine);

/// <summary>
/// WHERE A SEAT START IS BUILT: ONE PLACE (Factory Definition mission, phase 5; design section 3). From a published
/// definition version, a seat of it and the start's own words (its name and first prompt), the create the Gateway
/// sends: agent and model from the seat, else the factory's defaults; the machine likewise; the repository the seat
/// works in by NAME (<see cref="NewSessionRequest.FactoryWorktree"/>), or <see cref="NewSessionRequest.FactoryHome"/>
/// for a seat with none; the seat, the version and a seat kit; and NO folder - the Director decides where the seat
/// runs from the name, never from a path the Gateway holds.
///
/// The cron starter and the talk call it for a defined factory, and phase 5b's check starts call it too, so a seat
/// started any way starts the same. Pure: it reads no store and no clock.
/// </summary>
public static class FactorySeatStart
{
    /// <summary>
    /// The create for one start of <paramref name="seat"/> of <paramref name="published"/>. A talk passes the person's
    /// <paramref name="byPerson"/> override, already allowed by <see cref="RefuseOverride"/>; it replaces the agent, model
    /// or machine it names and is recorded in words on <see cref="NewSessionRequest.FactoryStartOverride"/>. Who started
    /// it (origin), and the scheduled run's own settings, are the caller's to add.
    /// </summary>
    public static FactorySeatStartBuilt Build(PublishedFactoryDefinition published, SeatDefinition seat, string name,
        string prePrompt, FactorySeatStartOverride? byPerson = null)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(seat);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(prePrompt);
        var definition = published.Definition;
        var chosen = byPerson is { IsEmpty: false } ? byPerson : null;

        var agent = Chosen(chosen?.Agent) ?? seat.AgentOrDefault(definition.Defaults);
        var model = Chosen(chosen?.Model) ?? seat.ModelOrDefault(definition.Defaults);
        var machine = Chosen(chosen?.Machine) ?? seat.MachineOrDefault(definition.Defaults);
        var repository = Chosen(seat.Repository);

        var request = new NewSessionRequest
        {
            // No folder: the seat works in its repository, resolved by name on its machine, or in its kit's work folder.
            RepoPath = "",
            Agent = agent,
            Model = model,
            Name = name,
            PrePrompt = prePrompt,
            Factory = definition.Factory,
            FactorySeat = seat.Seat,
            FactoryDefinitionVersion = published.Version,
            FactoryKit = FactoryKits.Seat,
            FactoryWorktree = repository,
            FactoryHome = repository is null ? true : null,
            FactoryStartOverride = chosen?.Words(),
        };
        FileLog.Write($"[FactorySeatStart] Build: {definition.Factory}/{seat.Seat} v{published.Version} on {machine}, agent={agent}, model={model ?? "(agent default)"}, "
                      + (repository is null ? "home (no repository)" : $"repository {repository}")
                      + (chosen is null ? "" : $", override: {chosen.Words()}"));
        return new FactorySeatStartBuilt(request, machine);
    }

    /// <summary>
    /// Why a person's override of a talk with <paramref name="seat"/> cannot be taken, or null when it can. Asked
    /// before anything is sent. An agent that is not one a seat can run is refused by name; an agent that cannot take
    /// the seat's rules, or load its factory skills, is refused with that agent's own reason, because a seat whose
    /// limits do not bind is worse than a seat that did not start (rule A4). A blank model or machine is "as the
    /// definition says", not a choice.
    /// </summary>
    public static string? RefuseOverride(SeatDefinition seat, FactorySeatStartOverride? byPerson)
    {
        ArgumentNullException.ThrowIfNull(seat);
        if (byPerson is null || string.IsNullOrWhiteSpace(byPerson.Agent)) return null;

        var word = byPerson.Agent.Trim();
        var known = AgentWord(word);
        if (known is null)
            return $"'{word}' is not an agent a seat can run. Choose one of: {string.Join(", ", FactorySkillSupport.SupportedAgents)}.";

        var agent = Enum.Parse<AgentKind>(known);
        if (seat.Rules.Count > 0 && FactoryRulesSupport.For(agent) is { CanTake: false } rules)
            return rules.Reason;
        if (seat.FactorySkills.Count > 0 && FactorySkillSupport.For(agent) is { CanLoad: false } skills)
            return skills.Reason;
        return null;
    }

    /// <summary>
    /// The published definition of <paramref name="factory"/> in one account, or null when the factory is not defined
    /// (no version of it is published) - then it takes every path it had before. A published version that does not
    /// parse cannot start a seat, and that is said in words: a run is never started from a definition nobody can read.
    /// </summary>
    /// <exception cref="InvalidOperationException">The published version cannot be found or read; the message says
    /// which, in words a person can act on.</exception>
    public static PublishedFactoryDefinition? ReadPublished(FactoryDefinitionStore store, TenantId tenant, string? factory)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(factory)) return null;
        var id = factory.Trim();
        if (store.Standing(tenant, id).Published is not { } published) return null;

        if (store.Find(tenant, id, published.Version) is not { } version)
            throw new InvalidOperationException($"version {published.Version} of the definition of '{id}' is published but cannot be found");
        var read = FactoryDefinitionCheck.Read(version.Files);
        if (read.Definition is not { } definition)
        {
            FileLog.Write($"[FactorySeatStart] ReadPublished: {id} v{published.Version} does not parse: {string.Join(" | ", read.Findings.Where(f => f.IsError).Select(f => $"{f.File}:{f.Line} {f.Message}"))}");
            throw new InvalidOperationException($"version {published.Version} of the definition of '{id}' is published but cannot be read; publish a version that passes the check");
        }
        FileLog.Write($"[FactorySeatStart] ReadPublished: {id} v{published.Version}");
        return new PublishedFactoryDefinition(published.Version, definition);
    }

    /// <summary>The refusal for a seat the published version does not hold.</summary>
    public static string SeatNotInVersion(PublishedFactoryDefinition published, string seat)
    {
        ArgumentNullException.ThrowIfNull(published);
        return $"the seat '{seat}' is not in version {published.Version} of the definition of '{published.Definition.Factory}', "
               + "the published one, so no session was started. Publish a definition that holds it, or use one of its seats: "
               + string.Join(", ", published.Definition.Seats.Select(s => s.Seat)) + ".";
    }

    /// <summary>
    /// The override as the person sent it, made one spelling: each value trimmed, a blank one null, and the agent in the
    /// spelling a definition uses ("codex" is Codex). Null when the person chose nothing. An agent no seat can run is kept
    /// as written, for <see cref="RefuseOverride"/> to refuse by name.
    /// </summary>
    public static FactorySeatStartOverride? Normalize(FactorySeatStartOverride? byPerson)
    {
        if (byPerson is null || byPerson.IsEmpty) return null;
        var agent = Chosen(byPerson.Agent);
        return new FactorySeatStartOverride(agent is null ? null : AgentWord(agent) ?? agent, Chosen(byPerson.Model), Chosen(byPerson.Machine));
    }

    private static string? AgentWord(string word)
        => FactorySkillSupport.SupportedAgents.FirstOrDefault(a => string.Equals(a, word, StringComparison.OrdinalIgnoreCase));

    private static string? Chosen(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
