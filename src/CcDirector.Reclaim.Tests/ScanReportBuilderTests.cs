using CcDirector.Reclaim.Reporting;
using CcDirector.Reclaim.Scanning;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The report: the sentences the engine writes and every screen and tool prints without working
/// anything out again.
/// </summary>
public class ScanReportBuilderTests
{
    [Fact]
    public void Build_FixtureTree_StatesBytesSeenAgainstVolumeUsedAndTheDifferenceAsANumber()
    {
        using var tree = StandardFixture.Build(nameof(Build_FixtureTree_StatesBytesSeenAgainstVolumeUsedAndTheDifferenceAsANumber));
        var scan = StandardFixture.Scan(tree);

        var report = ScanReportBuilder.Build(scan);

        Assert.Equal(ReportVerdict.Ok, report.Verdict);
        Assert.Null(report.BrokenReason);

        // The unseen gap is required output. It is the volume's own used figure less what the walk
        // saw, and it is a number on its own line, never a hint and never left out.
        Assert.True(scan.Volume.Available);
        Assert.Equal(scan.Volume.UsedBytes - StandardFixture.ExpectedBytesSeen, report.UnseenBytes);

        var seen = Line(report, "seen: ");
        Assert.Contains($"{StandardFixture.ExpectedBytesSeen} bytes", seen, StringComparison.Ordinal);

        var unseen = Line(report, "unseen: ");
        Assert.Contains($"{report.UnseenBytes} bytes", unseen, StringComparison.Ordinal);

        var volume = Line(report, "volume: ");
        Assert.Contains($"{scan.Volume.UsedBytes} bytes", volume, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FixtureTree_NamesEveryFolderThatRefusedAListingBesideTheUnseenNumber()
    {
        using var tree = StandardFixture.Build(nameof(Build_FixtureTree_NamesEveryFolderThatRefusedAListingBesideTheUnseenNumber));
        var scan = StandardFixture.Scan(tree);

        var report = ScanReportBuilder.Build(scan);

        Assert.Contains(
            report.Lines,
            line => line.StartsWith("refused: 1 folder refused a listing", StringComparison.Ordinal));
        Assert.Contains("refused-folders[1]{path,reason,code}:", report.Lines);
        Assert.Contains(
            report.Lines,
            line => line.Contains(Path.Combine(tree.Root, "refused"), StringComparison.Ordinal)
                    && line.Contains("access denied", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_FixtureTree_CountsTheLinkAndThePlaceholderOnTheirOwnLines()
    {
        using var tree = StandardFixture.Build(nameof(Build_FixtureTree_CountsTheLinkAndThePlaceholderOnTheirOwnLines));
        var scan = StandardFixture.Scan(tree);

        var report = ScanReportBuilder.Build(scan);

        Assert.Contains("links: 1 link or junction was found, and none were followed", report.Lines);
        Assert.Contains(
            $"placeholders: 1 cloud placeholder file holds {StandardFixture.ExpectedPlaceholderBytesInCloud} bytes " +
            "(4.0 kilobytes) in a cloud store and no bytes on this disk",
            report.Lines);
    }

    /// <summary>
    /// The instrument check the mission asks for by name. A scan that saw nothing says BROKEN. It
    /// never says there is nothing here, because a working scan of an empty folder and a scan that
    /// failed produce the same zeroes, and only one of the two is safe to act on.
    /// </summary>
    [Fact]
    public void Build_ScanThatSawNothing_SaysBrokenAndNeverSaysNothingHere()
    {
        using var tree = new FixtureTree(nameof(Build_ScanThatSawNothing_SaysBrokenAndNeverSaysNothingHere));
        var scan = new DirectoryScanner().Scan(new ScanOptions { RootPath = tree.Root });

        var report = ScanReportBuilder.Build(scan);

        Assert.Equal(ReportVerdict.Broken, report.Verdict);
        Assert.Equal("verdict: broken", report.Lines[0]);
        Assert.NotNull(report.BrokenReason);
        Assert.Contains("broken instrument", report.BrokenReason, StringComparison.Ordinal);
        Assert.Contains(report.Lines, line => line.StartsWith("next: ", StringComparison.Ordinal));
        Assert.Contains(report.Lines, line => line.Contains("cc-cleanup-storage scan", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ScanThatSawFilesAndNotOneByte_SaysBroken()
    {
        using var tree = new FixtureTree(nameof(Build_ScanThatSawFilesAndNotOneByte_SaysBroken));
        tree.File("empty-one.txt", 0);
        tree.File(Path.Combine("folder", "empty-two.txt"), 0);
        var scan = new DirectoryScanner().Scan(new ScanOptions { RootPath = tree.Root });

        var report = ScanReportBuilder.Build(scan);

        Assert.Equal(ReportVerdict.Broken, report.Verdict);
        Assert.NotNull(report.BrokenReason);
        Assert.Contains("not one byte", report.BrokenReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the instrument check. A volume that will not say how much of it is used
    /// takes away one side of the unseen-gap line, and a report with one side of that line is not a
    /// report - so it says BROKEN and prints "unknown" rather than a number nobody can check.
    ///
    /// The scan here is built by hand, because a volume that refuses to answer cannot be made on a
    /// working disk. What a real scan of a real tree produces is proven by DirectoryScannerTests, and
    /// what a real volume answers is proven by VolumeReaderTests; this test is about the one decision
    /// that sits between them.
    /// </summary>
    [Fact]
    public void Build_VolumeThatWillNotSayHowMuchIsUsed_SaysBrokenAndCallsTheUnseenNumberUnknown()
    {
        var scan = ScanThatSawSomething() with
        {
            Volume = new VolumeUsage(false, "Z:\\", 0, 0, 0, "the volume is not ready")
        };

        var report = ScanReportBuilder.Build(scan);

        Assert.Equal(ReportVerdict.Broken, report.Verdict);
        Assert.Null(report.UnseenBytes);
        Assert.Contains("unseen: unknown, because the volume would not say how much of it is used", report.Lines);
        Assert.NotNull(report.BrokenReason);
        Assert.Contains("the volume is not ready", report.BrokenReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ScanRootBelowTheVolumeRoot_SaysTheUnseenNumberCoversTheWholeVolume()
    {
        using var tree = StandardFixture.Build(nameof(Build_ScanRootBelowTheVolumeRoot_SaysTheUnseenNumberCoversTheWholeVolume));
        var scan = StandardFixture.Scan(tree);

        var report = ScanReportBuilder.Build(scan);

        Assert.False(scan.RootIsVolumeRoot);
        var scope = Line(report, "scope: ");
        Assert.Contains("outside this folder", scope, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ScanOfAWholeVolume_HasNoScopeLineBecauseTheGapNeedsNoExplaining()
    {
        var scan = ScanThatSawSomething() with { RootIsVolumeRoot = true };

        var report = ScanReportBuilder.Build(scan);

        Assert.DoesNotContain(report.Lines, line => line.StartsWith("scope: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_LargestFolders_AreNamedBiggestFirst()
    {
        using var tree = StandardFixture.Build(nameof(Build_LargestFolders_AreNamedBiggestFirst));
        var scan = StandardFixture.Scan(tree);

        var report = ScanReportBuilder.Build(scan);

        Assert.Equal(
            new[]
            {
                Path.Combine(tree.Root, "beta"),
                Path.Combine(tree.Root, "alpha"),
                Path.Combine(tree.Root, "alpha", "nested"),
                Path.Combine(tree.Root, "refused")
            },
            report.LargestFolders.Select(folder => folder.Path).ToArray());
    }

    [Fact]
    public void Build_FewerFoldersAskedForThanExist_NamesOnlyThatManyAndSaysHowMany()
    {
        using var tree = StandardFixture.Build(nameof(Build_FewerFoldersAskedForThanExist_NamesOnlyThatManyAndSaysHowMany));
        var scan = StandardFixture.Scan(tree);

        var report = ScanReportBuilder.Build(scan, largestFolders: 2);

        Assert.Equal(2, report.LargestFolders.Count);
        Assert.Contains("largest-folders[2]{path,bytes,size,files}:", report.Lines);
    }

    [Fact]
    public void Build_NoFolderUnderTheRoot_StillPrintsTheListHeaderWithZeroInIt()
    {
        var scan = ScanThatSawSomething() with { FolderTotals = [] };

        var report = ScanReportBuilder.Build(scan);

        Assert.Contains("largest-folders[0]{path,bytes,size,files}:", report.Lines);
        Assert.Contains("refused-folders[0]{path,reason,code}:", report.Lines);
        Assert.Contains("refused: no folder refused its listing", report.Lines);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ScanReportBuilder.MaximumLargestFolders + 1)]
    public void Build_NumberOfFoldersToNameOutsideTheAllowedRange_Throws(int howMany)
    {
        var scan = ScanThatSawSomething();

        Assert.Throws<ArgumentOutOfRangeException>(() => ScanReportBuilder.Build(scan, howMany));
    }

    [Fact]
    public void RefusalWords_AFolderThatWasListed_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScanReportBuilder.RefusalWords(DirectoryReadRefusal.None));
    }

    private static string Line(ScanReport report, string startsWith) =>
        Assert.Single(report.Lines, line => line.StartsWith(startsWith, StringComparison.Ordinal));

    // A scan that saw something, built by hand so a test can change ONE thing about it - the volume,
    // the scope, the folder list - and watch that one decision.
    private static ScanResult ScanThatSawSomething() => new()
    {
        RootPath = Path.Combine("Z:", "somewhere"),
        RootIsVolumeRoot = false,
        StartedUtc = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero),
        FinishedUtc = new DateTimeOffset(2026, 9, 18, 10, 0, 1, TimeSpan.Zero),
        ElapsedSeconds = 1.0,
        FilesSeen = 2,
        FoldersSeen = 1,
        BytesSeen = 3000,
        PlaceholderFiles = 0,
        PlaceholderBytesInCloud = 0,
        Links = [],
        RefusedFolders = [],
        FolderTotals = [new FolderTotal(Path.Combine("Z:", "somewhere", "one"), 1, 3000, 2)],
        Volume = new VolumeUsage(true, "Z:\\", 10_000, 8_000, 2_000, null)
    };
}
