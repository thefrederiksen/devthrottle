namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// A factory's definition, parsed (Factory Definition, phase 1). Built only from a content map whose files parse and
/// pass their schemas. Plain records, every list in a fixed order (seats, grants, rule sets, skills and paths sorted
/// ordinally; lists a person wrote, such as a seat's grants or a rule set's rules, kept in the order written), so two
/// definitions can be compared field by field for "what changes". Values are kept AS WRITTEN: a seat that names no
/// machine has a null <see cref="SeatDefinition.Machine"/>, and <see cref="SeatDefinition.MachineOrDefault"/> says
/// where it runs.
/// </summary>
public sealed record FactoryDefinition(
    string Factory,
    string Title,
    string? Purpose,
    string? GoalText,
    string? GoalApprovedOn,
    string? BossSeat,
    FactoryDefinitionDefaults Defaults,
    IReadOnlyList<string> Repositories,
    IReadOnlyList<SeatDefinition> Seats,
    IReadOnlyList<GrantDefinition> Grants,
    IReadOnlyList<RuleSetDefinition> RuleSets,
    IReadOnlyList<FactorySkillDefinition> Skills,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Checks);

/// <summary>What a seat runs on when it names nothing itself.</summary>
public sealed record FactoryDefinitionDefaults(string Machine, string Agent, string? Model);

/// <summary>One seat, from <c>seats/&lt;seat&gt;.yaml</c>.</summary>
public sealed record SeatDefinition(
    string Seat,
    string Name,
    string Role,
    string? Line,
    string? Machine,
    string? Agent,
    string? Model,
    string? Repository,
    string Brief,
    IReadOnlyList<SeatStartDefinition> Starts,
    IReadOnlyList<string> Grants,
    IReadOnlyList<string> Rules,
    IReadOnlyList<string> Secrets,
    IReadOnlyList<string> FactorySkills,
    IReadOnlyList<string> FleetSkills,
    IReadOnlyList<string> Files)
{
    /// <summary>The machine the seat runs on: its own, else the factory's default.</summary>
    public string MachineOrDefault(FactoryDefinitionDefaults defaults) => Machine ?? defaults.Machine;

    /// <summary>The agent the seat runs: its own, else the factory's default.</summary>
    public string AgentOrDefault(FactoryDefinitionDefaults defaults) => Agent ?? defaults.Agent;

    /// <summary>The model the seat runs: its own, else the factory's default (null when neither names one).</summary>
    public string? ModelOrDefault(FactoryDefinitionDefaults defaults) => Model ?? defaults.Model;
}

/// <summary>
/// One way a seat starts: at a time (<see cref="Cron"/> in <see cref="TimeZone"/>) or on an interval
/// (<see cref="Every"/>, also given in seconds), with an optional check and the prompt.
/// </summary>
public sealed record SeatStartDefinition(
    string? Cron,
    string? TimeZone,
    string? Every,
    int? EverySeconds,
    string? Check,
    IReadOnlyList<string> Secrets,
    string Prompt,
    bool PreventOverlap);

/// <summary>One grant from <c>grants.yaml</c>.</summary>
public sealed record GrantDefinition(string Id, string Allows, string? Limits, IReadOnlyList<string> Seats);

/// <summary>One rule set, <c>rules/&lt;name&gt;.json</c>: the permission rules it holds.</summary>
public sealed record RuleSetDefinition(string Name, IReadOnlyList<string> Allow, IReadOnlyList<string> Deny, IReadOnlyList<string> Ask);

/// <summary>One factory skill, <c>skills/&lt;name&gt;/SKILL.md</c>, and the paths of the files beside it.</summary>
public sealed record FactorySkillDefinition(string Name, string Description, IReadOnlyList<string> Files);
