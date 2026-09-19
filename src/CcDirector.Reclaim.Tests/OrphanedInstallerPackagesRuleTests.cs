using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The installer rule, against a package folder the test builds and a record set the test decides.
///
/// Nothing here reads the machine's own registry or its own package cache. The rule's comparison is
/// "which of these files is in that list", and the whole danger is what happens when the list fails
/// to load: every file then looks like an orphan, and the tool would confidently recommend deleting
/// the entire Windows package cache, leaving the machine unable to repair or remove anything it has
/// installed. Four of the tests below are about that one case.
/// </summary>
public class OrphanedInstallerPackagesRuleTests
{
    private static readonly DateTimeOffset LongAfterTheFixtureWasBuilt =
        DateTimeOffset.UtcNow.AddDays(400);

    [Fact]
    public void Examine_APackageNoRecordNames_IsOffered()
    {
        using var cache = new FixtureTree(nameof(Examine_APackageNoRecordNames_IsOffered));
        var kept = cache.File("kept.msi", 1000);
        var orphan = cache.File("orphan.msi", 2500);

        var finding = Fold(cache, Records([kept]), LongAfterTheFixtureWasBuilt);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        var offered = Assert.Single(finding.Candidates);
        Assert.Equal(orphan, offered.Path);
        Assert.Equal(2500, offered.Bytes);
        Assert.Equal(2500, finding.CandidateBytes);
    }

    [Fact]
    public void Examine_APackageARecordNames_IsNotOffered()
    {
        using var cache = new FixtureTree(nameof(Examine_APackageARecordNames_IsNotOffered));
        var kept = cache.File("kept.msi", 1000);

        var finding = Fold(cache, Records([kept]), LongAfterTheFixtureWasBuilt);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
    }

    /// <summary>
    /// One of the 211 orphans measured during the design had been written two days earlier. The age
    /// gate exists for exactly that file, and this is the test that holds it.
    /// </summary>
    [Fact]
    public void Examine_AnOrphanYoungerThanTheAgeGate_IsNotOfferedAndIsCounted()
    {
        using var cache = new FixtureTree(nameof(Examine_AnOrphanYoungerThanTheAgeGate_IsNotOfferedAndIsCounted));
        var kept = cache.File("kept.msi", 1000);
        cache.File("written-just-now.msi", 4000);

        // Asked about as of now, the orphan written a moment ago is inside the thirty day gate.
        var finding = Fold(cache, Records([kept]), DateTimeOffset.UtcNow);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(1, Control(finding, "orphans-too-young-to-offer"));
    }

