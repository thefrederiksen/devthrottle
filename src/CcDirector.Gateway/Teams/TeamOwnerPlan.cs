using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Teams;

/// <summary>What <see cref="TeamOwnerPlan"/> concluded about one team owner's own plan.</summary>
public enum TeamOwnerPlanAnswer
{
    /// <summary>The owner's own plan carries <see cref="EntitlementScopes.Teams"/>, or this Gateway has no plans (self-host).</summary>
    Granted,

    /// <summary>The owner's plan was read and does not carry teams: the free plan, the remote plan, an ended plan, the
    /// self-host plan, or a plan this code does not know.</summary>
    NotPro,

    /// <summary>The plan could not be read. Neither a yes nor a no: the caller refuses for now and says to try again.</summary>
    Unknown,
}

/// <summary>
/// TEAMS COME WITH THE PRO ACCOUNT (Team implementation piece 1.6, devthrottle_internal#2790; owner decisions of 7 and 8
/// November 2026: "Teams are not a separate version or cost: they come with the pro account. Anyone with a pro account can
/// create a team and invite people." and "Contributors are free seats.").
///
/// THE ONE QUESTION: does this person's OWN plan carry <see cref="EntitlementScopes.Teams"/>? Asked of the person creating a
/// team, and of a team's owner whenever a contributor is added. Nothing here derives a tier: the plan is read through
/// <see cref="EntitlementRegistry.EvaluatePersonalTenant"/> - the same read the enrolment gate, the access lease and the
/// AI gate use, so a running Pro trial counts and a lapsed account reads as the free plan - and what the plan grants is
/// asked of <see cref="EntitlementScopes.Grants"/>, the one table that owns that question.
///
/// SELF-HOST IS NOT GATED, as with <see cref="TenantAiGate.SelfHost"/>: a self-hosted Gateway has no plans at all, and
/// its team routes answer that it has no teams anyway. <see cref="SelfHost"/> says so by name rather than by a null.
/// </summary>
public sealed class TeamOwnerPlan
{
    private readonly EntitlementRegistry? _entitlements;

    /// <summary>The plan reader a self-hosted Gateway uses: no plans, so every owner is granted.</summary>
    public static TeamOwnerPlan SelfHost { get; } = new(null);

    /// <param name="entitlements">The personal plan read. Null only through <see cref="SelfHost"/>.</param>
    private TeamOwnerPlan(EntitlementRegistry? entitlements) => _entitlements = entitlements;

    /// <summary>The reader a hosted Gateway uses, over its one entitlement registry.</summary>
    public static TeamOwnerPlan Hosted(EntitlementRegistry entitlements) =>
        new(entitlements ?? throw new ArgumentNullException(nameof(entitlements)));

    /// <summary>Whether this reader checks anything at all. False on a self-hosted Gateway.</summary>
    public bool IsGating => _entitlements is not null;

    /// <summary>The answer for <paramref name="accountSubject"/>'s own plan at <paramref name="nowUtc"/>. Never logs the subject.</summary>
    public TeamOwnerPlanAnswer Read(string accountSubject, DateTime nowUtc)
        => Decide(accountSubject, nowUtc, EntitlementScopes.Teams).Answer;

    /// <summary>
    /// Whether <paramref name="accountSubject"/>'s own plan carries <paramref name="scope"/> at <paramref name="nowUtc"/>,
    /// with the tier the plan read named. <see cref="Read"/> asks it for <see cref="EntitlementScopes.Teams"/>; a team's
    /// factories ask it for <see cref="EntitlementScopes.Factories"/> (<see cref="TeamOwnerFactories"/>). Never logs the
    /// subject.
    /// </summary>
    public TeamOwnerPlanDecision Decide(string accountSubject, DateTime nowUtc, string scope)
    {
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("An account subject is required.", nameof(accountSubject));
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("A scope name is required.", nameof(scope));
        if (_entitlements is null)
            return new TeamOwnerPlanDecision(TeamOwnerPlanAnswer.Granted, null);

        var decision = _entitlements.EvaluatePersonalTenant(accountSubject, nowUtc);
        var answer = decision.Outcome switch
        {
            EntitlementOutcome.Unknown => TeamOwnerPlanAnswer.Unknown,
            EntitlementOutcome.Entitled when EntitlementScopes.Grants(decision.Tier, scope) => TeamOwnerPlanAnswer.Granted,
            _ => TeamOwnerPlanAnswer.NotPro,
        };
        FileLog.Write($"[TeamOwnerPlan] Decide: scope={scope} outcome={decision.Outcome} tier={decision.Tier ?? "none"} answer={answer}");
        return new TeamOwnerPlanDecision(answer, decision.Tier);
    }
}

/// <summary>One answer from <see cref="TeamOwnerPlan.Decide"/>: the answer, and the tier the owner's plan read named
/// (null when nothing was read or it named none).</summary>
public sealed record TeamOwnerPlanDecision(TeamOwnerPlanAnswer Answer, string? Tier);
