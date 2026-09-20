using CcDirector.Core.Storage;
using CcDirector.Reclaim.Removal;
using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;
using Xunit;
using Xunit.Abstractions;
using static CcDirector.Reclaim.Tests.ReclaimRefusalTests;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The ten refusals, each a numbered test named for what it declines, in the house style of
/// tools/cc-worktrees: nearly every test here is a refusal.
///
/// Every tree is built by the test that uses it and destroyed when it ends. No test points at
/// anything on the real machine: the protected paths are stand-ins the test builds over its own
/// fixture tree carrying the real sentences from CcStorage.ProtectedPaths, the user folders are
/// stand-ins, and the holding root always sits inside the tree.
///
/// Each refusal test triggers exactly its own refusal and nothing else. On 19 September 2026 each
/// refusal was taken out by hand, one at a time, and the whole project run: every numbered test went
/// red with its own refusal deleted - a refusal with a green test that stays green when the refusal
/// is deleted is not a refusal, it is a comment. They did not all go red the same way, and the
/// difference matters. For refusals 1, 2, 3, 6, 8, 9 and 10 the item became eligible or moved. For
/// refusals 4, 5 and 7 the numbered test went red only on the NAME of the refusal, because a later
/// check still refused the item: the final-path check (10) catches a junction on Windows, and the
/// changed-since-recommendation check (8) catches an item its rule no longer offers. Refusal 5 has a
/// second test below in which it is the only check that refuses. Refusals 4 and 7 have none, and
/// cannot on Windows: a link always has a different final path, and the fold empties a broken
/// rule's offer, so check 10 or check 8 always stands behind them. The mutations, the tests that went
/// red and what each said are in docs/missions/reclaim-the-disk-2026-09-18/phase-3-proof.md.
/// </summary>
public sealed class ReclaimRefusalTests(ITestOutputHelper output)
{
    // -- The ten, in the mandate's numbered order --

    [Fact]
    public void Reclaim_AnItemUnderAVaultPathTheResolverNames_IsRefusedEvenWhenARuleMatched()
    {
        // One fixture stand-in for every protected path the storage resolver names, each carrying the
        // resolver's own sentence for what it holds, each with a rule that matched an item under it.
        // The day somebody adds a path to CcStorage.ProtectedPaths, this test refuses it here with no
        // change anywhere in the reclaim code.
        using var tree = new FixtureTree(nameof(Reclaim_AnItemUnderAVaultPathTheResolverNames_IsRefusedEvenWhenARuleMatched));
        var protectedList = CcStorage.ProtectedPaths();
        var rules = new List<IReclaimRule>();
        var standIns = new List<CcStorage.ProtectedPath>();

        for (var index = 0; index < protectedList.Count; index++)
        {
            var folder = tree.Folder($"protect-{index}");
            var item = Path.Combine(folder, "cc-director-tests");
            Directory.CreateDirectory(item);
            File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);
            rules.Add(new TestScratchFoldersRule(folder));
            standIns.Add(new CcStorage.ProtectedPath(protectedList[index].Source, folder, protectedList[index].WhatItIs));
        }

        var result = DryRun(tree, rules, standIns);

        Assert.Equal(protectedList.Count, result.Items.Count);
        foreach (var item in result.Items)
        {
            Assert.False(item.Gate.Eligible);
            Assert.Equal(RefusalCheck.ProtectedPath, item.Gate.FiredCheck);
            Assert.Null(item.Gate.ConfigurationReason);
        }

        // Each refusal names its own protected path and the resolver's own sentence for what it holds.
        foreach (var (item, standIn) in result.Items.Zip(standIns))
        {
            Assert.Contains(standIn.WhatItIs, item.Gate.Reason);
            Assert.Contains(standIn.Path, item.Gate.Reason);
        }

