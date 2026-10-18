using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// WHAT A RULE IS AND WHAT A FIRING IS, IN ONE PLACE.
///
/// These invariants used to live in <see cref="SessionRuleStore"/> alone. The write gate in
/// <c>GatewayDbContext.SaveChanges</c> then grew up beside them and checked FOUR things - the tenant, the
/// call document, the initial dry-run state and the promotion marker - so a rule with no instruction, no
/// screen description, no trigger words, no cooldown and no daily cap went straight through it while the
/// store refused every one of those. Two boundaries disagreeing about what a rule is means one of them is
/// not a boundary, and the independent inspection of landing B found exactly that.
///
/// So there is one implementation and two call sites. The store calls it early, before it touches the
/// database, so a refusal costs nothing and reads as a refusal about the CALL; the gate calls it on every
/// save, so a caller that went round the store meets the same rules. Neither can drift from the other,
/// because there is nothing to drift from.
///
/// The refusals are written for a person to read. A rule that cannot be stored and does not say why is
/// worse than no rule at all.
/// </summary>
internal static class SessionRuleRecordRules
{
    /// <summary>The decisions a firing may record - the closed set the evaluator uses, so a record can be
    /// read rather than interpreted. Built from the constants themselves, never typed out a second
    /// time.</summary>
    internal static readonly IReadOnlyList<string> KnownDecisions = new[]
    {
        RuleDecisions.Act, RuleDecisions.Decline, RuleDecisions.Abandoned, RuleDecisions.Refused,
    };

    /// <summary>
    /// Everything a stored rule has to be, whichever route wrote it. It does NOT check the state or the
    /// tenant: those depend on how the write arrived (a new rule, a promotion, which connection), and they
    /// are checked where that is known.
    /// </summary>
    /// <exception cref="RuleRejectedException">Something about the rule is not something that can be
    /// stored; the reason says what and why.</exception>
    internal static void CheckRule(SessionRuleEntity rule, RulePrimitiveRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(rule.Instruction))
            throw new RuleRejectedException(
                "a rule is the sentence you said, so it cannot be empty - the instruction is the authority.");

