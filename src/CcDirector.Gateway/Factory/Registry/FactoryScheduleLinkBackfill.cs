using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Registry;

/// <summary>What one run of <see cref="FactoryScheduleLinkBackfill"/> did, for the log and the tests.</summary>
/// <param name="Linked">Schedules that were given the factory and seat their registration named.</param>
/// <param name="AlreadyLinked">Schedules that already pointed at exactly that seat.</param>
/// <param name="Skipped">Schedules a registration named that could not be linked, each with the reason.</param>
public sealed record FactoryScheduleLinkBackfillResult(
    IReadOnlyList<string> Linked,
    IReadOnlyList<string> AlreadyLinked,
    IReadOnlyList<string> Skipped);

/// <summary>
/// THE ONE-TIME BACKFILL OF ISSUE #3650. Until #3650 a seat's schedules were a list kept on the factory's registry
/// row; now they are derived from each schedule's own factory and seat. This moves the old lists onto the schedules
/// they name, once, and then empties them, so the factory's page shows the same seats with the same schedules the
/// moment after the deploy as the moment before it.
///
/// It runs at Gateway start-up, from the data as it stands - the registry the owner corrected by hand on 2026-10-08,
/// in which every factory seat lists its schedule ids. It is the Gateway itself writing links its own registry
/// already states, so it does not pass through the factory-naming gate a caller's write does.
///
/// IDEMPOTENT BY CONSTRUCTION: a registry row whose seats list no schedules is left alone, and every row this
/// touches is written back with empty lists in the same save as its links, so a second start-up finds nothing to do
/// and can never move a schedule back after someone has moved it on purpose.
///
/// A schedule the old list names but this cannot link - it no longer exists, or two seats both claimed it, or it
/// already points at a different factory or seat - is left as it is and written to the log with the reason. It is
/// never hidden: an enabled schedule outside every seat is on the Factories screen's "Schedules outside any factory"
/// list from the first read.
/// </summary>
public static class FactoryScheduleLinkBackfill
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Run the backfill for every account that has a registry row. The database must be open.</summary>
    public static FactoryScheduleLinkBackfillResult Run(GatewayDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        List<string> tenants;
        using (var all = db.CreateUnscopedContext())
            tenants = all.FactoryRegistry.IgnoreQueryFilters().AsNoTracking()
                .Select(f => f.TenantId).Distinct().ToList();

        var linked = new List<string>();
        var already = new List<string>();
        var skipped = new List<string>();
        foreach (var tenant in tenants)
        {
            // ONE ACCOUNT'S BAD ROW CANNOT STOP THE SERVICE (review finding 3). This runs at start-up for every
            // account on the hosted Gateway; a row that cannot be read would otherwise keep the whole Gateway down on
            // every restart. That account's backfill is abandoned whole - its save never runs, so nothing of it is
            // half done - and the failure is logged and reported; the next start tries it again.
            try
            {
                RunForTenant(db, new TenantId(tenant), linked, already, skipped);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or DbUpdateException)
            {
                FileLog.Write($"[FactoryScheduleLinkBackfill] Run FAILED for account {tenant}: {ex.Message}");
                skipped.Add($"{tenant}: the account's backfill failed and nothing of it was saved: {ex.Message}");
            }
        }

        FileLog.Write($"[FactoryScheduleLinkBackfill] Run: accounts={tenants.Count}, linked={linked.Count}, alreadyLinked={already.Count}, skipped={skipped.Count}");
        foreach (var line in skipped)
            FileLog.Write($"[FactoryScheduleLinkBackfill] skipped: {line}");
        return new FactoryScheduleLinkBackfillResult(linked, already, skipped);
    }

    private static void RunForTenant(GatewayDatabase db, TenantId tenant, List<string> linked, List<string> already, List<string> skipped)
    {
        using var ctx = db.CreateContext(tenant);
        var rows = ctx.FactoryRegistry.ToList();
        var seatsByRow = rows.ToDictionary(r => r, r => JsonSerializer.Deserialize<List<RegisteredFactorySeatDto>>(r.SeatsJson, Json)
            ?? throw new InvalidOperationException($"The seats of factory '{r.Factory}' are stored as something other than a list."));
        var claims = seatsByRow
            .SelectMany(kv => kv.Value.SelectMany(s => s.Schedules.Select(id => (Id: id.Trim(), Factory: kv.Key.Factory, Seat: s.Id))))
            .ToList();
        if (claims.Count == 0) return;

        var claimedTwice = claims.GroupBy(c => c.Id, StringComparer.Ordinal)
            .Where(g => g.Select(c => (c.Factory, c.Seat)).Distinct().Count() > 1)
            .ToDictionary(g => g.Key, g => string.Join(" and ", g.Select(c => $"{c.Factory}/{c.Seat}").Distinct()), StringComparer.Ordinal);

        foreach (var claim in claims.DistinctBy(c => (c.Id, c.Factory, c.Seat)))
        {
            var where = $"{tenant.Value} {claim.Factory}/{claim.Seat} {claim.Id}";
            if (claimedTwice.TryGetValue(claim.Id, out var both))
            {
                skipped.Add($"{where}: claimed by more than one seat ({both})");
                continue;
            }
            var job = ctx.CronJobs.FirstOrDefault(j => j.Id == claim.Id);
            if (job is null)
            {
                skipped.Add($"{where}: no such schedule");
                continue;
            }
            if (job.Factory == claim.Factory && job.Seat == claim.Seat)
            {
                already.Add(where);
                continue;
            }
            if (job.Factory is not null && job.Factory != claim.Factory || job.Seat is not null)
            {
                skipped.Add($"{where}: the schedule already points at {job.Factory ?? "no factory"}/{job.Seat ?? "no seat"}");
                continue;
            }
            job.Factory = claim.Factory;
            job.Seat = claim.Seat;
            linked.Add(where);
        }

        foreach (var (row, seats) in seatsByRow)
        {
            if (seats.All(s => s.Schedules.Count == 0)) continue;
            foreach (var s in seats) s.Schedules = new List<string>();
            row.SeatsJson = JsonSerializer.Serialize(seats, Json);
        }
        ctx.SaveChanges();
    }
}
