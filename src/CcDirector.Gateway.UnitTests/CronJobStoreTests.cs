using System.Text.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="CronJobStore"/> (epic #479, #482) over the EF data layer (Hosted Gateway
/// mission, Step 1b). Covers the CRUD contract, the id/created/next-run stamping on create, and the
/// persistence contract: a "restart" is a brand-new database + store over the same file - exactly what a new
/// Gateway process does. Also covers the one-time legacy-JSON import (lossless, fail-loud) that replaces the
/// old JSON store.
/// </summary>
public sealed class CronJobStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string LegacyPath() => _h.LegacyPath("cronjobs-" + Guid.NewGuid().ToString("N") + ".json");

    private static CronJobDto ValidJob(string name = "nightly") => new()
    {
        Name = name,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/work-list run Tonight", AutoDismiss = false },
    };

    [Fact]
    public void Create_AssignsId_StampsCreated_ComputesNextRun_AndPersists()
    {
        var db = _h.Open();
        var legacy = LegacyPath();
        var store = new CronJobStore(db, legacy);

        var created = store.Create(ValidJob());

        Assert.StartsWith("cj_", created.Id);
        Assert.NotEqual(default, created.CreatedUtc);
        Assert.NotNull(created.NextRunUtc);
        Assert.True(created.Enabled);
        Assert.Null(created.LastFiredUtc);

        // Written through: a fresh database + store over the same file sees it.
        var reloaded = new CronJobStore(_h.Open(), legacy).Get(created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("nightly", reloaded.Name);
        Assert.Equal("0 0 * * *", reloaded.CronExpression);
        // The nested action round-trips field-for-field, including AutoDismiss.
        Assert.Equal(@"D:\repo", reloaded.Action.RepoPath);
        Assert.False(reloaded.Action.AutoDismiss);
        Assert.Equal("workstation-A", reloaded.Target.Machine);
    }

    [Fact]
    public void Create_InvalidJob_Throws()
    {
        var store = new CronJobStore(_h.Open(), LegacyPath());
        var bad = ValidJob();
        bad.CronExpression = "not a cron";

        Assert.Throws<ArgumentException>(() => store.Create(bad));
    }

    [Fact]
    public void CreatedUtc_RoundTripsAsUtc()
    {
        var db = _h.Open();
        var legacy = LegacyPath();
        var created = new CronJobStore(db, legacy).Create(ValidJob());

        var reloaded = new CronJobStore(_h.Open(), legacy).Get(created.Id)!;
        Assert.Equal(DateTimeKind.Utc, reloaded.CreatedUtc.Kind);
        Assert.Equal(created.CreatedUtc, reloaded.CreatedUtc);
    }

    [Fact]
    public void RoundTrip_MultipleJobs_SurviveReloadWithRecomputedNextRun()
    {
        var legacy = LegacyPath();
        var store = new CronJobStore(_h.Open(), legacy);
        store.Create(ValidJob("a"));
        store.Create(ValidJob("b"));

        // "Restart": a fresh database + store over the same file.
        var reloaded = new CronJobStore(_h.Open(), legacy);

        Assert.Equal(2, reloaded.ListAll().Count);
        Assert.All(reloaded.ListAll(), j => Assert.NotNull(j.NextRunUtc));
        Assert.Contains(reloaded.ListAll(), j => j.Name == "a");
        Assert.Contains(reloaded.ListAll(), j => j.Name == "b");
    }

    [Fact]
    public void Update_ChangesFields_PreservesIdAndCreated_AndPersists()
    {
        var legacy = LegacyPath();
        var store = new CronJobStore(_h.Open(), legacy);
        var created = store.Create(ValidJob());

        var edit = ValidJob("renamed");
        edit.CronExpression = "30 9 * * 1-5";
        var updated = store.Update(created.Id, edit);

        Assert.NotNull(updated);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(created.CreatedUtc, updated.CreatedUtc);
        Assert.Equal("renamed", updated.Name);
        Assert.Equal("30 9 * * 1-5", updated.CronExpression);

        var reloaded = new CronJobStore(_h.Open(), legacy).Get(created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("renamed", reloaded.Name);
        Assert.Equal("30 9 * * 1-5", reloaded.CronExpression);
    }

    [Fact]
    public void Update_NoSuchId_ReturnsNull()
    {
        var store = new CronJobStore(_h.Open(), LegacyPath());
        Assert.Null(store.Update("cj_nope", ValidJob()));
    }

    [Fact]
    public void Update_InvalidJob_Throws()
    {
        var store = new CronJobStore(_h.Open(), LegacyPath());
        var created = store.Create(ValidJob());
        var bad = ValidJob();
        bad.TimeZoneId = "Nowhere/Land";

        Assert.Throws<ArgumentException>(() => store.Update(created.Id, bad));
    }

    [Fact]
    public void MarkFired_RecordsRunMetadata_AndPersists()
    {
        var legacy = LegacyPath();
        var store = new CronJobStore(_h.Open(), legacy);
        var created = store.Create(ValidJob());

        var firedUtc = new DateTime(2026, 6, 17, 5, 0, 0, DateTimeKind.Utc);
        var next = new DateTime(2026, 6, 18, 5, 0, 0, DateTimeKind.Utc);
        var marked = store.MarkFired(created.Id, firedUtc, "started", next, enabled: true);

        Assert.NotNull(marked);
        Assert.Equal(firedUtc, marked.LastFiredUtc);
        Assert.Equal("started", marked.LastStatus);

        var reloaded = new CronJobStore(_h.Open(), legacy).Get(created.Id)!;
        Assert.Equal(firedUtc, reloaded.LastFiredUtc);
        Assert.Equal("started", reloaded.LastStatus);
        Assert.Equal(DateTimeKind.Utc, reloaded.LastFiredUtc!.Value.Kind);
    }

    [Fact]
    public void Delete_RemovesJob_AndPersists()
    {
        var legacy = LegacyPath();
        var store = new CronJobStore(_h.Open(), legacy);
        var created = store.Create(ValidJob());

        Assert.True(store.Delete(created.Id));
        Assert.Null(store.Get(created.Id));
        Assert.Null(new CronJobStore(_h.Open(), legacy).Get(created.Id));
    }

    [Fact]
    public void Delete_NoSuchId_ReturnsFalse()
    {
        var store = new CronJobStore(_h.Open(), LegacyPath());
        Assert.False(store.Delete("cj_nope"));
    }

    [Fact]
    public void NoLegacyFile_StartsEmpty()
    {
        var store = new CronJobStore(_h.Open(), LegacyPath());
        Assert.Empty(store.ListAll());
    }

    [Fact]
    public void Constructor_NullDb_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CronJobStore(null!, LegacyPath()));
    }

    [Fact]
    public void Constructor_EmptyLegacyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CronJobStore(_h.Open(), " "));
    }

    [Fact]
    public void LegacyJson_ImportedOnce_Lossless_ThenRenamedAside()
    {
        // A legacy cronjobs.json written by the old store, including nested target/action and every field.
        var legacy = LegacyPath();
        var job = new CronJobDto
        {
            Id = "cj_abc123",
            Name = "midnight loop",
            Enabled = true,
            ScheduleKind = CronSchedule.KindRecurring,
            CronExpression = "0 0 * * *",
            RunAt = null,
            TimeZoneId = "America/Chicago",
            Target = new CronJobTarget { Machine = "workstation-B" },
            Action = new CronJobAction { RepoPath = @"D:\loop", Seed = "/work-list run Tonight", WorkListName = "Tonight", AutoDismiss = false },
            PreventOverlap = true,
            NotifyOn = CronNotify.Failure,
            NotifyWebhookUrl = "https://example.test/hook",
            CreatedUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            LastFiredUtc = new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc),
            NextRunUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
            LastStatus = "started",
        };
        WriteLegacyJobsFile(legacy, job);

        var store = new CronJobStore(_h.Open(), legacy);

        var loaded = store.Get("cj_abc123");
        Assert.NotNull(loaded);
        Assert.Equal("midnight loop", loaded.Name);
        Assert.Equal(CronSchedule.KindRecurring, loaded.ScheduleKind);
        Assert.Equal("0 0 * * *", loaded.CronExpression);
        Assert.Equal("America/Chicago", loaded.TimeZoneId);
        Assert.Equal("workstation-B", loaded.Target.Machine);
        Assert.Equal(@"D:\loop", loaded.Action.RepoPath);
        Assert.Equal("/work-list run Tonight", loaded.Action.Seed);
        Assert.Equal("Tonight", loaded.Action.WorkListName);
        Assert.False(loaded.Action.AutoDismiss);
        Assert.True(loaded.PreventOverlap);
        Assert.Equal(CronNotify.Failure, loaded.NotifyOn);
        Assert.Equal("https://example.test/hook", loaded.NotifyWebhookUrl);
        Assert.Equal(job.CreatedUtc, loaded.CreatedUtc);
        Assert.Equal(job.LastFiredUtc, loaded.LastFiredUtc);
        Assert.Equal("started", loaded.LastStatus);

        // The legacy file is renamed aside (kept as a backup), never left to re-import.
        Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(legacy)!, Path.GetFileName(legacy) + ".migrated-*"));

        // A fresh database + store over the same DB does NOT re-import (the file is gone) and still has it.
        Assert.NotNull(new CronJobStore(_h.Open(), legacy).Get("cj_abc123"));
    }

    [Fact]
    public void LegacyJson_RenameFailsAfterImport_NextConstructionRecoversWithoutReimporting()
    {
        // The cron rename-recovery gap flagged in review on #1772: a rename that fails AFTER the import
        // commits used to orphan the legacy file forever (the old cron path threw from RenameAside and had no
        // recovery branch). The shared recoverable-import plumbing closes it: the first construction imports and
        // commits, its rename-aside fails because the file is held open, and it does NOT throw; the next
        // construction sees the table already populated and renames the lingering file aside WITHOUT
        // re-importing.
        // Its own directory, because BlockedRename closes the whole directory to writing on macOS and Linux
        // and the harness's database lives in the directory above.
        var legacy = _h.LegacyPath(Path.Combine("import-" + Guid.NewGuid().ToString("N"), "cronjobs.json"));
        var seeded = ValidJob();
        seeded.Id = "cj_recover1";
        WriteLegacyJobsFile(legacy, seeded);

        var dir = Path.GetDirectoryName(legacy)!;
        var migratedGlob = Path.GetFileName(legacy) + ".migrated-*";

        // Close the legacy file off so it can still be READ (the import has to parse it) but cannot be
        // MOVED, so the post-commit rename-aside fails. Each system's own mechanism, and the fixture proves
        // the refusal really happened before the test proceeds.
        using (new BlockedRename(legacy))
        {
            // First construction: imports the job (committed), then the rename-aside fails on the locked file.
            // Best-effort - it is logged, NOT thrown - so the store constructs and holds the imported data.
            var store1 = new CronJobStore(_h.Open(), legacy);
            Assert.NotNull(store1.Get("cj_recover1"));
            Assert.Single(store1.ListAll());
            // The rename failed, so the legacy file lingers and nothing has been renamed aside yet.
            Assert.True(File.Exists(legacy));
            Assert.Empty(Directory.GetFiles(dir, migratedGlob));
        }

        // The lock is released. The next construction sees the table already populated and the file still
        // there: it renames the leftover aside (idempotent recovery) WITHOUT re-importing, and does not throw.
        var store2 = new CronJobStore(_h.Open(), legacy);

        Assert.Single(store2.ListAll());                      // no re-import - still exactly one job
        Assert.NotNull(store2.Get("cj_recover1"));
        Assert.False(File.Exists(legacy));                    // the lingering file was recovered (renamed aside)
        Assert.Single(Directory.GetFiles(dir, migratedGlob)); // exactly one backup, renamed once
    }

    [Fact]
    public void CorruptLegacyJson_FailsLoud_AndLeavesTheFileInPlace()
    {
        var legacy = LegacyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        const string corrupt = "{ this is not json !!!";
        File.WriteAllText(legacy, corrupt);

        // Fail-loud, no partial import, no silent quarantine (the EF data-layer contract).
        Assert.Throws<InvalidOperationException>(() => new CronJobStore(_h.Open(), legacy));

        // The corrupt file is left exactly as it was for the operator to recover.
        Assert.True(File.Exists(legacy));
        Assert.Equal(corrupt, File.ReadAllText(legacy));
    }

    private static void WriteLegacyJobsFile(string path, params CronJobDto[] jobs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(new { Jobs = jobs }, options));
    }
}
