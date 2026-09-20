using CcDirector.Core.Storage;
using CcDirector.Reclaim.Removal;
using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;
using Xunit;
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
/// Each refusal test triggers exactly its own refusal and nothing else, and every one of them has
/// been proven to fail with its refusal taken out - a refusal with a green test that stays green when
/// the refusal is deleted is not a refusal, it is a comment.
/// </summary>
public sealed class ReclaimRefusalTests
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
        using var tree = new FixtureTree(nameof(Reclaim_TheWholeHoldingFlow_ListsRestoresAndPurgesWithMeasuredBytesAtEachStep));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[4096]);

        var now = DateTimeOffset.UtcNow.AddDays(400);
        var rule = new TestScratchFoldersRule(temp);

        // Recommend, dry run, apply.
        var dryRun = DryRun(tree, [rule], nowUtc: now);
        Assert.Single(dryRun.Items, itemResult => itemResult.Gate.Eligible);
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

        // Holding list: the entry is named with its original path and its bytes.
        var holding = new HoldingStore(tree.Folder("holding"));
        var listing = holding.List();
        var entry = Assert.Single(listing.Complete);
        Assert.Equal(moved.HoldingEntryId, entry.EntryId);
        Assert.Equal(Path.GetFullPath(item), entry.Record.OriginalPath);
        Assert.Equal(4096, entry.Record.Bytes);

        // Restore: the item returns, byte for byte, and the entry is gone.
        var restored = holding.Restore(entry.EntryId);
        Assert.True(restored.Restored, restored.RefusalReason);
        Assert.True(File.Exists(Path.Combine(item, "scratch.txt")));
        Assert.Equal(4096, new FileInfo(Path.Combine(item, "scratch.txt")).Length);
        Assert.False(Directory.Exists(Path.Combine(holding.Root, entry.EntryId)));

        // The whole flow again, to have an entry to purge.
        apply = ReclaimRunner.Run(new ReclaimRunRequest
        {
            Rules = [rule],
            RootPath = tree.Root,
            HoldingRootPath = tree.Folder("holding"),
            ProtectedPaths = [],
            UserFolders = [],
            Apply = true,
            NowUtc = now
        });
        moved = Assert.Single(apply.Items, itemResult => itemResult.Moved);

        // A purge dry run names the entry as not yet purgeable.
        var purgeDry = holding.Purge(now, apply: false);
        Assert.Empty(purgeDry.Purgeable);
        Assert.Single(purgeDry.NotYetPurgeable);

        // After the holding period, and with the apply flag, the entry is gone and the space it held
        // is freed. The holding period is a parameter, so the test does not wait thirty days.
        var purge = holding.Purge(now.AddDays(HoldingStore.DefaultHoldingPeriodDays + 1), apply: true);
        Assert.Equal(moved.HoldingEntryId, Assert.Single(purge.PurgedEntryIds));
        Assert.False(Directory.Exists(Path.Combine(holding.Root, moved.HoldingEntryId!)));
        Assert.False(Directory.Exists(item));
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

    // -- The helpers --

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