        // And nothing moved: every stand-in is still on the disk with its bytes.
        for (var index = 0; index < standIns.Count; index++)
        {
            var item = Path.Combine(standIns[index].Path, "cc-director-tests");
            Assert.True(Directory.Exists(item));
            Assert.Equal(64, new FileInfo(Path.Combine(item, "scratch.txt")).Length);
        }
    }

    [Fact]
    public void Reclaim_AnItemInsideAGitWorkingTree_IsRefusedAndPointsAtCcWorktrees()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnItemInsideAGitWorkingTree_IsRefusedAndPointsAtCcWorktrees));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);

        // An entry named .git, which is exactly what the check examines. No git installation is
        // needed: the check must answer on a machine where git is not installed.
        Directory.CreateDirectory(Path.Combine(temp, ".git"));

        var result = DryRun(tree, [new TestScratchFoldersRule(temp)]);

        var outcome = Assert.Single(result.Items);
        Assert.False(outcome.Gate.Eligible);
        Assert.Equal(RefusalCheck.GitWorkingTree, outcome.Gate.FiredCheck);
        Assert.Contains("cc-worktrees", outcome.Gate.Reason);
        Assert.Contains(Path.Combine(temp, ".git"), outcome.Gate.Reason);
    }

    [Fact]
    public void Reclaim_AnItemInsideAWorkingTreeMarkedByAGitFile_IsRefusedToo()
    {
        // The file a linked worktree leaves, rather than a folder.
        using var tree = new FixtureTree(nameof(Reclaim_AnItemInsideAWorkingTreeMarkedByAGitFile_IsRefusedToo));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);
        File.WriteAllText(Path.Combine(temp, ".git"), "gitdir: ../elsewhere/.git/worktrees/one");

        var result = DryRun(tree, [new TestScratchFoldersRule(temp)]);

        var outcome = Assert.Single(result.Items);
        Assert.Equal(RefusalCheck.GitWorkingTree, outcome.Gate.FiredCheck);
    }

    [Fact]
    public void Reclaim_AnItemUnderTheUsersOwnFolders_IsRefused()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnItemUnderTheUsersOwnFolders_IsRefused));
        var myDocuments = tree.Folder("my-documents");
        var item = Path.Combine(myDocuments, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);

        var result = DryRun(tree, [new TestScratchFoldersRule(myDocuments)], userFolders: [myDocuments]);

        var outcome = Assert.Single(result.Items);
        Assert.False(outcome.Gate.Eligible);
        Assert.Equal(RefusalCheck.UserFolder, outcome.Gate.FiredCheck);
        Assert.Contains(myDocuments, outcome.Gate.Reason);
    }

    [Fact]
    public void Reclaim_AnItemReachedThroughALinkOrJunction_IsRefused()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnItemReachedThroughALinkOrJunction_IsRefused));
        var real = tree.Folder("real-temp");
        var item = Path.Combine(real, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);

        // A junction that stands where the rule's folder would, pointing at a folder full of bytes the
        // rule matched. The rule sees the item through the link; the gate must refuse it.
        tree.DirectoryLink("temp", "real-temp");

        var result = DryRun(tree, [new TestScratchFoldersRule(Path.Combine(tree.Root, "temp"))]);

        var outcome = Assert.Single(result.Items);
        Assert.False(outcome.Gate.Eligible);
        Assert.Equal(RefusalCheck.LinkOrJunction, outcome.Gate.FiredCheck);
        Assert.Contains(Path.Combine(tree.Root, "temp"), outcome.Gate.Reason);

        // The target is untouched: nothing was removed from behind the link.
        Assert.True(Directory.Exists(item));
        Assert.Equal(64, new FileInfo(Path.Combine(item, "scratch.txt")).Length);
    }

    [Fact]
    public void Reclaim_AnItemYoungerThanItsRulesAgeGate_IsRefusedAgainAtTheMomentOfTheMove()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnItemYoungerThanItsRulesAgeGate_IsRefusedAgainAtTheMomentOfTheMove));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);

        var rule = new TestScratchFoldersRule(temp);
        var written = new DirectoryInfo(item).LastWriteTimeUtc;

        // Aged past the gate at recommendation time: the rule is judged against a moment four
        // hundred days after the folder was written, and offers it.
        var recommendationMoment = new DateTimeOffset(written, TimeSpan.Zero).AddDays(400);
        var recommendation = RuleFold.Fold(rule, rule.Examine(new RuleContext
        {
            ScanRootPath = tree.Root,
            NowUtc = recommendationMoment
        }));
        var recommended = Assert.Single(recommendation.Candidates);

        // Judged against a moment that puts it inside the gate at move time: three days after it was
        // written, with a seven-day gate. The gate re-judges, and refuses.
        var moveMoment = new DateTimeOffset(written, TimeSpan.Zero).AddDays(3);
        var gate = new RefusalGate(GateOptions(tree, apply: false, nowUtc: moveMoment));
        var outcome = gate.Check(recommended, rule);

        Assert.False(outcome.Eligible);
        Assert.Equal(RefusalCheck.AgeGate, outcome.FiredCheck);
        Assert.Contains("age gate of 7 days", outcome.Reason);
        Assert.True(Directory.Exists(item));
    }

    [Fact]
    public void Reclaim_AnItemWithAFileOpenInIt_IsRefused()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnItemWithAFileOpenInIt_IsRefused));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);

        // A rule that matched the folder, and a file the test holds open for the length of the check.
        var rule = new OfferingRule(temp, new ReclaimCandidate(
            item, 64, DateTimeOffset.UtcNow, "created by one of DevThrottle's own test suites"));

        using (new FileStream(Path.Combine(item, "scratch.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = DryRun(tree, [rule]);
            var outcome = Assert.Single(result.Items);
            Assert.False(outcome.Gate.Eligible);
            Assert.Equal(RefusalCheck.OpenFile, outcome.Gate.FiredCheck);
        }

        Assert.True(File.Exists(Path.Combine(item, "scratch.txt")));
    }

    [Fact]
    public void Reclaim_AnItemOfARuleWhoseControlsAreEmptyAtTheMove_IsRefused()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnItemOfARuleWhoseControlsAreEmptyAtTheMove_IsRefused));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);

        // A rule that answers well at the recommendation and broken at the moment of the move - the
        // shape of a record source that loads once and fails the second time.
        var rule = new OfferingRule(
            temp,
            new ReclaimCandidate(item, 64, DateTimeOffset.UtcNow, "created by one of DevThrottle's own test suites"),
            answerBrokenFromExamination: 2);

        var result = DryRun(tree, [rule]);

        var outcome = Assert.Single(result.Items);
        Assert.False(outcome.Gate.Eligible);
        Assert.Equal(RefusalCheck.EmptyControls, outcome.Gate.FiredCheck);
        Assert.Contains("could not do its work at the moment of the move", outcome.Gate.Reason);
        Assert.True(Directory.Exists(item));
    }

    [Fact]
    public void Reclaim_AnItemThatChangedSinceItWasRecommended_IsRefused()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnItemThatChangedSinceItWasRecommended_IsRefused));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[700]);

        var rule = new TestScratchFoldersRule(temp);

        // The recommendation: measured at seven hundred bytes.
        var recommendation = RuleFold.Fold(rule, rule.Examine(new RuleContext
        {
            ScanRootPath = tree.Root,
            NowUtc = DateTimeOffset.UtcNow.AddDays(400)
        }));
        var recommended = Assert.Single(recommendation.Candidates);
        Assert.Equal(700, recommended.Bytes);

        // Between the recommendation and the gate run, the candidate grows by a hundred bytes.
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[800]);

        var gate = new RefusalGate(GateOptions(tree, apply: false, nowUtc: DateTimeOffset.UtcNow.AddDays(400)));
        var outcome = gate.Check(recommended, rule);

        Assert.False(outcome.Eligible);
        Assert.Equal(RefusalCheck.ChangedSinceRecommendation, outcome.FiredCheck);
        Assert.Contains("700", outcome.Reason);
        Assert.Contains("800", outcome.Reason);
        Assert.True(Directory.Exists(item));
    }

    [Fact]
    public void Reclaim_WithoutTheApplyFlag_MovesNothingEvenWhenEveryOtherCheckPassed()
    {
        using var tree = new FixtureTree(nameof(Reclaim_WithoutTheApplyFlag_MovesNothingEvenWhenEveryOtherCheckPassed));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[4096]);

        var result = DryRun(tree, [new TestScratchFoldersRule(temp)]);

        var outcome = Assert.Single(result.Items);
        Assert.True(outcome.Gate.Eligible, outcome.Gate.Reason ?? "the item should be eligible");
        Assert.False(result.Apply);

        // The ninth check fired, in the same enumeration as the other nine.
        var ninth = outcome.Gate.Checks.Single(check => check.Check == RefusalCheck.NoApplyFlag);
        Assert.Equal(CheckOutcome.Refused, ninth.Outcome);
        Assert.NotNull(ninth.Reason);

        // And nothing moved: the item is where it was, with the same bytes, and no holding root was
        // even created.
        Assert.True(File.Exists(Path.Combine(item, "scratch.txt")));
        Assert.Equal(4096, new FileInfo(Path.Combine(item, "scratch.txt")).Length);
        Assert.False(Directory.Exists(Path.Combine(tree.Root, "holding")));

        // The report says what it would move, and that a dry run is what is holding it back.
        Assert.Contains("items-would-move: 1", result.Lines);
        Assert.Contains(result.Lines, line => line.StartsWith("refusal 9 fired at the run level", StringComparison.Ordinal));
    }

    [Fact]
    public void Reclaim_APathThatIsNotCanonicalAfterResolution_IsRefused()
    {
        using var tree = new FixtureTree(nameof(Reclaim_APathThatIsNotCanonicalAfterResolution_IsRefused));
        var temp = tree.Folder("temp");
        var longNamed = Path.Combine(temp, "canonicalization-proof-folder");
        Directory.CreateDirectory(longNamed);
        File.WriteAllBytes(Path.Combine(longNamed, "scratch.txt"), new byte[64]);

        // The folder, referred to by the short name Windows keeps for it: a spelling the final-path
        // answer does not match. The fixture proves the short form is real before the test uses it,
        // and throws on a platform that does not keep short names rather than pretending.
        var shortPath = tree.WindowsShortPath(Path.Combine("temp", "canonicalization-proof-folder"));
        var rule = new OfferingRule(temp, new ReclaimCandidate(
            shortPath, 64, DateTimeOffset.UtcNow, "created by one of DevThrottle's own test suites"));

        var result = DryRun(tree, [rule]);

        var outcome = Assert.Single(result.Items);
        Assert.False(outcome.Gate.Eligible);
        Assert.Equal(RefusalCheck.NotCanonical, outcome.Gate.FiredCheck);
        Assert.Contains(shortPath, outcome.Gate.Reason);
        Assert.Contains(longNamed, outcome.Gate.Reason);
    }

    // -- The cannot-answer shapes, and the run's own configuration --

    [Fact]
    public void Reclaim_AHoldingRootOnADifferentVolume_IsRefusedAsABrokenConfiguration()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AHoldingRootOnADifferentVolume_IsRefusedAsABrokenConfiguration));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);

        var rule = new OfferingRule(temp, new ReclaimCandidate(
            item, 64, DateTimeOffset.UtcNow, "created by one of DevThrottle's own test suites"));

        // A holding root spelled onto another volume - a name, not a real volume, because asking the
        // path which volume it names touches no disk. Removal is a move on the same volume.
        var elsewhere = Path.Combine(Path.GetPathRoot(Path.GetFullPath(tree.Root)) == "X:\\" ? "Y:\\" : "X:\\", "cc-reclaim-holding");
        var result = ReclaimRunner.Run(new ReclaimRunRequest
        {
            Rules = [rule],
            RootPath = tree.Root,
            HoldingRootPath = elsewhere,
            ProtectedPaths = [],
            UserFolders = [],
            Apply = false,
            NowUtc = DateTimeOffset.UtcNow
        });

        var outcome = Assert.Single(result.Items);
        Assert.False(outcome.Gate.Eligible);
        Assert.NotNull(outcome.Gate.ConfigurationReason);
        Assert.Contains("different volumes", outcome.Gate.ConfigurationReason);
        // Every check is reported not reached: none of them could answer for a run that is not fit to ask.
        Assert.All(outcome.Gate.Checks, check => Assert.Equal(CheckOutcome.NotReached, check.Outcome));
    }

    [Fact]
    public void Reclaim_AnUnresolvablePath_IsRefusedWithEveryCheckBeforeTheTenthNotReached()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnUnresolvablePath_IsRefusedWithEveryCheckBeforeTheTenthNotReached));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");

        // A candidate for an item that is not there at all: the path cannot be resolved, so no check
        // that asks the disk about it could run.
        var rule = new OfferingRule(temp, new ReclaimCandidate(
            item, 64, DateTimeOffset.UtcNow, "created by one of DevThrottle's own test suites"));

        var result = DryRun(tree, [rule]);

        var outcome = Assert.Single(result.Items);
        Assert.False(outcome.Gate.Eligible);
        Assert.Equal(RefusalCheck.NotCanonical, outcome.Gate.FiredCheck);
        Assert.Contains("would not resolve", outcome.Gate.Reason);

        var byNumber = outcome.Gate.Checks.ToDictionary(check => check.Check);
        for (var number = 1; number <= 9; number++)
            Assert.Equal(CheckOutcome.NotReached, byNumber[(RefusalCheck)number].Outcome);
        Assert.Equal(CheckOutcome.Refused, byNumber[RefusalCheck.NotCanonical].Outcome);
    }

    [Fact]
    public void Reclaim_AnUnlistableFolderOnTheWayToTheItem_IsRefusedBecauseTheCheckCouldNotAnswer()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnUnlistableFolderOnTheWayToTheItem_IsRefusedBecauseTheCheckCouldNotAnswer));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);

        var rule = new OfferingRule(temp, new ReclaimCandidate(
            item, 64, DateTimeOffset.UtcNow, "created by one of DevThrottle's own test suites"));

        // The item's own folder cannot be listed. The git check has to list it to answer, and a check
        // that cannot answer refuses.
        tree.DenyListing("temp/cc-director-tests");

        var result = DryRun(tree, [rule]);

        var outcome = Assert.Single(result.Items);
        Assert.False(outcome.Gate.Eligible);
        Assert.Equal(RefusalCheck.GitWorkingTree, outcome.Gate.FiredCheck);
        Assert.Contains("could not be listed", outcome.Gate.Reason);
        Assert.Contains("could not answer", outcome.Gate.Reason);
    }

    [Fact]
    public void Reclaim_AFolderThatCannotBeAskedAboutItsFiles_IsRefused()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AFolderThatCannotBeAskedAboutItsFiles_IsRefused));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        Directory.CreateDirectory(Path.Combine(item, "locked"));
        File.WriteAllBytes(Path.Combine(item, "locked", "scratch.txt"), new byte[64]);

        var rule = new OfferingRule(temp, new ReclaimCandidate(
            item, 64, DateTimeOffset.UtcNow, "created by one of DevThrottle's own test suites"));

        // A folder inside the item that refuses to be listed. The open-file check walks the item, and
        // something inside it that cannot be asked is answered as in use, never as free.
        tree.DenyListing("temp/cc-director-tests/locked");

        var result = DryRun(tree, [rule]);

        var outcome = Assert.Single(result.Items);
        Assert.False(outcome.Gate.Eligible);
        Assert.Equal(RefusalCheck.OpenFile, outcome.Gate.FiredCheck);
        Assert.True(Directory.Exists(item));
    }

    // -- The shape of the answer --

    [Fact]
    public void RefusalGate_ARefusedItem_CarriesAllTenOutcomesWithTheNotReachedOnesMarked()
    {
        using var tree = new FixtureTree(nameof(RefusalGate_ARefusedItem_CarriesAllTenOutcomesWithTheNotReachedOnesMarked));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);
        Directory.CreateDirectory(Path.Combine(temp, ".git"));

        var result = DryRun(tree, [new TestScratchFoldersRule(temp)]);

        var outcome = Assert.Single(result.Items).Gate;
        Assert.Equal(10, outcome.Checks.Count);
        Assert.Equal(Enumerable.Range(1, 10), outcome.Checks.Select(check => (int)check.Check));

        // The second fired and every check after it is not-reached; the first ran and passed; the
        // ninth carries the run's own answer because the item never got that far.
        var byNumber = outcome.Checks.ToDictionary(check => check.Check);
        Assert.Equal(CheckOutcome.Passed, byNumber[RefusalCheck.ProtectedPath].Outcome);
        Assert.Equal(CheckOutcome.Refused, byNumber[RefusalCheck.GitWorkingTree].Outcome);
        for (var number = 3; number <= 10; number++)
            Assert.Equal(CheckOutcome.NotReached, byNumber[(RefusalCheck)number].Outcome);
    }

    [Fact]
    public void Reclaim_ADryRunAndAnApplyOnTheSameTree_AgreeAboutWhatIsEligible()
    {
        using var tree = new FixtureTree(nameof(Reclaim_ADryRunAndAnApplyOnTheSameTree_AgreeAboutWhatIsEligible));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);

        // A tree where one item is eligible and one is refused by the gate: a second scratch
        // folder, old enough, sitting inside a git working tree.
        var working = tree.Folder("working-tree");
        var insideWorking = Path.Combine(working, "cc-director-harness-tests");
        Directory.CreateDirectory(insideWorking);
        File.WriteAllBytes(Path.Combine(insideWorking, "theirs.txt"), new byte[32]);
        Directory.CreateDirectory(Path.Combine(working, ".git"));

        var rules = new List<IReclaimRule>
        {
            new TestScratchFoldersRule(temp, ageGateDays: 7),
            new TestScratchFoldersRule(working, ageGateDays: 7)
        };
        var now = DateTimeOffset.UtcNow.AddDays(400);

        var dryRun = DryRun(tree, rules, nowUtc: now);
        Assert.Equal(2, dryRun.Items.Count);

        var dryEligible = dryRun.Items.Where(itemResult => itemResult.Gate.Eligible).Select(itemResult => itemResult.Path).ToList();
        var dryRefused = dryRun.Items.Where(itemResult => !itemResult.Gate.Eligible).Select(itemResult => itemResult.Path).ToList();

        var apply = ReclaimRunner.Run(new ReclaimRunRequest
        {
            Rules = rules,
            RootPath = tree.Root,
            HoldingRootPath = tree.Folder("holding"),
            ProtectedPaths = [],
            UserFolders = [],
            Apply = true,
            NowUtc = now
        });

        var applyMoved = apply.Items.Where(itemResult => itemResult.Moved).Select(itemResult => itemResult.Path).ToList();
        var applyRefused = apply.Items.Where(itemResult => !itemResult.Gate.Eligible).Select(itemResult => itemResult.Path).ToList();

        // The two can never disagree about what is eligible: the apply moved exactly what the dry
        // run said it would move, and refused exactly what the dry run refused.
        Assert.Equal(dryEligible, applyMoved);
        Assert.Equal(dryRefused, applyRefused);
    }

    // -- The check made again at the moment of the move, seen through the runner itself --
    //
    // The ten numbered tests hand the gate or the runner one moment in time, so none of them can see
    // whether the apply loop checks an item AGAIN before its own move. Proven on 19 September 2026:
    // with the second check replaced by the report pass's answer, all 253 tests stayed green. The two
    // tests below change the disk between the report pass and a move, inside one apply run.

    [Fact]
    public void Reclaim_AnItemWrittenToBetweenTheReportPassAndItsOwnMove_IsRefusedAtTheMoveAndStaysWhereItWas()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnItemWrittenToBetweenTheReportPassAndItsOwnMove_IsRefusedAtTheMoveAndStaysWhereItWas));
        var first = tree.Folder("first-temp");
        var firstItem = Path.Combine(first, "cc-director-tests");
        Directory.CreateDirectory(firstItem);
        File.WriteAllBytes(Path.Combine(firstItem, "scratch.txt"), new byte[700]);

        var second = tree.Folder("second-temp");
        var secondItem = Path.Combine(second, "cc-director-tests");
        Directory.CreateDirectory(secondItem);
        File.WriteAllBytes(Path.Combine(secondItem, "scratch.txt"), new byte[300]);

        // The order of one apply run over two rules: both examine for the recommendation, then the
        // report pass checks the first item and then the second, then the first item is checked again
        // and moved, then the second. The second rule's SECOND examination is therefore the report
        // pass's check of the second item - after the report pass has already called the first item
        // eligible, and before the first item's own move. That is the moment something else writes
        // to the first item.
        var secondRule = new InterferingRule(new TestScratchFoldersRule(second), examination =>
        {
            if (examination == 2)
                File.WriteAllBytes(Path.Combine(firstItem, "scratch.txt"), new byte[800]);
        });

        var holdingRoot = Path.Combine(tree.Root, "holding");
        var apply = ReclaimRunner.Run(new ReclaimRunRequest
        {
            Rules = [new TestScratchFoldersRule(first), secondRule],
            RootPath = tree.Root,
            HoldingRootPath = holdingRoot,
            ProtectedPaths = [],
            UserFolders = [],
            Apply = true,
            NowUtc = DateTimeOffset.UtcNow.AddDays(400)
        });

        var firstOutcome = Assert.Single(apply.Items, itemResult => itemResult.Path == firstItem);
        Assert.False(firstOutcome.Moved, "an item that changed after the report pass must not move on the report pass's answer");
        Assert.False(firstOutcome.Gate.Eligible);
        Assert.Equal(RefusalCheck.ChangedSinceRecommendation, firstOutcome.Gate.FiredCheck);
        Assert.Contains("700", firstOutcome.Gate.Reason);
        Assert.Contains("800", firstOutcome.Gate.Reason);

        // It stays where it was, with what was written to it.
        Assert.Equal(800, new FileInfo(Path.Combine(firstItem, "scratch.txt")).Length);

        // The second item was untouched by any of this and moved, and it is the only thing in holding.
        var secondOutcome = Assert.Single(apply.Items, itemResult => itemResult.Path == secondItem);
        Assert.True(secondOutcome.Moved, secondOutcome.OutcomeReason ?? secondOutcome.Gate.Reason);
        var held = Assert.Single(new HoldingStore(holdingRoot).List().Complete);
        Assert.Equal(Path.GetFullPath(secondItem), held.Record.OriginalPath);
        Assert.Equal(300, apply.BytesMoved);

        // The premise, pinned last so that a missing second check says what it cost - an item that
        // moved - before it says how it happened: the second rule examined three times, once for the
        // recommendation, once in the report pass, and once immediately before its own move.
        Assert.Equal(3, secondRule.Examinations);
    }

    [Fact]
    public void Reclaim_AnItemInsideAnItemThatJustMovedInTheSameRun_IsCheckedAgainstTheDiskAsItIsNow()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnItemInsideAnItemThatJustMovedInTheSameRun_IsCheckedAgainstTheDiskAsItIsNow));
        var temp = tree.Folder("temp");
        var outer = Path.Combine(temp, "cc-director-tests");
        var inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllBytes(Path.Combine(inner, "scratch.txt"), new byte[64]);

        // Two offers, the second inside the first. The report pass finds both eligible, because both
        // are there. By the time the second is about to move, the first has moved and taken it along.
        var moment = DateTimeOffset.UtcNow;
        var outerRule = new OfferingRule(temp, new ReclaimCandidate(outer, 64, moment, "the outer folder"));
        var innerRule = new OfferingRule(temp, new ReclaimCandidate(inner, 64, moment, "the folder inside it"));

        var holdingRoot = Path.Combine(tree.Root, "holding");
        var apply = ReclaimRunner.Run(new ReclaimRunRequest
        {
            Rules = [outerRule, innerRule],
            RootPath = tree.Root,
            HoldingRootPath = holdingRoot,
            ProtectedPaths = [],
            UserFolders = [],
            Apply = true,
            NowUtc = moment.AddDays(400)
        });

        var outerOutcome = Assert.Single(apply.Items, itemResult => itemResult.Path == outer);
        Assert.True(outerOutcome.Moved, outerOutcome.OutcomeReason ?? outerOutcome.Gate.Reason);

        // The second item carries the answer of the check made at ITS move, against the disk as it
        // was then: the path no longer resolves, and nothing was attempted on the strength of an
        // answer given before the first move.
        var innerOutcome = Assert.Single(apply.Items, itemResult => itemResult.Path == inner);
        Assert.False(innerOutcome.Gate.Eligible);
        Assert.Equal(RefusalCheck.NotCanonical, innerOutcome.Gate.FiredCheck);
        Assert.Contains("would not resolve", innerOutcome.Gate.Reason);
        Assert.False(innerOutcome.Moved);
        Assert.Null(innerOutcome.HoldingEntryId);
        Assert.Null(innerOutcome.OutcomeReason);

        // One entry in holding, and the inner folder went along inside it, whole.
        var held = Assert.Single(new HoldingStore(holdingRoot).List().Complete);
        Assert.Empty(new HoldingStore(holdingRoot).List().Incomplete);
        Assert.Equal(64, new FileInfo(Path.Combine(held.EntryPath, "cc-director-tests", "inner", "scratch.txt")).Length);
    }

    // -- Refusals 5 and 8 where no other check stands behind them --

    [Fact]
    public void Reclaim_AnItemARuleOffersInsideItsOwnAgeGate_IsRefusedByTheGateWhenNoOtherCheckWould()
    {
        // The numbered test for refusal 5 uses a rule that stops offering a young item, so with
        // refusal 5 deleted the item is still refused, by refusal 8, and that test goes red only on
        // the name. Here the rule goes on offering the item, unchanged, three days after its newest
        // write: a rule that does not keep its own age gate. Refusal 5 is the only thing in the way.
        using var tree = new FixtureTree(nameof(Reclaim_AnItemARuleOffersInsideItsOwnAgeGate_IsRefusedByTheGateWhenNoOtherCheckWould));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);

        var now = DateTimeOffset.UtcNow;
        var rule = new OfferingRule(temp, new ReclaimCandidate(
            item, 64, now.AddDays(-3), "offered by a rule that does not keep its own seven day age gate"));

        var result = DryRun(tree, [rule], nowUtc: now);

        var outcome = Assert.Single(result.Items);
        Assert.False(outcome.Gate.Eligible, "an item three days old must not be eligible under a seven day age gate");
        Assert.Equal(RefusalCheck.AgeGate, outcome.Gate.FiredCheck);
        Assert.Contains("age gate of 7 days", outcome.Gate.Reason);
    }

    [Fact]
    public void Reclaim_AnItemWrittenToWithoutChangingItsSize_IsRefusedAsChanged()
    {
        // The numbered test for refusal 8 changes the bytes. A write that leaves the size alone is
        // the other way an item changes, and it has its own branch in the check.
        using var tree = new FixtureTree(nameof(Reclaim_AnItemWrittenToWithoutChangingItsSize_IsRefusedAsChanged));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        var file = Path.Combine(item, "scratch.txt");
        File.WriteAllBytes(file, new byte[700]);

        var rule = new TestScratchFoldersRule(temp);
        var now = DateTimeOffset.UtcNow.AddDays(400);
        var recommendation = RuleFold.Fold(rule, rule.Examine(new RuleContext { ScanRootPath = tree.Root, NowUtc = now }));
        var recommended = Assert.Single(recommendation.Candidates);

        // The same seven hundred bytes, written again an hour later.
        File.SetLastWriteTimeUtc(file, recommended.LastWrittenUtc.UtcDateTime.AddHours(1));

        var outcome = new RefusalGate(GateOptions(tree, apply: false, nowUtc: now)).Check(recommended, rule);

        Assert.False(outcome.Eligible);
        Assert.Equal(RefusalCheck.ChangedSinceRecommendation, outcome.FiredCheck);
        Assert.Contains("the newest write inside it moved", outcome.Reason);
    }

    // -- The whole flow on a fixture tree, with measured bytes at each step --

    [Fact]
    public void Reclaim_TheStandardTree_MovesExactlyWhatWasOfferedAndNothingElse()
    {
        using var tree = new FixtureTree(nameof(Reclaim_TheStandardTree_MovesExactlyWhatWasOfferedAndNothingElse));
        var temp = tree.Folder("temp");

        // The offered folder, a second with a name nobody here made, and a third that is young.
        var offered = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(offered);
        File.WriteAllBytes(Path.Combine(offered, "scratch.txt"), new byte[4096]);
        var notOurs = Path.Combine(temp, "somebody-elses-folder");
        Directory.CreateDirectory(notOurs);
        File.WriteAllBytes(Path.Combine(notOurs, "theirs.txt"), new byte[2048]);
        var young = Path.Combine(temp, "cc-director-harness-tests");
        Directory.CreateDirectory(young);
        File.WriteAllBytes(Path.Combine(young, "young.txt"), new byte[32]);

        var now = DateTimeOffset.UtcNow.AddDays(400);
        File.SetLastWriteTimeUtc(Path.Combine(young, "young.txt"), now.AddDays(-3).UtcDateTime);
        var rule = new TestScratchFoldersRule(temp, ageGateDays: 7);

        var dryRun = DryRun(tree, [rule], nowUtc: now);
        var wouldMove = Assert.Single(dryRun.Items, itemResult => itemResult.Gate.Eligible);
        Assert.Equal(offered, wouldMove.Path);
        Assert.Equal(4096, wouldMove.RecommendedBytes);

        var apply = ReclaimRunner.Run(new ReclaimRunRequest
        {
            Rules = [rule],
            RootPath = tree.Root,
            HoldingRootPath = tree.Folder("holding"),
            ProtectedPaths = [],
            UserFolders = [],
            Apply = true,
            NowUtc = now
        });

        var moved = Assert.Single(apply.Items, itemResult => itemResult.Moved);
        Assert.Equal(offered, moved.Path);

        // The tree afterwards, checked item by item: the offered folder is in holding, everything
        // else is where it was, with the same bytes.
        Assert.False(Directory.Exists(offered));
        Assert.NotNull(moved.HoldingEntryId);
        var entryPath = Path.Combine(tree.Folder("holding"), moved.HoldingEntryId);
        Assert.True(Directory.Exists(Path.Combine(entryPath, "cc-director-tests")));
        Assert.True(Directory.Exists(notOurs));
        Assert.Equal(2048, new FileInfo(Path.Combine(notOurs, "theirs.txt")).Length);
        Assert.True(Directory.Exists(young));
        Assert.Equal(32, new FileInfo(Path.Combine(young, "young.txt")).Length);

        // Measured before and after, never an estimate: the candidate bytes the run started with
        // left the disk. The volume's own free space is reported as a measurement and is not asserted
        // here, because the volume these tests run on is a live one that other processes are writing
        // to between the two readings.
        Assert.Equal(4096, apply.CandidateBytesBefore);
        Assert.Equal(0, apply.CandidateBytesAfter);
        Assert.True(apply.VolumeBefore.Available);
        Assert.True(apply.VolumeAfter.Available);
    }

    [Fact]
    public void Reclaim_TheWholeHoldingFlow_ListsRestoresAndPurgesWithMeasuredBytesAtEachStep()
    {
        // Recommend, dry run, apply, holding list, holding restore, holding purge - on a tree this test
        // builds and destroys. After EVERY step three things are measured by this test's own
        // instrument, which walks the disk and adds up file lengths and owes nothing to the code under
        // test: every file outside holding with its size, the bytes inside holding, and the bytes of
        // the whole tree. Each is asserted as an exact number, and each is written to the test's
        // output, so the proof document quotes a run rather than an intention.
        //
        // The volume's own free space is written out as a measurement and never asserted: the volume
        // these tests run on is a live one, and other processes write to it between two readings.
        using var tree = new FixtureTree(nameof(Reclaim_TheWholeHoldingFlow_ListsRestoresAndPurgesWithMeasuredBytesAtEachStep));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[4096]);

        // The bystanders: a folder beside the item that nobody here made, and a file elsewhere in the
        // tree. Nothing in the flow may touch either.
        tree.File(Path.Combine("temp", "somebody-elses-folder", "theirs.txt"), 2048);
        tree.File(Path.Combine("elsewhere", "keep.bin"), 1000);

        var holdingRoot = Path.Combine(tree.Root, "holding");
        var now = DateTimeOffset.UtcNow.AddDays(400);
        var rule = new TestScratchFoldersRule(temp);

        var itemFile = Path.Combine("temp", "cc-director-tests", "scratch.txt");
        var withTheItem = new SortedDictionary<string, long>(StringComparer.Ordinal)
        {
            [Path.Combine("elsewhere", "keep.bin")] = 1000,
            [itemFile] = 4096,
            [Path.Combine("temp", "somebody-elses-folder", "theirs.txt")] = 2048
        };
        var withoutTheItem = new SortedDictionary<string, long>(withTheItem, StringComparer.Ordinal);
        withoutTheItem.Remove(itemFile);

        void Measured(string step, SortedDictionary<string, long> expectedOutsideHolding, long expectedHoldingBytes)
        {
            var outside = FilesOutsideHolding(tree.Root, holdingRoot);
            var holdingBytes = BytesUnder(holdingRoot);
            var treeBytes = BytesUnder(tree.Root);
            output.WriteLine(
                $"{step}: outside-holding-bytes={outside.Values.Sum()}, holding-bytes={holdingBytes}, " +
                $"whole-tree-bytes={treeBytes}, files-outside-holding={outside.Count}");

            Assert.Equal(expectedOutsideHolding, outside);
            Assert.Equal(expectedHoldingBytes, holdingBytes);
            Assert.Equal(expectedOutsideHolding.Values.Sum() + expectedHoldingBytes, treeBytes);
        }

        Measured("0 built", withTheItem, 0);

        // 1. Recommend.
        var recommendation = RuleFold.Fold(rule, rule.Examine(new RuleContext { ScanRootPath = tree.Root, NowUtc = now }));
        var recommended = Assert.Single(recommendation.Candidates);
        Assert.Equal(item, recommended.Path);
        Assert.Equal(4096, recommended.Bytes);
        output.WriteLine($"1 recommend: offered={recommended.Path}, bytes={recommended.Bytes}");
        Measured("1 recommend", withTheItem, 0);

        // 2. Dry run: says what would move, measures, and moves nothing. No holding root is created.
        var dryRun = DryRun(tree, [rule], nowUtc: now);
        var wouldMove = Assert.Single(dryRun.Items);
        Assert.True(wouldMove.Gate.Eligible, wouldMove.Gate.Reason);
        Assert.False(wouldMove.Moved);
        Assert.Equal(4096, dryRun.CandidateBytesBefore);
        Assert.Equal(4096, dryRun.CandidateBytesAfter);
        Assert.Equal(0, dryRun.BytesMoved);
        Assert.False(Directory.Exists(holdingRoot));
        output.WriteLine(
            $"2 dry run: candidate-bytes-before={dryRun.CandidateBytesBefore}, candidate-bytes-after={dryRun.CandidateBytesAfter}, " +
            $"bytes-moved={dryRun.BytesMoved}, volume-free-before={dryRun.VolumeBefore.FreeBytes}, volume-free-after={dryRun.VolumeAfter.FreeBytes}");
        Measured("2 dry run", withTheItem, 0);

        // 3. Apply: the item, and only the item, moves into holding. The bytes do not leave the tree:
        // a move to holding frees no space, and the whole tree grows by exactly the record.
        ReclaimRunResult Apply() => ReclaimRunner.Run(new ReclaimRunRequest
        {
            Rules = [rule],
            RootPath = tree.Root,
            HoldingRootPath = holdingRoot,
            ProtectedPaths = [],
            UserFolders = [],
            Apply = true,
            NowUtc = now
        });

        var apply = Apply();
        var moved = Assert.Single(apply.Items);
        Assert.True(moved.Moved, moved.OutcomeReason ?? moved.Gate.Reason);
        Assert.Equal(4096, apply.CandidateBytesBefore);
        Assert.Equal(0, apply.CandidateBytesAfter);
        Assert.Equal(4096, apply.BytesMoved);
        Assert.Contains(apply.Lines, line => line.StartsWith("space: a move to holding frees no space", StringComparison.Ordinal));
        var entryPath = Path.Combine(holdingRoot, moved.HoldingEntryId!);
        var recordBytes = new FileInfo(Path.Combine(entryPath, "record.json")).Length;
        Assert.Equal(4096, new FileInfo(Path.Combine(entryPath, "cc-director-tests", "scratch.txt")).Length);
        output.WriteLine(
            $"3 apply: candidate-bytes-before={apply.CandidateBytesBefore}, candidate-bytes-after={apply.CandidateBytesAfter}, " +
            $"bytes-moved={apply.BytesMoved}, record-bytes={recordBytes}, " +
            $"volume-free-before={apply.VolumeBefore.FreeBytes}, volume-free-after={apply.VolumeAfter.FreeBytes}");
        Measured("3 apply", withoutTheItem, 4096 + recordBytes);

        // 4. Holding list: the entry is named with its original path and its bytes. Listing changes nothing.
        var holding = new HoldingStore(holdingRoot);
        var listing = holding.List();
        var entry = Assert.Single(listing.Complete);
        Assert.Empty(listing.Incomplete);
        Assert.Equal(moved.HoldingEntryId, entry.EntryId);
        Assert.Equal(Path.GetFullPath(item), entry.Record.OriginalPath);
        Assert.Equal(4096, entry.Record.Bytes);
        output.WriteLine($"4 holding list: entries={listing.Complete.Count}, entry-bytes={entry.Record.Bytes}, from={entry.Record.OriginalPath}");
        Measured("4 holding list", withoutTheItem, 4096 + recordBytes);

        // 5. Restore: the item returns, byte for byte, the entry is gone, and the tree is exactly the
        // tree the test built.
        var restored = holding.Restore(entry.EntryId);
        Assert.True(restored.Restored, restored.RefusalReason);
        Assert.False(Directory.Exists(entryPath));
        output.WriteLine($"5 holding restore: restored={restored.Restored}, to={restored.OriginalPath}");
        Measured("5 holding restore", withTheItem, 0);

        // 6. The item moves again, to have an entry to purge.
        apply = Apply();
        moved = Assert.Single(apply.Items);
        Assert.True(moved.Moved, moved.OutcomeReason ?? moved.Gate.Reason);
        recordBytes = new FileInfo(Path.Combine(holdingRoot, moved.HoldingEntryId!, "record.json")).Length;
        Measured("6 apply again", withoutTheItem, 4096 + recordBytes);

        // 7. A purge WITH the apply flag, inside the holding period, removes nothing.
        var purgeTooEarly = holding.Purge(now, apply: true);
        Assert.Empty(purgeTooEarly.PurgedEntryIds);
        Assert.Single(purgeTooEarly.NotYetPurgeable);
        Measured("7 purge with the apply flag inside the holding period", withoutTheItem, 4096 + recordBytes);

        // 8. A purge dry run, after the holding period, names the entry and removes nothing.
        var afterThePeriod = now.AddDays(HoldingStore.DefaultHoldingPeriodDays + 1);
        var purgeDryRun = holding.Purge(afterThePeriod, apply: false);
        Assert.Single(purgeDryRun.Purgeable);
        Assert.Empty(purgeDryRun.PurgedEntryIds);
        Measured("8 purge dry run after the holding period", withoutTheItem, 4096 + recordBytes);

        // 9. The purge with the apply flag is the one step that frees space: the entry is gone, the
        // whole tree is smaller by exactly the item and its record, and the bystanders are untouched.
        var treeBytesBeforePurge = BytesUnder(tree.Root);
        var purge = holding.Purge(afterThePeriod, apply: true);
        Assert.Equal(moved.HoldingEntryId, Assert.Single(purge.PurgedEntryIds));
        Assert.False(Directory.Exists(Path.Combine(holdingRoot, moved.HoldingEntryId!)));
        Assert.False(Directory.Exists(item));
        var freed = treeBytesBeforePurge - BytesUnder(tree.Root);
        Assert.Equal(4096 + recordBytes, freed);
        output.WriteLine($"9 holding purge: purged={purge.PurgedEntryIds.Count}, bytes-freed-from-the-tree={freed}");
        Measured("9 holding purge", withoutTheItem, 0);
    }

    /// <summary>
    /// This suite's own instrument: every file under a folder, added up. It walks the disk with the
    /// framework's own enumeration and owes nothing to the measuring code under test.
    /// </summary>
    private static long BytesUnder(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
            : 0;

    /// <summary>Every file in the tree that is not inside holding, by its path below the root, with its size.</summary>
    private static SortedDictionary<string, long> FilesOutsideHolding(string root, string holdingRoot)
    {
        var files = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(holdingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            files[Path.GetRelativePath(root, file)] = new FileInfo(file).Length;
        }

        return files;
    }

    [Fact]
    public void Reclaim_AnOwnersOwnCommand_RunsWithTheHarmlessCommandTheTestSuppliesAndHoldsNothing()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnOwnersOwnCommand_RunsWithTheHarmlessCommandTheTestSuppliesAndHoldsNothing));
        var cache = tree.Folder("package-cache");
        var item = Path.Combine(cache, "the-cache");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "cached.bin"), new byte[2048]);

        var marker = Path.Combine(tree.Root, "the-command-ran.txt");
        var command = OperatingSystem.IsWindows()
            ? $"cmd.exe /c \"echo cleaned > {marker}\""
            : $"/bin/sh -c \"echo cleaned > {marker}\"";

        // A rule the test constructs with a harmless command the test supplies. The real Windows rule
        // set is never constructed with the apply flag in any test.
        var rule = new OfferingRule(
            cache,
            new ReclaimCandidate(item, 2048, DateTimeOffset.UtcNow, "the cache of a package manager, cleared by its own command"),
            proof: ProofKind.OwnersOwnCommand,
            commandToRun: command);

        // The dry run reports the command and runs nothing.
        var dryRun = DryRun(tree, [rule]);
        var dryItem = Assert.Single(dryRun.Items);
        Assert.True(dryItem.Gate.Eligible);
        Assert.False(File.Exists(marker));

        var holdingRoot = Path.Combine(tree.Root, "holding");
        var apply = ReclaimRunner.Run(new ReclaimRunRequest
        {
            Rules = [rule],
            RootPath = tree.Root,
            HoldingRootPath = holdingRoot,
            ProtectedPaths = [],
            UserFolders = [],
            Apply = true,
            NowUtc = DateTimeOffset.UtcNow.AddDays(400)
        });

        var applied = Assert.Single(apply.Items);
        Assert.True(applied.OwnersCommandRan, applied.OutcomeReason ?? "the command should have run");
        Assert.Equal(0, applied.OwnersCommandExitCode);
        Assert.True(File.Exists(marker), "the harmless command the test supplied should have run");

        // The item cleared by the owner's own command is never held: there is no way back, and the
        // rule's what-is-lost already said so. No holding root is even created.
        Assert.Null(applied.HoldingEntryId);
        Assert.False(Directory.Exists(holdingRoot));

        // Measured before and after: the run measured the candidate before the command ran.
        Assert.Equal(2048, apply.CandidateBytesBefore);
    }

    [Fact]
    public void Reclaim_AnOwnersOwnCommandThatWillNotStart_SaysSoAndTouchesNothing()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnOwnersOwnCommandThatWillNotStart_SaysSoAndTouchesNothing));
        var (rule, item, holdingRoot) = AnOwnerCommandRule(tree, "a-program-nobody-installed-7f3a9c --clean");

        var applied = Assert.Single(ApplyWith(tree, rule, holdingRoot).Items);

        // The gate passed the item, the command could not be started, and the answer says exactly
        // that: it did not run, there is no exit code, and the reason names the command.
        Assert.True(applied.Gate.Eligible, applied.Gate.Reason);
        Assert.False(applied.OwnersCommandRan);
        Assert.Null(applied.OwnersCommandExitCode);
        Assert.Contains("could not be started", applied.OutcomeReason);
        Assert.Contains("a-program-nobody-installed-7f3a9c", applied.OutcomeReason);

        // Nothing was held and nothing was touched.
        Assert.False(applied.Moved);
        Assert.False(Directory.Exists(holdingRoot));
        Assert.Equal(2048, new FileInfo(Path.Combine(item, "cached.bin")).Length);
    }

    [Fact]
    public void Reclaim_AnOwnersOwnCommandThatFails_ReportsItsExitCodeAndHoldsNothing()
    {
        using var tree = new FixtureTree(nameof(Reclaim_AnOwnersOwnCommandThatFails_ReportsItsExitCodeAndHoldsNothing));
        var command = OperatingSystem.IsWindows() ? "cmd.exe /c \"exit 3\"" : "/bin/sh -c \"exit 3\"";
        var (rule, item, holdingRoot) = AnOwnerCommandRule(tree, command);

        var result = ApplyWith(tree, rule, holdingRoot);
        var applied = Assert.Single(result.Items);

        // The command ran and failed. Its own exit code is reported as it was, never rounded to a
        // success, and the report's line for the item carries it.
        Assert.True(applied.OwnersCommandRan, applied.OutcomeReason);
        Assert.Equal(3, applied.OwnersCommandExitCode);
        Assert.Contains("command: ran, exit code 3", result.Lines);

        // The bytes are measured after as well as before, and a command that cleared nothing is
        // reported as having cleared nothing.
        Assert.Equal(2048, result.CandidateBytesBefore);
        Assert.Equal(2048, result.CandidateBytesAfter);
        Assert.Equal(0, result.BytesMoved);
        Assert.False(Directory.Exists(holdingRoot));
        Assert.Equal(2048, new FileInfo(Path.Combine(item, "cached.bin")).Length);
    }

    // -- The helpers --

    private static (IReclaimRule Rule, string Item, string HoldingRoot) AnOwnerCommandRule(FixtureTree tree, string command)
    {
        var cache = tree.Folder("package-cache");
        var item = Path.Combine(cache, "the-cache");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "cached.bin"), new byte[2048]);

        var rule = new OfferingRule(
            cache,
            new ReclaimCandidate(item, 2048, DateTimeOffset.UtcNow, "the cache of a package manager, cleared by its own command"),
            proof: ProofKind.OwnersOwnCommand,
            commandToRun: command);
        return (rule, item, Path.Combine(tree.Root, "holding"));
    }

    private static ReclaimRunResult ApplyWith(FixtureTree tree, IReclaimRule rule, string holdingRoot) =>
        ReclaimRunner.Run(new ReclaimRunRequest
        {
            Rules = [rule],
            RootPath = tree.Root,
            HoldingRootPath = holdingRoot,
            ProtectedPaths = [],
            UserFolders = [],
            Apply = true,
            NowUtc = DateTimeOffset.UtcNow.AddDays(400)
        });

    private static ReclaimRunResult DryRun(
        FixtureTree tree,
        IReadOnlyList<IReclaimRule> rules,
        IReadOnlyList<CcStorage.ProtectedPath>? protectedPaths = null,
        IReadOnlyList<string>? userFolders = null,
        DateTimeOffset? nowUtc = null)
    {
        return ReclaimRunner.Run(new ReclaimRunRequest
        {
            Rules = rules,
            RootPath = tree.Root,
            HoldingRootPath = Path.Combine(tree.Root, "holding"),
            ProtectedPaths = protectedPaths ?? [],
            UserFolders = userFolders ?? [],
            Apply = false,
            NowUtc = nowUtc ?? DateTimeOffset.UtcNow.AddDays(400)
        });
    }

    private static RefusalGateOptions GateOptions(FixtureTree tree, bool apply, DateTimeOffset nowUtc) => new()
    {
        ProtectedPaths = [],
        UserFolders = [],
        HoldingRootPath = Path.Combine(tree.Root, "holding"),
        ScanRootPath = tree.Root,
        Apply = apply,
        NowUtc = nowUtc
    };
}

