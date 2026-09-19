using CcDirector.Core.Utilities;

namespace CcDirector.Reclaim.Rules;

/// <summary>
/// Turns what a rule reported into the finding a reader acts on, and is the ONE place that decides
/// whether a rule's answer can be believed.
///
/// This is where the mission's hardest requirement lives: a rule that compares two lists and finds
/// one of them empty must report BROKEN, never "nothing to remove". The two produce the same empty
/// answer and only one of them is safe to act on - a comparison against a record set that failed to
/// load finds nothing every single time, and reads exactly like a clean disk.
///
/// It is folded here rather than in each rule for the same reason the Gateway folds a session's
/// display state rather than letting each client work it out: a rule that ruled on itself could
/// forget to, every new rule would have to remember, and the forgetting is invisible. One rule
/// cannot fail closed on its own; they all do, or the next one written will not.
/// </summary>
public static class RuleFold
{
    /// <summary>
    /// Fold one rule's answer into a finding, deciding the verdict and writing the finished sentences.
    /// </summary>
    /// <param name="rule">The rule that answered.</param>
    /// <param name="answer">What it reported.</param>
    public static RuleFinding Fold(IReclaimRule rule, RuleAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(answer);
        FileLog.Write($"[RuleFold] Fold: rule={rule.Id}, controls={answer.Controls.Count}, candidates={answer.Candidates.Count}");

        var brokenReason = BrokenReasonFor(rule, answer);
        var broken = brokenReason is not null;

        var finding = new RuleFinding
        {
            RuleId = rule.Id,
            RuleName = rule.Name,
            Proof = rule.Proof,
            Verdict = broken ? RuleVerdict.Broken : RuleVerdict.Ok,
            BrokenReason = brokenReason,
            WhatItRemoves = rule.WhatItRemoves,
            WhyItIsSafe = rule.WhyItIsSafe,
            WhatIsLost = rule.WhatIsLost,
            HowToGetItBack = rule.HowToGetItBack,
            AgeGateDays = rule.AgeGateDays,
            NeedsAdministrator = rule.NeedsAdministrator,
            CommandToRun = rule.CommandToRun,
            Controls = answer.Controls,

            // A broken rule offers nothing. Whatever it happened to collect before it discovered it
            // could not do its work is not a recommendation, and carrying it through would put items
            // in front of a reader under an answer the rule itself has disowned.
            Candidates = broken ? [] : answer.Candidates,
            Lines = []
        };

        var complete = finding with { Lines = RuleFinding.Describe(finding) };
        FileLog.Write($"[RuleFold] Fold done: rule={rule.Id}, verdict={complete.Verdict}, bytes={complete.CandidateBytes}");
        return complete;
    }

    private static string? BrokenReasonFor(IReclaimRule rule, RuleAnswer answer)
    {
        if (answer.CannotRunReason is not null)
            return answer.CannotRunReason;

        // A rule with no controls at all has told us nothing about whether it worked, so its empty
        // answer cannot be distinguished from a failure and is not accepted as one.
        if (answer.Controls.Count == 0)
        {
            return $"the rule {rule.Id} reported no controls, so there is no way to tell an answer of " +
                   "nothing to remove from a rule that could not do its work";
        }

        // A rule whose every control MAY be empty is a rule no count can ever alarm, so it can never
        // report broken, so its empty answer is always believed. That is the same failure as counting
        // nothing at all wearing different clothes, and it is the one the next rule written is most
        // likely to arrive in: phase 5 turns rules into data refreshed from the Gateway, and this fold
        // is the single gate a refreshed rule passes through. An empty result is a broken instrument
        // until proven otherwise, and a rule with no load-bearing control proves nothing.
        if (!answer.Controls.Any(control => control.MustNotBeEmpty))
        {
            return $"the rule {rule.Id} declared no control that must not be empty, so no count it " +
                   "reports can ever tell an answer of nothing to remove from a rule that could not " +
                   "do its work";
        }

        var empty = answer.Controls.Where(control => control.MustNotBeEmpty && control.Count == 0).ToList();
        if (empty.Count == 0) return null;

        var names = string.Join(", ", empty.Select(control => control.Name));
        return $"the rule {rule.Id} counted nought for {names}, which means it could not do its work " +
               "rather than that there is nothing to remove, so it offers nothing";
    }
}
