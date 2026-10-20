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
    /// <param name="dryRunValue">The wire value for dry run.</param>
    /// <exception cref="RuleRejectedException">The row is not a record of anything; the reason says why.</exception>
    internal static void CheckFiring(
        SessionRuleFiringEntity firing, RulePrimitiveRegistry registry, string? ruleState, string dryRunValue)
    {
        if (ruleState is null)
            throw new RuleRejectedException(
                $"there is no rule with the id {firing.RuleId}, so this is a record of nothing.");

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

    /// <summary>Refuse a required part of the record that is missing, saying what it was for.</summary>
    private static void RequireSomething(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new RuleRejectedException(
                "the record is the product, so a firing has to say " + what + ".");
    }

    /// <summary>
    /// Everything a stored change has to be. The history is how an account finds out which session changed one
    /// of its rules, so a row that does not say what was done, by whom, or what the rule was is refused rather
    /// than written - a reader trusts the row.
    /// </summary>
    /// <exception cref="RuleRejectedException">The row is not a record of a change; the reason says why.</exception>
    internal static void CheckChange(SessionRuleChangeEntity change)
    {
        if (!RuleChangeActions.All.Contains(change.Action, StringComparer.Ordinal))
            throw new RuleRejectedException(
                $"a change to a rule is one of {string.Join(", ", RuleChangeActions.All)}, and this one says " +
                $"'{change.Action}'.");

        if (string.IsNullOrWhiteSpace(change.ChangedBy))
            throw new RuleRejectedException(
                "a change to a rule has to say who made it. That is the whole of what the history is for.");

        var isCreate = change.Action == RuleChangeActions.Create;
        var isDelete = change.Action == RuleChangeActions.Delete;
        if (isCreate != string.IsNullOrEmpty(change.Before))
            throw new RuleRejectedException(isCreate
                ? "a rule that was just created had no earlier form, so its change record cannot carry one."
                : "a change record has to carry the rule as it was before the change.");
        if (isDelete != string.IsNullOrEmpty(change.After))
            throw new RuleRejectedException(isDelete
                ? "a rule that was deleted has no later form, so its change record cannot carry one."
                : "a change record has to carry the rule as it is after the change.");
    }
}