/// <summary>
/// A real rule with something else happening on the disk while it works. It answers exactly as the
/// rule inside it answers; before each examination it tells the test which examination this is, so
/// the test can change the disk at a known moment INSIDE one run - the only way a test can stand
/// between the report pass and a move.
/// </summary>
/// <param name="inner">The rule that does the examining.</param>
/// <param name="beforeExamination">Called with the number of the examination about to be made, from one.</param>
file sealed class InterferingRule(IReclaimRule inner, Action<int> beforeExamination) : IReclaimRule
{
    /// <summary>How many times the rule has been asked to examine.</summary>
    public int Examinations { get; private set; }

    public string Id => inner.Id;
    public string Name => inner.Name;
    public ProofKind Proof => inner.Proof;
    public string WhatItRemoves => inner.WhatItRemoves;
    public string WhyItIsSafe => inner.WhyItIsSafe;
    public string WhatIsLost => inner.WhatIsLost;
    public string HowToGetItBack => inner.HowToGetItBack;
    public int AgeGateDays => inner.AgeGateDays;
    public bool NeedsAdministrator => inner.NeedsAdministrator;
    public string? CommandToRun => inner.CommandToRun;
    public string LooksIn => inner.LooksIn;

    public RuleAnswer Examine(RuleContext context)
    {
        Examinations++;
        beforeExamination(Examinations);
        return inner.Examine(context);
    }
}

