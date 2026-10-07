namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One TEAM's bill, owned and written by THIS Gateway (Teams v1, the team bill without Stripe; devthrottle_internal#2098).
/// The owner's ruling, 7 Oct 2026: "we're doing everything except actually charging, but we are pretending we're
/// charging." So a team has a real plan - a status, a seat count, a price per seat, a monthly period and an auto-renew
/// switch - and every period writes a charge line (<see cref="TeamBillChargeEntity"/>) that says what the period would
/// have cost and that nothing was charged.
///
/// THE ONE SOURCE OF A TEAM'S BILL. <see cref="Tenancy.EntitlementRegistry.ReadTeamBill"/> reads this table and only this
/// table. The website's <c>team_entitlements</c> table (<see cref="TeamEntitlementEntity"/>) stays mapped for the later
/// payment work and is read by nothing.
///
/// Written ONLY by <see cref="Teams.TeamBillStore"/>: the Owner's actions (start, renew, auto-renew, cancel), the seat
/// count when the team's paid members change, and the renewal pass at the end of each period.
///
/// GLOBAL, like <see cref="TeamEntity"/>: keyed by the team id, which IS the team's tenant id, so it carries no separate
/// tenant_id column and no query filter. It is read by the access lease and the renewal pass, which run before or across
/// any request's tenant.
/// </summary>
public sealed class TeamBillEntity
{
    /// <summary>The team (<see cref="TeamEntity.Id"/>), which is also the team's tenant id. Primary key. Logged only
    /// hashed.</summary>
    public string TeamId { get; set; } = "";

    /// <summary><c>active</c> or <c>canceled</c> (<see cref="Tenancy.EntitlementRegistry.StatusActive"/>,
    /// <see cref="Tenancy.EntitlementRegistry.StatusCanceled"/>). An active bill whose auto-renew is off is "ending": it
    /// stays active to the end of its period and the renewal pass then makes it canceled.</summary>
    public string Status { get; set; } = "";

    /// <summary>The paid seats on the bill: the team's Owner, Managers and Developers
    /// (<see cref="Tenancy.TeamSeatRoles"/>), recorded when they change and at each renewal.</summary>
    public int Seats { get; set; }

    /// <summary>The price of one seat for one month, in US cents (4900 = US$49.00).</summary>
    public int PricePerSeatCents { get; set; }

    /// <summary>When the current run of the plan began (UTC): when the Owner started it, or last renewed it after it
    /// ended. Every period is a whole number of months from this moment, so the billing day never drifts after a short
    /// month (<see cref="Teams.TeamBillStore.PeriodEnd"/>).</summary>
    public DateTime PlanStartedUtc { get; set; }

    /// <summary>When the current period began (UTC).</summary>
    public DateTime CurrentPeriodStartUtc { get; set; }

    /// <summary>When the current period ends (UTC): one calendar month after it began.</summary>
    public DateTime CurrentPeriodEndUtc { get; set; }

    /// <summary>Whether the bill rolls to the next month at <see cref="CurrentPeriodEndUtc"/>. Off means it ends then.</summary>
    public bool AutoRenew { get; set; }

    /// <summary>When the team's plan was first started (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the row last changed (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>A concurrency token, bumped by every write. Two Gateway processes on one database (a deploy) that both
    /// change the bill cannot both save: the second write's version no longer matches and the database refuses it.</summary>
    public int Version { get; set; }
}
