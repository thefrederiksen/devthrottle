using CcDirector.Reclaim.Indexing;
using CcDirector.Reclaim.Scanning;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The saved index: the file one program writes and another reads much later. Every test here is
/// about that contract holding, or about the reader refusing loudly when it does not.
/// </summary>
public class ScanIndexStoreTests
{
    [Fact]
    public void SaveThenLoad_AScanOfTheFixtureTree_ReturnsEveryNumberUnchanged()
    {
        using var tree = StandardFixture.Build(nameof(SaveThenLoad_AScanOfTheFixtureTree_ReturnsEveryNumberUnchanged));
        using var home = new FixtureTree("index-home");
        var scan = StandardFixture.Scan(tree);
        var indexPath = ScanIndexStore.PathFor(home.Root, tree.Root);

        ScanIndexStore.Save(indexPath, scan, DateTimeOffset.UtcNow);
        var loaded = ScanIndexStore.Load(indexPath);

        Assert.Equal(ScanIndex.FormatName, loaded.Format);
        Assert.Equal(ScanIndex.CurrentFormatVersion, loaded.FormatVersion);
        Assert.Equal(scan.RootPath, loaded.Scan.RootPath);
        Assert.Equal(StandardFixture.ExpectedBytesSeen, loaded.Scan.BytesSeen);
        Assert.Equal(StandardFixture.ExpectedFilesSeen, loaded.Scan.FilesSeen);
        Assert.Equal(StandardFixture.ExpectedFoldersSeen, loaded.Scan.FoldersSeen);
        Assert.Equal(StandardFixture.ExpectedPlaceholderBytesInCloud, loaded.Scan.PlaceholderBytesInCloud);
        Assert.Equal(scan.Links.Select(link => link.Path), loaded.Scan.Links.Select(link => link.Path));
        Assert.Equal(
            scan.RefusedFolders.Select(folder => folder.Path),
            loaded.Scan.RefusedFolders.Select(folder => folder.Path));
        Assert.Equal(
            DirectoryReadRefusal.AccessDenied,
            Assert.Single(loaded.Scan.RefusedFolders).Refusal);
        Assert.Equal(
            scan.FolderTotals.Select(folder => (folder.Path, folder.BytesSeen, folder.FilesSeen)),
            loaded.Scan.FolderTotals.Select(folder => (folder.Path, folder.BytesSeen, folder.FilesSeen)));
        Assert.Equal(scan.Volume, loaded.Scan.Volume);
    }

