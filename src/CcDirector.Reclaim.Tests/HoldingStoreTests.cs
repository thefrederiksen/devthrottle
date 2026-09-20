using System.Globalization;
using System.Text.Json;
using CcDirector.Reclaim.Removal;
using CcDirector.Reclaim.Rules;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The holding folder: where removal puts what it moves, and the record that makes each move
/// answerable. Every tree here is built by the test that uses it and destroyed when it ends - the
/// holding root always sits INSIDE the fixture tree, because the engine takes it as a parameter and
/// only the command line computes a per-volume default.
///
/// The record's shape on disk is pinned by a test that reads the raw record.json, because the record
/// is the contract between a move and every later restore: a field renamed here is a field no entry
/// written by an older build can answer.
/// </summary>
public sealed class HoldingStoreTests
{
    private const int HoldingPeriod = 30;

    private static ReclaimCandidate FolderCandidate(string path, long bytes) => new(
        path,
        bytes,
        new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        "created by one of DevThrottle's own test suites, older than the age gate, with nothing in it open");

    [Fact]
    public void Hold_AFolderThatWasProvedDisposable_MovesItIntoHoldingWithACompleteRecord()
    {
        using var tree = new FixtureTree(nameof(Hold_AFolderThatWasProvedDisposable_MovesItIntoHoldingWithACompleteRecord));
        var item = tree.Folder("temp/cc-director-tests");
        tree.File("temp/cc-director-tests/inside.txt", 700);
        var holding = new HoldingStore(tree.Folder("holding"));

        var outcome = holding.Hold(FolderCandidate(item, 700), "devthrottle-test-scratch-folders", DateTimeOffset.UtcNow);

        Assert.True(outcome.Held, outcome.RefusalReason);
        Assert.Null(outcome.IncompleteReason);
        Assert.False(Directory.Exists(item));
        var entryPath = Path.Combine(holding.Root, outcome.EntryId!);
        Assert.True(Directory.Exists(Path.Combine(entryPath, "cc-director-tests")));

        var record = ReadRecord(entryPath);
        Assert.Equal(HoldingState.Held, record.State);
        Assert.Equal(Path.GetFullPath(item), record.OriginalPath);
        Assert.Equal("cc-director-tests", record.Name);
        Assert.Equal(700, record.Bytes);
        Assert.Equal("devthrottle-test-scratch-folders", record.Rule);
        Assert.Equal(record.MovedAtUtc.AddDays(HoldingPeriodDays()), record.PurgeNotBeforeUtc);
    }

    [Fact]
    public void Hold_AFile_MovesItToo()
    {
        using var tree = new FixtureTree(nameof(Hold_AFile_MovesItToo));
        var item = tree.File("temp/orphan.msi", 4096);
        var holding = new HoldingStore(tree.Folder("holding"));

        var outcome = holding.Hold(new ReclaimCandidate(item, 4096, DateTimeOffset.UtcNow, "a record says nothing needs it"),
            "orphaned-windows-installer-packages", DateTimeOffset.UtcNow);

        Assert.True(outcome.Held, outcome.RefusalReason);
        Assert.False(File.Exists(item));
        var entryPath = Path.Combine(holding.Root, outcome.EntryId!);
        Assert.True(File.Exists(Path.Combine(entryPath, "orphan.msi")));
        Assert.Equal("orphan.msi", ReadRecord(entryPath).Name);
    }

    [Fact]
    public void Hold_AnItemThatIsNotThereAnyMore_IsRefusedAndNothingIsWritten()
    {
        using var tree = new FixtureTree(nameof(Hold_AnItemThatIsNotThereAnyMore_IsRefusedAndNothingIsWritten));
        // The holding root is only a path here, never created: the refusal must happen before anything
        // is made on the disk.
        var holding = new HoldingStore(Path.Combine(tree.Root, "holding"));

        var outcome = holding.Hold(FolderCandidate(Path.Combine(tree.Root, "temp", "not-there"), 1),
            "devthrottle-test-scratch-folders", DateTimeOffset.UtcNow);

        Assert.False(outcome.Held);
        Assert.Null(outcome.EntryId);
        Assert.NotNull(outcome.RefusalReason);
        Assert.False(Directory.Exists(holding.Root));
    }

