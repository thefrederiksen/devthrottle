using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The team bill a hosted test Gateway reads: the Gateway's own <c>team_bills</c> table (Teams v1, the team bill without
/// Stripe), which the Gateway's migrations create - so every team starts with no bill, and every member is on the free
/// tier until a test starts one.
/// </summary>
internal static class HostedTeamBill
{
    private const string Table = "team_bills";
    private const string HiddenTable = "team_bills_unreadable";

    /// <summary>Give a team a running bill: active, auto-renew on, for a period well into the future.</summary>
    public static void Start(GatewayHost gateway, string teamId, int seats = 1)
    {
        var now = DateTime.UtcNow;
        using var ctx = gateway.GatewayDatabaseForTests.CreateUnscopedContext();
        ctx.TeamBills.Add(new TeamBillEntity
        {
            TeamId = teamId,
            Status = EntitlementRegistry.StatusActive,
            Seats = seats,
            PricePerSeatCents = CcDirector.Gateway.Teams.TeamBillStore.PricePerSeatCents,
            PlanStartedUtc = now,
            CurrentPeriodStartUtc = now,
            CurrentPeriodEndUtc = now.AddYears(5),
            AutoRenew = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        });
        ctx.SaveChanges();
    }

    /// <summary>Move the table out of the way, so every read of a team's bill FAILS until <see cref="RestoreReads"/>.</summary>
    public static void BreakReads(GatewayHost gateway)
    {
        using var ctx = gateway.GatewayDatabaseForTests.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw($"ALTER TABLE {Table} RENAME TO {HiddenTable}");
    }

    /// <summary>Put the table back, rows and all, after <see cref="BreakReads"/>.</summary>
    public static void RestoreReads(GatewayHost gateway)
    {
        using var ctx = gateway.GatewayDatabaseForTests.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw($"ALTER TABLE {HiddenTable} RENAME TO {Table}");
    }

    /// <summary>Put a person on the free personal plan: no personal entitlement row (the hosted test Gateway seeds a
    /// hosted one for every account it binds).</summary>
    public static void MakePersonalFree(GatewayHost gateway, string subject)
    {
        using var ctx = gateway.GatewayDatabaseForTests.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("DELETE FROM entitlements WHERE subject = {0}", subject);
    }
}