    [Fact]
    public void Load_AFileThatIsNotThere_ThrowsAndNamesTheScanCommand()
    {
        using var home = new FixtureTree(nameof(Load_AFileThatIsNotThere_ThrowsAndNamesTheScanCommand));
        var missing = Path.Combine(home.Root, "not-here.json");

        var error = Assert.Throws<FileNotFoundException>(() => ScanIndexStore.Load(missing));

        Assert.Contains("cc-cleanup-storage scan", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AFileThatIsNotASavedScan_ThrowsAndNamesTheScanCommand()
    {
        using var home = new FixtureTree(nameof(Load_AFileThatIsNotASavedScan_ThrowsAndNamesTheScanCommand));
        var path = Path.Combine(home.Root, "rubbish.json");
        File.WriteAllText(path, "this is not a saved scan");

        var error = Assert.Throws<InvalidDataException>(() => ScanIndexStore.Load(path));

        Assert.Contains("cc-cleanup-storage scan", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason the format carries a version from the first commit. A reader that met a file from a
    /// later version and did its best with it would put numbers on a screen that nobody could check.
    /// </summary>
    [Fact]
    public void Load_AVersionThisLibraryDoesNotRead_ThrowsRatherThanReadingWhatItRecognises()
    {
        using var tree = StandardFixture.Build(nameof(Load_AVersionThisLibraryDoesNotRead_ThrowsRatherThanReadingWhatItRecognises));
        using var home = new FixtureTree("index-home");
        var indexPath = ScanIndexStore.PathFor(home.Root, tree.Root);
        ScanIndexStore.Save(indexPath, StandardFixture.Scan(tree), DateTimeOffset.UtcNow);

        var text = File.ReadAllText(indexPath).Replace(
            $"\"formatVersion\": {ScanIndex.CurrentFormatVersion}",
            $"\"formatVersion\": {ScanIndex.CurrentFormatVersion + 1}",
            StringComparison.Ordinal);
        File.WriteAllText(indexPath, text);

        var error = Assert.Throws<InvalidDataException>(() => ScanIndexStore.Load(indexPath));

        Assert.Contains("reads version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AFileThatSaysItIsSomethingElse_Throws()
    {
        using var tree = StandardFixture.Build(nameof(Load_AFileThatSaysItIsSomethingElse_Throws));
        using var home = new FixtureTree("index-home");
        var indexPath = ScanIndexStore.PathFor(home.Root, tree.Root);
        ScanIndexStore.Save(indexPath, StandardFixture.Scan(tree), DateTimeOffset.UtcNow);

        var text = File.ReadAllText(indexPath).Replace(
            ScanIndex.FormatName, "somebody-elses-index", StringComparison.Ordinal);
        File.WriteAllText(indexPath, text);

        Assert.Throws<InvalidDataException>(() => ScanIndexStore.Load(indexPath));
    }

    [Fact]
    public void PathFor_TheSameFolderTwice_IsOneFile()
    {
        using var home = new FixtureTree(nameof(PathFor_TheSameFolderTwice_IsOneFile));

        Assert.Equal(
            ScanIndexStore.PathFor(home.Root, home.Root),
            ScanIndexStore.PathFor(home.Root, home.Root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void PathFor_TwoFoldersWithTheSameName_AreTwoFiles()
    {
        using var home = new FixtureTree(nameof(PathFor_TwoFoldersWithTheSameName_AreTwoFiles));
        var first = home.Folder(Path.Combine("one", "cache"));
        var second = home.Folder(Path.Combine("two", "cache"));

        Assert.NotEqual(
            ScanIndexStore.PathFor(home.Root, first),
            ScanIndexStore.PathFor(home.Root, second));
    }

    /// <summary>
    /// One folder spelled two ways is one file or two, according to the platform the test runs on.
    /// On Windows letter case does not tell folders apart, so the two spellings are one folder and
    /// must be one file - without the case fold in the fingerprint, a report asked for with a
    /// lowercase drive letter answers that no scan was ever saved, which is the review's first
    /// finding. On every other platform the two spellings are two real folders and must remain two
    /// files, so a later change that folded case everywhere would be caught here rather than quietly
    /// merging two folders into one saved scan.
    /// </summary>
    [Fact]
    public void PathFor_TwoSpellingsThatDifferOnlyInCase_FollowThePlatformItRunsOn()
    {
        using var home = new FixtureTree(nameof(PathFor_TwoSpellingsThatDifferOnlyInCase_FollowThePlatformItRunsOn));
        var folder = home.Folder("data");
        var respelled = SpelledPath.WithFirstLetterCaseFlipped(folder);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                ScanIndexStore.PathFor(home.Root, folder),
                ScanIndexStore.PathFor(home.Root, respelled));
        }
        else
        {
            Assert.NotEqual(
                ScanIndexStore.PathFor(home.Root, folder),
                ScanIndexStore.PathFor(home.Root, respelled));
        }
    }

    [Fact]
    public void List_AFolderThatDoesNotExistYet_SaysNothingIsSavedRatherThanFailing()
    {
        using var home = new FixtureTree(nameof(List_AFolderThatDoesNotExistYet_SaysNothingIsSavedRatherThanFailing));

        var listing = ScanIndexStore.List(Path.Combine(home.Root, "never-written"));

        Assert.Empty(listing.Scans);
        Assert.Empty(listing.Unreadable);
    }

    [Fact]
    public void List_ASavedScan_NamesTheFolderItScannedAndWhatItSaw()
    {
        using var tree = StandardFixture.Build(nameof(List_ASavedScan_NamesTheFolderItScannedAndWhatItSaw));
        using var home = new FixtureTree("index-home");
        var indexPath = ScanIndexStore.PathFor(home.Root, tree.Root);
        ScanIndexStore.Save(indexPath, StandardFixture.Scan(tree), DateTimeOffset.UtcNow);

        var listing = ScanIndexStore.List(home.Root);

        var saved = Assert.Single(listing.Scans);
        Assert.Equal(tree.Root, saved.RootPath);
        Assert.Equal(StandardFixture.ExpectedBytesSeen, saved.BytesSeen);
        Assert.Equal(StandardFixture.ExpectedFilesSeen, saved.FilesSeen);
        Assert.Empty(listing.Unreadable);
    }

    /// <summary>
    /// One file nobody can read must not take the listing with it, and must not vanish from it either.
    /// It is named on its own list, so the count of saved scans stays a count of scans really read.
    /// </summary>
    [Fact]
    public void List_AFileThatIsNotASavedScan_NamesItApartFromTheScansThatWereRead()
    {
        using var tree = StandardFixture.Build(nameof(List_AFileThatIsNotASavedScan_NamesItApartFromTheScansThatWereRead));
        using var home = new FixtureTree("index-home");
        ScanIndexStore.Save(
            ScanIndexStore.PathFor(home.Root, tree.Root),
            StandardFixture.Scan(tree),
            DateTimeOffset.UtcNow);
        var rubbish = Path.Combine(home.Root, "rubbish.json");
        File.WriteAllText(rubbish, "{}");

        var listing = ScanIndexStore.List(home.Root);

        Assert.Single(listing.Scans);
        Assert.Equal(rubbish, Assert.Single(listing.Unreadable).IndexPath);
    }

    [Fact]
    public void DefaultIndexDirectory_IsOneFolderForTheWholeMachine()
    {
        var directory = ScanIndexStore.DefaultIndexDirectory();

        Assert.EndsWith(Path.Combine("reclaim", "index"), directory, StringComparison.Ordinal);
        Assert.True(Path.IsPathRooted(directory));
    }
}
