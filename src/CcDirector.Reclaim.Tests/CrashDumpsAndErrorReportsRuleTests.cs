using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// Crash dumps and Windows error reports, against a tree the test builds.
///
/// The whole rule rests on the system's own records of crashes that already finished, and the
/// tests hold the two sides of that: an old dump or an already-sent archive is offered, and a
/// report still waiting to be sent is counted and never offered, because deleting it would mean
/// the error it describes never reaches anybody. The broken case is the machine where not one of
/// the places Windows Error Reporting itself maintains can be found: that is a machine this rule
/// can say nothing about, and it must not read as a machine with nothing to remove.
/// </summary>
public class CrashDumpsAndErrorReportsRuleTests
{
    private static readonly DateTimeOffset LongAgo = DateTimeOffset.UtcNow.AddDays(-400);
    private static readonly DateTimeOffset JustNow = DateTimeOffset.UtcNow;

    [Fact]
    public void Examine_OldCrashDumps_AreOfferedOneByOne()
    {
        using var machine = new FixtureTree(nameof(Examine_OldCrashDumps_AreOfferedOneByOne));
        var oldOne = OldDump(machine, "conhost.exe.13996.dmp", 900);
        var oldTwo = OldDump(machine, "POWERPNT.EXE.27188.dmp", 5_000);

        var finding = Fold(machine);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Equal(2, finding.Candidates.Count);
        Assert.Equal(oldOne, finding.Candidates[0].Path);
        Assert.Equal(900, finding.Candidates[0].Bytes);
        Assert.Contains(finding.Candidates, candidate => candidate.Path == oldTwo);
        Assert.Contains(finding.Candidates, candidate => candidate.Bytes == 5_000);
    }

    /// <summary>
    /// The record is the deletion... the crash itself: a dump written yesterday is one somebody
    /// may still be debugging, so the age gate keeps it and the control says how many.
    /// </summary>
    [Fact]
    public void Examine_ADumpYoungerThanTheAgeGate_IsDeclinedAndCounted()
    {
        using var machine = new FixtureTree(nameof(Examine_ADumpYoungerThanTheAgeGate_IsDeclinedAndCounted));
        OldDump(machine, "old.dmp", 900);
        var youngPath = machine.File(Path.Combine("CrashDumps", "young.dmp"), 5_000);
        File.SetLastWriteTimeUtc(youngPath, JustNow.UtcDateTime);

        var finding = Fold(machine);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Single(finding.Candidates);
        Assert.Equal(1, Control(finding, "crash-dumps-too-young-to-offer"));
    }

    /// <summary>
    /// Only the dump files themselves: a folder Windows Error Reporting writes its dumps into can
    /// hold other things, and none of them is a thing this rule has a record for.
    /// </summary>
    [Fact]
    public void Examine_AFileThatIsNotADump_IsIgnored()
    {
        using var machine = new FixtureTree(nameof(Examine_AFileThatIsNotADump_IsIgnored));
        OldDump(machine, "old.dmp", 900);
        var notADump = machine.File(Path.Combine("CrashDumps", "notes.txt"), 5_000);
        File.SetLastWriteTimeUtc(notADump, LongAgo.UtcDateTime);

        var finding = Fold(machine);

        Assert.Single(finding.Candidates);
        Assert.Equal(1, Control(finding, "crash-dumps-found"));
    }

