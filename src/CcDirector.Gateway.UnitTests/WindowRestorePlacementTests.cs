using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A factory's Restore gives its window schedules their minutes (WindowRestorePlacement), against a real schedule store:
/// one no minute fits for stays on and is named in a sentence, and a schedule that is not a window is left as it is.
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
