using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// PRO CARRIES FACTORIES INTO THE TEAMS ITS OWNER OWNS (owner ruling, 9 November 2026: "Yes, Pro carries factories into
/// my teams."). In a team's account, factories are allowed when the team's Owner's OWN plan includes
/// <see cref="EntitlementScopes.Factories"/> - Pro, or a running trial, which the entitlement read already counts as Pro.
/// No team bill is needed.
///
/// THE ONE QUESTION: is <paramref name="tenant"/> a team, and if so does its Owner's own plan include factories? Asked
/// only by <see cref="Factory.FactoryAgentsSwitch"/>, which every factory reader goes through, and which combines this
/// answer with the team's bill by OR.
///
/// READ LIVE, NEVER COPIED. Each question reads the team table, the Owner's membership row and the Owner's plan, so an
/// Owner who loses Pro turns the team's factories off on the very next question - nothing is cached and nothing is
/// written to the team. The plan is read through <see cref="TeamOwnerPlan"/>, the same reader piece 1.6's join gate uses.
///
/// A READ THAT FAILS IS NOT A VERDICT. When the team or membership read throws, the answer is
/// <see cref="TeamOwnerPlanAnswer.Unknown"/> - never "not Pro" (it would turn a paying Owner's factories off) and never
/// "Pro" (it would hand the area away). A team with no Owner recorded has no Pro account behind it: not Pro, logged.
/// </summary>
public sealed class TeamOwnerFactories
{
    private readonly TeamRegistry _teams;
    private readonly TeamOwnerPlan _ownerPlan;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _log;

    /// <param name="teams">Whether a tenant is a team, and who its Owner is.</param>
    /// <param name="ownerPlan">The Owner's own plan read. The hosted Gateway's <see cref="TeamOwnerPlan.Hosted"/>.</param>
    /// <param name="utcNow">The clock the plan is judged against; the system clock when omitted.</param>
    /// <param name="log">Where the faults go; the Gateway log when null.</param>
    public TeamOwnerFactories(TeamRegistry teams, TeamOwnerPlan ownerPlan, Func<DateTime>? utcNow = null,
        Action<string>? log = null)
    {
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _ownerPlan = ownerPlan ?? throw new ArgumentNullException(nameof(ownerPlan));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log ?? FileLog.Write;
    }

    /// <summary>
    /// Null when <paramref name="tenant"/> is not a team's account (a personal account is never asked about an Owner).
    /// Otherwise whether the team's Owner's own plan includes factories, with the tier it named.
    /// </summary>
    /// <param name="quiet">True for a caller on a timer that asks the same question over and over: the verdict lines
    /// are not written. A read that FAILS is logged either way - that is a fault, not a repeated answer.</param>
    public TeamOwnerPlanDecision? Read(TenantId tenant, bool quiet = false)
    {
        string? owner;
        try
        {
            if (!_teams.IsTeam(tenant))
                return null;
            owner = _teams.OwnerSubjectOf(tenant.Value);
        }
        catch (Exception ex)
        {
            _log($"[TeamOwnerFactories] Read FAILED: tenant={tenant.ToLogString()}: {ex.GetType().Name}: {ex.Message} - the Owner's plan is Unknown");
            return new TeamOwnerPlanDecision(TeamOwnerPlanAnswer.Unknown, null);
        }

        if (string.IsNullOrWhiteSpace(owner))
        {
            if (!quiet) _log($"[TeamOwnerFactories] Read: team {tenant.ToLogString()} has NO Owner recorded, so no Pro account stands behind its factories");
            return new TeamOwnerPlanDecision(TeamOwnerPlanAnswer.NotPro, null);
        }

        return _ownerPlan.Decide(owner, _utcNow(), EntitlementScopes.Factories, quiet);
    }
}
