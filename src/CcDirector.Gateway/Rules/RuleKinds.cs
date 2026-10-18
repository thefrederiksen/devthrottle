using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// What starts a rule (mission decision D6). A SCREEN rule reads an idle session's terminal - the September
/// rule. A HOOK rule is asked by the agent's own hook before it does something. A SESSION START rule runs when
/// a session starts; the value exists so the store and the page can name it, and creating one is refused
/// until phase 5 builds it.
/// </summary>
public static class RuleKinds
{
    public const string Screen = "screen";
    public const string Hook = "hook";
    public const string SessionStart = "session_start";

    /// <summary>Every kind this build can name, in the order a page lists them.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Screen, Hook, SessionStart };
}

/// <summary>
/// The four places a rule can apply (mission decision D1, Tech Lead ruling P4), from the least specific to
/// the most. The most specific rule that matches decides (D2).
/// </summary>
public static class RuleAppliesToKinds
{
    public const string Everywhere = "everywhere";
    public const string Machine = "machine";
    public const string Repo = "repo";
    public const string RepoOnMachine = "repo_on_machine";

    /// <summary>Every place this build can name.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Everywhere, Machine, Repo, RepoOnMachine };

    /// <summary>How specific a place is (ruling P3): repo on machine 4, repo 3, machine 2, everywhere 1.</summary>
    /// <exception cref="ArgumentException">The kind is not one this build knows.</exception>
    public static int RankOf(string kind) => kind switch
    {
        RepoOnMachine => 4,
        Repo => 3,
        Machine => 2,
        Everywhere => 1,
        _ => throw new ArgumentException($"'{kind}' is not a place a rule can apply.", nameof(kind)),
    };
}

/// <summary>
/// The words a hook rule is written in (Tech Lead rulings P2 and P7). Phase 1 accepts exactly one event, one
/// tool and one handler; the other values are refused with the phase that brings them.
/// </summary>
public static class HookRuleWords
{
    /// <summary>The event: before the agent runs a command.</summary>
    public const string BeforeCommand = "before_command";

    /// <summary>The tool: the agent's shell.</summary>
    public const string Shell = "shell";

    /// <summary>The handler: literal text the command contains (ruling P1).</summary>
    public const string Match = "match";

    /// <summary>A rule result: the command would be refused.</summary>
    public const string Deny = "deny";

    /// <summary>A rule result: the command is allowed, which lets a more specific rule undo a broader deny.</summary>
    public const string Allow = "allow";

    /// <summary>A dry-run decision: a deny rule decided.</summary>
    public const string WouldDeny = "would_deny";

    /// <summary>A dry-run decision: an allow rule decided.</summary>
    public const string WouldAllow = "would_allow";

    /// <summary>The decisions a phase 1 hook firing may record. Everything is dry run, so nothing else exists.</summary>
    public static IReadOnlyList<string> Decisions { get; } = new[] { WouldDeny, WouldAllow };

    /// <summary>The longest command text a firing keeps (wire contract W3).</summary>
    public const int SubjectMaxLength = 4000;

    /// <summary>The most decisions one W5 post may carry - the Director's own batch size, so the contract holds
    /// at both ends. Decisions past it are refused by id with the reason.</summary>
    public const int MaxDecisionsPerPost = 100;
}

