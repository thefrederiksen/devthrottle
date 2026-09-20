using System.Buffers.Binary;
using System.Text;
using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The recycle bin rule, against a bin the test builds with the system's own record format.
///
/// The record is the point of the rule: every candidate must carry the bin's own deletion record,
/// written here in the same bytes Windows writes them - a version, a size, the deletion moment,
/// and the original path - and everything else in the bin must survive. On the machine this rule
/// was measured on, more than half the bytes in the bin folders are entries without records, and
/// Windows' own view of the bin says it is empty, so the tests hold the boundary hardest exactly
/// where the real machine puts the pressure: data without a record is never offered, no matter how
/// much of the bin it is.
/// </summary>
public class RecycleBinRuleTests
{
    private const string FirstAccount = "S-1-5-21-one";
    private const string SecondAccount = "S-1-5-21-two";

    private static readonly DateTimeOffset LongAgo = DateTimeOffset.UtcNow.AddDays(-400);
    private static readonly DateTimeOffset JustNow = DateTimeOffset.UtcNow;

    [Fact]
    public void Examine_ARecordedDeletionOlderThanTheAgeGate_IsOfferedWithItsDataAsOneThing()
    {
        using var bin = Bin(nameof(Examine_ARecordedDeletionOlderThanTheAgeGate_IsOfferedWithItsDataAsOneThing));
        Record(bin, FirstAccount, "deleted-long-ago", deletedAt: LongAgo, dataBytes: 1_000);

        var finding = Fold(bin);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        var offered = Assert.Single(finding.Candidates);
        Assert.Equal(Path.Combine(bin.Root, FirstAccount), offered.Path);
        Assert.Equal(1_000, offered.Bytes);
        Assert.Equal(LongAgo.UtcDateTime, offered.LastWrittenUtc.UtcDateTime);
        Assert.Contains("records", offered.Why, StringComparison.Ordinal);
        Assert.Equal(1, Control(finding, "records-read"));
    }

    /// <summary>
    /// The age gate is judged on the deletion moment in the record, which is when the owner acted -
    /// not on any file's write time.
    /// </summary>
    [Fact]
    public void Examine_ARecordDeletedYesterday_IsDeclinedAndCounted()
    {
        using var bin = Bin(nameof(Examine_ARecordDeletedYesterday_IsDeclinedAndCounted));
        Record(bin, FirstAccount, "deleted-yesterday", deletedAt: JustNow, dataBytes: 1_000);

        var finding = Fold(bin);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(1, Control(finding, "records-too-young-to-offer"));
    }

    /// <summary>
    /// Data with no record is a thing nobody has proven was deleted, so it is counted and never
    /// offered, however much of the bin it is. On the measured machine this is most of the bytes in
    /// the bin folders, and the boundary holds there anyway.
    /// </summary>
    [Fact]
    public void Examine_DataWithoutARecord_IsCountedAndNeverOffered()
    {
        using var bin = Bin(nameof(Examine_DataWithoutARecord_IsCountedAndNeverOffered));
        bin.File(Path.Combine(FirstAccount, "$RNOBODY.claims.it"), 50_000);
        bin.File(Path.Combine(FirstAccount, "an-entry-no-record-names"), 25_000);

        var finding = Fold(bin);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(2, Control(finding, "entries-without-a-record"));
        Assert.Equal(0, Control(finding, "records-read"));
    }

    [Fact]
    public void Examine_ARecordWithoutItsData_IsCountedAndNeverOffered()
    {
        using var bin = Bin(nameof(Examine_ARecordWithoutItsData_IsCountedAndNeverOffered));
        Record(bin, FirstAccount, "data-already-gone", deletedAt: LongAgo, dataBytes: 0, writeData: false);

        var finding = Fold(bin);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(1, Control(finding, "records-without-their-data"));
    }

    /// <summary>
    /// A record this rule cannot read is a record it declines: a pile of bytes that is not a
    /// deletion record proves nothing, and nothing is offered on the strength of it.
    /// </summary>
    [Fact]
    public void Examine_ARecordThatIsNotARecord_IsDeclinedAndCounted()
    {
        using var bin = Bin(nameof(Examine_ARecordThatIsNotARecord_IsDeclinedAndCounted));
        bin.File(Path.Combine(FirstAccount, "$IGARBAGE.bin"), 20);

        var finding = Fold(bin);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(1, Control(finding, "records-that-would-not-be-read"));
    }