    [Fact]
    public void Examine_AnArchiveOfReportsWindowsAlreadySent_IsOfferedWhole()
    {
        using var machine = new FixtureTree(nameof(Examine_AnArchiveOfReportsWindowsAlreadySent_IsOfferedWhole));
        SentReport(machine, "AppCrash_one", 1_000);
        SentReport(machine, "AppCrash_two", 2_500);

        var finding = Fold(machine);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        var offered = Assert.Single(finding.Candidates);
        Assert.Equal(Path.Combine(machine.Root, "WER", "ReportArchive"), offered.Path);
        Assert.Equal(3_500, offered.Bytes);
        Assert.Equal(2, Control(finding, "wer-reports-already-sent-and-archived"));
        Assert.Contains("already sent", offered.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// The queue is the opposite of the archive: a report still waiting to be sent has not finished
    /// happening yet, so it is counted where the reader can see it and never offered.
    /// </summary>
    [Fact]
    public void Examine_ReportsStillWaitingToBeSent_AreCountedAndNeverOffered()
    {
        using var machine = new FixtureTree(nameof(Examine_ReportsStillWaitingToBeSent_AreCountedAndNeverOffered));
        machine.Folder(Path.Combine("WER", "ReportQueue", "AppCrash_waiting"));
        SentReport(machine, "AppCrash_sent", 1_000);

        var finding = Fold(machine);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Single(finding.Candidates);
        Assert.Equal(1, Control(finding, "wer-reports-still-waiting-to-be-sent"));
        Assert.DoesNotContain(finding.Candidates, candidate =>
            candidate.Path.Contains("ReportQueue", StringComparison.Ordinal));
    }

    [Fact]
    public void Examine_ArchiveThatReceivedAReportYesterday_IsDeclinedAsTooYoung()
    {
        using var machine = new FixtureTree(nameof(Examine_ArchiveThatReceivedAReportYesterday_IsDeclinedAsTooYoung));
        var report = machine.File(Path.Combine("WER", "ReportArchive", "AppCrash_fresh", "Report.wer"), 1_000);
        File.SetLastWriteTimeUtc(report, JustNow.UtcDateTime);

        var finding = Fold(machine);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(1, Control(finding, "wer-stores-too-young-to-offer"));
    }

    /// <summary>
    /// The broken case. Windows Error Reporting maintains every one of these places itself, so a
    /// machine where not one of them can be found is a machine this rule can say nothing about -
    /// and that must never read as a machine with nothing to remove.
    /// </summary>
    [Fact]
    public void Examine_NotOneOfThePlaces_ReportsBrokenRatherThanNothingToRemove()
    {
        using var machine = new FixtureTree(nameof(Examine_NotOneOfThePlaces_ReportsBrokenRatherThanNothingToRemove));

        var finding = Fold(machine);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Contains("places-found", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Contains("could not do its work", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Contains("verdict: broken", finding.Lines);
    }

    /// <summary>
    /// The machine's own dump folders are usually closed to an ordinary account, and that is a
    /// normal machine, not a broken one: the place is declined and counted, and the rule says what
    /// it found everywhere else.
    /// </summary>
    [Fact]
    public void Examine_AMachineDumpFolderThatWouldNotBeListed_IsDeclinedAndNotBroken()
    {
        using var machine = new FixtureTree(nameof(Examine_AMachineDumpFolderThatWouldNotBeListed_IsDeclinedAndNotBroken));
        machine.Folder("Minidump");
        machine.DenyListing("Minidump");
        OldDump(machine, "old.dmp", 900);

        var finding = Fold(machine);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Equal(1, Control(finding, "places-that-would-not-be-listed"));
        Assert.Single(finding.Candidates);
    }

    /// <summary>
    /// The full memory dump of the machine needs an administrator even to remove, and the candidate
    /// says so, because the rule never raises itself to one.
    /// </summary>
    [Fact]
    public void Examine_TheMachinesOwnMemoryDump_IsOfferedAndSaysItNeedsAnAdministrator()
    {
        using var machine = new FixtureTree(nameof(Examine_TheMachinesOwnMemoryDump_IsOfferedAndSaysItNeedsAnAdministrator));
        var memoryDump = machine.File(Path.Combine("MEMORY.DMP"), 40_000);
        File.SetLastWriteTimeUtc(memoryDump, LongAgo.UtcDateTime);

        var finding = Fold(machine);

        Assert.True(finding.NeedsAdministrator);
        var offered = Assert.Single(finding.Candidates);
        Assert.Equal(memoryDump, offered.Path);
        Assert.Contains("needs an administrator", offered.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rule looks inside the folder that was asked about. The places outside it are not examined
    /// at all, and are counted rather than passed over in silence.
    /// </summary>
    [Fact]
    public void Examine_PlacesOutsideTheFolderAskedAbout_AreNotExaminedAndAreCounted()
    {
        using var machine = new FixtureTree(nameof(Examine_PlacesOutsideTheFolderAskedAbout_AreNotExaminedAndAreCounted));
        OldDump(machine, "old.dmp", 900);
        SentReport(machine, "AppCrash_sent", 1_000);

        // Asked about the crash dumps folder alone: the memory dump and every report store sit
        // outside it, so they are counted rather than examined.
        var finding = Fold(machine, scanRootPath: Path.Combine(machine.Root, "CrashDumps"));

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Single(finding.Candidates);
        Assert.Equal(6, Control(finding, "places-outside-the-folder-asked-about"));
    }

    private static RuleFinding Fold(FixtureTree machine, string? scanRootPath = null)
    {
        var rule = new CrashDumpsAndErrorReportsRule(
            Path.Combine(machine.Root, "CrashDumps"),
            Path.Combine(machine.Root, "Minidump"),
            Path.Combine(machine.Root, "MEMORY.DMP"),
            Path.Combine(machine.Root, "WER"),
            Path.Combine(machine.Root, "account-WER"));

        return RuleFold.Fold(rule, rule.Examine(new RuleContext
        {
            ScanRootPath = scanRootPath ?? machine.Root,
            NowUtc = DateTimeOffset.UtcNow
        }));
    }

    private static string OldDump(FixtureTree machine, string name, int size)
    {
        var path = machine.File(Path.Combine("CrashDumps", name), size);
        File.SetLastWriteTimeUtc(path, LongAgo.UtcDateTime);
        return path;
    }

    private static void SentReport(FixtureTree machine, string name, int size)
    {
        var path = machine.File(Path.Combine("WER", "ReportArchive", name, "Report.wer"), size);
        File.SetLastWriteTimeUtc(path, LongAgo.UtcDateTime);
    }

    private static long Control(RuleFinding finding, string name) =>
        finding.Controls.Single(control => control.Name == name).Count;
}
