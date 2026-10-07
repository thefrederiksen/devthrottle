using System;
using System.Linq;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// Puts a team's bill straight into the Gateway's own <c>team_bills</c> table (Teams v1, the team bill without Stripe),
/// for a test whose subject is something that READS the bill - inviting, accepting, paid features - rather than the
/// Owner's actions that write it, which <see cref="TeamBillStore"/> tests drive for real.
/// </summary>
internal static class TeamBillSeed
{
    private static readonly DateTime Start = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>A running bill: active, auto-renew on, for a period well into the future.</summary>
    public static void Active(GatewayDatabase db, string teamId, int seats = 1) =>
        Put(db, teamId, EntitlementRegistry.StatusActive, seats, autoRenew: true);

    /// <summary>An ended bill: canceled.</summary>
    public static void Canceled(GatewayDatabase db, string teamId, int seats = 1) =>
        Put(db, teamId, EntitlementRegistry.StatusCanceled, seats, autoRenew: false);

    /// <summary>A bill with any status, replacing whatever the team had.</summary>
    public static void Put(GatewayDatabase db, string teamId, string status, int seats, bool autoRenew)
    {
        using var ctx = db.CreateUnscopedContext();
        var existing = ctx.TeamBills.FirstOrDefault(b => b.TeamId == teamId);
        if (existing is not null)
            ctx.TeamBills.Remove(existing);
        ctx.SaveChanges();
        ctx.TeamBills.Add(new TeamBillEntity
        {
            TeamId = teamId,
            Status = status,
            Seats = seats,
            PricePerSeatCents = TeamBillStore.PricePerSeatCents,
            PlanStartedUtc = Start,
            CurrentPeriodStartUtc = Start,
            CurrentPeriodEndUtc = Start.AddYears(5),
            AutoRenew = autoRenew,
            CreatedAtUtc = Start,
            UpdatedAtUtc = Start,
            Version = 1,
        });
        ctx.SaveChanges();
    }

    /// <summary>Take the team's bill away: the team has no bill.</summary>
    public static void Remove(GatewayDatabase db, string teamId)
    {
        using var ctx = db.CreateUnscopedContext();
        var existing = ctx.TeamBills.FirstOrDefault(b => b.TeamId == teamId);
        if (existing is null)
            return;
        ctx.TeamBills.Remove(existing);
        ctx.SaveChanges();
    }
}
