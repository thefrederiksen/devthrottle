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
/// plan does not hold. Whether a session's owner holds it is decided by <see cref="TenantAiGate"/> (Pricing Change
/// mission, B1), the one place every AI call asks; this holds only what is said in place of a narration.
/// </summary>
public static class NarrationPlanRule
{
    /// <summary>What an account without the Wingman on its plan reads and hears in place of a narration.</summary>
    public const string NeedsProText =
        "A full narration of each stop needs a Pro account. Upgrade to Pro to get the Wingman's complete explanation of what this session did and what it needs.";
}
