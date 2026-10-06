using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The team bill table a hosted test Gateway reads (devthrottle_internal#2299, #2311). The website owns and creates it in
/// production; here it is created with the columns the seam states, so a team key's request reads a team's bill rather
/// than a table that is not there - which is a FAILED read, answered 503 for a paid seat.
/// </summary>
internal static class HostedTeamBill
{
    /// <summary>Create the empty table: every team has no bill, so every member is on the free tier.</summary>
    public static void CreateTable(GatewayHost gateway)
    {
        using var ctx = gateway.GatewayDatabaseForTests.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS team_entitlements (" +
            "team_id TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, seats INTEGER NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, livemode INTEGER NULL, updated_at TEXT NULL)");
    }

    /// <summary>Give a team a running bill, live money, the way the website's webhook writes it.</summary>
    public static void Start(GatewayHost gateway, string teamId, int seats = 1)
    {
        using var ctx = gateway.GatewayDatabaseForTests.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO team_entitlements (team_id, status, seats, livemode) VALUES ({0}, 'active', {1}, 1)", teamId, seats);
    }

    /// <summary>Take the table away, so every read of a team's bill FAILS.</summary>
    public static void DropTable(GatewayHost gateway)
    {
        using var ctx = gateway.GatewayDatabaseForTests.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("DROP TABLE team_entitlements");
    }

    /// <summary>Put a person on the free personal plan: no personal entitlement row (the hosted test Gateway seeds a
    /// hosted one for every account it binds).</summary>
    public static void MakePersonalFree(GatewayHost gateway, string subject)
    {
        using var ctx = gateway.GatewayDatabaseForTests.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("DELETE FROM entitlements WHERE subject = {0}", subject);
    }
}
