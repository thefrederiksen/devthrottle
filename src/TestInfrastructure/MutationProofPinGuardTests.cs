#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace CcDirector.TestInfrastructure;

/// <summary>
/// Proves the mutation-proof pin guard CAN FIRE, not merely that it is present.
///
/// This is the point of the whole file, so it is worth being blunt about why. A guard that never fires
/// looks exactly like a guard that works: both are silent, and both let every run through. An instrument
/// that cannot observe its subject reports the subject's absence, and absence reads as success. So each
/// refusal below is paired with a CONTROL that must be admitted, because a guard which refused everything
/// would sail through the refusal tests on its own.
///
/// Four properties are pinned, and the last two are the controls:
///
///   1. A baseline on a tree carrying an uncommitted modification is REFUSED, and the refusal names the
///      file. The case is built by REPLAYING the event of 2026-07-19 against a real git repository: a
///      security guard block deleted from a source file and left uncommitted. The deletion leaves a
///      brace-balanced file, which is asserted here, because a detector that could only see a
///      syntactically broken tree would not have caught the real event and would be worthless.
///   2. A baseline on a tree whose head has moved off the pinned head is REFUSED, naming both heads.
///   3. CONTROL: a clean tree at the pinned head is ADMITTED.
///   4. CONTROL FOR SCOPE: a mid-rework run with NO pin is ADMITTED however dirty the tree is. This is what
///      proves the guard the Architect ruled for was built - gated to baselines and arms - rather than a
///      blanket refusal on any dirty tree. A blanket guard would be switched off within a day, and a guard
///      that gets switched off protects nothing.
///
/// The refusal cases and the admission cases are driven through the SAME repository, differing only by the
/// contamination under test, so nothing here can pass because the two arms were set up differently.
/// </summary>
public sealed class MutationProofPinGuardTests
{
    // -------------------------------------------------------------------------------------------------
    // The guard on the guard.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A module initializer that silently does not run leaves no trace whatsoever - the suite would go
    /// straight back to admitting contaminated baselines and every test here would still pass, because
    /// every test here drives the decision directly. So the fact that the mechanism actually executed in
    /// this process is asserted separately from the decision it makes.
    /// </summary>
    // The per-assembly sentinel lives in MutationProofPinGuardArmedTests, which is linked into every
    // test assembly. See that file for why the behavioural suite below is not.

    // -------------------------------------------------------------------------------------------------
    // 1. The event of 2026-07-19, replayed against a real git repository.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The source file as it is committed: a security guard block inside a request handler.
    /// </summary>
    private const string FileWithTheGuardBlock =
        """
        namespace Example;

        public static class RequestHandler
        {
            public static string Handle(string tenantId, string callerTenantId)
            {
                if (tenantId != callerTenantId)
                {
                    throw new UnauthorizedAccessException("Cross-tenant read refused.");
                }

                return Read(tenantId);
            }

            private static string Read(string tenantId) => tenantId;
        }
        """;

    /// <summary>
    /// The same file with the guard block deleted, which is precisely what a killed mutation-arm run leaves
    /// behind when it never gets to restore what it removed. It COMPILES. That is the whole danger: the
    /// baseline taken over this file is green, complete, and measures nothing.
    /// </summary>
    private const string FileWithTheGuardBlockDeleted =
        """
        namespace Example;

        public static class RequestHandler
        {
            public static string Handle(string tenantId, string callerTenantId)
            {
                return Read(tenantId);
            }

            private static string Read(string tenantId) => tenantId;
        }
        """;

    [Fact]
    public void ABaselineOverAnUncommittedlyDeletedGuardBlockIsRefused_AndTheRefusalNamesTheFile()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var pin = BaselinePinAt(repository.Head());

        // THE OTHER ARM, taken first and from the same repository: before the contamination, this exact
        // baseline is admitted. Without this, a guard that refused unconditionally would pass the assertion
        // below and nobody would learn anything.
        var beforeContamination = MutationProofPinGuard.Decide(pin, MutationProofPinGuard.ReadTree(repository.Root));
        Assert.True(
            beforeContamination.Admitted,
            "A clean tree at the pinned head must be admitted, or the refusal below proves nothing. "
            + beforeContamination.Message);

