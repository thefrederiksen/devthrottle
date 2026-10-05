using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

public sealed class RepoHistoryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ccd-repohist-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose()
    {
        foreach (var p in new[] { _path, _path + ".bak", _path + ".tmp" })
            if (File.Exists(p)) File.Delete(p);
    }

    private static RepoStatusDto Repo(string name, int worktrees = 0, int safe = 0, long bytes = 0,
        int uncommitted = 0, DateTime? dirtySince = null, bool provisional = false) => new()
    {
        Name = name,
        MachineName = "M1",
        DirectorId = "d1",
        Path = $@"D:\repos\{name}",
        WorktreeCount = worktrees,
        WorktreesSafeToReap = safe,
        WorktreeBytes = bytes,
        UncommittedCount = uncommitted,
        IsClean = uncommitted == 0,
        DirtySinceUtc = dirtySince,
        Provisional = provisional,
    };

    [Fact]
    public void Observe_IsIdempotentPerDay_LastWriteWins_AndPersists()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 23);

        var store = new RepoHistoryStore(_path);
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 6, safe: 2) }, day);
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 4, safe: 1) }, day); // same day again
        store.SaveIfChanged(); // the host's clock

        // A fresh store (new process) reads the persisted file: one row for the day, the LAST values.
        var reloaded = new RepoHistoryStore(_path);
        var trends = reloaded.WeeklyTrends(tenant, weeks: 1, today: day);
        Assert.Equal(4, trends[^1].MaxWorktrees);
        Assert.Equal(1, trends[^1].MaxSafeToReap);
    }

    [Fact]
    public void WeeklyTrends_PeaksPerWeek_AndEmptyWeeksAreZero()
    {
        var tenant = TenantId.Local;
        var store = new RepoHistoryStore(_path);
        var monday = new DateOnly(2026, 07, 20); // a Monday

        // Two weeks ago: heavy. This week: light. Last week: nothing.
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 30, safe: 9, bytes: 9_000) }, monday.AddDays(-14));
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 3, safe: 1, bytes: 1_000) }, monday);

        var trends = store.WeeklyTrends(tenant, weeks: 3, today: monday);
        Assert.Equal(3, trends.Count);
        Assert.Equal(30, trends[0].MaxWorktrees);   // oldest week
        Assert.Equal(0, trends[1].MaxWorktrees);    // the silent week reads as zero, not carried over
        Assert.Equal(3, trends[2].MaxWorktrees);    // this week
    }

    [Fact]
    public void DirtyOverThreshold_ListsOnlyTodaysOffenders()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 23);
        var store = new RepoHistoryStore(_path);
        store.ObserveSnapshot(tenant, "d1", new[]
        {
            Repo("old-mess", uncommitted: 434, dirtySince: DateTime.UtcNow.AddDays(-12)),
            Repo("fresh", uncommitted: 3, dirtySince: DateTime.UtcNow.AddDays(-1)),
        }, day);

        var dirty = store.DirtyOverThreshold(tenant, day);
        Assert.Equal("old-mess", Assert.Single(dirty).Name);
        Assert.True(dirty[0].DirtyDays >= 12);
    }

    [Fact]
    public void ProvisionalRows_NeverBecomeHistory()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 23);
        var store = new RepoHistoryStore(_path);
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("cached", worktrees: 99, provisional: true) }, day);

        Assert.Equal(0, store.WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F13): the history key is the repository PATH, not the leaf
    // name. Two repositories sharing a folder name on one machine keep separate daily rows.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void SameLeafName_DifferentPaths_DoNotOverwriteEachOther()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        var first = Repo("widget", worktrees: 5, safe: 2);
        first.Path = @"D:\repos\widget";
        var second = Repo("widget", worktrees: 3, safe: 1);
        second.Path = @"D:\other\widget";

        store.ObserveSnapshot(tenant, "d1", new[] { first, second }, day);

        // Both rows survive: the day's fleet totals SUM the two same-named repositories.
        var trends = store.WeeklyTrends(tenant, weeks: 1, today: day);
        Assert.Equal(8, trends[^1].MaxWorktrees);
        Assert.Equal(3, trends[^1].MaxSafeToReap);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F13): legacy rows without a path (the pre-path file
    // format, unreleased) cannot be keyed and are ignored on load - no migration.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void Load_IgnoresLegacyRowsWithoutAPath()
    {
        var day = new DateOnly(2026, 07, 24);
        File.WriteAllLines(_path, new[]
        {
            // A legacy line: no Path property at all.
            "{\"Tenant\":\"local\",\"Date\":\"2026-07-24\",\"MachineName\":\"M1\",\"Name\":\"widget\",\"WorktreeCount\":9}",
        });

        var store = new RepoHistoryStore(_path);
        Assert.Equal(0, store.WeeklyTrends(TenantId.Local, 1, day)[^1].MaxWorktrees);
    }

    [Fact]
    public void PathlessPushedRow_IsIgnored_NotKeyed()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        var pathless = Repo("ghost", worktrees: 9);
        pathless.Path = "";
        store.ObserveSnapshot(tenant, "d1", new[] { pathless }, day);

        Assert.Equal(0, store.WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-9): the history key includes the DirectorId.
    // Two Directors reporting the same machine name and repository path (overlapping upgrades,
    // duplicate registrations) must keep separate daily rows, not overwrite each other.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void SameMachineAndPath_TwoDirectors_KeepSeparateRows()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        var fromDirector1 = Repo("widget", worktrees: 5, safe: 2);
        var fromDirector2 = Repo("widget", worktrees: 3, safe: 1);
        fromDirector2.DirectorId = "d2"; // same machine, same path, different Director

        store.ObserveSnapshot(tenant, "d1", new[] { fromDirector1 }, day);
        store.ObserveSnapshot(tenant, "d2", new[] { fromDirector2 }, day);

        // Both rows survive: the day's fleet totals SUM the two Directors' rows.
        var trends = store.WeeklyTrends(tenant, weeks: 1, today: day);
        Assert.Equal(8, trends[^1].MaxWorktrees);
        Assert.Equal(3, trends[^1].MaxSafeToReap);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-9): trailing path separators are trimmed
    // before keying, so the same repository pushed as "...\widget" and "...\widget\" is ONE
    // row (last write wins), never two rows double-counting the totals.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void TrailingSeparator_DoesNotSplitTheRow()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        var bare = Repo("widget", worktrees: 5, safe: 2);
        var trailing = Repo("widget", worktrees: 4, safe: 1);
        trailing.Path = bare.Path + @"\";

        store.ObserveSnapshot(tenant, "d1", new[] { bare }, day);
        store.ObserveSnapshot(tenant, "d1", new[] { trailing }, day);

        // One row, last write wins - never a double count.
        var trends = store.WeeklyTrends(tenant, weeks: 1, today: day);
        Assert.Equal(4, trends[^1].MaxWorktrees);
        Assert.Equal(1, trends[^1].MaxSafeToReap);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-9): legacy rows without a DirectorId (the
    // pre-DirectorId file format, unreleased) cannot be keyed and are ignored on load - the
    // same no-migration rule as the pathless rows from finding F13.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void Load_IgnoresLegacyRowsWithoutADirectorId()
    {
        var day = new DateOnly(2026, 07, 24);
        File.WriteAllLines(_path, new[]
        {
            // A legacy line: a Path but no DirectorId property.
            "{\"Tenant\":\"local\",\"Date\":\"2026-07-24\",\"MachineName\":\"M1\",\"Path\":\"D:\\\\repos\\\\widget\",\"Name\":\"widget\",\"WorktreeCount\":9}",
        });

        var store = new RepoHistoryStore(_path);
        Assert.Equal(0, store.WeeklyTrends(TenantId.Local, 1, day)[^1].MaxWorktrees);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): a single corrupt line in the middle of the history file must NOT
    // erase the good rows AFTER it. The old load caught the first malformed line OUTSIDE the loop,
    // so every row past the damage was dropped, and the next save rewrote the file from the
    // truncated result - permanently erasing that history.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void Load_WithACorruptMiddleLine_KeepsTheGoodRowsAfterIt()
    {
        var day = new DateOnly(2026, 07, 24);
        File.WriteAllLines(_path, new[]
        {
            "{\"Tenant\":\"local\",\"Date\":\"2026-07-24\",\"MachineName\":\"M1\",\"DirectorId\":\"d1\",\"Path\":\"D:\\\\repos\\\\a\",\"Name\":\"a\",\"WorktreeCount\":5}",
            "{ this line is a torn, unparseable write",
            "{\"Tenant\":\"local\",\"Date\":\"2026-07-24\",\"MachineName\":\"M1\",\"DirectorId\":\"d1\",\"Path\":\"D:\\\\repos\\\\b\",\"Name\":\"b\",\"WorktreeCount\":3}",
        });

        var store = new RepoHistoryStore(_path);
        // Both good rows survive: the same-day fleet total sums them (5 + 3), rather than the row
        // after the corruption being lost.
        Assert.Equal(8, store.WeeklyTrends(TenantId.Local, 1, day)[^1].MaxWorktrees);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): the file is written atomically (temp + swap) and the previous file is
    // kept as a .bak, so an interrupted write cannot truncate the live history. After a second
    // save the .bak exists; the old in-place File.WriteAllLines produced no backup at all.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void Save_WritesAtomically_AndKeepsABackup()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 5) }, day);
        store.SaveIfChanged();
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 6) }, day);
        store.SaveIfChanged(); // second save swaps in a new file

        Assert.True(File.Exists(_path));
        Assert.True(File.Exists(_path + ".bak"), "the atomic swap must keep the previous file as a backup");
        Assert.False(File.Exists(_path + ".tmp"), "the temp file must be gone after the swap");
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): a full snapshot that no longer contains a repository must remove
    // its row from today, not leave it in the dirty callouts and double-counted. The old observe
    // only upserted the rows present and never reconciled the ones that disappeared.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void Snapshot_ThatDropsARepo_RemovesItsRowFromToday()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 5), Repo("b", worktrees: 3) }, day);
        // The next push from the same Director no longer includes "b" (removed or moved).
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 5) }, day);

        // Today's fleet total reflects only "a"; "b" is gone rather than double-counted.
        Assert.Equal(5, store.WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees);
        Assert.Empty(store.DirtyOverThreshold(tenant, day)); // and not lingering as a callout
    }

    // A renamed/moved repository: the old path's row is dropped and only the new path counts.
    [Fact]
    public void Snapshot_RenamingARepo_DropsTheOldPathRow()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        var oldPath = Repo("widget", worktrees: 5);
        oldPath.Path = @"D:\repos\widget";
        store.ObserveSnapshot(tenant, "d1", new[] { oldPath }, day);

        var newPath = Repo("widget", worktrees: 5);
        newPath.Path = @"D:\repos\widget-renamed";
        store.ObserveSnapshot(tenant, "d1", new[] { newPath }, day);

        // Only the new path counts - the old path is not left behind doubling the total.
        Assert.Equal(5, store.WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees);
    }

    // REGRESSION (inspection): an EMPTY or all-provisional snapshot must NOT be mistaken for "all
    // repositories removed" and erase today's verified history - a cold start or warm-cache push
    // sends exactly that before the first live scan. Reconciliation only runs on a real observation.
    [Fact]
    public void EmptySnapshot_DoesNotEraseVerifiedHistory()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 5) }, day);
        store.ObserveSnapshot(tenant, "d1", Array.Empty<RepoStatusDto>(), day); // startup / warm-cache empty push

        Assert.Equal(5, store.WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees); // preserved, not erased
    }

    [Fact]
    public void AllProvisionalSnapshot_DoesNotEraseVerifiedHistory()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 5) }, day);
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 99, provisional: true) }, day);

        Assert.Equal(5, store.WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees); // the provisional push changed nothing
    }

    // REGRESSION (inspection): reconciliation is scoped to the BOUND Director, and rows are stamped
    // with it - a payload DirectorId cannot make one Director rewrite or reconcile another's history.
    [Fact]
    public void PayloadDirectorId_CannotReconcileAnotherDirectorsRows()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        // d2 has a real row for the day.
        var real = Repo("widget", worktrees: 3);
        real.DirectorId = "d2";
        store.ObserveSnapshot(tenant, "d2", new[] { real }, day);

        // A connection BOUND as d1 pushes a row whose PAYLOAD claims d2. It must be stamped d1 and
        // reconcile only d1's scope, never d2's.
        var spoof = Repo("evil", worktrees: 1);
        spoof.DirectorId = "d2"; // spoofed payload
        store.ObserveSnapshot(tenant, "d1", new[] { spoof }, day);

        // d2's widget survives; the spoof landed as a d1 row. Fleet total for the day = 3 + 1.
        Assert.Equal(4, store.WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): an accepted push whose values did not change must NOT rewrite the
    // whole global history file. Every Director re-pushes its full snapshot on a periodic cadence,
    // and the old observe rewrote every historical row for every tenant on each one.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void RepeatedIdenticalSnapshot_DoesNotRewriteTheFile()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 5) }, day);
        store.SaveIfChanged();
        Assert.True(File.Exists(_path));
        var firstWrite = File.GetLastWriteTimeUtc(_path);

        Thread.Sleep(80);
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 5) }, day); // identical values
        store.SaveIfChanged();

        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(_path)); // no rewrite for an unchanged observation
    }

    // A genuinely changed value still rewrites (the change-detection is not too eager).
    [Fact]
    public void ChangedSnapshot_DoesRewriteTheFile()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 5) }, day);
        store.SaveIfChanged();
        var firstWrite = File.GetLastWriteTimeUtc(_path);

        Thread.Sleep(80);
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 6) }, day); // a real change
        store.SaveIfChanged();

        Assert.True(File.GetLastWriteTimeUtc(_path) > firstWrite);
        Assert.Equal(6, store.WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): rows older than the retention window are pruned, so the file and
    // the in-memory dictionary do not grow forever. The old store never aged anything out.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void Observe_PrunesRowsOlderThanTheRetentionWindow()
    {
        var tenant = TenantId.Local;
        var store = new RepoHistoryStore(_path);
        var today = new DateOnly(2026, 07, 24);
        var ancient = today.AddDays(-(RepoHistoryStore.RetentionDays + 10)); // safely outside the window

        store.ObserveSnapshot(tenant, "d1", new[] { Repo("ancient", worktrees: 9) }, ancient);
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("recent", worktrees: 2) }, today); // prunes on this push
        store.SaveIfChanged();

        // Persisted: the ancient row is gone; the recent one stays.
        var reloaded = new RepoHistoryStore(_path);
        Assert.Equal(0, reloaded.WeeklyTrends(tenant, 1, ancient)[^1].MaxWorktrees);
        Assert.Equal(2, reloaded.WeeklyTrends(tenant, 1, today)[^1].MaxWorktrees);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection): a SUPPRESSED save failure must be retried, even when the next
    // observation is logically unchanged. Change detection compares in-memory rows, so without a
    // pending-failure flag the update would stay non-durable until some unrelated value changed.
    // Since saves moved onto the host's clock (Money Saver), the retry is the next clock tick.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void SuppressedSaveFailure_IsRetriedOnTheNextSave_EvenWhenUnchanged()
    {
        // Force the first save to fail: put a FILE where the history file's parent directory would be,
        // so Directory.CreateDirectory throws.
        var blocker = _path + "-blocker";
        File.WriteAllText(blocker, "x");
        var historyPath = Path.Combine(blocker, "history.jsonl");
        try
        {
            var store = new RepoHistoryStore(historyPath);
            var day = new DateOnly(2026, 07, 24);

            store.ObserveSnapshot(TenantId.Local, "d1", new[] { Repo("a", worktrees: 5) }, day);
            Assert.False(store.SaveIfChanged(), "the first save was forced to fail");
            Assert.False(File.Exists(historyPath), "the first save was forced to fail");

            // Clear the blocker so a save can now succeed, then push an IDENTICAL snapshot. Change
            // detection alone would skip the write; the pending failure must force a retry.
            File.Delete(blocker);
            store.ObserveSnapshot(TenantId.Local, "d1", new[] { Repo("a", worktrees: 5) }, day);
            Assert.True(store.SaveIfChanged());

            Assert.True(File.Exists(historyPath), "a suppressed save failure must be retried on the next save");
        }
        finally
        {
            if (File.Exists(historyPath)) File.Delete(historyPath);
            if (File.Exists(blocker)) File.Delete(blocker);
            if (Directory.Exists(blocker)) Directory.Delete(blocker, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Money Saver (4 October 2026): an observation does not touch the file; the host's clock does. One
    // file holds every account's history on a billed share and was rewritten on every changed push.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void ChangedObservation_DoesNotWriteTheFile_UntilSaveIfChanged()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 5) }, day);
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 6) }, day);

        Assert.False(File.Exists(_path), "an observation must only mark the rows unsaved");
        Assert.Equal(6, store.WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees); // readers see it at once

        Assert.True(store.SaveIfChanged());
        Assert.Equal(6, new RepoHistoryStore(_path).WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees);
    }

    [Fact]
    public void SaveIfChanged_WithNothingUnsaved_DoesNotRewriteTheFile()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("a", worktrees: 5) }, day);
        store.SaveIfChanged();
        var firstWrite = File.GetLastWriteTimeUtc(_path);

        Thread.Sleep(80);
        Assert.True(store.SaveIfChanged());

        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(_path));
    }

    // REGRESSION (inspection): a readable-but-corrupt live file lost row B, so the load must recover
    // B from the good backup rather than silently drop it (and let the next save overwrite the good
    // backup with the corrupt file).
    [Fact]
    public void CorruptLiveFile_RecoversTheLostRowsFromTheBackup()
    {
        var day = new DateOnly(2026, 07, 24);
        string Row(string name, int wt) =>
            "{\"Tenant\":\"local\",\"Date\":\"2026-07-24\",\"MachineName\":\"M1\",\"DirectorId\":\"d1\"," +
            $"\"Path\":\"D:\\\\repos\\\\{name}\",\"Name\":\"{name}\",\"WorktreeCount\":{wt}}}";

        File.WriteAllLines(_path + ".bak", new[] { Row("A", 1), Row("B", 2), Row("C", 4) });
        File.WriteAllLines(_path, new[] { Row("A", 1), "{ this line is torn", Row("C", 4) });

        var store = new RepoHistoryStore(_path);
        // B (2) is recovered from the backup: A + B + C = 7, not 5.
        Assert.Equal(7, store.WeeklyTrends(TenantId.Local, 1, day)[^1].MaxWorktrees);
    }

    // REGRESSION (inspection): a MIXED startup snapshot - some repos verified, one still provisional -
    // must NOT reconcile away the verified history of the still-warming-up repository.
    [Fact]
    public void MixedSnapshot_WithAProvisionalRow_DoesNotEraseTheOtherRepositorysHistory()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        store.ObserveSnapshot(tenant, "d1", new[] { Repo("A", worktrees: 5), Repo("B", worktrees: 3) }, day);
        // Later push: A verified, B still provisional (warming up) - a partial view.
        store.ObserveSnapshot(tenant, "d1", new[] { Repo("A", worktrees: 5), Repo("B", worktrees: 99, provisional: true) }, day);

        // B's verified row (3) survives: A + B = 8, not 5.
        Assert.Equal(8, store.WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees);
    }

    [Fact]
    public void TenantPartition_TrendsNeverMix()
    {
        var a = new TenantId("tenant-a");
        var b = new TenantId("tenant-b");
        var day = new DateOnly(2026, 07, 23);
        var store = new RepoHistoryStore(_path);
        store.ObserveSnapshot(a, "d1", new[] { Repo("a-repo", worktrees: 7) }, day);
        store.ObserveSnapshot(b, "d1", new[] { Repo("b-repo", worktrees: 2) }, day);

        Assert.Equal(7, store.WeeklyTrends(a, 1, day)[^1].MaxWorktrees);
        Assert.Equal(2, store.WeeklyTrends(b, 1, day)[^1].MaxWorktrees);
    }
}
