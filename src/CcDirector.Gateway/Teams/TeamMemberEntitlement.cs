using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// THE PAID-FEATURES ANSWER FOR ONE PERSON IN A TEAM'S TENANT (devthrottle_internal#2311, Gateway step 2; #2299). Inside
/// a team the answer comes from the TEAM's bill and the person's role there, never from the person's own subject: their
/// membership is read from <c>gateway.team_members</c> and handed to
/// <see cref="EntitlementRegistry.EvaluateTeamTenant"/>, which never refuses a member (free tier when the bill grants
/// nothing, team tier when it does) and answers a non-member as a refusal of the PERSON, never as the team being unpaid.
///
/// One place, asked by the access lease, the Wingman's narration plan and the start-up key reinstatement, so the three
/// cannot disagree. Whether a tenant IS a team is read from <c>gateway.teams</c> (<see cref="TeamRegistry.IsTeam"/>),
/// never inferred from a tenant having no personal subject.
/// </summary>
public sealed class TeamMemberEntitlement
{
    private readonly TeamRegistry _teams;
    private readonly EntitlementRegistry _entitlements;

    public TeamMemberEntitlement(TeamRegistry teams, EntitlementRegistry entitlements)
    {
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));
    }

    /// <summary>Whether <paramref name="tenant"/> is a team's tenant, read from the teams table.</summary>
    public bool IsTeam(TenantId tenant) => _teams.IsTeam(tenant);

    /// <summary>
    /// The decision for <paramref name="accountSubject"/> in the team <paramref name="team"/>. A membership read that
    /// fails throws, and the caller answers that as Unknown - never as "not a member", which is a verdict.
    /// </summary>
    public TeamTenantDecision Decide(TenantId team, string accountSubject, DateTime nowUtc)
    {
        if (!team.IsValid)
            throw new ArgumentException("A team tenant is required.", nameof(team));
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("An account subject is required.", nameof(accountSubject));

        var role = _teams.RoleOf(team.Value, accountSubject);
        var membership = role is { } r ? TeamMembership.Member(TeamRoles.ToStored(r)) : TeamMembership.NotAMember;
        var decision = _entitlements.EvaluateTeamTenant(team.Value, membership, nowUtc);
        FileLog.Write($"[TeamMemberEntitlement] Decide: team {team.ToLogString()} member={decision.IsMember} " +
                      $"outcome={decision.Entitlement?.Outcome.ToString() ?? "none"} tier={decision.Entitlement?.Tier ?? "none"}");
        return decision;
    }

    /// <summary>
    /// <see cref="Decide"/>, with a membership read that FAILED answered as null - "not known", which every caller
    /// treats as no grant and no refusal (the lease's Unknown, reinstatement's "not a yes"). The one place that
    /// translation is made, as <see cref="TeamMembership"/> requires: a failed lookup is never "not a member".
    /// </summary>
    public TeamTenantDecision? DecideOrUnknown(TenantId team, string accountSubject, DateTime nowUtc)
    {
        try
        {
            return Decide(team, accountSubject, nowUtc);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            FileLog.Write($"[TeamMemberEntitlement] DecideOrUnknown: team {team.ToLogString()} - the membership read FAILED ({ex.GetType().Name}: {ex.Message}) - not known");
            return null;
        }
    }
}
