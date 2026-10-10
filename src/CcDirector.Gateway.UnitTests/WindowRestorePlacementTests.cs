using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A factory's Restore gives its window schedules their minutes (WindowRestorePlacement), against a real schedule store:
/// a window that was never placed comes back with a next run, one that runs after another is placed after it whatever
/// order Restore names them in, and one no minute fits for stays on and is named in a sentence.
/// </summary>
public sealed class WindowRestorePlacementTests : IDisposable
{
    private static readonly TenantId T = TenantId.Local;
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();

    public void Dispose() => _h.Dispose();

    private CronJobStore NewStore() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));

    private static CronJobDto Window(string name, string settings) => new()
    {
        Name = name,
        Enabled = false,
        ScheduleKind = CronSchedule.KindWindow,
        CronExpression = settings,
        TimeZoneId = "America/Toronto",
        Target = new CronJobTarget { Machine = "SOREN_NORTH" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    private static IReadOnlyDictionary<string, TimeSpan> ThirtyMinutes(IReadOnlyCollection<string> ids) =>
        ids.ToDictionary(id => id, _ => TimeSpan.FromMinutes(30));

    [Fact]
    public void PlaceSwitchedOn_AFollowerNamedFirst_IsPlacedAfterItsAnchor_AndBothGetANextRun()
    {
        var store = NewStore();
        var anchor = store.Create(Window("anchor", "window=01:00-03:00"));
        var follower = store.Create(Window("follower", $"window=01:00-06:00 after={anchor.Id} gap=60"));
        // Restore switches them on through the store, which leaves an unplaced window with no next run.
        store.SetEnabled(T, follower.Id, true);
        store.SetEnabled(T, anchor.Id, true);
        Assert.Null(store.Get(T, anchor.Id)!.NextRunUtc);

        var problems = WindowRestorePlacement.PlaceSwitchedOn(store, T, new[] { follower.Id, anchor.Id }, ThirtyMinutes, DateTime.UtcNow);

        Assert.Empty(problems);
        var a = store.Get(T, anchor.Id)!;
        var f = store.Get(T, follower.Id)!;
        Assert.Contains("placed=", a.CronExpression);
        Assert.Contains("placed=", f.CronExpression);
        Assert.NotNull(a.NextRunUtc);
        Assert.NotNull(f.NextRunUtc);
        Assert.True(f.NextRunUtc >= a.NextRunUtc!.Value.AddMinutes(60));
    }

    [Fact]
    public void PlaceSwitchedOn_AWindowNoMinuteFits_StaysOn_AndIsNamedInASentence()
    {
        var store = NewStore();
        var tight = store.Create(Window("tight", "window=02:00-02:30 deadline=02:40"));
        store.SetEnabled(T, tight.Id, true);

        var problems = WindowRestorePlacement.PlaceSwitchedOn(store, T, new[] { tight.Id },
            ids => ids.ToDictionary(id => id, _ => TimeSpan.FromMinutes(90)), DateTime.UtcNow);

        Assert.StartsWith("'tight' is switched on but could not be given a time to run:", Assert.Single(problems));
        Assert.True(store.Get(T, tight.Id)!.Enabled);
    }

    [Fact]
    public void PlaceSwitchedOn_LeavesSchedulesThatAreNotWindows_AsTheyAre()
    {
        var store = NewStore();
        var fixedJob = store.Create(new CronJobDto
        {
            Name = "fixed",
            Enabled = true,
            ScheduleKind = CronSchedule.KindRecurring,
            CronExpression = "0 7 * * *",
            TimeZoneId = "America/Toronto",
            Target = new CronJobTarget { Machine = "SOREN_NORTH" },
            Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
        });

        Assert.Empty(WindowRestorePlacement.PlaceSwitchedOn(store, T, new[] { fixedJob.Id }, ThirtyMinutes, DateTime.UtcNow));
        Assert.Equal("0 7 * * *", store.Get(T, fixedJob.Id)!.CronExpression);
    }
}