    /// <summary>
    /// The case the whole rule is shaped around. No records at all is NOT a machine with nothing to
    /// remove; it is a machine we cannot say anything about. Without this the rule would offer every
    /// package in the folder.
    /// </summary>
    [Fact]
    public void Examine_NoRecordsAtAll_ReportsBrokenAndOffersNothing()
    {
        using var cache = new FixtureTree(nameof(Examine_NoRecordsAtAll_ReportsBrokenAndOffersNothing));
        cache.File("one.msi", 1000);
        cache.File("two.msi", 2000);

        var finding = Fold(cache, Records([]), LongAfterTheFixtureWasBuilt);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Contains("records-read", finding.BrokenReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Records that name files which are all gone is the same failure wearing different clothes: the
    /// record set loaded, but nothing in it can be matched, so every file in the folder looks like an
    /// orphan. The second control catches it.
    /// </summary>
    [Fact]
    public void Examine_RecordsThatNameOnlyFilesThatAreGone_ReportsBroken()
    {
        using var cache = new FixtureTree(nameof(Examine_RecordsThatNameOnlyFilesThatAreGone_ReportsBroken));
        cache.File("one.msi", 1000);

        var finding = Fold(
            cache,
            Records([Path.Combine(cache.Root, "a-package-that-is-not-there.msi")]),
            LongAfterTheFixtureWasBuilt);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Contains("records-found-on-disk", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Empty(finding.Candidates);
    }

    [Fact]
    public void Examine_APackageFolderWithNoPackagesInIt_ReportsBroken()
    {
        using var cache = new FixtureTree(nameof(Examine_APackageFolderWithNoPackagesInIt_ReportsBroken));
        var kept = cache.File("not-a-package.txt", 50);

        var finding = Fold(cache, Records([kept]), LongAfterTheFixtureWasBuilt);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Contains("candidates-examined", finding.BrokenReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Examine_APackageFolderThatIsNotThere_ReportsBrokenAndNeverReadsTheRecords()
    {
        var records = new StubRecordSource(new InstallerRecords(new HashSet<string>(), 0, 0));
        var rule = new OrphanedInstallerPackagesRule(
            records, Path.Combine(Path.GetTempPath(), "cc-reclaim-tests", "a-folder-that-is-not-there"));

        var finding = RuleFold.Fold(rule, rule.Examine(Context(DateTimeOffset.UtcNow)));

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Contains("is not there", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.False(records.WasRead);
    }

    [Fact]
    public void Examine_AMachineThatIsNotWindows_ReportsBrokenRatherThanOfferingEveryPackage()
    {
        using var cache = new FixtureTree(nameof(Examine_AMachineThatIsNotWindows_ReportsBrokenRatherThanOfferingEveryPackage));
        cache.File("one.msi", 1000);

        var rule = new OrphanedInstallerPackagesRule(new NotWindowsRecordSource(), cache.Root);
        var finding = RuleFold.Fold(rule, rule.Examine(Context(LongAfterTheFixtureWasBuilt)));

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Contains("not running Windows", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Empty(finding.Candidates);
    }

    /// <summary>
    /// The mission names three controls by name and the mission's own check reads them, so the rule
    /// reports exactly those three and every one of them is declared as one that must not be empty.
    /// </summary>
    [Fact]
    public void Examine_TheThreeControlsTheMissionNames_AreReportedAndAllMustNotBeEmpty()
    {
        using var cache = new FixtureTree(nameof(Examine_TheThreeControlsTheMissionNames_AreReportedAndAllMustNotBeEmpty));
        var kept = cache.File("kept.msi", 1000);
        cache.File("orphan.msp", 2000);

        var finding = Fold(cache, Records([kept]), LongAfterTheFixtureWasBuilt);

        foreach (var name in new[] { "records-read", "records-found-on-disk", "candidates-examined" })
        {
            var control = finding.Controls.Single(control => control.Name == name);
            Assert.True(control.MustNotBeEmpty, $"the control {name} must be one that cannot be nought");
            Assert.True(control.Count > 0, $"the control {name} was nought in a fixture that has one");
        }
    }

    /// <summary>A patch file counts exactly as a package file does; both are cached by Windows.</summary>
    [Fact]
    public void Examine_AnOrphanedPatchFile_IsOfferedJustAsAPackageIs()
    {
        using var cache = new FixtureTree(nameof(Examine_AnOrphanedPatchFile_IsOfferedJustAsAPackageIs));
        var kept = cache.File("kept.msi", 1000);
        var orphanPatch = cache.File("orphan.msp", 7000);

        var finding = Fold(cache, Records([kept]), LongAfterTheFixtureWasBuilt);

        Assert.Equal(orphanPatch, Assert.Single(finding.Candidates).Path);
    }

    /// <summary>
    /// Windows keeps its packages at the top of the folder. What is in the folders beneath it is
    /// something else, and this rule says nothing about any of it.
    /// </summary>
    [Fact]
    public void Examine_APackageInAFolderBeneathTheCache_IsNotOffered()
    {
        using var cache = new FixtureTree(nameof(Examine_APackageInAFolderBeneathTheCache_IsNotOffered));
        var kept = cache.File("kept.msi", 1000);
        cache.File(Path.Combine("$PatchCache$", "buried.msi"), 5000);

        var finding = Fold(cache, Records([kept]), LongAfterTheFixtureWasBuilt);

        Assert.Empty(finding.Candidates);
        Assert.Equal(1, Control(finding, "candidates-examined"));
    }

    private static RuleFinding Fold(FixtureTree cache, InstallerRecords records, DateTimeOffset now)
    {
        var rule = new OrphanedInstallerPackagesRule(new StubRecordSource(records), cache.Root);
        return RuleFold.Fold(rule, rule.Examine(Context(now)));
    }

    private static RuleContext Context(DateTimeOffset now) =>
        new() { ScanRootPath = Path.GetTempPath(), NowUtc = now };

    private static InstallerRecords Records(IReadOnlyList<string> referenced) =>
        new(new HashSet<string>(referenced, StringComparer.OrdinalIgnoreCase), referenced.Count, 0);

    private static long Control(RuleFinding finding, string name) =>
        finding.Controls.Single(control => control.Name == name).Count;

    private sealed class StubRecordSource(InstallerRecords records) : IInstallerRecordSource
    {
        public bool WasRead { get; private set; }

        public InstallerRecords Read()
        {
            WasRead = true;
            return records;
        }
    }

    private sealed class NotWindowsRecordSource : IInstallerRecordSource
    {
        public InstallerRecords Read() =>
            throw new PlatformNotSupportedException(
                "The Windows installer records live in the Windows registry and this machine is not running Windows.");
    }
}
