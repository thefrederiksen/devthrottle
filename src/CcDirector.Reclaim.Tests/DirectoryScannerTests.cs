using CcDirector.Reclaim.Scanning;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The walk, proved against a tree the test builds and destroys, with exact numbers.
/// </summary>
public class DirectoryScannerTests
{
    [Fact]
    public void Scan_FixtureTree_CountsExactlyTheBytesAndFilesThatWerePlaced()
    {
        using var tree = StandardFixture.Build(nameof(Scan_FixtureTree_CountsExactlyTheBytesAndFilesThatWerePlaced));

        var result = StandardFixture.Scan(tree);

        Assert.Equal(StandardFixture.ExpectedBytesSeen, result.BytesSeen);
        Assert.Equal(StandardFixture.ExpectedFilesSeen, result.FilesSeen);
        Assert.Equal(StandardFixture.ExpectedFoldersSeen, result.FoldersSeen);
    }

    [Fact]
    public void Scan_FolderBehindARefusal_IsNeverCountedInTheBytesSeen()
    {
        using var tree = StandardFixture.Build(nameof(Scan_FolderBehindARefusal_IsNeverCountedInTheBytesSeen));

        var result = StandardFixture.Scan(tree);

        Assert.Equal(StandardFixture.ExpectedBytesSeen, result.BytesSeen);
        Assert.DoesNotContain(
            result.FolderTotals,
            folder => folder.BytesSeen >= StandardFixture.HiddenFileBytes);
    }