        // KIND-AWARE (Tech Lead ruling P7). A screen rule needs a screen description, words and ceilings; a
        // hook rule needs an event, a tool, a handler and a result, and none of the screen fields mean
        // anything for it. Demanding the other kind's fields would make every hook rule unwritable, and
        // accepting them would store something that silently does nothing.
        switch (rule.Kind)
        {
            case RuleKinds.Screen:
                CheckAppliesTo(rule);
                CheckScreenRule(rule, registry);
                return;
            case RuleKinds.Hook:
                CheckAppliesTo(rule);
                CheckHookRule(rule);
                return;
            case RuleKinds.SessionStart:
                throw new RuleRejectedException(
                    "session start rules are not built yet - they arrive in phase 5. A rule of a kind nothing " +
                    "runs would be stored and never fire.");
            default:
                throw new RuleRejectedException(
                    $"'{rule.Kind}' is not a kind of rule this build knows. A rule is one of: " +
                    string.Join(", ", RuleKinds.All) + ".");
        }
    }

    /// <summary>
    /// Where a rule applies has to be one of the four shapes ruling P4 names, and nothing in between. A shape
    /// that is half one thing and half another has no single meaning, so it is refused rather than read one
    /// way here and another way by the hook.
    /// </summary>
    private static void CheckAppliesTo(SessionRuleEntity rule)
    {
        var kind = rule.AppliesToKind ?? "";
        var machines = rule.AppliesToMachines ?? new List<string>();
        var except = rule.AppliesToExceptMachines ?? new List<string>();
        var repo = rule.AppliesToRepo ?? "";

        if (!RuleAppliesToKinds.All.Contains(kind, StringComparer.Ordinal))
            throw new RuleRejectedException(
                $"'{kind}' is not a place a rule can apply. A rule applies to one of: " +
                string.Join(", ", RuleAppliesToKinds.All) + ".");

        if (machines.Concat(except).Any(m => string.IsNullOrWhiteSpace(m) || m != m.Trim()))
            throw new RuleRejectedException(
                "every machine a rule names has to be a machine name, as the Director reports it, with no " +
                "blank names and no spaces at either end.");

        if (repo.Length > 0 && !string.Equals(GitRemoteIdentity.Normalise(repo), repo, StringComparison.Ordinal))
            throw new RuleRejectedException(
                $"the repository '{repo}' is not in its one spelling ('{GitRemoteIdentity.Normalise(repo)}'). " +
                "A repository is compared as text, so one written any other way would never match and the rule " +
                "would silently never fire.");

        switch (kind)
        {
            case RuleAppliesToKinds.Everywhere:
                if (machines.Count > 0 || except.Count > 0 || repo.Length > 0)
                    throw new RuleRejectedException(
                        "a rule that applies everywhere names no machines and no repository - naming one means " +
                        "it applies somewhere narrower, so say which.");
                break;
            case RuleAppliesToKinds.Machine:
                if (repo.Length > 0)
                    throw new RuleRejectedException(
                        "a rule that applies on machines names no repository. A repository on a machine is " +
                        "'repo_on_machine'.");
                if (machines.Count == 0 && except.Count == 0)
                    throw new RuleRejectedException(
                        "a rule that applies on machines has to name them - either the machines it applies on, " +
                        "or the machines it does not.");
                if (machines.Count > 0 && except.Count > 0)
                    throw new RuleRejectedException(
                        "a rule names either the machines it applies on or the machines it does not, never " +
                        "both - the two together have no single meaning.");
                break;
            case RuleAppliesToKinds.Repo:
                if (repo.Length == 0)
                    throw new RuleRejectedException(
                        "a rule that applies in a repository has to name it, by its git remote.");
                if (machines.Count > 0 || except.Count > 0)
                    throw new RuleRejectedException(
                        "a rule that applies in a repository names no machines. A repository on some machines " +
                        "is 'repo_on_machine'.");
                break;
            case RuleAppliesToKinds.RepoOnMachine:
                if (repo.Length == 0)
                    throw new RuleRejectedException(
                        "a rule that applies in a repository on some machines has to name the repository, by " +
                        "its git remote.");
                if (machines.Count == 0)
                    throw new RuleRejectedException(
                        "a rule that applies in a repository on some machines has to name the machines.");
                if (except.Count > 0)
                    throw new RuleRejectedException(
                        "a rule that applies in a repository on some machines names the machines it applies " +
                        "on, not the ones it does not.");
                break;
        }
    }

    /// <summary>A hook rule (rulings P1, P2, P4 and P7).</summary>
    private static void CheckHookRule(SessionRuleEntity rule)
    {
        // ONE RULE, ONE SCOPE SYSTEM (P4). The September scope is the screen evaluator's session filter; a
        // hook rule says where it applies with appliesTo alone.
        if (rule.ScopeAgent is not null || rule.ScopeRepository is not null
            || rule.ScopeMachine is not null || rule.ScopeMission is not null)
            throw new RuleRejectedException(
                "a hook rule says where it applies with appliesTo, and leaves the screen rule's session scope " +
                "empty - one rule, one scope system.");

        if (rule.Calls is { Count: > 0 })
            throw new RuleRejectedException(
                "a hook rule cannot run checks yet - the Check handler arrives in phase 4. In phase 1 a hook " +
                "rule matches literal text.");

        if (!string.IsNullOrWhiteSpace(rule.ScreenDescription))
            throw new RuleRejectedException(
                "a hook rule does not watch the screen, so it has no screen description.");

        if (rule.TriggerWords is { Count: > 0 })
            throw new RuleRejectedException(
                "a hook rule does not watch the screen, so it has no trigger words - its match text is what it " +
                "looks for.");

        if (!string.Equals(rule.HookEvent, HookRuleWords.BeforeCommand, StringComparison.Ordinal))
            throw new RuleRejectedException(
                $"a hook rule listens for '{HookRuleWords.BeforeCommand}' in phase 1, and this one says " +
                $"'{rule.HookEvent}'. The other events (after an edit, when a session stops) arrive in phase 4.");

        if (!string.Equals(rule.HookTool, HookRuleWords.Shell, StringComparison.Ordinal))
            throw new RuleRejectedException(
                $"a hook rule polices the '{HookRuleWords.Shell}' tool in phase 1, and this one says " +
                $"'{rule.HookTool}'. Other tools arrive in phase 4.");

        if (!string.Equals(rule.HookHandler, HookRuleWords.Match, StringComparison.Ordinal))
            throw new RuleRejectedException(rule.HookHandler switch
            {
                "check" => "the Check handler arrives in phase 4. In phase 1 a hook rule uses 'match'.",
                "ask" => "the Ask handler (approve on the phone) arrives in phase 5. In phase 1 a hook rule uses 'match'.",
                "session" => "the Session handler arrives in phase 5. In phase 1 a hook rule uses 'match'.",
                _ => $"'{rule.HookHandler}' is not a handler. In phase 1 a hook rule uses '{HookRuleWords.Match}'.",
            });

        if (rule.HookMatch is null || rule.HookMatch.Count == 0)
            throw new RuleRejectedException(
                "a match rule needs at least one piece of text to match - the rule matches a command that " +
                "contains it.");

        if (rule.HookMatch.Any(string.IsNullOrWhiteSpace))
            throw new RuleRejectedException(
                "a match rule cannot match blank text: every command contains it, so the rule would match " +
                "everything.");

        if (!string.Equals(rule.HookResult, HookRuleWords.Deny, StringComparison.Ordinal)
            && !string.Equals(rule.HookResult, HookRuleWords.Allow, StringComparison.Ordinal))
            throw new RuleRejectedException(
                $"a hook rule's result is '{HookRuleWords.Deny}' or '{HookRuleWords.Allow}' in phase 1, and this " +
                $"one says '{rule.HookResult}'. Telling the agent and asking the owner arrive in phase 4 and 5.");

        // Live hook rules arrive in phase 4. The gate refuses one however it was written, so dry run is a
        // property of the store rather than of the route that happened to write the row.
        if (string.Equals(rule.State, LiveValue, StringComparison.Ordinal))
            throw new RuleRejectedException(
                "live hook rules arrive in phase 4. In phase 1 every hook rule stays in dry run and records " +
                "what it would have done.");
    }

    private static readonly string LiveValue = RuleWireNames.ToWireName(nameof(RuleState.Live));

    /// <summary>A screen rule - the September checks - which in phase 1 applies everywhere and carries no
    /// hook fields.</summary>
    private static void CheckScreenRule(SessionRuleEntity rule, RulePrimitiveRegistry registry)
    {
        // A screen rule may only apply everywhere in phase 1 (P4): the roster does not push a session's git
        // remote yet, so a narrower screen rule would silently never fire.
        if (!string.Equals(rule.AppliesToKind, RuleAppliesToKinds.Everywhere, StringComparison.Ordinal))
            throw new RuleRejectedException(
                "a screen rule applies everywhere in phase 1. The Gateway does not know a session's repository " +
                "remote yet, so a screen rule tied to a repository or a machine would silently never fire. " +
                "Use the scope to narrow which sessions it watches.");

        if (!string.IsNullOrEmpty(rule.HookEvent) || !string.IsNullOrEmpty(rule.HookTool)
            || !string.IsNullOrEmpty(rule.HookHandler) || !string.IsNullOrEmpty(rule.HookResult)
            || rule.HookMatch is { Count: > 0 })
            throw new RuleRejectedException(
                "a screen rule reads the screen, so it has no hook event, tool, handler, match text or result. " +
                "A rule that listens to a hook is a hook rule.");

        if (string.IsNullOrWhiteSpace(rule.ScreenDescription))
            throw new RuleRejectedException(
                "a rule has to say, in plain words, what it is watching for on the screen.");

        if (rule.TriggerWords is null || !rule.TriggerWords.Any(w => !string.IsNullOrWhiteSpace(w)))
            throw new RuleRejectedException(
                "a rule needs at least one word to watch for, or it would cost a model call on every " +
                "screen. The words are worked out from the instruction, not chosen by hand.");

        if (rule.CooldownSeconds <= 0)
            throw new RuleRejectedException(
                "a rule has to say how long to wait before acting on the same session again. " +
                "The ceiling is what makes a rule in a loop finite.");

        if (rule.DailyCap <= 0)
            throw new RuleRejectedException(
                "a rule has to say how many times a day it may act on one session. " +
                "The ceiling is what makes a rule in a loop finite.");

        var validation = RuleCallValidator.ValidateAll(rule.Calls, registry);
        if (!validation.IsValid) throw new RuleRejectedException(validation.Reason);
    }

    /// <summary>
    /// Everything a stored firing has to be, whichever route wrote it. THE RECORD IS THE PRODUCT: a row
    /// that cannot establish what happened is worse than no row, because a reader trusts it.
    /// </summary>
    /// <param name="firing">The row about to be written.</param>
    /// <param name="registry">The checks this build ships, so a firing cannot name one that does not exist.</param>
    /// <param name="ruleState">The stored state of the rule this firing is against, or null when there is
    /// no such rule.</param>
    /// <param name="ruleKind">The stored kind of that rule, or null when there is no such rule.</param>
    /// <param name="dryRunValue">The wire value for dry run.</param>
    /// <exception cref="RuleRejectedException">The row is not a record of anything; the reason says why.</exception>
    internal static void CheckFiring(
        SessionRuleFiringEntity firing, RulePrimitiveRegistry registry, string? ruleState, string? ruleKind,
        string dryRunValue)
    {
        if (ruleState is null)
            throw new RuleRejectedException(
                $"there is no rule with the id {firing.RuleId}, so this is a record of nothing.");

        // A FIRING IS OF THE SAME KIND AS ITS RULE. A screen firing records a screen and a model's reading of
        // it; a hook firing records a command the hook matched. One recorded against the other kind of rule
        // would be a record of something that rule cannot do.
        if (string.Equals(firing.Kind, RuleKinds.Hook, StringComparison.Ordinal))
        {
            if (!string.Equals(ruleKind, RuleKinds.Hook, StringComparison.Ordinal))
                throw new RuleRejectedException(
                    $"the rule {firing.RuleId} is a {ruleKind} rule, not a hook rule, so a hook decision cannot " +
                    "be recorded against it.");
            CheckHookFiring(firing);
            return;
        }

        if (!string.Equals(firing.Kind, RuleKinds.Screen, StringComparison.Ordinal))
            throw new RuleRejectedException(
                $"'{firing.Kind}' is not a kind of firing this build records. A firing is a screen firing or a " +
                "hook firing.");

        if (!string.Equals(ruleKind, RuleKinds.Screen, StringComparison.Ordinal))
            throw new RuleRejectedException(
                $"the rule {firing.RuleId} is a {ruleKind} rule; it does not read the screen, so a screen firing " +
                "cannot be recorded against it.");

        RequireSomething(firing.SessionId, "which session this fired on");
        RequireSomething(firing.Reason, "why it decided that - a decline with no reason is not a record of anything");
        RequireSomething(firing.Outcome, "what happened next");
        RequireSomething(firing.Grounding,
            "what checking the stated reason against this screen found. A firing that cannot say is a " +
            "firing where that check may never have run");

        var decision = (firing.Decision ?? "").Trim();
        if (!KnownDecisions.Contains(decision, StringComparer.Ordinal))
            throw new RuleRejectedException(
                $"'{decision}' is not a decision this build knows. A firing records one of: " +
                string.Join(", ", KnownDecisions) + ".");

        foreach (var run in firing.PrimitiveRuns ?? new List<RulePrimitiveRunEntity>())
        {
            if (run is null)
                throw new RuleRejectedException("a firing cannot record a check that is nothing at all.");
            if (registry.Find(run.Name) is null)
                throw new RuleRejectedException(
                    $"this firing says the check '{run.Name}' ran, and there is no such check. The record " +
                    "names what we ship: " + string.Join(", ", registry.Primitives.Select(p => p.Name)) + ".");
            if (string.IsNullOrWhiteSpace(run.Answer))
                throw new RuleRejectedException(
                    $"this firing says the check '{run.Name}' ran and does not say what it answered. A " +
                    "check with no answer changed no decision, so recording it as evidence would be a " +
                    "claim nobody can read.");
        }

        if (!string.IsNullOrEmpty(firing.TypedText)
            && string.Equals(ruleState, dryRunValue, StringComparison.Ordinal))
            throw new RuleRejectedException(
                "this rule is in dry run, so it types nothing - a firing cannot record it having typed '" +
                firing.TypedText + "'. Promote the rule first.");
    }

    /// <summary>
    /// A hook firing (wire contract W5): the decision the hook made, forwarded by the Director. Phase 1 is
    /// dry run, so the decision is one of the "would" decisions and nothing was typed or blocked.
    /// </summary>
    private static void CheckHookFiring(SessionRuleFiringEntity firing)
    {
        if (string.IsNullOrWhiteSpace(firing.DecisionId))
            throw new RuleRejectedException(
                "a hook firing has to carry the hook's decision id - it is what tells a retried post from a " +
                "second decision.");

        RequireSomething(firing.SessionId, "which session this fired on");
        RequireSomething(firing.Reason, "why - for a hook rule, the rule's own sentence");
        RequireSomething(firing.Outcome, "what happened next");
        RequireSomething(firing.Machine, "which machine the hook ran on");

        // EVERY VALUE FITS ITS COLUMN, checked here rather than left to the database. SQLite does not enforce a
        // length and PostgreSQL does, so a value too long for its column would pass every local test and then
        // fail the hosted save as a server error - for the whole post, not for this one decision. The lengths
        // are the model's (GatewayDbContext); this is the one place a forwarded decision is refused by id.
        FitsItsColumn("decisionId", firing.DecisionId, 64);
        FitsItsColumn("sessionId", firing.SessionId, 64);
        FitsItsColumn("machine", firing.Machine, 256);
        FitsItsColumn("repo", firing.Repo, 512);
        FitsItsColumn("agent", firing.Agent, 64);
        FitsItsColumn("agentEvent", firing.AgentEvent, 64);
        FitsItsColumn("agentTool", firing.AgentTool, 64);
        FitsItsColumn("subject", firing.Subject, HookRuleWords.SubjectMaxLength);

        if (!HookRuleWords.Decisions.Contains(firing.Decision ?? "", StringComparer.Ordinal))
            throw new RuleRejectedException(
                $"'{firing.Decision}' is not a decision a phase 1 hook makes. Every hook rule is in dry run, so a " +
                "hook firing records one of: " + string.Join(", ", HookRuleWords.Decisions) + ".");

        if (!string.Equals(firing.HookEvent, HookRuleWords.BeforeCommand, StringComparison.Ordinal))
            throw new RuleRejectedException(
                $"a phase 1 hook fires on '{HookRuleWords.BeforeCommand}', and this decision says " +
                $"'{firing.HookEvent}'.");

        if (!string.Equals(firing.HookTool, HookRuleWords.Shell, StringComparison.Ordinal))
            throw new RuleRejectedException(
                $"a phase 1 hook polices the '{HookRuleWords.Shell}' tool, and this decision says '{firing.HookTool}'.");

        if (string.IsNullOrWhiteSpace(firing.Subject))
            throw new RuleRejectedException(
                "a hook firing records the command its rule matched, and this one records no command.");

        if (!string.IsNullOrEmpty(firing.TypedText))
            throw new RuleRejectedException("a hook rule types nothing, so a hook firing cannot record having typed.");

        if (firing.PrimitiveRuns is { Count: > 0 })
            throw new RuleRejectedException(
                "a phase 1 hook rule runs no checks, so a hook firing cannot record one having run.");
    }

    /// <summary>Refuse a hook decision value longer than the column that stores it, naming the field.</summary>
    private static void FitsItsColumn(string field, string? value, int maxLength)
    {
        if (value is not null && value.Length > maxLength)
            throw new RuleRejectedException(
                $"{field} is at most {maxLength} characters, and this decision's is {value.Length}.");
    }

    /// <summary>Refuse a required part of the record that is missing, saying what it was for.</summary>
    private static void RequireSomething(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new RuleRejectedException(
                "the record is the product, so a firing has to say " + what + ".");
    }
}
