using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="CronJobStore"/> (epic #479, #482). Covers the CRUD contract, the
/// id/created/next-run stamping on create, and the persistence contract (the
/// <see cref="WorkListStore"/> precedent): a "restart" is a brand-new store instance loading the
/// same file - exactly what a new Gateway process does. Asserts round-trip, write-through on every
/// mutation, atomic write (no .tmp residue), missing-file first boot, corrupt-file quarantine, and
/// next-run recompute on load.
/// </summary>
public sealed class CronJobStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-cronjob-tests-" + Guid.NewGuid().ToString("N"));

    private string NewPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CronJobDto ValidJob(string name = "nightly") => new()
    {
        Name = name,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { DirectorId = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/work-list run Tonight" },
    };

    [Fact]
    public void Create_AssignsId_StampsCreated_ComputesNextRun_AndPersists()
    {
        var path = NewPath();
        var store = new CronJobStore(path);

        var created = store.Create(ValidJob());

        Assert.StartsWith("cj_", created.Id);
        Assert.NotEqual(default, created.CreatedUtc);
        Assert.NotNull(created.NextRunUtc);
        Assert.True(created.Enabled);
        Assert.Null(created.LastFiredUtc);

        // Written through: a fresh store on the same file sees it.
        var reloaded = new CronJobStore(path).Get(created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("nightly", reloaded.Name);
        Assert.Equal("0 0 * * *", reloaded.CronExpression);
    }

    [Fact]
    public void Create_InvalidJob_Throws()
    {
        var path = NewPath();
        var store = new CronJobStore(path);
        var bad = ValidJob();
        bad.CronExpression = "not a cron";

        Assert.Throws<ArgumentException>(() => store.Create(bad));
    }

    [Fact]
    public void RoundTrip_MultipleJobs_SurviveReloadWithRecomputedNextRun()
    {
        var path = NewPath();
        var store = new CronJobStore(path);
        store.Create(ValidJob("a"));
        store.Create(ValidJob("b"));

        // "Restart": a fresh store instance on the same file.
        var reloaded = new CronJobStore(path);

        Assert.Equal(2, reloaded.ListAll().Count);
        Assert.All(reloaded.ListAll(), j => Assert.NotNull(j.NextRunUtc));
        Assert.Contains(reloaded.ListAll(), j => j.Name == "a");
        Assert.Contains(reloaded.ListAll(), j => j.Name == "b");
    }

    [Fact]
    public void Update_ChangesFields_PreservesIdAndCreated_AndPersists()
    {
        var path = NewPath();
        var store = new CronJobStore(path);
        var created = store.Create(ValidJob());

        var edit = ValidJob("renamed");
        edit.CronExpression = "30 9 * * 1-5";
        var updated = store.Update(created.Id, edit);

        Assert.NotNull(updated);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(created.CreatedUtc, updated.CreatedUtc);
        Assert.Equal("renamed", updated.Name);
        Assert.Equal("30 9 * * 1-5", updated.CronExpression);

        var reloaded = new CronJobStore(path).Get(created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("renamed", reloaded.Name);
        Assert.Equal("30 9 * * 1-5", reloaded.CronExpression);
    }

    [Fact]
    public void Update_NoSuchId_ReturnsNull()
    {
        var store = new CronJobStore(NewPath());
        Assert.Null(store.Update("cj_nope", ValidJob()));
    }

    [Fact]
    public void Update_InvalidJob_Throws()
    {
        var store = new CronJobStore(NewPath());
        var created = store.Create(ValidJob());
        var bad = ValidJob();
        bad.TimeZoneId = "Nowhere/Land";

        Assert.Throws<ArgumentException>(() => store.Update(created.Id, bad));
    }

    [Fact]
    public void Delete_RemovesJob_AndPersists()
    {
        var path = NewPath();
        var store = new CronJobStore(path);
        var created = store.Create(ValidJob());

        Assert.True(store.Delete(created.Id));
        Assert.Null(store.Get(created.Id));
        Assert.Null(new CronJobStore(path).Get(created.Id));
    }

    [Fact]
    public void Delete_NoSuchId_ReturnsFalse()
    {
        var store = new CronJobStore(NewPath());
        Assert.False(store.Delete("cj_nope"));
    }

    [Fact]
    public void MissingFile_StartsEmpty_NoFileUntilFirstWrite()
    {
        var path = NewPath();
        var store = new CronJobStore(path);

        Assert.Empty(store.ListAll());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CorruptFile_IsQuarantined_StoreStartsEmpty_BytesPreserved()
    {
        var path = NewPath();
        Directory.CreateDirectory(_dir);
        const string corrupt = "{ this is not json !!!";
        File.WriteAllText(path, corrupt);

        var store = new CronJobStore(path);

        Assert.Empty(store.ListAll());
        var quarantined = Directory.GetFiles(_dir, Path.GetFileName(path) + ".corrupt-*");
        var q = Assert.Single(quarantined);
        Assert.Equal(corrupt, File.ReadAllText(q));
        Assert.False(File.Exists(path));

        // The original path is freed for the next write-through.
        var created = store.Create(ValidJob());
        Assert.NotNull(new CronJobStore(path).Get(created.Id));
    }

    [Fact]
    public void Save_LeavesNoTempResidue_AndFileIsWholeJson()
    {
        var path = NewPath();
        var store = new CronJobStore(path);
        for (var i = 0; i < 25; i++)
            store.Create(ValidJob("job-" + i));

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(25, new CronJobStore(path).ListAll().Count);
    }

    [Fact]
    public void Constructor_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CronJobStore(" "));
    }
}