    [Fact]
    public void Scan_DirectoryJunction_IsCountedOnceAndNotFollowed()
    {
        using var tree = StandardFixture.Build(nameof(Scan_DirectoryJunction_IsCountedOnceAndNotFollowed));

        var result = StandardFixture.Scan(tree);

        var link = Assert.Single(result.Links);
        Assert.Equal(Path.Combine(tree.Root, "link-to-alpha"), link.Path);
        Assert.True(link.IsDirectory);

        // If the walk had gone through the junction it would have counted alpha twice, and there
        // would be a folder total for a path underneath the link.
        Assert.Equal(StandardFixture.ExpectedBytesSeen, result.BytesSeen);
        Assert.DoesNotContain(result.FolderTotals, folder => folder.Path.StartsWith(link.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_FolderThatRefusesAListing_IsCountedAndNamedWithItsReason()
    {
        using var tree = StandardFixture.Build(nameof(Scan_FolderThatRefusesAListing_IsCountedAndNamedWithItsReason));

        var result = StandardFixture.Scan(tree);

        var refused = Assert.Single(result.RefusedFolders);
        Assert.Equal(Path.Combine(tree.Root, "refused"), refused.Path);
        Assert.Equal(DirectoryReadRefusal.AccessDenied, refused.Refusal);
        Assert.NotEqual(0, refused.RefusalCode);
    }

    [Fact]
    public void Scan_CloudPlaceholderFile_IsCountedApartAndAddsNoBytesToWhatWasSeen()
    {
        using var tree = StandardFixture.Build(nameof(Scan_CloudPlaceholderFile_IsCountedApartAndAddsNoBytesToWhatWasSeen));

        var result = StandardFixture.Scan(tree);

        Assert.Equal(1L, result.PlaceholderFiles);
        Assert.Equal(StandardFixture.ExpectedPlaceholderBytesInCloud, result.PlaceholderBytesInCloud);
        Assert.Equal(StandardFixture.ExpectedBytesSeen, result.BytesSeen);
        Assert.Equal(StandardFixture.ExpectedFilesSeen, result.FilesSeen);
    }

    [Fact]
    public void Scan_FolderTotals_HoldEveryFolderDownToTheRequestedDepthAndNoDeeper()
    {
        using var tree = StandardFixture.Build(nameof(Scan_FolderTotals_HoldEveryFolderDownToTheRequestedDepthAndNoDeeper));

        var deep = StandardFixture.Scan(tree, maxFolderDepth: 2);
        var shallow = StandardFixture.Scan(tree, maxFolderDepth: 1);

        Assert.Equal(
            new[]
            {
                Path.Combine(tree.Root, "alpha"),
                Path.Combine(tree.Root, "alpha", "nested"),
                Path.Combine(tree.Root, "beta"),
                Path.Combine(tree.Root, "refused")
            },
            deep.FolderTotals.Select(folder => folder.Path).ToArray());

        Assert.Equal(
            new[]
            {
                Path.Combine(tree.Root, "alpha"),
                Path.Combine(tree.Root, "beta"),
                Path.Combine(tree.Root, "refused")
            },
            shallow.FolderTotals.Select(folder => folder.Path).ToArray());
    }

    [Fact]
    public void Scan_FolderTotals_CountEveryByteBeneathEachFolder()
    {
        using var tree = StandardFixture.Build(nameof(Scan_FolderTotals_CountEveryByteBeneathEachFolder));

        var result = StandardFixture.Scan(tree);
        var byPath = result.FolderTotals.ToDictionary(folder => folder.Path, StringComparer.Ordinal);

        Assert.Equal(StandardFixture.ExpectedAlphaBytes, byPath[Path.Combine(tree.Root, "alpha")].BytesSeen);
        Assert.Equal(StandardFixture.ExpectedAlphaFiles, byPath[Path.Combine(tree.Root, "alpha")].FilesSeen);
        Assert.Equal(StandardFixture.ExpectedNestedBytes, byPath[Path.Combine(tree.Root, "alpha", "nested")].BytesSeen);
        Assert.Equal(StandardFixture.ExpectedBetaBytes, byPath[Path.Combine(tree.Root, "beta")].BytesSeen);
        Assert.Equal(0L, byPath[Path.Combine(tree.Root, "refused")].BytesSeen);
    }

    [Fact]
    public void Scan_SameTreeTwice_ProducesTheSameOrderedLists()
    {
        using var tree = StandardFixture.Build(nameof(Scan_SameTreeTwice_ProducesTheSameOrderedLists));

        var first = StandardFixture.Scan(tree);
        var second = StandardFixture.Scan(tree);

        Assert.Equal(
            first.FolderTotals.Select(folder => folder.Path),
            second.FolderTotals.Select(folder => folder.Path));
        Assert.Equal(first.Links.Select(link => link.Path), second.Links.Select(link => link.Path));
        Assert.Equal(
            first.RefusedFolders.Select(folder => folder.Path),
            second.RefusedFolders.Select(folder => folder.Path));
    }

    [Fact]
    public void Scan_EmptyFolder_ReturnsZeroesRatherThanFailing()
    {
        using var tree = new FixtureTree(nameof(Scan_EmptyFolder_ReturnsZeroesRatherThanFailing));

        var result = new DirectoryScanner().Scan(new ScanOptions { RootPath = tree.Root });

        // The scan is a measuring instrument and says what it measured. Calling an empty answer
        // broken is the report's job, and ScanReportBuilderTests proves it does.
        Assert.Equal(0L, result.BytesSeen);
        Assert.Equal(0L, result.FilesSeen);
        Assert.Equal(0L, result.FoldersSeen);
        Assert.Empty(result.RefusedFolders);
    }

    [Fact]
    public void Scan_RootThatDoesNotExist_ThrowsAndSaysSo()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cc-reclaim-tests", $"missing-{Guid.NewGuid():N}");

        var error = Assert.Throws<DirectoryNotFoundException>(
            () => new DirectoryScanner().Scan(new ScanOptions { RootPath = missing }));

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ScanOptions.MaximumFolderDepth + 1)]
    public void Scan_FolderDepthOutsideTheAllowedRange_Throws(int depth)
    {
        using var tree = new FixtureTree(nameof(Scan_FolderDepthOutsideTheAllowedRange_Throws));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DirectoryScanner().Scan(new ScanOptions { RootPath = tree.Root, MaxFolderDepth = depth }));
    }

    [Fact]
    public void Scan_BlankRoot_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new DirectoryScanner().Scan(new ScanOptions { RootPath = "   " }));
    }

    [Fact]
    public void Canonical_PathWrittenTwoWays_IsOneAnswer()
    {
        using var tree = new FixtureTree(nameof(Canonical_PathWrittenTwoWays_IsOneAnswer));
        tree.Folder("here");

        var plain = DirectoryScanner.Canonical(Path.Combine(tree.Root, "here"));
        var roundabout = DirectoryScanner.Canonical(Path.Combine(tree.Root, "here", ".") + Path.DirectorySeparatorChar);

        Assert.Equal(plain, roundabout);
    }
}