    [Fact]
    public void Hold_WhenTheHoldingRootCannotBeCreated_RefusesAndTheItemStays()
    {
        using var tree = new FixtureTree(nameof(Hold_WhenTheHoldingRootCannotBeCreated_RefusesAndTheItemStays));
        var item = tree.Folder("temp/cc-director-tests");
        tree.File("temp/cc-director-tests/inside.txt", 100);
        // A file standing where the holding root would be: the root cannot be created, and the item
        // must be refused rather than held anywhere else.
        var holdingPath = tree.File("blocked", 1);
        var holding = new HoldingStore(holdingPath);

        var outcome = holding.Hold(FolderCandidate(item, 100), "devthrottle-test-scratch-folders", DateTimeOffset.UtcNow);

        Assert.False(outcome.Held);
        Assert.Contains("cannot be created or written", outcome.RefusalReason);
        Assert.True(Directory.Exists(item));
    }

    [Fact]
    public void Hold_WhenTheFileSystemRefusesTheMove_KeepsTheItemAndLeavesNoEntry()
    {
        using var tree = new FixtureTree(nameof(Hold_WhenTheFileSystemRefusesTheMove_KeepsTheItemAndLeavesNoEntry));
        var item = tree.Folder("temp/cc-director-tests");
        tree.File("temp/cc-director-tests/inside.txt", 100);
        var holding = new HoldingStore(tree.Folder("holding"));

        // A file held by this test for the length of the check: the move of the folder around it is
        // refused by the file system, which is exactly the shape a live folder on a real disk has.
        using (new FileStream(Path.Combine(item, "inside.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = holding.Hold(FolderCandidate(item, 100), "devthrottle-test-scratch-folders", DateTimeOffset.UtcNow);
            Assert.False(outcome.Held);
            Assert.Contains("refused to move", outcome.RefusalReason);
            Assert.True(Directory.Exists(item));
        }

        Assert.True(File.Exists(Path.Combine(item, "inside.txt")));
        Assert.Empty(Directory.GetDirectories(holding.Root));
    }

    [Fact]
    public void Hold_TwiceOnTheSameDay_GivesTwoDifferentEntries()
    {
        using var tree = new FixtureTree(nameof(Hold_TwiceOnTheSameDay_GivesTwoDifferentEntries));
        var holding = new HoldingStore(tree.Folder("holding"));
        var first = tree.Folder("temp/cc-director-tests");
        var second = tree.Folder("temp/cc-director-harness-tests");

        var firstOutcome = holding.Hold(FolderCandidate(first, 0), "devthrottle-test-scratch-folders", DateTimeOffset.UtcNow);
        var secondOutcome = holding.Hold(FolderCandidate(second, 0), "devthrottle-test-scratch-folders", DateTimeOffset.UtcNow);

        Assert.True(firstOutcome.Held && secondOutcome.Held);
        Assert.NotEqual(firstOutcome.EntryId, secondOutcome.EntryId);
    }

    [Fact]
    public void List_ARootThatDoesNotExist_IsCountZeroNotAnError()
    {
        using var tree = new FixtureTree(nameof(List_ARootThatDoesNotExist_IsCountZeroNotAnError));
        var holding = new HoldingStore(Path.Combine(tree.Root, "holding"));

        var listing = holding.List();

        Assert.False(listing.RootExists);
        Assert.Empty(listing.Complete);
        Assert.Empty(listing.Incomplete);
        Assert.Null(listing.UnreadableReason);
    }

    [Fact]
    public void List_SeparatesCompleteEntriesFromIncompleteOnesAndNamesBoth()
    {
        using var tree = new FixtureTree(nameof(List_SeparatesCompleteEntriesFromIncompleteOnesAndNamesBoth));
        var holding = new HoldingStore(tree.Folder("holding"));

        var item = tree.Folder("temp/cc-director-tests");
        var held = holding.Hold(FolderCandidate(item, 0), "devthrottle-test-scratch-folders", DateTimeOffset.UtcNow);
        Assert.True(held.Held);

        // An incomplete entry, written directly by this test the way a crash between the record and
        // the move would have left it.
        WriteRecord(
            Path.Combine(holding.Root, "2026-01-01-00000000"),
            new HoldingRecord
            {
                OriginalPath = Path.Combine(tree.Root, "temp", "cc-instances-something"),
                Name = "cc-instances-something",
                Bytes = 5,
                LastWrittenUtc = DateTimeOffset.UtcNow,
                Rule = "devthrottle-test-scratch-folders",
                MovedAtUtc = DateTimeOffset.UtcNow,
                PurgeNotBeforeUtc = DateTimeOffset.UtcNow.AddDays(HoldingPeriodDays()),
                State = HoldingState.Moving
            });

        var listing = holding.List();

        Assert.True(listing.RootExists);
        Assert.Single(listing.Complete);
        Assert.Equal(held.EntryId, listing.Complete[0].EntryId);
        Assert.Single(listing.Incomplete);
        Assert.Equal("2026-01-01-00000000", listing.Incomplete[0].EntryId);
        Assert.Null(listing.UnreadableReason);
    }

    [Fact]
    public void List_ARootThatExistsButCannotBeRead_IsAnErrorNamingWhy_NeverAnEmptyList()
    {
        using var tree = new FixtureTree(nameof(List_ARootThatExistsButCannotBeRead_IsAnErrorNamingWhy_NeverAnEmptyList));
        var holdingRoot = tree.Folder("holding");
        var holding = new HoldingStore(holdingRoot);

        var item = tree.Folder("temp/cc-director-tests");
        Assert.True(holding.Hold(FolderCandidate(item, 0), "devthrottle-test-scratch-folders", DateTimeOffset.UtcNow).Held);

        tree.DenyListing("holding");

        var listing = holding.List();

        Assert.NotNull(listing.UnreadableReason);
        Assert.Contains("cannot be read", listing.UnreadableReason);
    }

    [Fact]
    public void Restore_ANewlyHeldEntry_ReturnsTheItemByteForByteAndClearsTheEntry()
    {
        using var tree = new FixtureTree(nameof(Restore_ANewlyHeldEntry_ReturnsTheItemByteForByteAndClearsTheEntry));
        var holding = new HoldingStore(tree.Folder("holding"));
        var item = tree.Folder("temp/cc-director-tests");
        tree.File("temp/cc-director-tests/inside.txt", 4242);
        var held = holding.Hold(FolderCandidate(item, 4242), "devthrottle-test-scratch-folders", DateTimeOffset.UtcNow);
        Assert.True(held.Held);

        var outcome = holding.Restore(held.EntryId!);

        Assert.True(outcome.Restored, outcome.RefusalReason);
        Assert.False(outcome.AlreadyHome);
        Assert.Equal(Path.GetFullPath(item), outcome.OriginalPath);
        Assert.True(File.Exists(Path.Combine(item, "inside.txt")));
        Assert.Equal(4242, new FileInfo(Path.Combine(item, "inside.txt")).Length);
        Assert.False(Directory.Exists(Path.Combine(holding.Root, held.EntryId!)));
    }

    [Fact]
    public void Restore_WhenTheOriginalParentIsGone_RefusesAndKeepsTheEntry()
    {
        using var tree = new FixtureTree(nameof(Restore_WhenTheOriginalParentIsGone_RefusesAndKeepsTheEntry));
        var holding = new HoldingStore(tree.Folder("holding"));
        var item = tree.Folder("temp/cc-director-tests");
        var held = holding.Hold(FolderCandidate(item, 0), "devthrottle-test-scratch-folders", DateTimeOffset.UtcNow);
        Assert.True(held.Held);
        Directory.Delete(tree.Folder("temp"));

        var outcome = holding.Restore(held.EntryId!);

        Assert.False(outcome.Restored);
        Assert.Contains("nowhere to go back to", outcome.RefusalReason);
        Assert.True(Directory.Exists(Path.Combine(holding.Root, held.EntryId!)));
    }

    [Fact]
    public void Restore_WhenSomethingNowStandsAtTheOriginalPath_RefusesAndNeverOverwrites()
    {
        using var tree = new FixtureTree(nameof(Restore_WhenSomethingNowStandsAtTheOriginalPath_RefusesAndNeverOverwrites));
        var holding = new HoldingStore(tree.Folder("holding"));
        var item = tree.Folder("temp/cc-director-tests");
        var held = holding.Hold(FolderCandidate(item, 0), "devthrottle-test-scratch-folders", DateTimeOffset.UtcNow);
        Assert.True(held.Held);

        // Somebody else's folder, standing where the held item came from.
        var newcomer = tree.Folder("temp/cc-director-tests");
        tree.File("temp/cc-director-tests/somebody-else.txt", 12);

        var outcome = holding.Restore(held.EntryId!);

        Assert.False(outcome.Restored);
        Assert.Contains("never overwrites", outcome.RefusalReason);
        Assert.True(File.Exists(Path.Combine(newcomer, "somebody-else.txt")));
        Assert.True(Directory.Exists(Path.Combine(holding.Root, held.EntryId!)));
    }

    [Fact]
    public void Restore_AnIncompleteEntryWhoseMoveNeverHappened_ReportsAlreadyHomeAndClearsTheEntry()
    {
        using var tree = new FixtureTree(
            nameof(Restore_AnIncompleteEntryWhoseMoveNeverHappened_ReportsAlreadyHomeAndClearsTheEntry));
        var holding = new HoldingStore(tree.Folder("holding"));
        var item = tree.Folder("temp/cc-director-tests");
        tree.File("temp/cc-director-tests/inside.txt", 100);

        // The crash shape: a record written for a move that never happened, with the item still at
        // its original path.
        WriteRecord(
            Path.Combine(holding.Root, "2026-01-01-00000000"),
            new HoldingRecord
            {
                OriginalPath = Path.GetFullPath(item),
                Name = "cc-director-tests",
                Bytes = 100,
                LastWrittenUtc = DateTimeOffset.UtcNow,
                Rule = "devthrottle-test-scratch-folders",
                MovedAtUtc = DateTimeOffset.UtcNow,
                PurgeNotBeforeUtc = DateTimeOffset.UtcNow.AddDays(HoldingPeriodDays()),
                State = HoldingState.Moving
            });

        var outcome = holding.Restore("2026-01-01-00000000");

        Assert.True(outcome.AlreadyHome);
        Assert.False(outcome.Restored);
        Assert.True(File.Exists(Path.Combine(item, "inside.txt")));
        Assert.False(Directory.Exists(Path.Combine(holding.Root, "2026-01-01-00000000")));
    }

    [Fact]
    public void Restore_AnIncompleteEntryWhoseMoveDidHappen_PutsTheItemBack()
    {
        using var tree = new FixtureTree(nameof(Restore_AnIncompleteEntryWhoseMoveDidHappen_PutsTheItemBack));
        var holding = new HoldingStore(tree.Folder("holding"));
        var item = tree.Folder("temp/cc-director-tests");
        tree.File("temp/cc-director-tests/inside.txt", 100);
        var held = holding.Hold(FolderCandidate(item, 100), "devthrottle-test-scratch-folders", DateTimeOffset.UtcNow);
        Assert.True(held.Held);

        // The other crash shape: the move happened but the record was never finalized. The item is
        // in holding, the original path is free, and the record still says the move is in progress.
        WriteRecord(
            Path.Combine(holding.Root, held.EntryId!),
            new HoldingRecord
            {
                OriginalPath = Path.GetFullPath(item),
                Name = "cc-director-tests",
                Bytes = 100,
                LastWrittenUtc = DateTimeOffset.UtcNow,
                Rule = "devthrottle-test-scratch-folders",
                MovedAtUtc = DateTimeOffset.UtcNow,
                PurgeNotBeforeUtc = DateTimeOffset.UtcNow.AddDays(HoldingPeriodDays()),
                State = HoldingState.Moving
            });

        var outcome = holding.Restore(held.EntryId!);

        Assert.True(outcome.Restored, outcome.RefusalReason);
        Assert.True(File.Exists(Path.Combine(item, "inside.txt")));
        Assert.False(Directory.Exists(Path.Combine(holding.Root, held.EntryId!)));
    }

    [Fact]
    public void Restore_AnEntryThatIsNotThere_IsRefusedNamingWhy()
    {
        using var tree = new FixtureTree(nameof(Restore_AnEntryThatIsNotThere_IsRefusedNamingWhy));
        var holding = new HoldingStore(tree.Folder("holding"));

        var outcome = holding.Restore("2026-01-01-ffffffff");

        Assert.False(outcome.Restored);
        Assert.Contains("there is no entry", outcome.RefusalReason);
    }

    [Fact]
    public void Purge_ByDefault_IsADryRunThatNamesWhatIsNotYetPurgeable()
    {
        using var tree = new FixtureTree(nameof(Purge_ByDefault_IsADryRunThatNamesWhatIsNotYetPurgeable));
        var holding = new HoldingStore(tree.Folder("holding"));
        var item = tree.Folder("temp/cc-director-tests");
        var now = DateTimeOffset.UtcNow;
        var held = holding.Hold(FolderCandidate(item, 0), "devthrottle-test-scratch-folders", now);
        Assert.True(held.Held);

        var outcome = holding.Purge(now, apply: false);

        Assert.False(outcome.Applied);
        Assert.Empty(outcome.Purgeable);
        Assert.Single(outcome.NotYetPurgeable);
        Assert.Empty(outcome.PurgedEntryIds);
        Assert.True(Directory.Exists(Path.Combine(holding.Root, held.EntryId!)));
    }

    [Fact]
    public void Purge_AfterTheHoldingPeriod_WithApply_RemovesTheEntry()
    {
        using var tree = new FixtureTree(nameof(Purge_AfterTheHoldingPeriod_WithApply_RemovesTheEntry));
        var holding = new HoldingStore(tree.Folder("holding"));
        var item = tree.Folder("temp/cc-director-tests");
        var now = DateTimeOffset.UtcNow;
        var held = holding.Hold(FolderCandidate(item, 0), "devthrottle-test-scratch-folders", now);
        Assert.True(held.Held);

        var outcome = holding.Purge(now.AddDays(HoldingPeriodDays() + 1), apply: true);

        Assert.True(outcome.Applied);
        Assert.Single(outcome.Purgeable);
        Assert.Equal(held.EntryId, outcome.PurgedEntryIds[0]);
        Assert.False(Directory.Exists(Path.Combine(holding.Root, held.EntryId!)));
        Assert.False(Directory.Exists(item));
    }

    [Fact]
    public void Purge_WithADaysOverrideForThisCall_JudgesAgainstIt()
    {
        using var tree = new FixtureTree(nameof(Purge_WithADaysOverrideForThisCall_JudgesAgainstIt));
        var holding = new HoldingStore(tree.Folder("holding"));
        var item = tree.Folder("temp/cc-director-tests");
        var now = DateTimeOffset.UtcNow;
        var held = holding.Hold(FolderCandidate(item, 0), "devthrottle-test-scratch-folders", now);
        Assert.True(held.Held);

        var twoDaysLater = now.AddDays(2);
        var withDefault = holding.Purge(twoDaysLater, apply: false);
        var withOneDay = holding.Purge(twoDaysLater, apply: false, daysOverride: 1);

        Assert.Empty(withDefault.Purgeable);
        Assert.Single(withOneDay.Purgeable);
    }

    [Fact]
    public void Purge_AlwaysRefusesIncompleteEntries_EvenPastTheirPeriod()
    {
        using var tree = new FixtureTree(nameof(Purge_AlwaysRefusesIncompleteEntries_EvenPastTheirPeriod));
        var holding = new HoldingStore(tree.Folder("holding"));
        var now = DateTimeOffset.UtcNow;

        WriteRecord(
            Path.Combine(holding.Root, "2026-01-01-00000000"),
            new HoldingRecord
            {
                OriginalPath = Path.Combine(tree.Root, "temp", "cc-instances-something"),
                Name = "cc-instances-something",
                Bytes = 5,
                LastWrittenUtc = now.AddDays(-400),
                Rule = "devthrottle-test-scratch-folders",
                MovedAtUtc = now.AddDays(-400),
                PurgeNotBeforeUtc = now.AddDays(-400 + HoldingPeriodDays()),
                State = HoldingState.Moving
            });

        var outcome = holding.Purge(now, apply: true);

        Assert.Empty(outcome.Purgeable);
        Assert.Single(outcome.Incomplete);
        Assert.Empty(outcome.PurgedEntryIds);
        Assert.True(Directory.Exists(Path.Combine(holding.Root, "2026-01-01-00000000")));
    }

    [Fact]
    public void Purge_AnEntryWhoseFolderCannotBeRemoved_IsKeptAndNamed()
    {
        using var tree = new FixtureTree(nameof(Purge_AnEntryWhoseFolderCannotBeRemoved_IsKeptAndNamed));
        var holding = new HoldingStore(tree.Folder("holding"));
        var item = tree.Folder("temp/cc-director-tests");
        tree.File("temp/cc-director-tests/inside.txt", 100);
        var now = DateTimeOffset.UtcNow;
        var held = holding.Hold(FolderCandidate(item, 100), "devthrottle-test-scratch-folders", now);
        Assert.True(held.Held);

        // A file inside the entry, held open by this test for the length of the call: the entry
        // cannot be removed, so it is kept and named rather than assumed.
        using (new FileStream(
            Path.Combine(holding.Root, held.EntryId!, "cc-director-tests", "inside.txt"),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = holding.Purge(now.AddDays(HoldingPeriodDays() + 1), apply: true);

            Assert.Empty(outcome.PurgedEntryIds);
            Assert.Single(outcome.Kept);
            Assert.Contains("could not be removed", outcome.Kept[0].Reason);
        }
    }

    [Fact]
    public void RecordShape_OnDisk_CarriesExactlyTheFieldsThePlanNames()
    {
        using var tree = new FixtureTree(nameof(RecordShape_OnDisk_CarriesExactlyTheFieldsThePlanNames));
        var holding = new HoldingStore(tree.Folder("holding"));
        var item = tree.Folder("temp/cc-director-tests");
        var movedAt = new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);
        var held = holding.Hold(
            FolderCandidate(item, 700),
            "devthrottle-test-scratch-folders",
            movedAt,
            holdingPeriodDays: 7);
        Assert.True(held.Held);

        var raw = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(holding.Root, held.EntryId!, HoldingRecord.RecordFileName)));
        var root = raw.RootElement;

        string Field(string name)
        {
            Assert.True(root.TryGetProperty(name, out var value), $"record.json has no field {name}");
            return value.ToString();
        }

        DateTimeOffset Moment(string name)
        {
            Assert.True(root.TryGetProperty(name, out var value), $"record.json has no field {name}");
            return DateTimeOffset.Parse(value.GetString() ?? string.Empty, CultureInfo.InvariantCulture);
        }

        Assert.Equal(Path.GetFullPath(item), Field("original-path"));
        Assert.Equal("cc-director-tests", Field("name"));
        Assert.Equal("700", Field("bytes"));
        Assert.Equal("devthrottle-test-scratch-folders", Field("rule"));
        Assert.Equal(movedAt, Moment("moved-at-utc"));
        Assert.Equal(movedAt.AddDays(7), Moment("purge-not-before-utc"));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), Moment("last-written-utc"));
        Assert.Equal("held", Field("state"));

        var named = root.EnumerateObject().Select(property => property.Name).ToList();
        Assert.Equal(
        [
            "original-path", "name", "bytes", "last-written-utc", "rule", "moved-at-utc", "purge-not-before-utc", "state"
        ], named);
    }

    private static int HoldingPeriodDays() => HoldingStore.DefaultHoldingPeriodDays;

    private static HoldingRecord ReadRecord(string entryPath) =>
        JsonSerializer.Deserialize<HoldingRecord>(
            File.ReadAllText(Path.Combine(entryPath, HoldingRecord.RecordFileName)), HoldingRecord.Json)
        ?? throw new InvalidOperationException($"The record at {entryPath} could not be read back.");

    private static void WriteRecord(string entryPath, HoldingRecord record)
    {
        Directory.CreateDirectory(entryPath);
        File.WriteAllText(
            Path.Combine(entryPath, HoldingRecord.RecordFileName),
            JsonSerializer.Serialize(record, HoldingRecord.Json));
    }
}