        // The event: the guard block is deleted and left uncommitted. Nothing else changes.
        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);

        var afterContamination = MutationProofPinGuard.Decide(pin, MutationProofPinGuard.ReadTree(repository.Root));

        Assert.False(
            afterContamination.Admitted,
            "A baseline was admitted over a working tree in which a security guard block had been deleted "
            + "and left uncommitted. That baseline would look green and complete and would reconcile "
            + "perfectly against its own mutation arm while measuring nothing. " + afterContamination.Message);

        Assert.Contains("src/RequestHandler.cs", afterContamination.Message, StringComparison.Ordinal);
        Assert.Contains("NO TESTS WILL RUN", afterContamination.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The property that makes the case above a real reconstruction rather than a convenient one: the
    /// mutation leaves a file the compiler still accepts. If the only trees this guard could detect were
    /// syntactically broken ones, it would not have caught the event it was built for - the broken tree
    /// announces itself with a build failure and needs no guard at all.
    /// </summary>
    [Fact]
    public void TheReplayedMutationLeavesAFileThatStillCompiles_OtherwiseItWouldNotBeTheRealEvent()
    {
        Assert.NotEqual(FileWithTheGuardBlock, FileWithTheGuardBlockDeleted);

        Assert.Equal(
            FileWithTheGuardBlock.Count(c => c == '{'),
            FileWithTheGuardBlock.Count(c => c == '}'));

        Assert.Equal(
            FileWithTheGuardBlockDeleted.Count(c => c == '{'),
            FileWithTheGuardBlockDeleted.Count(c => c == '}'));

        // The security decision is gone; everything the compiler needs is still there.
        Assert.Contains("UnauthorizedAccessException", FileWithTheGuardBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("UnauthorizedAccessException", FileWithTheGuardBlockDeleted, StringComparison.Ordinal);
        Assert.Contains("return Read(tenantId);", FileWithTheGuardBlockDeleted, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // 2. The head has moved.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void ABaselineOnATreeWhoseHeadHasMovedIsRefused_AndNamesBothHeads()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var pinnedHead = repository.Head();
        var pin = BaselinePinAt(pinnedHead);

        // Clean tree, different commit - the arm this test exists to separate from a dirty tree. A baseline
        // and its mutation arm taken at different commits compare two different programs, and the
        // reconciliation arithmetic silently means nothing.
        repository.WriteAndCommit("src/Other.cs", "namespace Example; public static class Other { }", "unrelated work");
        var movedHead = repository.Head();

        Assert.NotEqual(pinnedHead, movedHead);
        Assert.Empty(MutationProofPinGuard.ReadTree(repository.Root).Changes);

        var verdict = MutationProofPinGuard.Decide(pin, MutationProofPinGuard.ReadTree(repository.Root));

        Assert.False(verdict.Admitted, verdict.Message);
        Assert.Contains(pinnedHead, verdict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(movedHead, verdict.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------------------------------
    // 3. CONTROL: the guard does not refuse everything.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void CONTROL_ACleanTreeAtThePinnedHeadIsAdmitted()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var verdict = MutationProofPinGuard.Decide(
            BaselinePinAt(repository.Head()),
            MutationProofPinGuard.ReadTree(repository.Root));

        Assert.True(
            verdict.Admitted,
            "A clean tree at the pinned head was refused. A guard that refuses everything would pass every "
            + "other test in this file while stopping all proof work, and would be removed within the day. "
            + verdict.Message);
    }

    // -------------------------------------------------------------------------------------------------
    // 4. CONTROL FOR SCOPE: an unpinned run is never this guard's business.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void CONTROL_AMidReworkRunWithNoPinIsAdmittedHoweverDirtyTheTreeIs()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        // As dirty as a working tree gets mid-rework: a modified file, a deleted file, and a new one.
        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);
        repository.WriteAndCommit("src/Doomed.cs", "namespace Example; public static class Doomed { }", "add");
        repository.Delete("src/Doomed.cs");
        repository.Write("src/Scratch.cs", "namespace Example; public static class Scratch { }");

        var tree = MutationProofPinGuard.ReadTree(repository.Root);
        Assert.True(tree.Changes.Count >= 3, "the tree under test must actually be dirty for this control to mean anything");

        var noPin = MutationProofPinGuard.PinReading.NoPin();
        var verdict = MutationProofPinGuard.Decide(noPin, tree);

        Assert.True(
            verdict.Admitted,
            "A run with no mutation-proof pin was refused because the tree was dirty. That is a BLANKET "
            + "guard, which the Architect ruled against: a worker mid-rework is legitimately dirty, and a "
            + "guard that blocks ordinary work is switched off within a day, after which it protects "
            + "nothing. " + verdict.Message);
    }

    // -------------------------------------------------------------------------------------------------
    // The mutation arm. An arm is SUPPOSED to be dirty, so it is checked against a tighter rule than a
    // baseline: exactly the declared change, no more and no less.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void AnArmCarryingExactlyItsDeclaredMutationIsAdmitted()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");
        var pin = ArmPinAt(repository.Head(), "src/RequestHandler.cs");

        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);

        var verdict = MutationProofPinGuard.Decide(pin, MutationProofPinGuard.ReadTree(repository.Root));

        Assert.True(
            verdict.Admitted,
            "A mutation arm carrying exactly the mutation it declared was refused, which would make the "
            + "guard impossible to use for the arm half of every proof. " + verdict.Message);
    }

    [Fact]
    public void AnArmCarryingAnUndeclaredChangeAsWellIsRefused_AndNamesOnlyTheUndeclaredFile()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");
        repository.WriteAndCommit("src/OtherGuard.cs", FileWithTheGuardBlock.Replace("RequestHandler", "OtherGuard"), "add another guard");

        var pin = ArmPinAt(repository.Head(), "src/RequestHandler.cs");

        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);

        // The contaminated-arm case: the intended mutation PLUS a leftover somebody else's killed run left
        // behind. The counts would still reconcile, because both runs carry the leftover.
        repository.Write("src/OtherGuard.cs", FileWithTheGuardBlockDeleted.Replace("RequestHandler", "OtherGuard"));

        var verdict = MutationProofPinGuard.Decide(pin, MutationProofPinGuard.ReadTree(repository.Root));

        Assert.False(verdict.Admitted, verdict.Message);
        Assert.Contains("src/OtherGuard.cs", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("THIS PROOF DID NOT DECLARE", verdict.Message, StringComparison.Ordinal);

        // Only the undeclared one. Naming the declared mutation here would train a worker to ignore the
        // list, which is the same as not printing it.
        Assert.DoesNotContain("    src/RequestHandler.cs", verdict.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mirror image, and the one most likely to be dismissed as unnecessary: an arm run on a tree where
    /// the mutation was never applied, or was already restored, is a SECOND BASELINE wearing an arm's name.
    /// It reconciles perfectly against the first baseline - passed plus zero failed equals passed - and the
    /// proof reads as a clean pass.
    /// </summary>
    [Fact]
    public void AnArmWhoseDeclaredMutationIsAbsentIsRefused_BecauseItIsASecondBaselineInDisguise()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var pin = ArmPinAt(repository.Head(), "src/RequestHandler.cs");

        // No mutation applied at all.
        var verdict = MutationProofPinGuard.Decide(pin, MutationProofPinGuard.ReadTree(repository.Root));

        Assert.False(verdict.Admitted, verdict.Message);
        Assert.Contains("src/RequestHandler.cs", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("SECOND BASELINE", verdict.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // A pin that cannot be understood must REFUSE, never quietly become "no pin".
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pinnedHead=0123456789abcdef0123456789abcdef01234567", "declares no phase")]
    [InlineData("phase=baseline", "not a full forty-character commit id")]
    [InlineData("phase=warmup\npinnedHead=0123456789abcdef0123456789abcdef01234567", "is neither")]
    [InlineData("phase=arm\npinnedHead=0123456789abcdef0123456789abcdef01234567", "declares no mutation")]
    [InlineData("phase=baseline\npinnedHead=0123456789abcdef0123456789abcdef01234567\nmutates=src/A.cs", "yet declares mutations")]
    [InlineData("phase=baseline\npinnedHead=0123456789abcdef0123456789abcdef01234567\nmutate=src/A.cs", "is not one this guard understands")]
    public void APinThatCannotBeUnderstoodRefuses_ItDoesNotDegradeIntoNoPin(string text, string expectedDetail)
    {
        var reading = MutationProofPinGuard.ParsePin(text, "C:/pins/example.pin");

        Assert.NotEqual(MutationProofPinGuard.PinOutcome.NoPinPresent, reading.Outcome);
        Assert.Equal(MutationProofPinGuard.PinOutcome.CouldNotDetermine, reading.Outcome);
        Assert.Null(reading.Pin);
        Assert.NotNull(reading.Problem);
        Assert.Contains(expectedDetail, reading.Problem!, StringComparison.Ordinal);

        var verdict = MutationProofPinGuard.Decide(
            reading,
            new MutationProofPinGuard.TreeReading(true, "0123456789abcdef0123456789abcdef01234567", Array.Empty<MutationProofPinGuard.ChangedPath>(), null));

        Assert.False(
            verdict.Admitted,
            "A pin file that exists but cannot be read was treated as no pin at all, which disarms the "
            + "guard for exactly the proof that most needs it. " + verdict.Message);
    }

    /// <summary>
    /// A typo of "mutates" is worth its own case because it is the failure that would look like the
    /// guard working: the arm would be refused for carrying an undeclared change, the worker would edit the
    /// pin, and nobody would learn that the pin format had silently dropped a key.
    /// </summary>
    [Fact]
    public void AGoodPinParsesEverythingItWasGiven()
    {
        var reading = MutationProofPinGuard.ParsePin(
            "# a comment\n"
            + "proofId=7f3c\n"
            + "phase=arm\n"
            + "pinnedHead=0123456789ABCDEF0123456789abcdef01234567\n"
            + "pinnedUtc=2026-07-20T00:00:00.0000000Z\n"
            + "tree=D:/ReposFred/_wt/example\n"
            + "mutates=src/A.cs\n"
            + "mutates=src/B.cs\n"
            + "note=removing the tenant comparison\n",
            "C:/pins/example.pin");

        Assert.Null(reading.Problem);
        Assert.NotNull(reading.Pin);
        Assert.Equal("7f3c", reading.Pin!.ProofId);
        Assert.Equal(MutationProofPinGuard.ArmPhase, reading.Pin.Phase);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", reading.Pin.PinnedHead);
        Assert.Equal(new[] { "src/A.cs", "src/B.cs" }, reading.Pin.DeclaredMutations);
        Assert.Equal("removing the tenant comparison", reading.Pin.Note);
    }

    // -------------------------------------------------------------------------------------------------
    // A pinned run that cannot read its own tree must stop, not proceed.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void APinnedRunThatCannotReadTheTreeIsRefused()
    {
        var pin = BaselinePinAt("0123456789abcdef0123456789abcdef01234567");
        var unreadable = new MutationProofPinGuard.TreeReading(
            false, "", Array.Empty<MutationProofPinGuard.ChangedPath>(), "git is not on the path");

        var verdict = MutationProofPinGuard.Decide(pin, unreadable);

        Assert.False(verdict.Admitted, verdict.Message);
        Assert.Contains("git is not on the path", verdict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CONTROL_AnUnpinnedRunThatCannotReadTheTreeIsStillAdmitted()
    {
        var noPin = MutationProofPinGuard.PinReading.NoPin();
        var unreadable = new MutationProofPinGuard.TreeReading(
            false, "", Array.Empty<MutationProofPinGuard.ChangedPath>(), "git is not on the path");

        Assert.True(
            MutationProofPinGuard.Decide(noPin, unreadable).Admitted,
            "An ordinary run was blocked because git could not be invoked. The guard must be inert for "
            + "unpinned runs even when its own inputs are unavailable.");
    }

    // -------------------------------------------------------------------------------------------------
    // Parsing what git actually emits.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A rename entry carries a SECOND path in its own field. Consuming it is not cosmetic: miss it and
    /// every subsequent entry is shifted by one, so the guard names the wrong file in its refusal - which
    /// is worse than not refusing, because somebody acts on the name.
    /// </summary>
    [Fact]
    public void RenameEntriesDoNotShiftEverySubsequentPath()
    {
        var output = "R  src/New.cs\0src/Old.cs\0 M src/Changed.cs\0?? src/Untracked.cs\0";

        var changes = MutationProofPinGuard.ParsePorcelainZ(output);

        Assert.Equal(
            new[] { "src/New.cs", "src/Changed.cs", "src/Untracked.cs" },
            changes.Select(c => c.Path));
    }

    [Fact]
    public void PathsWithSpacesSurviveParsing()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/a file with spaces.cs", "namespace Example; public static class A { }", "add");
        repository.Write("src/a file with spaces.cs", "namespace Example; public static class A { public const int X = 1; }");

        var tree = MutationProofPinGuard.ReadTree(repository.Root);

        Assert.Contains("src/a file with spaces.cs", tree.Changes.Select(c => c.Path));
    }

    // -------------------------------------------------------------------------------------------------
    // Where the pin lives.
    // -------------------------------------------------------------------------------------------------

    // -------------------------------------------------------------------------------------------------
    // IS THE GUARD ACTUALLY ARMED ON THE PLATFORM IT IS RUNNING ON?
    //
    // A reviewer found that it was not, on Linux and macOS. The pin's location was DERIVED twice - once in
    // PowerShell to write it, once in C# to read it - and the two derivations disagreed off Windows. The
    // script printed "PINNED" while the guard looked somewhere else, found nothing, and admitted
    // contaminated baselines. Armed according to its own output, inert in fact. Continuous integration runs
    // on Linux, so the platform where it was dead is the platform nobody watches.
    //
    // These run on WHATEVER PLATFORM executes them, so the Linux answer is checked by the Linux run rather
    // than reasoned about from Windows.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The end-to-end arming check: a pin placed where the SCRIPT puts it must be found by the GUARD, here,
    /// on this operating system.
    ///
    /// The script's location is reproduced the way the script gets it - by asking git, in this process, in
    /// a real repository - rather than by restating a path. That is the whole repair: there is no longer a
    /// derivation on either side to disagree about, only one question with one answer.
    /// </summary>
    [Fact]
    public void TheGuardFindsAPinWrittenWhereTheScriptWritesIt_OnThisPlatform()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        // Exactly what scripts/mutation-proof-pin.ps1 does to find the pin's home.
        var asTheScriptResolvesIt = Path.Combine(
            repository.Git("rev-parse --absolute-git-dir").Trim(),
            MutationProofPinGuard.PinFileName);

        var asTheGuardResolvesIt = MutationProofPinGuard.ResolvePinFilePath(repository.Root);

        Assert.NotNull(asTheGuardResolvesIt);
        Assert.Equal(
            Path.GetFullPath(asTheScriptResolvesIt),
            Path.GetFullPath(asTheGuardResolvesIt!));

        // And a pin written there is genuinely readable as a pin, rather than merely landing at a matching
        // path. A path that agrees but content that does not parse would be the same silence.
        File.WriteAllText(
            asTheScriptResolvesIt,
            "proofId=abc123\nphase=baseline\npinnedHead=" + repository.Head() + "\n");

        var reading = MutationProofPinGuard.ParsePin(
            File.ReadAllText(asTheGuardResolvesIt!), asTheGuardResolvesIt!);

        Assert.Null(reading.Problem);
        Assert.Equal("abc123", reading.Pin!.ProofId);
    }

    /// <summary>
    /// The pin must not sit anywhere "git status" can see, or the guard would trip over its own
    /// declaration and need an exemption - and an exemption is a hole.
    /// </summary>
    [Fact]
    public void ThePinDoesNotDirtyTheTreeItGuards()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        File.WriteAllText(
            MutationProofPinGuard.ResolvePinFilePath(repository.Root)!,
            "proofId=abc123\nphase=baseline\npinnedHead=" + repository.Head() + "\n");

        Assert.Empty(MutationProofPinGuard.ReadTree(repository.Root).Changes);
    }

    /// <summary>
    /// Two worktrees of one repository - how every mission here runs - must hold independent pins. A shared
    /// pin would mean one worker's proof declaration silently governing another worker's tree. This is now
    /// a property of git's own answer rather than of a hash we maintain, so it is checked against a REAL
    /// second worktree.
    /// </summary>
    [Fact]
    public void TwoWorktreesOfOneRepositoryGetDifferentPins()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var secondRoot = Path.Combine(repository.ScratchPath, "second-worktree");
        repository.Git("worktree add \"" + secondRoot + "\" -b second");

        var first = MutationProofPinGuard.ResolvePinFilePath(repository.Root);
        var second = MutationProofPinGuard.ResolvePinFilePath(secondRoot);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(Path.GetFullPath(first!), Path.GetFullPath(second!));
    }

    /// <summary>
    /// The one string the script and the guard still have to agree on, checked by reading the script's own
    /// text. The previous version froze a golden file NAME while the two sides disagreed about the
    /// DIRECTORY, which is precisely the gap the reviewer walked through - so this asserts the location
    /// primitive as well, not just the name.
    /// </summary>
    [Fact]
    public void TheScriptAndTheGuardAgreeOnThePinFileName()
    {
        var script = ReadPinScript();
        if (script is null)
            return; // Not run from a checkout that carries the script; the arming test above still applies.

        Assert.Contains(
            "$script:PinFileName = '" + MutationProofPinGuard.PinFileName + "'",
            script,
            StringComparison.Ordinal);

        // Both sides must locate the pin by asking git the same question. If either ever goes back to
        // deriving a path, this is the line that should stop it.
        Assert.Contains("rev-parse --absolute-git-dir", script, StringComparison.Ordinal);
    }

    private static string? ReadPinScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "mutation-proof-pin.ps1");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        return null;
    }

    [Fact]
    public void WithNoLocalApplicationDataFolder_TheLedgerRefusesRatherThanFallingBack()
    {
        Assert.Throws<InvalidOperationException>(
            () => MutationProofPinGuard.ComputeLedgerDirectory(""));
    }

    // -------------------------------------------------------------------------------------------------
    // THE PROPERTY THAT JUSTIFIES THE WHOLE DESIGN: THE PINNED HEAD DOES NOT MOVE.
    //
    // Nothing tested this in the first round, and the reason is worth keeping: the pin being fixed is the
    // PREMISE, so no test was aimed at it. A reviewer then found the supported workflow broke it - every
    // "set", including the baseline-to-arm transition, recomputed HEAD and overwrote the pin, so a head
    // that moved between the two runs was silently re-pinned and the arm ADMITTED. The documented happy
    // path walked into the exact event the guard exists to refuse.
    //
    // The script is fixed. These pin the SECOND mechanism, which holds however the pin file came to say
    // what it says: a hand edit, a copied file, a future script change, a tool nobody has written yet.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void AProofWhoseHeadMovedSinceAnEarlierRunOfTheSameProofIsRefused()
    {
        const string headTheBaselineMeasured = "1111111111111111111111111111111111111111";
        const string headThePinNowClaims = "2222222222222222222222222222222222222222";

        var priorBaselineRun = LedgerLine("proof-42", headTheBaselineMeasured);

        // The arm, at a tree that exactly matches the RE-PINNED head - so every other check in this guard
        // passes. Only the proof's own history shows that its foundation moved.
        var pin = ArmPinAt(headThePinNowClaims, "src/RequestHandler.cs");
        var tree = new MutationProofPinGuard.TreeReading(
            true,
            headThePinNowClaims,
            new[] { new MutationProofPinGuard.ChangedPath(" M", "src/RequestHandler.cs") },
            null);

        var verdict = MutationProofPinGuard.Decide(pin, tree, new[] { priorBaselineRun });

        Assert.False(
            verdict.Admitted,
            "A proof was admitted whose pinned head had moved since its own baseline ran. Every other check "
            + "passes in this state - the tree matches the new pin exactly - so this is the only thing "
            + "standing between a silently re-pinned proof and a clean-looking result. " + verdict.Message);

        Assert.Contains(headTheBaselineMeasured, verdict.Message, StringComparison.Ordinal);
        Assert.Contains(headThePinNowClaims, verdict.Message, StringComparison.Ordinal);
        Assert.Contains("PINNED HEAD HAS MOVED", verdict.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// CONTROL. The same proof, run again at the same head, must be admitted - otherwise the check above
    /// would pass by refusing every second run of every proof, and the mechanism would be discarded.
    /// </summary>
    [Fact]
    public void CONTROL_AProofRunAgainAtItsOwnPinnedHeadIsAdmitted()
    {
        const string head = "1111111111111111111111111111111111111111";

        var verdict = MutationProofPinGuard.Decide(
            ArmPinAt(head, "src/RequestHandler.cs"),
            new MutationProofPinGuard.TreeReading(
                true, head,
                new[] { new MutationProofPinGuard.ChangedPath(" M", "src/RequestHandler.cs") }, null),
            new[] { LedgerLine("proof-42", head), LedgerLine("proof-42", head) });

        Assert.True(verdict.Admitted, verdict.Message);
    }

    /// <summary>
    /// CONTROL. A DIFFERENT proof at a different head is somebody else's business. Without this, the check
    /// could pass by refusing any run that followed any earlier proof, which would stop the second proof
    /// ever run on a machine.
    /// </summary>
    [Fact]
    public void CONTROL_AnEarlierUnRELATEDProofAtAnotherHeadDoesNotRefuseThisOne()
    {
        const string head = "1111111111111111111111111111111111111111";

        var verdict = MutationProofPinGuard.Decide(
            BaselinePinAt(head),
            new MutationProofPinGuard.TreeReading(
                true, head, Array.Empty<MutationProofPinGuard.ChangedPath>(), null),
            new[]
            {
                LedgerLine("some-other-proof", "9999999999999999999999999999999999999999"),
                LedgerLine("another-proof", "8888888888888888888888888888888888888888"),
            });

        Assert.True(verdict.Admitted, verdict.Message);
    }

    /// <summary>
    /// A pin with no proof identity cannot be checked against its own history at all, so it is refused
    /// outright rather than admitted with the moved-head check quietly skipped. Minting an identity here
    /// instead would be worse than doing nothing: every run would look like the first run of a brand-new
    /// proof, and the mechanism would report itself in force while never firing.
    /// </summary>
    [Fact]
    public void APinWithNoProofIdentityIsRefused_RatherThanCheckedAgainstNothing()
    {
        var reading = MutationProofPinGuard.ParsePin(
            "phase=baseline\npinnedHead=1111111111111111111111111111111111111111\n",
            "C:/pins/example.pin");

        Assert.NotNull(reading.Problem);
        Assert.Contains("declares no proofId", reading.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A prior run that names this proof but whose head cannot be read is treated as a MISMATCH. Skipping
    /// it would mean the check is skipped on exactly the input that is already wrong.
    /// </summary>
    [Fact]
    public void AProofsHistoryThatCannotBeReadIsTreatedAsAMismatch_NotAsAgreement()
    {
        var problem = MutationProofPinGuard.DetectMovedPin(
            new MutationProofPinGuard.ProofPin(
                "proof-42", MutationProofPinGuard.BaselinePhase,
                "1111111111111111111111111111111111111111", "when",
                Array.Empty<string>(), "", "C:/pins/example.pin"),
            new[] { "when=2026-07-20T00:00:00Z  verdict=admitted  proofId=proof-42  phase=baseline" });

        Assert.NotNull(problem);
    }

    // -------------------------------------------------------------------------------------------------
    // THE PROOFS THAT WERE ONCE DRIVEN BY HAND.
    //
    // The two refusals below were first demonstrated as a sequence typed at a terminal on one machine on
    // one night. That proves the mechanism worked that day and protects nothing afterwards: a script, a
    // path or a parser can regress, and hand-run evidence - being prose in a pull request - cannot notice.
    // Worse, the hand run happened on Windows only, which is the same blind spot that produced the
    // platform divergence a reviewer had to find: I could only ever see the arm that worked.
    //
    // These drive the REAL pin file, the REAL working tree and the REAL ledger, so they re-run on every
    // push. See the pull request for what this does and does not close about the platform question.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole ledger-memory refusal, on disk, with no hand-typed steps.
    ///
    /// This is the case the script fix alone cannot catch, and therefore the one worth mechanising most: a
    /// pin that names the wrong head, where the working tree matches that wrong head EXACTLY. Every other
    /// check in the guard passes. Only the proof's own history says the foundation moved.
    /// </summary>
    [Fact]
    public void APinReWrittenToANewHeadIsRefusedFromDisk_EvenThoughTheTreeMatchesItExactly()
    {
        using var repository = TemporaryGitRepository.Create();
        using var ledger = new TemporaryDirectory();

        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");
        var headTheBaselineMeasured = repository.Head();

        var pinPath = MutationProofPinGuard.ResolvePinFilePath(repository.Root)!;
        WritePin(pinPath, "proof-99", MutationProofPinGuard.BaselinePhase, headTheBaselineMeasured);

        // The baseline runs and is admitted, and the ledger remembers the head it measured.
        var baseline = MutationProofPinGuard.Evaluate(
            repository.Root, MutationProofPinGuard.ReadLedger(ledger.Path));

        Assert.True(baseline.Admitted, "the baseline itself must be admitted. " + baseline.Message);
        AppendLedger(ledger.Path, LedgerLine("proof-99", headTheBaselineMeasured));

        // The head moves, and the pin is REWRITTEN to the new head - what the old script did by itself,
        // and what a hand edit or a future script regression would do again.
        repository.WriteAndCommit("src/Other.cs", "namespace Example; public static class Other { }", "later work");
        var headThePinNowClaims = repository.Head();
        Assert.NotEqual(headTheBaselineMeasured, headThePinNowClaims);

        WritePin(pinPath, "proof-99", MutationProofPinGuard.BaselinePhase, headThePinNowClaims);

        // The tree is clean and sits exactly on the re-pinned head, so the head comparison AGREES.
        var tree = MutationProofPinGuard.ReadTree(repository.Root);
        Assert.Empty(tree.Changes);
        Assert.Equal(headThePinNowClaims, tree.Head);

        var verdict = MutationProofPinGuard.Evaluate(
            repository.Root, MutationProofPinGuard.ReadLedger(ledger.Path));

        Assert.False(
            verdict.Admitted,
            "A proof was admitted after its pinned head was rewritten to a later commit. The tree matches "
            + "the new pin exactly, so every other check in this guard passes - the ledger's memory of the "
            + "baseline is the only thing that can catch it. " + verdict.Message);

        Assert.Contains(headTheBaselineMeasured, verdict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(headThePinNowClaims, verdict.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// CONTROL for the test above, and it is not optional: without it, a guard that refused every run whose
    /// ledger was non-empty would pass. Same repository, same ledger, same proof - the pin simply is not
    /// rewritten.
    /// </summary>
    [Fact]
    public void CONTROL_AProofWhosePinWasNotRewrittenIsAdmittedFromDisk()
    {
        using var repository = TemporaryGitRepository.Create();
        using var ledger = new TemporaryDirectory();

        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");
        var head = repository.Head();

        WritePin(
            MutationProofPinGuard.ResolvePinFilePath(repository.Root)!,
            "proof-99", MutationProofPinGuard.BaselinePhase, head);

        AppendLedger(ledger.Path, LedgerLine("proof-99", head));

        var verdict = MutationProofPinGuard.Evaluate(
            repository.Root, MutationProofPinGuard.ReadLedger(ledger.Path));

        Assert.True(verdict.Admitted, verdict.Message);
    }

    /// <summary>
    /// The arm transition, driven through the REAL script.
    ///
    /// This is the bypass a reviewer found: "set -Phase arm" recomputed the head, so a head that moved
    /// between the baseline and the arm was silently re-pinned and the arm admitted. Asserting the script's
    /// behaviour by reading its source would test my belief about PowerShell; running it tests PowerShell.
    ///
    /// Two things are asserted, and the second matters more: the transition is REFUSED, and the pin file
    /// still names the BASELINE's head afterwards. A refusal that had already overwritten the pin would
    /// leave the next run to be measured against the wrong commit.
    /// </summary>
    [Fact]
    public void TheScriptRefusesAnArmTransitionAfterTheHeadMoves_AndLeavesThePinOnTheBaselineHead()
    {
        var shell = RequirePowerShell();
        var script = RequirePinScript();

        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var baselineHead = repository.Head();

        var pinned = RunScript(shell, script, repository.Root, "set -Phase baseline -Note \"transition test\"");
        Assert.True(pinned.ExitCode == 0, "pinning the baseline failed: " + pinned.Output + pinned.Error);

        // The head moves between the baseline run and the arm - the whole condition under test.
        repository.WriteAndCommit("src/Other.cs", "namespace Example; public static class Other { }", "later work");
        var movedHead = repository.Head();
        Assert.NotEqual(baselineHead, movedHead);

        var arm = RunScript(
            shell, script, repository.Root, "set -Phase arm -Mutates src/RequestHandler.cs");

        Assert.False(
            arm.ExitCode == 0,
            "The script accepted an arm transition after the head had moved. That silently re-pins the "
            + "proof to the new head, and the guard then finds an exact match and admits an arm that "
            + "measured a different program from its own baseline. Output: " + arm.Output + arm.Error);

        Assert.Contains("the head has moved", arm.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(baselineHead, arm.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(movedHead, arm.Output, StringComparison.OrdinalIgnoreCase);

        // And the pin was NOT quietly updated on the way out.
        var pinText = File.ReadAllText(MutationProofPinGuard.ResolvePinFilePath(repository.Root)!);
        Assert.Contains("pinnedHead=" + baselineHead, pinText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pinnedHead=" + movedHead, pinText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// CONTROL. The same transition, with the head left alone, must SUCCEED - otherwise the refusal above
    /// would be satisfied by a script that refuses every arm, which would make the tool unusable for the
    /// arm half of every proof and would be discovered only by whoever tried to run one.
    /// </summary>
    [Fact]
    public void CONTROL_TheScriptAcceptsAnArmTransitionAtTheBaselineHead()
    {
        var shell = RequirePowerShell();
        var script = RequirePinScript();

        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");
        var head = repository.Head();

        RunScript(shell, script, repository.Root, "set -Phase baseline");

        // Apply the mutation, exactly as a real arm would, then declare it.
        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);

        var arm = RunScript(shell, script, repository.Root, "set -Phase arm -Mutates src/RequestHandler.cs");

        Assert.True(
            arm.ExitCode == 0,
            "A legitimate arm transition at the pinned head was refused. Output: " + arm.Output + arm.Error);

        var pinText = File.ReadAllText(MutationProofPinGuard.ResolvePinFilePath(repository.Root)!);
        Assert.Contains("phase=arm", pinText, StringComparison.Ordinal);
        Assert.Contains("pinnedHead=" + head, pinText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mutates=src/RequestHandler.cs", pinText, StringComparison.Ordinal);

        // And the guard admits the run that follows, which is the point of the whole transition.
        using var ledger = new TemporaryDirectory();
        var verdict = MutationProofPinGuard.Evaluate(
            repository.Root, MutationProofPinGuard.ReadLedger(ledger.Path));

        Assert.True(verdict.Admitted, verdict.Message);
    }

    // -------------------------------------------------------------------------------------------------
    // THE SCRIPT'S OTHER REFUSALS.
    //
    // Found by auditing this unit's controls one at a time rather than reading its own summary. The script
    // has four refusal branches; only one of them had a test. The other three were enforcement nobody had
    // ever watched work - which is the same failure shape as an untested guard, and would let a future
    // edit remove them silently while the pull request still described them.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheScriptRefusesToPinABaselineOverAnActivePin()
    {
        var shell = RequirePowerShell();
        var script = RequirePinScript();

        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        Assert.Equal(0, RunScript(shell, script, repository.Root, "set -Phase baseline").ExitCode);

        var second = RunScript(shell, script, repository.Root, "set -Phase baseline");

        Assert.False(
            second.ExitCode == 0,
            "A second baseline was pinned over a live one. That silently starts a new proof at a possibly "
            + "different commit while the numbers already collected still claim to belong to the first. "
            + second.Output + second.Error);
        Assert.Contains("already has an active pin", second.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheScriptRefusesToPinABaselineOnADirtyTree()
    {
        var shell = RequirePowerShell();
        var script = RequirePinScript();

        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        // The 2026-07-19 event, present before the baseline is even taken.
        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);

        var result = RunScript(shell, script, repository.Root, "set -Phase baseline");

        Assert.False(
            result.ExitCode == 0,
            "A baseline was pinned over uncommitted changes, which records a false starting point and "
            + "leaves the run-time refusal looking like a mystery. " + result.Output + result.Error);
        Assert.Contains("uncommitted changes", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/RequestHandler.cs", result.Output, StringComparison.Ordinal);

        // And it must not have written a pin on its way out.
        Assert.False(File.Exists(MutationProofPinGuard.ResolvePinFilePath(repository.Root)!));
    }

    [Fact]
    public void TheScriptRefusesAnArmWithNoBaselineBeforeIt()
    {
        var shell = RequirePowerShell();
        var script = RequirePinScript();

        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var result = RunScript(
            shell, script, repository.Root, "set -Phase arm -Mutates src/RequestHandler.cs");

        Assert.False(
            result.ExitCode == 0,
            "An arm was declared with no baseline before it. That mints a proof whose baseline was never "
            + "taken, and it looks identical to a correct one. " + result.Output + result.Error);
        Assert.Contains("no active baseline pin", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(MutationProofPinGuard.ResolvePinFilePath(repository.Root)!));
    }

    /// <summary>
    /// The arming check, driven through the real script rather than by reproducing its algorithm: a pin the
    /// SCRIPT wrote must be found and understood by the GUARD. This is the property that was false off
    /// Windows, and running the actual writer is the only way to test the actual writer.
    /// </summary>
    [Fact]
    public void APinWrittenByTheRealScriptIsFoundAndUnderstoodByTheGuard()
    {
        var shell = RequirePowerShell();
        var script = RequirePinScript();

        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var result = RunScript(shell, script, repository.Root, "set -Phase baseline -Note \"arming check\"");
        Assert.True(result.ExitCode == 0, result.Output + result.Error);

        var reading = MutationProofPinGuard.ReadPinFor(repository.Root);

        Assert.Equal(MutationProofPinGuard.PinOutcome.PinActive, reading.Outcome);
        Assert.Null(reading.Problem);
        Assert.Equal(repository.Head(), reading.Pin!.PinnedHead, ignoreCase: true);
    }

    // -------------------------------------------------------------------------------------------------
    // FAILING OPEN: the guard must never admit because it FAILED TO LOOK.
    //
    // A reviewer found that it did. Resolving the pin's location launched git; any failure - git missing
    // from the path, a timeout, a broken install - returned null, which became "no pin found", which
    // ADMITTED. So on a machine without git a contaminated baseline ran to completion while the pin file
    // sat in the git directory the whole time, silently.
    //
    // The two directions of a guard's failure are not symmetric. Refusing wrongly is loud and
    // self-correcting; somebody complains within the hour. Admitting wrongly is silent and
    // self-concealing, and it is worse precisely because the guard's existence stops everyone checking.
    // These pin the repair, each against the control that stops it being satisfied by refusing everything.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The exact bypass, end to end: a REAL pin on disk, and git genuinely unavailable to the run.
    ///
    /// The pre-existing test for an unreadable tree could not catch this, and the reason is worth keeping:
    /// it built a PinReading that was already "found" and called Decide directly. The defect lived in the
    /// wiring BETWEEN the lookup and the decision, where an active pin never reached that state at all. A
    /// test that hands a decision its inputs cannot test how those inputs are obtained.
    /// </summary>
    [Fact]
    public void AnActivePinIsNeverTreatedAsAbsentWhenGitIsUnavailable()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        WritePin(
            MutationProofPinGuard.ResolvePinFilePath(repository.Root)!,
            "proof-77", MutationProofPinGuard.BaselinePhase, repository.Head());

        // A contaminant, so that if this run were admitted it would be a genuinely corrupt baseline rather
        // than a harmless one.
        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);

        var verdict = MutationProofPinGuard.Evaluate(
            repository.Root, Array.Empty<string>(), GitThatDoesNotExist);

        Assert.False(
            verdict.Admitted,
            "A proof run was ADMITTED while a pin file sat in the git directory, because git could not be "
            + "launched to locate it. That is the worst direction for this guard to fail in: the run "
            + "proceeds, the numbers look complete, and nothing anywhere says the tree was never checked. "
            + verdict.Message);
    }

    /// <summary>
    /// THE CONTROL, and without it the test above is satisfied by a guard that refuses whenever git is
    /// missing - which would be a blanket refusal on every ordinary run on such a machine, exactly the
    /// scope the Architect ruled against. Same repository, same absent git, no pin.
    /// </summary>
    [Fact]
    public void CONTROL_AnUnpinnedRunIsStillAdmittedWhenGitIsUnavailable()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        // Dirty, unpinned, and git unavailable: the mid-rework worker on a machine without git.
        repository.Write("src/RequestHandler.cs", FileWithTheGuardBlockDeleted);

        Assert.False(File.Exists(MutationProofPinGuard.ResolvePinFilePath(repository.Root)!));

        var verdict = MutationProofPinGuard.Evaluate(
            repository.Root, Array.Empty<string>(), GitThatDoesNotExist);

        Assert.True(
            verdict.Admitted,
            "An ordinary unpinned run was refused because git was unavailable. The pin lookup must not "
            + "need git at all, or every run on such a machine is blocked and the guard is switched off "
            + "within a day. " + verdict.Message);
    }

    /// <summary>
    /// The lookup answers from the filesystem, so "is a proof active here" no longer depends on launching
    /// a subprocess. Asserted directly, because it is the property that makes the two tests above possible
    /// rather than an implementation detail of them.
    /// </summary>
    [Fact]
    public void ThePinLookupDoesNotNeedGitAtAll()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        WritePin(
            MutationProofPinGuard.ResolvePinFilePath(repository.Root)!,
            "proof-77", MutationProofPinGuard.BaselinePhase, repository.Head());

        // No git executable is named here at all - this is a pure filesystem answer.
        var reading = MutationProofPinGuard.ReadPinFor(repository.Root);

        Assert.Equal(MutationProofPinGuard.PinOutcome.PinActive, reading.Outcome);
        Assert.Equal("proof-77", reading.Pin!.ProofId);
    }

    /// <summary>
    /// The filesystem answer must agree with git's own, in BOTH repository shapes - a plain checkout where
    /// ".git" is a directory, and a worktree where it is a file naming somewhere else. This is what keeps
    /// the lookup from becoming a second, drifting derivation of the location the pin script computes: the
    /// script asks git, this reads what git wrote, and if they ever disagree this reddens.
    /// </summary>
    [Fact]
    public void TheFileSystemLookupAgreesWithGitsOwnAnswer_InAPlainCheckoutAndInAWorktree()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        AssertLookupMatchesGit(repository, repository.Root);

        var secondRoot = Path.Combine(repository.ScratchPath, "second-worktree");
        repository.Git("worktree add \"" + secondRoot + "\" -b second");

        AssertLookupMatchesGit(repository, secondRoot);
    }

    private static void AssertLookupMatchesGit(TemporaryGitRepository repository, string workingTreeRoot)
    {
        var asGitSaysIt = Path.Combine(
            repository.GitIn(workingTreeRoot, "rev-parse --absolute-git-dir").Trim(),
            MutationProofPinGuard.PinFileName);

        var located = MutationProofPinGuard.LocatePin(workingTreeRoot);

        Assert.Equal(MutationProofPinGuard.PinLocationOutcome.Located, located.Outcome);
        Assert.Equal(Path.GetFullPath(asGitSaysIt), Path.GetFullPath(located.PinFilePath!));
    }

    /// <summary>
    /// A repository marker that exists but cannot be understood is INDETERMINATE, and indeterminate
    /// refuses. Under the old shape this resolved to "no pin" and admitted - the same collapse of "could
    /// not look" into "nothing there".
    /// </summary>
    [Fact]
    public void AnUnreadableRepositoryMarkerRefuses_RatherThanReadingAsNoPin()
    {
        using var scratch = new TemporaryDirectory();
        var root = Path.Combine(scratch.Path, "tree");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".git"), "this is not a gitdir pointer");

        var located = MutationProofPinGuard.LocatePin(root);
        Assert.Equal(MutationProofPinGuard.PinLocationOutcome.Indeterminate, located.Outcome);

        var verdict = MutationProofPinGuard.Decide(
            MutationProofPinGuard.ReadPinFor(root),
            new MutationProofPinGuard.TreeReading(false, "", Array.Empty<MutationProofPinGuard.ChangedPath>(), "n/a"),
            Array.Empty<string>());

        Assert.False(verdict.Admitted, verdict.Message);
        Assert.Contains("CANNOT ESTABLISH", verdict.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A marker naming a git directory that is not one must also refuse. Without the HEAD check the guard
    /// would build a pin path under a wrong directory, find no pin there, and admit - looking in the wrong
    /// place is indistinguishable from finding nothing.
    /// </summary>
    [Fact]
    public void AMarkerNamingSomethingThatIsNotAGitDirectoryRefuses()
    {
        using var scratch = new TemporaryDirectory();
        var root = Path.Combine(scratch.Path, "tree");
        var decoy = Path.Combine(scratch.Path, "not-a-git-directory");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(decoy);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: " + decoy);

        var located = MutationProofPinGuard.LocatePin(root);

        Assert.Equal(MutationProofPinGuard.PinLocationOutcome.Indeterminate, located.Outcome);
        Assert.Contains("HEAD", located.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// CONTROL for the two above: outside any repository there is no pin and no repository, which is a
    /// COMPLETED search rather than a failure, and it admits. Without this the indeterminate cases could be
    /// satisfied by refusing everything that is not a pristine repository.
    /// </summary>
    [Fact]
    public void CONTROL_OutsideARepositoryTheAnswerIsNoPin_NotIndeterminate()
    {
        using var scratch = new TemporaryDirectory();
        var deep = Path.Combine(scratch.Path, "a", "b");
        Directory.CreateDirectory(deep);

        var located = MutationProofPinGuard.LocatePin(deep);

        // The scratch directory is not inside a repository. If some ancestor ever were one, the walk would
        // legitimately find it, so the assertion allows that and forbids only the indeterminate answer.
        Assert.NotEqual(MutationProofPinGuard.PinLocationOutcome.Indeterminate, located.Outcome);
    }

    /// <summary>
    /// The structural claim, asserted rather than left to prose: every pin state OTHER than a settled
    /// absence refuses. If a state is ever added, this fails until somebody decides what it means - which
    /// is the point, since the defect being repaired was a new failure quietly reusing the admit path.
    /// </summary>
    [Fact]
    public void OnlyASettledAbsenceAdmits()
    {
        // Enumerated rather than listed, so that ADDING a state is caught here instead of silently
        // inheriting whatever the fall-through happens to do. That is precisely how the defect arose: a new
        // way of failing reused the existing "not found" value, and "not found" admitted.
        var everyOtherState = Enum.GetValues<MutationProofPinGuard.PinOutcome>()
            .Where(o => o != MutationProofPinGuard.PinOutcome.NoPinPresent)
            .ToList();

        Assert.NotEmpty(everyOtherState);

        foreach (var outcome in everyOtherState)
        {
            var verdict = MutationProofPinGuard.Decide(
                new MutationProofPinGuard.PinReading(outcome, null, "unreadable for this test"),
                new MutationProofPinGuard.TreeReading(
                    true, "1111111111111111111111111111111111111111",
                    Array.Empty<MutationProofPinGuard.ChangedPath>(), null),
                Array.Empty<string>());

            Assert.False(
                verdict.Admitted,
                "The pin state '" + outcome + "' ADMITTED. Only a settled absence may admit: every other "
                + "state means the guard does not know whether a proof is active, and admitting on a "
                + "not-known is how this guard failed open. " + verdict.Message);
        }
    }

    [Fact]
    public void CONTROL_ASettledAbsenceDoesAdmit()
    {
        var verdict = MutationProofPinGuard.Decide(
            MutationProofPinGuard.PinReading.NoPin(),
            new MutationProofPinGuard.TreeReading(
                true, "1111111111111111111111111111111111111111",
                Array.Empty<MutationProofPinGuard.ChangedPath>(), null),
            Array.Empty<string>());

        Assert.True(verdict.Admitted, verdict.Message);
    }

    // -------------------------------------------------------------------------------------------------
    // ONLY A COMPLETED, ERROR-FREE NOT-FOUND MAY CONSTRUCT THE ADMITTING STATE.
    //
    // The three-state outcome was necessary and not sufficient. A reviewer found the collapse it was meant
    // to end still happening one level down: absence was being ESTABLISHED with File.Exists and
    // Directory.Exists, which answer false for an access failure, an invalid path or an input-output error
    // exactly as they do for a file that genuinely is not there. By the time the value reached the enum,
    // "could not look" and "there is nothing there" were already the same boolean - and no downstream
    // typing can recover what a predicate threw away. The surrounding try/catch blocks saw nothing,
    // because those APIs are documented to return false rather than throw.
    //
    // A three-state type is only as good as the narrowest source feeding it. Predicates destroy error
    // information; operations preserve it. These pin the repair at the level where it now lives.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A pin path that cannot be READ but is certainly there, on a real filesystem: a DIRECTORY sitting
    /// where the pin file belongs.
    ///
    /// This is the reviewer finding made concrete without needing permissions or an injected filesystem.
    /// File.Exists answers FALSE for a directory, so the old lookup concluded "no pin" and ADMITTED, while
    /// opening it raises an access error that says plainly that something is there and unreadable.
    /// </summary>
    [Fact]
    public void APinPathThatCannotBeReadIsIndeterminate_NotAProvenAbsence()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var pinPath = MutationProofPinGuard.ResolvePinFilePath(repository.Root)!;
        Directory.CreateDirectory(pinPath);

        // The predicate the old code trusted says there is no pin here.
        Assert.False(File.Exists(pinPath));

        var reading = MutationProofPinGuard.ReadPinFor(repository.Root);

        Assert.Equal(MutationProofPinGuard.PinOutcome.CouldNotDetermine, reading.Outcome);

        var verdict = MutationProofPinGuard.Decide(
            reading,
            MutationProofPinGuard.ReadTree(repository.Root),
            Array.Empty<string>());

        Assert.False(
            verdict.Admitted,
            "An unreadable pin path was treated as a proven absence and the run was ADMITTED. File.Exists "
            + "answers false for an inaccessible path exactly as it does for a missing one, which is how "
            + "a live proof can be skipped silently. " + verdict.Message);
    }

    /// <summary>
    /// CONTROL. The same repository with a genuinely missing pin must still admit, or the test above is
    /// satisfied by refusing every unpinned run - which is the blanket behaviour the Architect ruled out.
    /// </summary>
    [Fact]
    public void CONTROL_AGenuinelyMissingPinIsAnAbsenceAndAdmits()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var reading = MutationProofPinGuard.ReadPinFor(repository.Root);

        Assert.Equal(MutationProofPinGuard.PinOutcome.NoPinPresent, reading.Outcome);
        Assert.True(
            MutationProofPinGuard.Decide(
                reading, MutationProofPinGuard.ReadTree(repository.Root), Array.Empty<string>()).Admitted);
    }

    /// <summary>
    /// A repository marker that cannot be EXAMINED must not be walked past.
    ///
    /// Walking past it ends at "no repository", which is the admitting state - so an unreadable `.git`
    /// would silently skip a live proof. The probe is injected because a path the operating system refuses
    /// to describe cannot be created portably, and a branch that only runs on someone else's machine is a
    /// branch nobody has watched work.
    /// </summary>
    [Fact]
    public void ARepositoryMarkerThatCannotBeExaminedIsIndeterminate_NotAbsent()
    {
        using var scratch = new TemporaryDirectory();
        var deep = Path.Combine(scratch.Path, "a", "b");
        Directory.CreateDirectory(deep);

        var located = MutationProofPinGuard.LocatePin(
            deep,
            path => path.EndsWith(".git", StringComparison.Ordinal)
                ? new MutationProofPinGuard.Probe(
                    MutationProofPinGuard.ProbeOutcome.CouldNotTell, false, "access is denied")
                : MutationProofPinGuard.ProbePath(path));

        Assert.Equal(MutationProofPinGuard.PinLocationOutcome.Indeterminate, located.Outcome);

        Assert.Equal(
            MutationProofPinGuard.PinOutcome.CouldNotDetermine,
            MutationProofPinGuard.ReadPin(located).Outcome);
    }

    /// <summary>
    /// CONTROL for the probe. When every probe answers a definite NOT THERE, the walk completes and the
    /// answer is a genuine absence - otherwise the test above would be satisfied by a lookup that never
    /// concludes anything, and no ordinary run outside a repository could proceed.
    /// </summary>
    [Fact]
    public void CONTROL_AWalkWhereEveryProbeSaysNotThereIsACompletedSearch()
    {
        using var scratch = new TemporaryDirectory();
        var deep = Path.Combine(scratch.Path, "a", "b");
        Directory.CreateDirectory(deep);

        var located = MutationProofPinGuard.LocatePin(
            deep,
            _ => new MutationProofPinGuard.Probe(
                MutationProofPinGuard.ProbeOutcome.DoesNotExist, false, null));

        Assert.Equal(MutationProofPinGuard.PinLocationOutcome.NotInARepository, located.Outcome);
        Assert.Equal(
            MutationProofPinGuard.PinOutcome.NoPinPresent,
            MutationProofPinGuard.ReadPin(located).Outcome);
    }

    /// <summary>
    /// The probe itself must distinguish the two, on the real filesystem of whatever machine runs this.
    /// Everything above rests on it.
    /// </summary>
    [Fact]
    public void TheProbeTellsAMissingPathFromOneItCannotDescribe()
    {
        using var scratch = new TemporaryDirectory();

        var missing = MutationProofPinGuard.ProbePath(Path.Combine(scratch.Path, "nothing-here"));
        Assert.Equal(MutationProofPinGuard.ProbeOutcome.DoesNotExist, missing.Outcome);

        var directory = MutationProofPinGuard.ProbePath(scratch.Path);
        Assert.Equal(MutationProofPinGuard.ProbeOutcome.Exists, directory.Outcome);
        Assert.True(directory.IsDirectory);

        var file = Path.Combine(scratch.Path, "a-file");
        File.WriteAllText(file, "x");
        var probedFile = MutationProofPinGuard.ProbePath(file);
        Assert.Equal(MutationProofPinGuard.ProbeOutcome.Exists, probedFile.Outcome);
        Assert.False(probedFile.IsDirectory);

        // An input the operating system will not describe at all. It must NOT come back as "not there".
        Assert.Equal(MutationProofPinGuard.ProbeOutcome.CouldNotTell, MutationProofPinGuard.ProbePath("").Outcome);
    }

    // -------------------------------------------------------------------------------------------------
    // A HISTORY THAT CANNOT BE READ IS NOT A HISTORY WITH NOTHING IN IT.
    //
    // Found by applying to my own code the rule a reviewer had just applied to it: the try/catch around
    // the ledger read made failure look handled while turning it into an empty list - which silently
    // disabled DetectMovedPin, the second mechanism, the one built to catch what the first cannot.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void AProofHistoryThatCannotBeReadRefuses_RatherThanReadingAsNoHistory()
    {
        var verdict = MutationProofPinGuard.Decide(
            BaselinePinAt("1111111111111111111111111111111111111111"),
            new MutationProofPinGuard.TreeReading(
                true, "1111111111111111111111111111111111111111",
                Array.Empty<MutationProofPinGuard.ChangedPath>(), null),
            MutationProofPinGuard.LedgerReading.Unreadable("access is denied"));

        Assert.False(
            verdict.Admitted,
            "A proof ran with its history unreadable, which silently switches off the check that catches a "
            + "pinned head moving midway through a proof. " + verdict.Message);
        Assert.Contains("PROOF HISTORY COULD NOT BE READ", verdict.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// CONTROL. A genuinely EMPTY history - the first proof ever run on a machine - must admit, or no
    /// proof could ever start.
    /// </summary>
    [Fact]
    public void CONTROL_AnEmptyProofHistoryIsFineAndAdmits()
    {
        var verdict = MutationProofPinGuard.Decide(
            BaselinePinAt("1111111111111111111111111111111111111111"),
            new MutationProofPinGuard.TreeReading(
                true, "1111111111111111111111111111111111111111",
                Array.Empty<MutationProofPinGuard.ChangedPath>(), null),
            MutationProofPinGuard.LedgerReading.Of(Array.Empty<string>()));

        Assert.True(verdict.Admitted, verdict.Message);
    }

    /// <summary>A missing ledger is an empty history, not an unreadable one - the first run on a machine.</summary>
    [Fact]
    public void AMissingLedgerReadsAsAnEmptyHistory_NotAnUnreadableOne()
    {
        using var scratch = new TemporaryDirectory();

        var reading = MutationProofPinGuard.ReadLedger(Path.Combine(scratch.Path, "never-written"));

        Assert.True(reading.Read);
        Assert.Empty(reading.Lines);
    }

    // -------------------------------------------------------------------------------------------------
    // THE PINNED COMMIT MUST BE ONE SOMEBODY ELSE CAN READ.
    //
    // From another unit on this mission, which built the same precondition independently and flagged the
    // overlap rather than shipping a duplicate. This guard verifies the tree matches the pinned head; a
    // reader hears "this arm measured the code under review", and the gap between those two sentences is
    // a STASH - a stashed repair leaves a perfectly clean tree at exactly the pinned head.
    //
    // Requiring a published commit does not close that gap; nothing here can know what a head was SUPPOSED
    // to contain. It makes the claim checkable by somebody else, and catches the common shape, where the
    // stash leaves the tree on a local commit that was never pushed.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void APinnedCommitThatWasNeverPushedIsRefused()
    {
        var verdict = DecideWithPublication(MutationProofPinGuard.HeadPublication.NotPublished);

        Assert.False(verdict.Admitted, verdict.Message);
        Assert.Contains("HAS NOT BEEN PUSHED", verdict.Message, StringComparison.Ordinal);
        Assert.Contains("stashed", verdict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APublicationStateThatCannotBeEstablishedIsRefused()
    {
        var verdict = MutationProofPinGuard.Decide(
            BaselinePinAt("1111111111111111111111111111111111111111"),
            CleanTreeAt("1111111111111111111111111111111111111111"),
            MutationProofPinGuard.LedgerReading.Of(Array.Empty<string>()),
            new MutationProofPinGuard.HeadPublicationReading(
                MutationProofPinGuard.HeadPublication.CouldNotTell, "the remote could not be listed"));

        Assert.False(verdict.Admitted, verdict.Message);
        Assert.Contains("COULD NOT BE ESTABLISHED", verdict.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// CONTROL. A published commit admits - otherwise the check above would refuse every proof, which is
    /// the same blanket failure the Architect ruled out in the original scope.
    /// </summary>
    [Fact]
    public void CONTROL_APublishedPinnedCommitIsAdmitted()
    {
        Assert.True(
            DecideWithPublication(MutationProofPinGuard.HeadPublication.Published).Admitted);
    }

    /// <summary>
    /// CONTROL. A repository with no remote at all - a scratch clone, a fixture, an offline experiment -
    /// has no publication question to answer, and must not be blocked by one.
    /// </summary>
    [Fact]
    public void CONTROL_ARepositoryWithNoRemoteIsNotHeldToPublication()
    {
        Assert.True(
            DecideWithPublication(MutationProofPinGuard.HeadPublication.NoRemoteConfigured).Admitted);
    }

    /// <summary>
    /// The real reader, against a real repository with no remote configured - the case every temporary
    /// fixture in this file is in, so this is also what stops the new check breaking them all.
    /// </summary>
    [Fact]
    public void ARepositoryWithNoRemoteReportsNoRemoteConfigured()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var reading = MutationProofPinGuard.ReadHeadPublication(
            repository.Root, MutationProofPinGuard.GitExecutable);

        Assert.Equal(MutationProofPinGuard.HeadPublication.NoRemoteConfigured, reading.Outcome);
    }

    /// <summary>With git unreachable, publication is unknown - and unknown is never read as published.</summary>
    [Fact]
    public void WithGitUnreachableThePublicationStateIsUnknown()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        var reading = MutationProofPinGuard.ReadHeadPublication(repository.Root, GitThatDoesNotExist);

        Assert.Equal(MutationProofPinGuard.HeadPublication.CouldNotTell, reading.Outcome);
    }

    private static MutationProofPinGuard.TreeReading CleanTreeAt(string head) =>
        new(true, head, Array.Empty<MutationProofPinGuard.ChangedPath>(), null);

    private static MutationProofPinGuard.Verdict DecideWithPublication(
        MutationProofPinGuard.HeadPublication publication) =>
        MutationProofPinGuard.Decide(
            BaselinePinAt("1111111111111111111111111111111111111111"),
            CleanTreeAt("1111111111111111111111111111111111111111"),
            MutationProofPinGuard.LedgerReading.Of(Array.Empty<string>()),
            new MutationProofPinGuard.HeadPublicationReading(publication, null));

    // -------------------------------------------------------------------------------------------------
    // THE REMAINDER OF THE SITE-BY-SITE AUDIT.
    //
    // Having just criticised this unit's own script for controls that were right but never watched, these
    // close the same state in the guard. Each is a refusal that existed and had no test naming it - one
    // edit away from not happening, with nothing going red on the way. None of them found a bug; they
    // convert four claims into four facts, which is the whole point of the exercise.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A marker file that exists but cannot be read must be indeterminate. Under a predicate this looked
    /// like any other unreadable path; the danger is that failing to read it and concluding "no repository"
    /// lands on the admitting state.
    /// </summary>
    [Fact]
    public void ARepositoryMarkerThatExistsButCannotBeReadIsIndeterminate()
    {
        using var scratch = new TemporaryDirectory();
        var deep = Path.Combine(scratch.Path, "a", "b");
        Directory.CreateDirectory(deep);

        // The probe reports a marker FILE that is not actually on disk, so the read that follows fails -
        // which is the shape of a marker that exists and refuses to be read.
        var located = MutationProofPinGuard.LocatePin(
            deep,
            path => path.EndsWith(".git", StringComparison.Ordinal)
                ? new MutationProofPinGuard.Probe(MutationProofPinGuard.ProbeOutcome.Exists, false, null)
                : MutationProofPinGuard.ProbePath(path));

        Assert.Equal(MutationProofPinGuard.PinLocationOutcome.Indeterminate, located.Outcome);
        Assert.Contains("could not be read", located.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pin whose directory has vanished between locating it and reading it is a change underneath the
    /// run, not a settled absence.
    /// </summary>
    [Fact]
    public void APinWhoseDirectoryHasVanishedIsIndeterminate_NotAbsent()
    {
        using var scratch = new TemporaryDirectory();

        var reading = MutationProofPinGuard.ReadPin(
            new MutationProofPinGuard.PinLocation(
                MutationProofPinGuard.PinLocationOutcome.Located,
                scratch.Path,
                Path.Combine(scratch.Path, "a-directory-that-is-not-there", MutationProofPinGuard.PinFileName),
                null));

        Assert.Equal(MutationProofPinGuard.PinOutcome.CouldNotDetermine, reading.Outcome);
    }

    /// <summary>
    /// The defensive branch: an active proof with no pin attached cannot be checked, so it refuses. It
    /// should be unreachable, and an unreachable refusal that was never exercised is indistinguishable
    /// from one that admits.
    /// </summary>
    [Fact]
    public void AnActiveProofWithNoPinAttachedRefuses()
    {
        var verdict = MutationProofPinGuard.Decide(
            new MutationProofPinGuard.PinReading(MutationProofPinGuard.PinOutcome.PinActive, null, null),
            CleanTreeAt("1111111111111111111111111111111111111111"),
            MutationProofPinGuard.LedgerReading.Of(Array.Empty<string>()));

        Assert.False(verdict.Admitted, verdict.Message);
        Assert.Contains("no pin attached", verdict.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The probe's other not-found shape: a missing intermediate DIRECTORY rather than a missing file. It
    /// raises a different exception, and it must classify the same way or a path under a directory that
    /// does not exist would read as unknown and refuse every ordinary run beneath it.
    /// </summary>
    [Fact]
    public void TheProbeTreatsAMissingIntermediateDirectoryAsNotFound()
    {
        using var scratch = new TemporaryDirectory();

        var probe = MutationProofPinGuard.ProbePath(
            Path.Combine(scratch.Path, "no-such-directory", "no-such-file"));

        Assert.Equal(MutationProofPinGuard.ProbeOutcome.DoesNotExist, probe.Outcome);
    }

    /// <summary>A git that is certainly not installed, so the run genuinely cannot reach git.</summary>
    private const string GitThatDoesNotExist = "git-not-installed-on-this-machine-b6f2a1";

    private static void WritePin(string path, string proofId, string phase, string head, params string[] mutations)
    {
        var text = "proofId=" + proofId + "\nphase=" + phase + "\npinnedHead=" + head + "\n"
            + string.Concat(mutations.Select(m => "mutates=" + m + "\n"));
        File.WriteAllText(path, text);
    }

    private static void AppendLedger(string ledgerDirectory, string line) =>
        File.AppendAllText(
            Path.Combine(ledgerDirectory, MutationProofPinGuard.ProofLedgerFileName),
            line + Environment.NewLine);

    /// <summary>
    /// The script host, or a LOUD FAILURE.
    ///
    /// Deliberately not a silent skip. A skipped test is invisible in a summary line and looks exactly like
    /// a test that ran - which is the failure mode this entire unit exists to end, reproduced in its own
    /// test suite. The tests run on Windows, which always has a PowerShell host, and the hosted Linux
    /// images carry pwsh; a machine with neither cannot check the writer at all, and should be told so
    /// rather than quietly reassured.
    /// </summary>
    private static string RequirePowerShell() =>
        FindPowerShell()
        ?? throw new InvalidOperationException(
            "No PowerShell host (pwsh or powershell) could be executed on this machine, so the pin script "
            + "cannot be run and the writer half of this mechanism cannot be checked here. This is reported "
            + "as a failure rather than skipped on purpose: a silent skip would leave the suite looking "
            + "green while the script that WRITES pins went unchecked.");

    private static string RequirePinScript() =>
        FindPinScriptPath()
        ?? throw new InvalidOperationException(
            "scripts/mutation-proof-pin.ps1 was not found above " + AppContext.BaseDirectory
            + ", so the writer half of this mechanism cannot be checked. Reported rather than skipped for "
            + "the same reason as the missing-host case above.");

    private static string? FindPinScriptPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "mutation-proof-pin.ps1");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// pwsh first, because it is the cross-platform host and is present on the hosted Linux images;
    /// powershell second, because Windows always has it. Resolved by running it, not by probing a path -
    /// a name on the path that cannot execute is not a host.
    /// </summary>
    private static string? FindPowerShell()
    {
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            try
            {
                using var probe = new Process();
                probe.StartInfo = new ProcessStartInfo(candidate)
                {
                    Arguments = "-NoProfile -Command \"exit 0\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                probe.Start();
                probe.StandardOutput.ReadToEnd();
                probe.StandardError.ReadToEnd();

                if (probe.WaitForExit(30_000) && probe.ExitCode == 0)
                    return candidate;
            }
            catch (Exception)
            {
                // Not present on this machine; try the next.
            }
        }

        return null;
    }

    private readonly record struct ScriptResult(int ExitCode, string Output, string Error);

    private static ScriptResult RunScript(string shell, string scriptPath, string workingDirectory, string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(shell)
        {
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" " + arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);

        return new ScriptResult(process.ExitCode, output, error);
    }

    private static string LedgerLine(string proofId, string pinnedHead) =>
        "when=2026-07-20T00:00:00.0000000Z  verdict=admitted  proofId=" + proofId
        + "  phase=baseline  pinnedHead=" + pinnedHead + "  observedHead=" + pinnedHead
        + "  headVerified=yes  tree=D:/ReposFred/_wt/w-example";

    [Fact]
    public void LedgerFieldsAreReadableEvenWhenAValueContainsSpaces()
    {
        const string line = "when=2026-07-20T00:00:00Z  proofId=proof-42  observedChanges= M src/a file.cs  tree=D:/x";

        Assert.Equal("proof-42", MutationProofPinGuard.ReadLedgerField(line, "proofId"));
        Assert.Equal("M src/a file.cs", MutationProofPinGuard.ReadLedgerField(line, "observedChanges"));
        Assert.Null(MutationProofPinGuard.ReadLedgerField(line, "absent"));
    }

    // -------------------------------------------------------------------------------------------------
    // Finding the working tree root.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// In a git worktree - how every mission in this repository is run - ".git" is a FILE, not a directory.
    /// A root-finder that only accepted a directory would find nothing in exactly the trees this guard
    /// exists to protect, and would then admit every proof run silently.
    /// </summary>
    [Fact]
    public void TheWorkingTreeRootIsFoundWhenDotGitIsAFile()
    {
        using var repository = TemporaryGitRepository.Create();
        repository.WriteAndCommit("src/RequestHandler.cs", FileWithTheGuardBlock, "add the tenant guard");

        // A real worktree, where ".git" is a FILE naming somewhere else - how every mission here runs.
        var worktree = Path.Combine(repository.ScratchPath, "linked-worktree");
        repository.Git("worktree add \"" + worktree + "\" -b linked");

        var deep = Path.Combine(worktree, "src", "Project", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(deep);

        var located = MutationProofPinGuard.LocatePin(deep);

        Assert.Equal(MutationProofPinGuard.PinLocationOutcome.Located, located.Outcome);
        Assert.Equal(Path.GetFullPath(worktree), Path.GetFullPath(located.WorkingTreeRoot!));
    }

    [Fact]
    public void OutsideAnyRepositoryTheLookupSaysNotInARepository()
    {
        using var scratch = new TemporaryDirectory();
        var deep = Path.Combine(scratch.Path, "a", "b");
        Directory.CreateDirectory(deep);

        var located = MutationProofPinGuard.LocatePin(deep);

        // If some ancestor of the temporary directory ever were a repository the walk would legitimately
        // find it, so the assertion forbids only the answer that matters: it must never be Indeterminate,
        // and it must never report a root inside the scratch directory, where none was created.
        Assert.NotEqual(MutationProofPinGuard.PinLocationOutcome.Indeterminate, located.Outcome);
        Assert.True(
            located.WorkingTreeRoot is null
            || !located.WorkingTreeRoot.StartsWith(scratch.Path, StringComparison.OrdinalIgnoreCase),
            "no repository was created under the scratch directory, so none may be reported from inside it");
    }

    // -------------------------------------------------------------------------------------------------
    // The ledger: the tree's state at run time, recorded as a fact that outlives the tree.
    //
    // Four already-merged security proofs cannot now be shown to have been taken on clean trees, because
    // their worktrees were removed after merging and that removal destroyed the only evidence. These pin
    // the properties that stop the fifth one joining them.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// An ADMITTED baseline must leave a POSITIVE statement behind - which head was verified, and that the
    /// tree matched it. This is the property the Manager's finding turns on: a ledger that only writes on
    /// refusal cannot afterwards distinguish "verified clean" from "the guard never ran", because both are
    /// silence, and silence reads as success.
    /// </summary>
    [Fact]
    public void AnAdmittedBaselineIsRecordedAsAPositiveVerifiedFact_NotAsAnAbsenceOfComplaint()
    {
        const string head = "0123456789abcdef0123456789abcdef01234567";

        var pin = BaselinePinAt(head);
        var tree = new MutationProofPinGuard.TreeReading(
            true, head, Array.Empty<MutationProofPinGuard.ChangedPath>(), null);
        var verdict = MutationProofPinGuard.Decide(pin, tree);

        Assert.True(verdict.Admitted, verdict.Message);

        var line = MutationProofPinGuard.FormatRunRecord(new MutationProofPinGuard.RunRecord(
            "2026-07-20T01:02:03.0000000Z", 4242, "net10.0", "D:/ReposFred/_wt/w-example",
            "cj_example", pin, tree, verdict));

        Assert.Contains("verdict=admitted", line, StringComparison.Ordinal);
        Assert.Contains("headVerified=yes", line, StringComparison.Ordinal);
        Assert.Contains("pinnedHead=" + head, line, StringComparison.Ordinal);
        Assert.Contains("VERIFIED", line, StringComparison.Ordinal);
        Assert.Contains("MATCHED it", line, StringComparison.Ordinal);

        // The identity that ties the record to the run it admitted, so the record still means something
        // once the tree it describes has been removed.
        Assert.Contains("tree=D:/ReposFred/_wt/w-example", line, StringComparison.Ordinal);
        Assert.Contains("session=cj_example", line, StringComparison.Ordinal);
        Assert.Contains("pid=4242", line, StringComparison.Ordinal);

        // One line per run: a partially written record from an interrupted process must not be able to
        // corrupt its neighbours, and the file must stay greppable.
        Assert.DoesNotContain('\n', line);
    }

    /// <summary>
    /// The refusal arm of the same record, so the ledger distinguishes the three outcomes rather than two:
    /// verified, refused, and never checked. A refused run's line must say that no tests ran, because
    /// somebody reading a proof's numbers later needs to know they did not come from this process.
    /// </summary>
    [Fact]
    public void ARefusedRunIsRecordedAsRefused_AndSaysItsNumbersAreNotItsOwn()
    {
        const string pinnedHead = "0123456789abcdef0123456789abcdef01234567";

        var pin = BaselinePinAt(pinnedHead);
        var tree = new MutationProofPinGuard.TreeReading(
            true,
            pinnedHead,
            new[] { new MutationProofPinGuard.ChangedPath(" M", "src/RequestHandler.cs") },
            null);
        var verdict = MutationProofPinGuard.Decide(pin, tree);

        Assert.False(verdict.Admitted, verdict.Message);

        var line = MutationProofPinGuard.FormatRunRecord(new MutationProofPinGuard.RunRecord(
            "2026-07-20T01:02:03.0000000Z", 4242, "net10.0", "D:/ReposFred/_wt/w-example",
            "cj_example", pin, tree, verdict));

        Assert.Contains("verdict=refused", line, StringComparison.Ordinal);
        Assert.Contains("NO TESTS RAN", line, StringComparison.Ordinal);
        Assert.Contains("src/RequestHandler.cs", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The third outcome, and the one that must never be confused with the first: a run nobody declared
    /// part of a proof verified NOTHING. Recording it as a bland success would manufacture exactly the
    /// false reassurance this ledger exists to prevent.
    /// </summary>
    [Fact]
    public void AnUnpinnedRunIsRecordedAsHavingVerifiedNothing_NotAsVerified()
    {
        var noPin = MutationProofPinGuard.PinReading.NoPin();
        var tree = new MutationProofPinGuard.TreeReading(
            true,
            "0123456789abcdef0123456789abcdef01234567",
            new[] { new MutationProofPinGuard.ChangedPath(" M", "src/InProgress.cs") },
            null);
        var verdict = MutationProofPinGuard.Decide(noPin, tree);

        Assert.True(verdict.Admitted, verdict.Message);

        var line = MutationProofPinGuard.FormatRunRecord(new MutationProofPinGuard.RunRecord(
            "2026-07-20T01:02:03.0000000Z", 4242, "net10.0", "D:/ReposFred/_wt/w-example",
            "cj_example", noPin, tree, verdict));

        Assert.Contains("headVerified=no", line, StringComparison.Ordinal);
        Assert.Contains("NOT PART OF A PROOF", line, StringComparison.Ordinal);
        Assert.DoesNotContain("VERIFIED - ", line, StringComparison.Ordinal);

        // The observed state is still kept, which is what makes a FORGOTTEN pin answerable after the fact.
        Assert.Contains("src/InProgress.cs", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The record must not live where the evidence died last time. A worktree is removed after its work
    /// merges - correct hygiene - and anything stored inside it goes with it.
    /// </summary>
    [Fact]
    public void TheLedgerLivesOutsideEveryWorkingTree_SoWorktreeRemovalCannotDestroyIt()
    {
        const string workingTree = "D:/ReposFred/_wt/w-example";

        var directory = MutationProofPinGuard.ComputeLedgerDirectory("C:/Users/example/AppData/Local");
        var ledger = Path.Combine(directory, MutationProofPinGuard.ProofLedgerFileName);
        var allRuns = Path.Combine(directory, MutationProofPinGuard.AllRunsFileName);

        foreach (var path in new[] { ledger, allRuns })
        {
            Assert.DoesNotContain(
                workingTree,
                path.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_wt", path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

            // And not in the git directory either, which is where the PIN lives. The pin is per-worktree
            // and dies with it, correctly; the ledger has the opposite requirement and must not.
            Assert.DoesNotContain(".git", path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        // And it is one file for the whole machine, so "every proof run ever taken here" is one read
        // rather than a hunt through directories that no longer exist. Compared with separators
        // normalized, because the directory is spelled with the separator the caller supplied and the
        // combine uses the platform's.
        Assert.Equal(
            directory.Replace('\\', '/'),
            Path.GetDirectoryName(ledger)!.Replace('\\', '/'));
    }

    /// <summary>
    /// The ledger's identity must not move with whoever launched the run, for the same reason the per-user
    /// suite lock's did not: a baseline and its arm writing to two different ledgers would each look like
    /// the only run that ever happened.
    /// </summary>
    [Fact]
    public void TheLedgerDoesNotMoveWithTheWorkingTreeThatWroteToIt()
    {
        // One rule on every platform, and no branch on the operating system - the branch is what diverged
        // from the writer last time.
        Assert.Equal(
            MutationProofPinGuard.ComputeLedgerDirectory("C:/Users/example/AppData/Local"),
            MutationProofPinGuard.ComputeLedgerDirectory("C:/Users/example/AppData/Local"));

        Assert.Contains(
            "cc-director",
            MutationProofPinGuard.ComputeLedgerDirectory("/home/example/.local/share").Replace('\\', '/'),
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------------

    private static MutationProofPinGuard.PinReading BaselinePinAt(string head) =>
        MutationProofPinGuard.PinReading.Active(
            new MutationProofPinGuard.ProofPin(
                "proof-42", MutationProofPinGuard.BaselinePhase, head, "2026-07-20T00:00:00Z",
                Array.Empty<string>(), "test", "C:/pins/example.pin"));

    private static MutationProofPinGuard.PinReading ArmPinAt(string head, params string[] mutations) =>
        MutationProofPinGuard.PinReading.Active(
            new MutationProofPinGuard.ProofPin(
                "proof-42", MutationProofPinGuard.ArmPhase, head, "2026-07-20T00:00:00Z",
                mutations, "test", "C:/pins/example.pin"));

    /// <summary>A scratch directory that removes itself.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        // THE CANONICAL TEMPORARY PATH, not the raw one, and this unit is the reason it matters most.
        // These tests compare the pin location the SCRIPT derives (by asking git) against the one the
        // GUARD derives (by combining onto the repository root). On macOS Path.GetTempPath answers
        // /var/folders/... while /var is a symbolic link to /private/var, and git always answers with the
        // resolved spelling - so the two derivations disagreed on the STRING while naming the very same
        // file, and this unit's own arming check failed for a reason that had nothing to do with arming.
        // Resolving the fixture root once means both sides are asked about one spelling of one directory.
        // (CcDirector.Core.Tests.TestTempRoot: this file is compiled into that assembly alone - see
        // Directory.Build.props - so the helper is in scope.)
        public string Path { get; } = System.IO.Path.Combine(
            CcDirector.Core.Tests.TestTempRoot.Canonical(System.IO.Path.GetTempPath()),
            "cc-mutation-pin-tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => DeleteTree(Path);

        /// <summary>
        /// Git marks its object files read-only, which makes a plain recursive delete throw on Windows, so
        /// the attributes are cleared first. Failure to clean up is swallowed: a leftover directory under
        /// the temporary path is untidy, whereas a test that fails during cleanup reports a defect that is
        /// not there.
        /// </summary>
        internal static void DeleteTree(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return;

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);

                Directory.Delete(path, recursive: true);
            }
            catch (Exception)
            {
                // Cleanup only. See the remarks above.
            }
        }
    }

    /// <summary>
    /// A real git repository in a scratch directory.
    ///
    /// Real, rather than a hand-written status string, because the properties under test are properties of
    /// what GIT actually reports - the exact status codes, the exact path spelling, the difference between
    /// a modification and a move of the head. A fixture built from what the author believed git emits would
    /// test the author's belief.
    /// </summary>
    private sealed class TemporaryGitRepository : IDisposable
    {
        private readonly TemporaryDirectory _scratch;

        public string Root { get; }

        /// <summary>The directory holding the tree, so a second worktree can be created beside it.</summary>
        public string ScratchPath => _scratch.Path;

        private TemporaryGitRepository(TemporaryDirectory scratch)
        {
            _scratch = scratch;
            Root = Path.Combine(scratch.Path, "tree");
            Directory.CreateDirectory(Root);
        }

        public static TemporaryGitRepository Create()
        {
            var repository = new TemporaryGitRepository(new TemporaryDirectory());
            repository.Git("init --initial-branch=main");
            repository.Git("config user.email tests@example.invalid");
            repository.Git("config user.name Tests");
            repository.Git("config commit.gpgsign false");
            return repository;
        }

        public void Write(string relativePath, string content)
        {
            var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Delete(string relativePath) =>
            File.Delete(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        /// <summary>
        /// Stages ONLY the named path. An earlier draft used "git add --all", which quietly committed
        /// whatever else the test had already left in the tree - so the mid-rework control ended up
        /// asserting against a tree far cleaner than the one it meant to build. A fixture that tidies up
        /// behind the test destroys the condition under test.
        /// </summary>
        public void WriteAndCommit(string relativePath, string content, string message)
        {
            Write(relativePath, content);
            Git("add -- \"" + relativePath + "\"");
            Git("commit -m \"" + message + "\"");
        }

        public string Head() => Git("rev-parse HEAD").Trim().ToLowerInvariant();

        /// <summary>Runs git in a nominated working tree - used to ask git about a second worktree.</summary>
        public string GitIn(string workingDirectory, string arguments)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("git")
            {
                Arguments = "-C \"" + workingDirectory + "\" " + arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "git " + arguments + " failed in '" + workingDirectory + "': " + error);
            }

            return output;
        }

        public string Git(string arguments)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("git")
            {
                Arguments = "-C \"" + Root + "\" " + arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "git " + arguments + " failed in the test repository with exit code "
                    + process.ExitCode + ": " + error);
            }

            return output;
        }

        public void Dispose() => _scratch.Dispose();
    }
}