    /// <summary>
    /// The mandate's own words: per-user and per-volume bins are what they are, not one thing. Two
    /// accounts' bins on one volume are two candidates, each carrying only its own records.
    /// </summary>
    [Fact]
    public void Examine_TwoAccountsBinsOnOneVolume_AreOfferedAsTwoSeparateThings()
    {
        using var bin = Bin(nameof(Examine_TwoAccountsBinsOnOneVolume_AreOfferedAsTwoSeparateThings));
        Record(bin, FirstAccount, "firsts-item", deletedAt: LongAgo, dataBytes: 1_000);
        Record(bin, SecondAccount, "seconds-item", deletedAt: LongAgo, dataBytes: 2_000);

        var finding = Fold(bin);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Equal(2, finding.Candidates.Count);
        Assert.Contains(finding.Candidates, candidate =>
            candidate.Path == Path.Combine(bin.Root, FirstAccount) && candidate.Bytes == 1_000);
        Assert.Contains(finding.Candidates, candidate =>
            candidate.Path == Path.Combine(bin.Root, SecondAccount) && candidate.Bytes == 2_000);
    }

    /// <summary>
    /// Another account's bin refuses its listing to this one, and that is a normal machine rather
    /// than a broken instrument: it is counted, and the account's own bin is still offered.
    /// </summary>
    [Fact]
    public void Examine_AnotherAccountsBinThatWouldNotBeListed_IsCountedAndNotBroken()
    {
        using var bin = Bin(nameof(Examine_AnotherAccountsBinThatWouldNotBeListed_IsCountedAndNotBroken));
        bin.Folder(SecondAccount);
        bin.DenyListing(SecondAccount);
        Record(bin, FirstAccount, "mine", deletedAt: LongAgo, dataBytes: 1_000);

        var finding = Fold(bin);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Equal(1, Control(finding, "bins-that-would-not-be-listed"));
        Assert.Single(finding.Candidates);
    }

