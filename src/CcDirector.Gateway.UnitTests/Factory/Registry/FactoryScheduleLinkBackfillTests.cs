using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Registry;

/// <summary>
/// The one-time backfill of issue #3650 (point 6): every schedule a registry seat used to list is given that factory
/// and seat, the old lists are emptied in the same save, a second run does nothing, and a schedule that cannot be
/// linked is left alone and reported - never guessed at.
/// </summary>
public sealed class FactoryScheduleLinkBackfillTests : IDisposable
{
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;

    public FactoryScheduleLinkBackfillTests() => _db = _h.Open();
    public void Dispose() => _h.Dispose();

    /// <summary>A registry row as it was stored before #3650: each seat carrying its own schedule list.</summary>
    private void OldRow(TenantId tenant, string factory, params (string Seat, string[] Schedules)[] seats)
    {
        using var ctx = _db.CreateContext(tenant);
        ctx.FactoryRegistry.Add(new FactoryRegistryEntity
        {
            TenantId = tenant.Value,
            Factory = factory,
            Title = factory,
            Folder = @"D:\f",
            Computer = "SOREN_NORTH",
            SeatsJson = JsonSerializer.Serialize(seats.Select(s => new RegisteredFactorySeatDto
            {
                Id = s.Seat, Name = s.Seat, Role = s.Seat, BriefFile = "b.yaml", Computer = "SOREN_NORTH", Schedules = s.Schedules.ToList(),
            }).ToList(), Web),
            RegisteredBy = "the owner (test)",
            RegisteredAtUtc = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc),
        });
        ctx.SaveChanges();
    }

    private (string? Factory, string? Seat) LinkOf(TenantId tenant, string id)
    {
        using var ctx = _db.CreateContext(tenant);
        var job = ctx.CronJobs.Single(j => j.Id == id);
        return (job.Factory, job.Seat);
    }

    private List<string> StoredSchedules(TenantId tenant, string factory)
    {
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.FactoryRegistry.Single(f => f.Factory == factory);
        return JsonSerializer.Deserialize<List<RegisteredFactorySeatDto>>(row.SeatsJson, Web)!.SelectMany(s => s.Schedules).ToList();
    }

    [Fact]
    public void Run_LinksEverySeatsSchedules_EmptiesTheOldLists_AndTheSeatShowsTheSameSchedules()
    {
        ScheduleLinkSeed.Link(_db, A, null, null, "cj_mail1");
        ScheduleLinkSeed.Link(_db, A, null, null, "cj_mail2");
        ScheduleLinkSeed.Link(_db, A, null, null, "cj_ceo", enabled: false);
        ScheduleLinkSeed.Link(_db, A, null, null, "cj_plain");
        OldRow(A, "devthrottle", ("mail-desk", new[] { "cj_mail1", "cj_mail2" }), ("ceo", new[] { "cj_ceo" }));

        var result = FactoryScheduleLinkBackfill.Run(_db);

        Assert.Equal(3, result.Linked.Count);
        Assert.Empty(result.Skipped);
        Assert.Equal(("devthrottle", "mail-desk"), LinkOf(A, "cj_mail1"));
        Assert.Equal(("devthrottle", "mail-desk"), LinkOf(A, "cj_mail2"));
        Assert.Equal(("devthrottle", "ceo"), LinkOf(A, "cj_ceo"));
        Assert.Equal(((string?)null, (string?)null), LinkOf(A, "cj_plain"));
        Assert.Empty(StoredSchedules(A, "devthrottle"));
        var seats = new FactoryRegistryStore(_db).Find(A, "devthrottle")!.Seats;
        Assert.Equal(new[] { "cj_mail1", "cj_mail2" }, seats.Single(s => s.Id == "mail-desk").Schedules);
        Assert.Equal(new[] { "cj_ceo" }, seats.Single(s => s.Id == "ceo").Schedules);
    }

    [Fact]
    public void Run_IsPerAccount_AndASecondRunDoesNothing()
    {
        ScheduleLinkSeed.Link(_db, A, null, null, "cj_1");
        ScheduleLinkSeed.Link(_db, B, null, null, "cj_1");
        OldRow(A, "devthrottle", ("ceo", new[] { "cj_1" }));
        OldRow(B, "warmforward", ("nora-hale", new[] { "cj_1" }));

        var first = FactoryScheduleLinkBackfill.Run(_db);
        var second = FactoryScheduleLinkBackfill.Run(_db);

        Assert.Equal(2, first.Linked.Count);
        Assert.Equal(("devthrottle", "ceo"), LinkOf(A, "cj_1"));
        Assert.Equal(("warmforward", "nora-hale"), LinkOf(B, "cj_1"));
        Assert.Empty(second.Linked);
        Assert.Empty(second.Skipped);
    }

    [Fact]
    public void Run_LeavesAloneAndReports_AMissingSchedule_OneClaimedByTwoSeats_AndOneAlreadyElsewhere()
    {
        ScheduleLinkSeed.Link(_db, A, null, null, "cj_shared");
        ScheduleLinkSeed.Link(_db, A, "money-saver", "daily", "cj_taken");
        OldRow(A, "devthrottle", ("ceo", new[] { "cj_gone", "cj_shared", "cj_taken" }), ("outreach", new[] { "cj_shared" }));

        var result = FactoryScheduleLinkBackfill.Run(_db);

        Assert.Empty(result.Linked);
        Assert.Contains(result.Skipped, s => s.Contains("cj_gone") && s.Contains("no such schedule"));
        Assert.Contains(result.Skipped, s => s.Contains("cj_shared") && s.Contains("claimed by more than one seat"));
        Assert.Contains(result.Skipped, s => s.Contains("cj_taken") && s.Contains("already points at money-saver/daily"));
        Assert.Equal(((string?)null, (string?)null), LinkOf(A, "cj_shared"));
        Assert.Equal(("money-saver", "daily"), LinkOf(A, "cj_taken"));
        Assert.Empty(StoredSchedules(A, "devthrottle"));
    }
}