/// <summary>
/// Where a rule applies: everywhere, on some machines (or every machine except some), in one repository, or
/// in one repository on some machines (Tech Lead ruling P4). Machines are the names the Directors report,
/// compared without regard to case (P5); the repository is the one spelling of the <c>origin</c> remote (P6).
/// This is a NEW field beside the September <see cref="RuleScope"/>, which stays the screen evaluator's
/// session filter.
/// </summary>
public sealed record RuleAppliesTo(
    string Kind,
    IReadOnlyList<string> Machines,
    IReadOnlyList<string> ExceptMachines,
    string Repo)
{
    /// <summary>Every machine and every repository - what every rule written before phase 1 became.</summary>
    public static RuleAppliesTo Everywhere { get; } =
        new(RuleAppliesToKinds.Everywhere, Array.Empty<string>(), Array.Empty<string>(), "");

    /// <summary>Only these machines.</summary>
    public static RuleAppliesTo OnMachines(IEnumerable<string> machines) =>
        new(RuleAppliesToKinds.Machine, machines.ToList(), Array.Empty<string>(), "");

    /// <summary>Every machine except these.</summary>
    public static RuleAppliesTo ExceptOnMachines(IEnumerable<string> machines) =>
        new(RuleAppliesToKinds.Machine, Array.Empty<string>(), machines.ToList(), "");

    /// <summary>One repository, on any machine.</summary>
    public static RuleAppliesTo InRepo(string repo) =>
        new(RuleAppliesToKinds.Repo, Array.Empty<string>(), Array.Empty<string>(), repo);

    /// <summary>One repository, only on these machines.</summary>
    public static RuleAppliesTo InRepoOnMachines(string repo, IEnumerable<string> machines) =>
        new(RuleAppliesToKinds.RepoOnMachine, machines.ToList(), Array.Empty<string>(), repo);

    /// <summary>How specific this is (ruling P3). The most specific matching rule decides.</summary>
    public int Rank => RuleAppliesToKinds.RankOf(Kind);

    /// <summary>
    /// Whether this reaches a session on <paramref name="machine"/> in <paramref name="repo"/>. The repository
    /// is compared in its one spelling, so the caller may pass any form of the remote. A checkout with no
    /// remote has no repository, and no repository rule reaches it.
    /// </summary>
    public bool Reaches(string machine, string repo)
    {
        var theRepo = GitRemoteIdentity.Normalise(repo);
        var ruleRepo = GitRemoteIdentity.Normalise(Repo);
        return Kind switch
        {
            RuleAppliesToKinds.Everywhere => true,
            RuleAppliesToKinds.Machine => Machines.Count > 0
                ? Names(Machines, machine)
                : !Names(ExceptMachines, machine),
            RuleAppliesToKinds.Repo => theRepo.Length > 0 && string.Equals(theRepo, ruleRepo, StringComparison.Ordinal),
            RuleAppliesToKinds.RepoOnMachine => theRepo.Length > 0
                && string.Equals(theRepo, ruleRepo, StringComparison.Ordinal)
                && Names(Machines, machine),
            _ => throw new InvalidOperationException($"'{Kind}' is not a place a rule can apply."),
        };
    }

    private static bool Names(IReadOnlyList<string> machines, string machine) =>
        machines.Any(m => string.Equals(m.Trim(), (machine ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What a hook rule listens for and what it says: the event, the tool, the handler, the literal text it
/// matches and its result (Tech Lead rulings P1, P2 and P7). The rule's reason is its instruction sentence.
/// </summary>
public sealed record HookRuleTrigger(
    string Event,
    string Tool,
    string Handler,
    IReadOnlyList<string> Match,
    string Result);

/// <summary>A hook rule that reaches one machine and repository, with how specific it is (wire contract W1).</summary>
public sealed record EffectiveRule(SessionRule Rule, int Rank);

/// <summary>
/// One decision the hook made, as the Director forwards it (wire contract W3). Read from the wire by
/// <c>SessionRuleWire.ReadHookDecisions</c>; checked and recorded by <see cref="SessionRuleStore.RecordHookFirings"/>.
/// </summary>
public sealed record HookDecision(
    int Version,
    string DecisionId,
    string SessionId,
    DateTime OccurredUtc,
    string Agent,
    string Event,
    string AgentEvent,
    string Tool,
    string AgentTool,
    string Subject,
    string Machine,
    string Repo,
    string Decision,
    Guid DecidingRuleId,
    IReadOnlyList<Guid> OverriddenRuleIds,
    IReadOnlyList<Guid> MatchedRuleIds,
    long EvaluationMicroseconds);

/// <summary>A forwarded decision that was not recorded, and why (wire contract W5).</summary>
public sealed record HookFiringRefusal(string DecisionId, string Reason);

/// <summary>What became of a batch of forwarded decisions (wire contract W5).</summary>
public sealed record HookFiringsAnswer(int Accepted, int Duplicates, IReadOnlyList<HookFiringRefusal> Refused);
