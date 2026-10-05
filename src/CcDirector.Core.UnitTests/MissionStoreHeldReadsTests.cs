using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Core.UnitTests;

/// <summary>
/// <see cref="MissionStore"/> serves reads from memory (Money Saver, 4 October 2026): the hosted Gateway re-read
/// its whole missions file from a billed network share on every GET /missions. These pin the three things that
/// make that safe: reads stop touching the file while the held copy is fresh, a change written by ANOTHER
/// process (the second container during a deploy) still arrives within the refresh interval, and a write never
/// works from the held copy, so it cannot overwrite what the other process wrote.
/// </summary>
public sealed class MissionStoreHeldReadsTests : IDisposable
{
    private static readonly TenantId Tenant = new("11111111-1111-1111-1111-111111111111");
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"test_missions_held_{Guid.NewGuid()}.json");
    private DateTime _now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private MissionStore NewStore() =>
        new(_path, adoptUnattributedAs: null, refreshAfter: TimeSpan.FromMinutes(1), utcNow: () => _now);

    /// <summary>What a second process does: rewrite the file behind this store's back.</summary>
    private void WriteFromAnotherProcess(params Mission[] missions) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(missions.ToList(), new JsonSerializerOptions { WriteIndented = true }));

    private static Mission Row(string name) => new()
    {
        MissionId = Guid.NewGuid(), MissionName = name, CreatedAt = DateTimeOffset.UtcNow, TenantId = Tenant.Value,
    };

    [Fact]
    public void List_WithinRefreshInterval_DoesNotReadTheFile()
    {
        var store = NewStore();
        store.Create(Tenant, "First");
        Assert.Single(store.List(Tenant));

        // If a read went to the file, it would now see an empty set (or fail). It must still serve the held copy.
        File.Delete(_path);
        _now = _now.AddSeconds(59);

        Assert.Equal("First", Assert.Single(store.List(Tenant)).MissionName);
    }

    [Fact]
    public void List_AfterRefreshInterval_SeesAnotherProcessesWrite()
    {
        var store = NewStore();
        store.Create(Tenant, "Mine");
        Assert.Single(store.List(Tenant));

        var theirs = Row("Written by the other container");
        WriteFromAnotherProcess(Assert.Single(store.List(Tenant)), theirs);

        // Inside the interval the held copy answers; once it has passed, the file is read again.
        Assert.Single(store.List(Tenant));
        _now = _now.AddMinutes(1);
        Assert.Equal(2, store.List(Tenant).Count);
        Assert.NotNull(store.Get(Tenant, theirs.MissionId));
    }

    [Fact]
    public void Create_AfterAnotherProcessesWrite_KeepsTheirMission()
    {
        var store = NewStore();
        var mine = store.Create(Tenant, "Mine");
        Assert.Single(store.List(Tenant));   // the held copy now has one row

        var theirs = Row("Written by the other container");
        WriteFromAnotherProcess(Assert.Single(store.List(Tenant)), theirs);

        // A write reads the file fresh, so the other container's row survives this write and is served at once.
        var second = store.Create(Tenant, "Mine too");

        var ids = store.List(Tenant).Select(m => m.MissionId).ToHashSet();
        Assert.Equal(new HashSet<Guid> { mine.MissionId, theirs.MissionId, second.MissionId }, ids);
    }

    [Fact]
    public void Write_IsServedToTheNextReadImmediately()
    {
        var store = NewStore();
        var mission = store.Create(Tenant, "Before");
        Assert.Single(store.List(Tenant));

        store.Rename(Tenant, mission.MissionId, "After");

        Assert.Equal("After", store.Get(Tenant, mission.MissionId)!.MissionName);
    }

    [Fact]
    public void ReturnedRecord_ChangedByTheCaller_DoesNotChangeTheStore()
    {
        var store = NewStore();
        var created = store.Create(Tenant, "Original");

        created.MissionName = "changed on the returned Create result";
        store.Get(Tenant, created.MissionId)!.MissionName = "changed on a Get result";
        store.List(Tenant)[0].MissionName = "changed on a List result";

        Assert.Equal("Original", store.Get(Tenant, created.MissionId)!.MissionName);
    }

    [Fact]
    public void HeldCopy_CarriesEverySettableField()
    {
        // Copy lists the fields by hand; a field added to Mission and not to Copy would be served at its default.
        // So the row on file gives EVERY writable field a value that is not its default, checked here, and the
        // served record must match it field by field.
        var full = new Mission
        {
            MissionId = Guid.NewGuid(),
            MissionName = "Every field",
            Why = "Because",
            WhyUpdatedAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
            State = MissionStates.Complete,
            StateChangedAt = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero),
            CreatedAt = new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero),
            TenantId = Tenant.Value,
        };
        var writable = typeof(Mission).GetProperties().Where(p => p.CanWrite).ToList();
        var blank = new Mission();
        foreach (var property in writable)
            Assert.True(!Equals(property.GetValue(full), property.GetValue(blank)),
                $"the test row must give {property.Name} a non-default value, or a missing copy of it would pass");
        WriteFromAnotherProcess(full);

        var served = NewStore().Get(Tenant, full.MissionId)!;

        foreach (var property in writable)
            Assert.Equal(property.GetValue(full), property.GetValue(served));
    }

    [Fact]
    public void Get_AMissionAnotherProcessJustCreated_IsFoundInsideTheRefreshInterval()
    {
        var store = NewStore();
        var mine = store.Create(Tenant, "Mine");
        Assert.Single(store.List(Tenant));   // held copy: one row, fresh

        var theirs = Row("Created on the other container a moment ago");
        WriteFromAnotherProcess(Assert.Single(store.List(Tenant)), theirs);

        // Still inside the interval: a spawn or attach naming it must not be told "unknown mission".
        Assert.NotNull(store.Get(Tenant, theirs.MissionId));
        Assert.NotNull(store.Get(Tenant, mine.MissionId));
    }
}
