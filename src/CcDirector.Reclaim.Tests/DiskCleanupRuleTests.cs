using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The Disk Cleanup rule, against a category list the test decides the machine holds.
///
/// The categories come from the machine's own registry and are never typed into the rule, so the
/// tests hand the rule a list through the same source the registry fills on a real machine. What
/// is held here is the shape the mandate demands: the categories are counted, the ones that need
/// an administrator are separated from the ones that do not, nothing is ever offered for removal -
/// because only Windows' own handlers know what they would take - and a machine whose list cannot
/// be found is a broken instrument, not a machine where Windows offers nothing.
/// </summary>
public class DiskCleanupRuleTests
{
    /// <summary>
    /// The account this test runs as. The rule decides "inside the account" against the RUNNING
    /// account's profile folder, so a folder written out as one machine's own path - C:\Users\soren
    /// - is inside the account on that machine and outside it everywhere else, which counted one
    /// more category as needing an administrator on the build machine than on the author's.
    /// </summary>
    private static readonly string Account = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void Examine_TheCategoriesTheMachineHolds_AreCountedAndSeparatedByAdministrator()
    {
        var rule = new DiskCleanupRule(
            new StubSource(
                Category("BranchCache", [@"C:\ProgramData\BranchCache"]),
                Category("DownloadsFolder", [Path.Combine(Account, "Downloads")]),
                Category("Temporary Files", [Path.Combine(Account, @"AppData\Local\Temp"), @"C:\WINDOWS\Temp"]),
                Category("Thumbnail Cache")),
            @"C:\");

        var finding = Fold(rule);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Equal(4, Control(finding, "categories-read"));
        Assert.Equal(2, Control(finding, "categories-that-need-an-administrator"));
        Assert.Equal(1, Control(finding, "categories-that-look-only-in-the-accounts-own-folders"));
        Assert.Equal(1, Control(finding, "categories-whose-folders-windows-decides-when-it-runs"));
    }

    /// <summary>
    /// The one deliberate non-offer in the whole rule set. Sizing a category here would mean
    /// measuring the folder its entry names and calling the result what the category would clear,
    /// and that is an estimate presented as a fact. The rule prints Windows' own command and
    /// Windows says the sizes when it runs.
    /// </summary>
    [Fact]
    public void Examine_ACategoriesListWithEverythingInIt_OffersNothingEver()
    {
        var rule = new DiskCleanupRule(
            new StubSource(
                Category("Recycle Bin"),
                Category("Update Cleanup"),
                Category("Windows Error Reporting Files", [@"C:\ProgramData\Microsoft\Windows\WER\"])),
            @"C:\");

        var finding = Fold(rule);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(0, finding.CandidateBytes);
        Assert.Contains("items: 0", finding.Lines);
        Assert.Contains("command: cleanmgr.exe /d C:", string.Join("\n", finding.Lines), StringComparison.Ordinal);
    }

    /// <summary>
    /// The broken case. An empty category list means the registry list Windows itself maintains
    /// could not be found, which is a machine this rule can say nothing about - and it must never
    /// read as a machine where Windows offers nothing.
    /// </summary>
    [Fact]
    public void Examine_AWindowsListThatIsNotThere_ReportsBrokenRatherThanNothingToOffer()
    {
        var rule = new DiskCleanupRule(new StubSource(), @"C:\");

        var finding = Fold(rule);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Contains("categories-read", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Contains("could not do its work", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Contains("verdict: broken", finding.Lines);
    }

    [Fact]
    public void Examine_AMachineThatIsNotWindows_ReportsBroken()
    {
        var rule = new DiskCleanupRule(new NotWindowsSource(), @"C:\");

        var finding = Fold(rule);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Contains("not running Windows", finding.BrokenReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A category whose folders name a question mark in place of a drive letter - Windows' own way
    /// of writing "this volume" - is a machine-wide place as far as the account is concerned, and
    /// is counted as needing an administrator rather than guessed at.
    /// </summary>
    [Fact]
    public void Examine_ACategoryThatNamesEveryVolume_IsCountedAsNeedingAnAdministrator()
    {
        var rule = new DiskCleanupRule(
            new StubSource(Category("Old ChkDsk Files", [@"?:\FOUND.000", @"?:\FOUND.001"])),
            @"C:\");

        var finding = Fold(rule);

        Assert.Equal(1, Control(finding, "categories-that-need-an-administrator"));
        Assert.Equal(0, Control(finding, "categories-that-look-only-in-the-accounts-own-folders"));
    }

    /// <summary>
    /// One rule per volume, because Windows' own tool is opened against one volume at a time and a
    /// rule looks in one place. The C: rule says C:, and the D: rule says D:.
    /// </summary>
    [Fact]
    public void Examine_TwoVolumes_GetTwoRulesWithTheirOwnCommands()
    {
        var source = new StubSource(Category("Recycle Bin"));

        var cFinding = Fold(new DiskCleanupRule(source, @"C:\"));
        var dFinding = Fold(new DiskCleanupRule(source, @"D:\"));

        Assert.Equal("windows-disk-cleanup-on-c", cFinding.RuleId);
        Assert.Equal("windows-disk-cleanup-on-d", dFinding.RuleId);
        Assert.Equal("cleanmgr.exe /d C:", cFinding.CommandToRun);
        Assert.Equal("cleanmgr.exe /d D:", dFinding.CommandToRun);
        Assert.Equal(@"C:\", new DiskCleanupRule(source, @"C:\").LooksIn);
    }

    private static RuleFinding Fold(DiskCleanupRule rule) =>
        RuleFold.Fold(rule, rule.Examine(new RuleContext
        {
            ScanRootPath = @"C:\",
            NowUtc = DateTimeOffset.UtcNow
        }));

    private static long Control(RuleFinding finding, string name) =>
        finding.Controls.Single(control => control.Name == name).Count;

    private static DiskCleanupCategory Category(string name, IReadOnlyList<string>? folders = null) =>
        new(name, folders ?? []);

    private sealed class StubSource(params DiskCleanupCategory[] categories) : IDiskCleanupSource
    {
        public IReadOnlyList<DiskCleanupCategory> Read() => categories;
    }

    private sealed class NotWindowsSource : IDiskCleanupSource
    {
        public IReadOnlyList<DiskCleanupCategory> Read() =>
            throw new PlatformNotSupportedException(
                "The Disk Cleanup categories live in the Windows registry and this machine is not running Windows.");
    }
}
