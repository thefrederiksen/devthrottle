using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// A package cache, against a cache folder the test builds.
///
/// The proof this rule holds is that somebody else's tool ships a command to clear its own store, so
/// what the rule owes a reader is a measurement and that command - never a deletion of its own. The
/// tests hold it to both: the whole cache is offered as one thing, because that is what the command
/// acts on, and a cache it could not measure properly says so rather than printing a smaller number.
/// </summary>
public class PackageCacheRuleTests
{
    [Fact]
    public void Examine_ACacheWithFilesInIt_OffersTheWholeCacheAndNamesItsOwnCommand()
    {
        using var cache = new FixtureTree(nameof(Examine_ACacheWithFilesInIt_OffersTheWholeCacheAndNamesItsOwnCommand));
        cache.File("one.tgz", 1000);
        cache.File(Path.Combine("deep", "two.tgz"), 2500);

        var finding = Fold(cache.Root);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        var offered = Assert.Single(finding.Candidates);
        Assert.Equal(cache.Root, offered.Path);
        Assert.Equal(3500, offered.Bytes);
        Assert.Equal("a command the owning tool ships", finding.CommandToRun);
        Assert.Contains("a command the owning tool ships", offered.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tool that would have made this cache is not installed here. That is a real answer, not a
    /// failure: there is genuinely nothing to clear, and saying "broken" would put a warning in front
    /// of the reader about a tool they do not have.
    /// </summary>
    [Fact]
    public void Examine_ACacheFolderThatIsNotOnThisMachine_IsOkAndOffersNothing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cc-reclaim-tests", "a-cache-that-was-never-made");

        var finding = Fold(missing);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(0, Control(finding, "cache-folders-found"));
    }

    [Fact]
    public void Examine_ACacheFolderWithNothingInIt_IsOkAndOffersNothing()
    {
        using var cache = new FixtureTree(nameof(Examine_ACacheFolderWithNothingInIt_IsOkAndOffersNothing));

        var finding = Fold(cache.Root);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(1, Control(finding, "cache-folders-found"));
        Assert.Equal(0, Control(finding, "files-measured"));
    }

    /// <summary>
    /// A folder inside the cache that will not be listed means the measured size is short by an
    /// unknown amount. This tool reports measured bytes, never an estimate presented as a fact, so it
    /// says it could not do its work rather than printing a number it cannot stand behind.
    /// </summary>
    [Fact]
    public void Examine_ACacheHoldingAFolderThatRefusesItsListing_ReportsBrokenRatherThanASmallerNumber()
    {
        using var cache = new FixtureTree(nameof(Examine_ACacheHoldingAFolderThatRefusesItsListing_ReportsBrokenRatherThanASmallerNumber));
        cache.File("visible.tgz", 1000);
        cache.Folder("locked");
        cache.File(Path.Combine("locked", "hidden.tgz"), 9000);
        cache.DenyListing("locked");

        var finding = Fold(cache.Root);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Contains("short by an unknown amount", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Empty(finding.Candidates);
    }

    /// <summary>
    /// A link out of a cache leads somewhere this rule has said nothing about, so it is not followed
    /// and its target's bytes are not counted as though they were in the cache.
    /// </summary>
    [Fact]
    public void Examine_ALinkInsideTheCache_IsNotFollowedAndItsBytesAreNotCounted()
    {
        using var tree = new FixtureTree(nameof(Examine_ALinkInsideTheCache_IsNotFollowedAndItsBytesAreNotCounted));

        // What the link points at sits outside the cache and is deliberately large, so following it
        // would show up plainly in the measured size rather than hiding in a rounding.
        tree.Folder("elsewhere");
        tree.File(Path.Combine("elsewhere", "big.bin"), 50_000);

        // The cache itself holds one real file and a link to that folder.
        tree.Folder("cache");
        tree.File(Path.Combine("cache", "real.tgz"), 1000);
        tree.DirectoryLink(Path.Combine("cache", "shortcut"), "elsewhere");

        var finding = Fold(Path.Combine(tree.Root, "cache"));

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Equal(1000, Assert.Single(finding.Candidates).Bytes);
        Assert.Equal(1, Control(finding, "files-measured"));
    }

    [Fact]
    public void Examine_ACacheRule_NeedsNoAdministratorAndHasNoAgeGate()
    {
        using var cache = new FixtureTree(nameof(Examine_ACacheRule_NeedsNoAdministratorAndHasNoAgeGate));
        cache.File("one.tgz", 10);

        var finding = Fold(cache.Root);

        Assert.False(finding.NeedsAdministrator);
        Assert.Equal(0, finding.AgeGateDays);
        Assert.Contains("age-gate: none", finding.Lines);
    }

    private static RuleFinding Fold(string cacheFolderPath)
    {
        var rule = new PackageCacheRule(
            "a-cache",
            "A cache that belongs to somebody else's tool",
            cacheFolderPath,
            "a command the owning tool ships",
            "nothing but the time to fetch it again, which is not known here");

        return RuleFold.Fold(rule, rule.Examine(new RuleContext
        {
            ScanRootPath = Path.GetTempPath(),
            NowUtc = DateTimeOffset.UtcNow
        }));
    }

    private static long Control(RuleFinding finding, string name) =>
        finding.Controls.Single(control => control.Name == name).Count;
}
