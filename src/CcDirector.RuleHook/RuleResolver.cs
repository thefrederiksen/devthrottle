namespace CcDirector.RuleHook;

/// <summary>What the resolver decided for one command.</summary>
/// <param name="Decision"><c>would_deny</c>, <c>would_allow</c>, or null when no rule matched.</param>
/// <param name="DecidingRule">The rule that decided, or null when no rule matched.</param>
/// <param name="OverriddenRuleIds">Every other matching rule whose result the decision went against.</param>
/// <param name="MatchedRuleIds">Every rule that matched, in rules-file order.</param>
internal sealed record Resolution(
    string? Decision,
    HookRule? DecidingRule,
    IReadOnlyList<string> OverriddenRuleIds,
    IReadOnlyList<string> MatchedRuleIds)
{
    public static readonly Resolution NoMatch = new(null, null, [], []);
}

/// <summary>
/// THE P3 RESOLVER. In phase 1 this is the only place a hook decision is made.
///
/// A rule matches a command when the command text CONTAINS any of the rule's literal match strings,
/// compared ordinally - case-sensitive, no patterns (ruling P1). Among the rules that match, the most
/// specific scope decides (owner decision D2): repo on machine 4, repo 3, machine 2, everywhere 1. Two
/// matching rules at the top rank that disagree: deny wins. Everything is dry run in phase 1 (P2), so
/// the decision is <c>would_deny</c> or <c>would_allow</c> and nothing is ever blocked.
/// </summary>
internal static class RuleResolver
{
    public const string BeforeCommandEvent = "before_command";
    public const string ShellTool = "shell";
    public const string Deny = "deny";
    public const string Allow = "allow";
    public const string WouldDeny = "would_deny";
    public const string WouldAllow = "would_allow";

    /// <summary>The rank each scope kind carries (P3). The rules file carries the rank the Gateway
    /// computed; it is checked against this so a disagreement is an error, never a silent re-ranking.</summary>
    public static int RankFor(string? appliesTo) => appliesTo switch
    {
        "repo_on_machine" => 4,
        "repo" => 3,
        "machine" => 2,
        "everywhere" => 1,
        _ => throw new RulesFileException($"a rule has an unknown appliesTo '{appliesTo}'"),
    };

    /// <summary>
    /// Check a rules file before deciding anything with it, and return its rules. A file the hook
    /// cannot read correctly is an ERROR in the evaluation log, never "no rules": a silent no-match
    /// on a broken file is exactly the shape of defect this mission is not allowed to ship.
    /// </summary>
    public static IReadOnlyList<HookRule> Validate(RulesFile file)
    {
        if (file.Version != 1)
            throw new RulesFileException($"rules file version {file.Version} is not 1");
        if (file.Rules is null)
            throw new RulesFileException("rules file has no rules list");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in file.Rules)
        {
            if (string.IsNullOrEmpty(rule.Id))
                throw new RulesFileException("a rule has no id");
            if (!seen.Add(rule.Id))
                throw new RulesFileException($"rule {rule.Id} appears twice");
            var expected = RankFor(rule.AppliesTo);
            if (rule.Rank != expected)
                throw new RulesFileException($"rule {rule.Id} has rank {rule.Rank} but appliesTo '{rule.AppliesTo}' is rank {expected}");
            if (rule.Result is not (Deny or Allow))
                throw new RulesFileException($"rule {rule.Id} has an unknown result '{rule.Result}'");
            if (rule.State is not ("dry_run" or "live"))
                throw new RulesFileException($"rule {rule.Id} has an unknown state '{rule.State}'");
            if (rule.Match is null || rule.Match.Count == 0)
                throw new RulesFileException($"rule {rule.Id} has no match text");
            // An empty string is contained in every command, so it would match everything.
            if (rule.Match.Any(string.IsNullOrEmpty))
                throw new RulesFileException($"rule {rule.Id} has an empty match text");
        }
        return file.Rules;
    }

    /// <summary>Decide one shell command against already-validated rules.</summary>
    public static Resolution Decide(IReadOnlyList<HookRule> rules, string command)
    {
        // Rules for another event or tool are for another hook call (phase 4 brings more); they are
        // read and counted but never match a shell command.
        var matched = rules
            .Where(r => r.Event == BeforeCommandEvent && r.Tool == ShellTool)
            .Where(r => r.Match!.Any(text => command.Contains(text, StringComparison.Ordinal)))
            .ToList();
        if (matched.Count == 0)
            return Resolution.NoMatch;

        var topRank = matched.Max(r => r.Rank);
        var top = matched.Where(r => r.Rank == topRank).ToList();
        var result = top.Any(r => r.Result == Deny) ? Deny : Allow;
        // The first top-rank rule with the winning result, in rules-file order, so the same file always
        // names the same deciding rule.
        var deciding = top.First(r => r.Result == result);

        // OVERRIDDEN means the decision went against it: a lower-rank rule that disagrees, or a same-rank
        // allow that lost to deny. A matching rule that AGREES with the decision was not overridden - it is
        // in the matched list only - so the Rules page never says a repo deny "overrode" a global deny.
        var overridden = matched
            .Where(r => !ReferenceEquals(r, deciding) && r.Result != result)
            .Select(r => r.Id!)
            .ToList();

        return new Resolution(
            result == Deny ? WouldDeny : WouldAllow,
            deciding,
            overridden,
            matched.Select(r => r.Id!).ToList());
    }
}

/// <summary>A rules file the hook cannot decide with. Its message goes into the evaluation log.</summary>
internal sealed class RulesFileException(string message) : Exception(message);