    /// <summary>
    /// The broken case. The bin folder exists and holds account bins, but every one of them
    /// refused its listing: this rule could not do its work, and that must never read as a bin
    /// with nothing in it.
    /// </summary>
    [Fact]
    public void Examine_ABinWhereEveryAccountRefusedItsListing_ReportsBrokenRatherThanNothingToRemove()
    {
        using var bin = Bin(nameof(Examine_ABinWhereEveryAccountRefusedItsListing_ReportsBrokenRatherThanNothingToRemove));
        bin.Folder(FirstAccount);
        bin.DenyListing(FirstAccount);

        var finding = Fold(bin);

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Contains("bins-listed", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Contains("could not do its work", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Contains("verdict: broken", finding.Lines);
    }

    /// <summary>
    /// A volume with no bin folder is a volume that has never held a deleted item or has recycling
    /// switched off: a real answer, offered nothing, not called broken.
    /// </summary>
    /// <summary>
    /// A volume with no bin folder is a volume that has never held a deleted item or has recycling
    /// switched off: a real answer, offered nothing, not called broken. The absence is established
    /// by the listing refusing with not-found, never by an existence question - see the test below.
    /// </summary>
    [Fact]
    public void Examine_AVolumeWithNoBinFolder_IsOkAndOffersNothing()
    {
        var rule = new RecycleBinRule(
            Path.Combine(Path.GetTempPath(), "cc-reclaim-tests", "a-bin-that-was-never-made"));

        var finding = RuleFold.Fold(rule, rule.Examine(new RuleContext
        {
            ScanRootPath = Path.GetTempPath(),
            NowUtc = DateTimeOffset.UtcNow
        }));

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(0, Control(finding, "bins-found"));
    }

    /// <summary>
    /// The Delivery Lead's finding, fixed before review. An existence question answers false for a
    /// folder that is not there AND for one that cannot be told, with the reason swallowed, and
    /// "could not tell" must never be reported as "nothing to remove" - the bin tree on this very
    /// machine holds a folder that refuses its listing. The rule probes the bin by attempting its
    /// listing: not-found stays the honest absent answer above, and every other failure says the
    /// rule could not do its work.
    /// </summary>
    [Fact]
    public void Examine_ABinFolderThatWouldNotBeListed_ReportsBrokenRatherThanNothingToRemove()
    {
        using var tree = new FixtureTree(nameof(Examine_ABinFolderThatWouldNotBeListed_ReportsBrokenRatherThanNothingToRemove));
        tree.Folder("bin");
        tree.DenyListing("bin");

        var rule = new RecycleBinRule(Path.Combine(tree.Root, "bin"));
        var finding = RuleFold.Fold(rule, rule.Examine(new RuleContext
        {
            ScanRootPath = Path.GetTempPath(),
            NowUtc = DateTimeOffset.UtcNow
        }));

        Assert.Equal(RuleVerdict.Broken, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Contains("would not be listed", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Contains("could not do its work", finding.BrokenReason!, StringComparison.Ordinal);
        Assert.Contains("verdict: broken", finding.Lines);
    }

    /// <summary>
    /// A record whose data cannot be measured is declined, because a size short by an unknown
    /// amount is not a measurement.
    /// </summary>
    [Fact]
    public void Examine_ARecordWhoseDataWouldNotBeRead_IsDeclinedAndCounted()
    {
        using var bin = Bin(nameof(Examine_ARecordWhoseDataWouldNotBeRead_IsDeclinedAndCounted));
        Record(bin, FirstAccount, "unreadable-folder", deletedAt: LongAgo, dataBytes: 1_000,
            writeData: true, dataIsFolder: true);
        bin.DenyListing(Path.Combine(FirstAccount, "$Runreadable-folder"));

        var finding = Fold(bin);

        Assert.Equal(RuleVerdict.Ok, finding.Verdict);
        Assert.Empty(finding.Candidates);
        Assert.Equal(1, Control(finding, "pairs-that-would-not-be-read"));
    }

    /// <summary>
    /// The mandate's own words, held as output: the bin is the one place on the machine that is
    /// already a holding folder, so emptying it is the end of the line, and "what is lost" says so.
    /// </summary>
    [Fact]
    public void Examine_WhatIsLost_SaysThatEmptyingTheBinIsTheEndOfTheLine()
    {
        using var bin = Bin(nameof(Examine_WhatIsLost_SaysThatEmptyingTheBinIsTheEndOfTheLine));
        Record(bin, FirstAccount, "item", deletedAt: LongAgo, dataBytes: 10);

        var text = string.Join("\n", Fold(bin).Lines);

        Assert.Contains("end of the line", text, StringComparison.Ordinal);
        Assert.Contains("already the holding folder", text, StringComparison.Ordinal);
        Assert.Contains("from the bin's own restore", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The account's own bin needs no administrator, and the rule never raises itself to one.
    /// </summary>
    [Fact]
    public void Examine_TheAccountsOwnBin_NeedsNoAdministratorAndPrintsNoCommand()
    {
        using var bin = Bin(nameof(Examine_TheAccountsOwnBin_NeedsNoAdministratorAndPrintsNoCommand));
        Record(bin, FirstAccount, "item", deletedAt: LongAgo, dataBytes: 10);

        var finding = Fold(bin);

        Assert.False(finding.NeedsAdministrator);
        Assert.Null(finding.CommandToRun);
        Assert.Contains("needs-administrator: no", finding.Lines);
    }

    private static FixtureTree Bin(string name)
    {
        var tree = new FixtureTree(name);
        tree.Folder(FirstAccount);
        return tree;
    }

    private static RuleFinding Fold(FixtureTree bin)
    {
        var rule = new RecycleBinRule(bin.Root);
        return RuleFold.Fold(rule, rule.Examine(new RuleContext
        {
            ScanRootPath = Path.GetTempPath(),
            NowUtc = DateTimeOffset.UtcNow
        }));
    }

    /// <summary>
    /// Write one deletion in the system's own record format: the record that says where the file
    /// came from and when the owner deleted it, and the data the record is paired with.
    /// </summary>
    private static void Record(
        FixtureTree bin,
        string account,
        string id,
        DateTimeOffset deletedAt,
        int dataBytes,
        bool writeData = true,
        bool dataIsFolder = false)
    {
        Directory.CreateDirectory(Path.Combine(bin.Root, account));

        var original = @"C:\Users\soren\Documents\somewhere\the-file.txt";
        var name = Encoding.Unicode.GetBytes(original);

        var record = new byte[28 + name.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(record, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), (ulong)dataBytes);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(16), deletedAt.ToFileTime());
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)name.Length);
        name.CopyTo(record, 28);

        File.WriteAllBytes(Path.Combine(bin.Root, account, $"$I{id}.txt"), record);

        if (!writeData) return;

        if (dataIsFolder)
        {
            bin.Folder(Path.Combine(account, $"$R{id}"));
            bin.File(Path.Combine(account, $"$R{id}", "inside.bin"), Math.Max(dataBytes, 1));
        }
        else
        {
            bin.File(Path.Combine(account, $"$R{id}.txt"), dataBytes);
        }
    }

    private static long Control(RuleFinding finding, string name) =>
        finding.Controls.Single(control => control.Name == name).Count;
}
