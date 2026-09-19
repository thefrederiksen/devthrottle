using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The test scratch folder rule, against a temporary folder the test builds.
///
/// Nothing here looks at the machine's own temporary folder. That folder is full of other programs'
/// work and none of it is ours to judge, which is exactly the point this rule turns on: the proof is
/// not that something looks temporary, it is that DevThrottle's own code makes a folder by an exact
/// name. Most of the tests below are about what the rule declines.
/// </summary>
public class TestScratchFoldersRuleTests
{
    private static readonly DateTimeOffset LongAfterTheFixtureWasBuilt =
        DateTimeOffset.UtcNow.AddDays(400);

    [Fact]
    public void Examine_AFolderNamedLikeOneOfOurTestSuitesMakes_IsOffered()
    {
        using var temporary = new FixtureTree(nameof(Examine_AFolderNamedLikeOneOfOurTestSuitesMakes_IsOffered));
        temporary.Folder("cc-director-tests");
        temporary.File(Path.Combine("cc-director-tests", "scratch.json"), 1200);

        var finding = Fold(temporary.Root, LongAfterTheFixtureWasBuilt);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        var offered = Assert.Single(finding.Candidates);
        Assert.Equal(Path.Combine(temporary.Root, "cc-director-tests"), offered.Path);
        Assert.Equal(1200, offered.Bytes);
    }

    [Fact]
    public void Examine_AFolderWhoseNameStartsWithOneOfOurPrefixes_IsOffered()
    {
        using var temporary = new FixtureTree(nameof(Examine_AFolderWhoseNameStartsWithOneOfOurPrefixes_IsOffered));
        temporary.Folder("cc-instances-9f2a1c");
        temporary.File(Path.Combine("cc-instances-9f2a1c", "one.txt"), 40);

        var finding = Fold(temporary.Root, LongAfterTheFixtureWasBuilt);

        Assert.Equal(Path.Combine(temporary.Root, "cc-instances-9f2a1c"), Assert.Single(finding.Candidates).Path);
    }

    /// <summary>
    /// The temporary folder belongs to the whole machine. Somebody else's folder is not ours to
    /// judge, whatever it holds and however old it is.
    /// </summary>
    [Fact]
    public void Examine_AFolderNobodyHereMade_IsNeverOffered()
    {
        using var temporary = new FixtureTree(nameof(Examine_AFolderNobodyHereMade_IsNeverOffered));
        temporary.Folder("SomeOtherProgramCache");
        temporary.File(Path.Combine("SomeOtherProgramCache", "big.bin"), 90_000);

        var finding = Fold(temporary.Root, LongAfterTheFixtureWasBuilt);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(1, Control(finding, "folders-examined"));
        Assert.Equal(0, Control(finding, "folders-matching-one-of-our-names"));
    }

    /// <summary>
    /// The bare name cc-director is deliberately not on the list, because product code makes it too.
    /// A rule that matched it would offer a running Director's own folder.
    /// </summary>
    [Fact]
    public void Examine_TheBareNameProductCodeAlsoMakes_IsNeverOffered()
    {
        using var temporary = new FixtureTree(nameof(Examine_TheBareNameProductCodeAlsoMakes_IsNeverOffered));
        temporary.Folder("cc-director");
        temporary.File(Path.Combine("cc-director", "live.db"), 500);

        var finding = Fold(temporary.Root, LongAfterTheFixtureWasBuilt);

        Assert.Empty(finding.Candidates);
        Assert.DoesNotContain("cc-director", TestScratchFoldersRule.ScratchFolderNames);
    }

    [Fact]
    public void Examine_AScratchFolderYoungerThanTheAgeGate_IsNotOfferedAndIsCounted()
    {
        using var temporary = new FixtureTree(nameof(Examine_AScratchFolderYoungerThanTheAgeGate_IsNotOfferedAndIsCounted));
        temporary.Folder("cc-plog-abc");
        temporary.File(Path.Combine("cc-plog-abc", "one.txt"), 10);

        var finding = Fold(temporary.Root, DateTimeOffset.UtcNow);

        Assert.Empty(finding.Candidates);
        Assert.Equal(1, Control(finding, "matches-too-young-to-offer"));
    }

