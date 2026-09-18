using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Wingman;

/// <summary>Whether an account's plan includes the Wingman's narration.</summary>
public enum NarrationPlan
{
    /// <summary>The plan includes the Wingman, or this Gateway has no billing gate (self-host).</summary>
    Allowed,

    /// <summary>The entitlement read succeeded and the plan does not include the Wingman: the narration says a Pro
    /// account is needed, and no model call is made.</summary>
    NeedsPro,

    /// <summary>The plan could not be read. No call is made and nothing is said about the plan, because saying
    /// "you need Pro" to a paying customer on a database hiccup would be false.</summary>
    Unknown,
}

/// <summary>
/// THE NARRATION NEEDS A PRO ACCOUNT (owner ruling, 2026-09-17). The narration call is a model call on our meter,
/// and the plan table (<see cref="EntitlementScopes"/>) puts every such call behind the Wingman scope, which the free
/// plan does not hold. One rule, decided here and read by the verdict service; nothing else re-derives it.
/// </summary>
public static class NarrationPlanRule
{
    /// <summary>What an account without the Wingman on its plan reads and hears in place of a narration.</summary>
    public const string NeedsProText =
        "A full narration of each stop needs a Pro account. Upgrade to Pro to get the Wingman's complete explanation of what this session did and what it needs.";

    /// <summary>Decide the plan answer from what the entitlement read established.</summary>
    /// <param name="hosted">False on a self-host Gateway, which has no billing gate: always allowed.</param>
    /// <param name="subject">The account subject the tenant maps to, or null when it maps to none.</param>
    /// <param name="decision">The entitlement read for that subject, or null when there was no subject to read.</param>
    public static NarrationPlan Decide(bool hosted, string? subject, EntitlementDecision? decision)
    {
        if (!hosted) return NarrationPlan.Allowed;
        if (string.IsNullOrWhiteSpace(subject) || decision is null) return NarrationPlan.NeedsPro;
        return decision.Outcome switch
        {
            EntitlementOutcome.Entitled when EntitlementScopes.Grants(decision.Tier, EntitlementScopes.Wingman) => NarrationPlan.Allowed,
            EntitlementOutcome.Entitled or EntitlementOutcome.NotEntitled => NarrationPlan.NeedsPro,
            _ => NarrationPlan.Unknown,
        };
    }
}