/// <summary>
/// A rule the test constructs, offering exactly the candidates the test gives it. The reclaim tests
/// need a rule whose offer does not depend on the disk - the open-file test, for instance, needs a
/// rule that offers a folder a live rule would have declined, because the refusal being tested is
/// the gate's and not the rule's.
/// </summary>
/// <param name="looksIn">The folder the rule says it looks in.</param>
/// <param name="candidate">What it offers, every time it is asked unless it turns broken.</param>
/// <param name="answerBrokenFromExamination">
/// From which examination on the rule answers broken - a must-not-be-empty control at nought, the
/// shape of a record source that loads once and fails the second time.
/// </param>
/// <param name="proof">Which of the three proofs the rule holds.</param>
/// <param name="commandToRun">The command, for a rule whose proof is the owner's own.</param>
file sealed class OfferingRule(
    string looksIn,
    ReclaimCandidate candidate,
    int? answerBrokenFromExamination = null,
    ProofKind proof = ProofKind.WeMadeIt,
    string? commandToRun = null) : IReclaimRule
{
    private int _examinations;

    public string Id => "the-offering-rule";
    public string Name => "The offering rule";
    public ProofKind Proof => proof;
    public string WhatItRemoves => "whatever the test that built this rule says it removes";
    public string WhyItIsSafe => "the test that built this rule vouches for it";
    public string WhatIsLost => "what the test that built this rule says is lost";
    public string HowToGetItBack => "from the holding folder until it is purged";
    public int AgeGateDays => 7;
    public bool NeedsAdministrator => false;
    public string? CommandToRun => commandToRun;
    public string LooksIn => looksIn;

    public RuleAnswer Examine(RuleContext context)
    {
        _examinations++;
        if (answerBrokenFromExamination is int from && _examinations >= from)
        {
            return new RuleAnswer(
                [new RuleControl("records-read", 0, MustNotBeEmpty: true)],
                [],
                null);
        }

        return new RuleAnswer(
            [new RuleControl("records-read", 1, MustNotBeEmpty: true)],
            [candidate],
            null);
    }
}