    /// <summary>
    /// A folder with a file open belongs to a run that has not finished, whatever its age says. The
    /// age gate alone would offer it, so this test holds the open-file check on its own.
    /// </summary>
    [Fact]
    public void Examine_AnOldScratchFolderWithAFileStillOpen_IsNotOfferedAndIsCounted()
    {
        using var temporary = new FixtureTree(nameof(Examine_AnOldScratchFolderWithAFileStillOpen_IsNotOfferedAndIsCounted));
        temporary.Folder("cc-conc-running");
        var busy = temporary.File(Path.Combine("cc-conc-running", "in-use.log"), 10);

        using (var held = new FileStream(busy, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var finding = Fold(temporary.Root, LongAfterTheFixtureWasBuilt);

            Assert.Empty(finding.Candidates);
            Assert.Equal(1, Control(finding, "matches-with-a-file-still-open"));
            Assert.Equal(1, Control(finding, "folders-matching-one-of-our-names"));
            held.Flush();
        }

        // With the file closed, the same folder is offered - which is what proves the test above
        // turned on the open file and not on something else about the folder.
        var afterwards = Fold(temporary.Root, LongAfterTheFixtureWasBuilt);
        Assert.Single(afterwards.Candidates);
    }

    /// <summary>
    /// A link that happens to be named like one of ours leads somewhere else entirely, and this rule
    /// has said nothing about wherever that is.
    /// </summary>
    [Fact]
    public void Examine_ALinkNamedLikeOneOfOurs_IsNeverOffered()
    {
        using var temporary = new FixtureTree(nameof(Examine_ALinkNamedLikeOneOfOurs_IsNeverOffered));
        temporary.Folder("somewhere-real");
        temporary.File(Path.Combine("somewhere-real", "precious.txt"), 100);
        temporary.DirectoryLink("cc-director-tests", "somewhere-real");

        var finding = Fold(temporary.Root, LongAfterTheFixtureWasBuilt);

        Assert.Empty(finding.Candidates);
        Assert.Equal(0, Control(finding, "folders-matching-one-of-our-names"));
    }

    /// <summary>
    /// The list of names is the one side of this rule that cannot be empty: an empty list matches
    /// nothing on every machine and reads exactly like a machine with no leftovers.
    /// </summary>
    [Fact]
    public void Examine_TheListOfNames_IsAControlThatCannotBeEmpty()
    {
        using var temporary = new FixtureTree(nameof(Examine_TheListOfNames_IsAControlThatCannotBeEmpty));
        temporary.Folder("something");

        var finding = Fold(temporary.Root, LongAfterTheFixtureWasBuilt);
        var control = finding.Controls.Single(control => control.Name == "names-looked-for");

        Assert.True(control.MustNotBeEmpty);
        Assert.Equal(TestScratchFoldersRule.ScratchFolderNames.Count, control.Count);
        Assert.NotEmpty(TestScratchFoldersRule.ScratchFolderNames);
    }

    [Fact]
    public void Examine_ATemporaryFolderThatIsNotThere_ReportsBroken()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cc-reclaim-tests", "a-temporary-folder-that-is-not-there");

        var finding = Fold(missing, LongAfterTheFixtureWasBuilt);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Contains("is not there", finding.BrokenReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A match whose contents will not be listed cannot be measured or checked for open files, so it
    /// is counted and left alone rather than offered on an incomplete reading.
    /// </summary>
    [Fact]
    public void Examine_AMatchHoldingAFolderThatRefusesItsListing_IsNotOfferedAndIsCounted()
    {
        using var temporary = new FixtureTree(nameof(Examine_AMatchHoldingAFolderThatRefusesItsListing_IsNotOfferedAndIsCounted));
        temporary.Folder("cc-machine-locked");
        temporary.Folder(Path.Combine("cc-machine-locked", "inner"));
        temporary.File(Path.Combine("cc-machine-locked", "inner", "one.txt"), 20);
        temporary.DenyListing(Path.Combine("cc-machine-locked", "inner"));

        var finding = Fold(temporary.Root, LongAfterTheFixtureWasBuilt);

        Assert.Empty(finding.Candidates);
        Assert.Equal(1, Control(finding, "matches-that-would-not-be-listed"));
    }

    private static RuleFinding Fold(string temporaryFolderPath, DateTimeOffset now)
    {
        var rule = new TestScratchFoldersRule(temporaryFolderPath);
        return RuleFold.Fold(rule, rule.Examine(new RuleContext
        {
            ScanRootPath = temporaryFolderPath,
            NowUtc = now
        }));
    }

    private static long Control(RuleFinding finding, string name) =>
        finding.Controls.Single(control => control.Name == name).Count;
}
