using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Tests.Factory.Registry;

/// <summary>
/// Writes a schedule row that points at a factory seat, with the id the test names (issue #3650). A seat's schedules
/// are derived from these links, so a test that registers a seat "run by cj_ceo" first puts cj_ceo in the database,
/// linked to that seat - the store mints its own ids, which is why this writes the row directly.
/// </summary>
internal static class ScheduleLinkSeed
{
    public static void Link(GatewayDatabase db, TenantId tenant, string? factory, string? seat, string id,
        bool enabled = true, string? name = null)
    {
        using var ctx = db.CreateContext(tenant);
        ctx.CronJobs.Add(new CronJobEntity
        {
            TenantId = tenant.Value,
            Id = id,
            Name = name ?? id,
            Enabled = enabled,
            ScheduleKind = "recurring",
            CronExpression = "0 7 * * *",
            TimeZoneId = "UTC",
            Factory = factory,
            Seat = seat,
            Target = new CronJobTarget { Machine = "SOREN_NORTH" },
            Action = new CronJobAction { RepoPath = @"D:\factory", Seed = "/run" },
            CreatedUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        ctx.SaveChanges();
    }
}
