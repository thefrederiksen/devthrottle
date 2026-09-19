using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CcDirector.Core.Instances;
using CcDirector.Core.Setup;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Setup;

/// <summary>
/// The four conditions of the tool-copy sweep, each watched deciding and each watched refusing.
///
/// THEY RUN THROUGH THE PURE CLASSIFIER because that is what the classifier is for: it takes facts and
/// returns a verdict, so "a Director is running there" and "the master is empty" are arguments rather
/// than machine states nobody can produce in a test. A rule that can only be exercised by building the
/// application, starting a Director and arranging a second live process is a rule that gets exercised
/// once.
///
/// WHAT THESE DO NOT COVER, said rather than implied: that the real
/// <see cref="DirectorInstanceLocator"/> answers Running for a real running Director, and that the real
/// <c>HoldsFleetTool</c> answers true for a real master. Those are the two facts fed in here as
/// arguments, and they are proved on a real Director start by scripts/one-tool-path-rig-proof.ps1.
/// </summary>
public class DirectorToolCopySweepTests
{
    // BUILT WITH Path.Combine, NEVER WRITTEN AS A WINDOWS LITERAL. The rules are written for Windows,
    // macOS and Linux, and a test whose input is "C:\root\instances\default" proves nothing on a machine
    // where a backslash is an ordinary character in a file name - it would quietly pass by never finding
    // a parent at all. These names are composed the same way the product composes them.
    private static readonly string MachineRoot = Path.Combine(Path.GetTempPath(), "cc-sweep-classify-root");
    private static readonly string DirectorHome = Path.Combine(MachineRoot, "instances", "default");
    private static readonly string OtherDirectorHome = Path.Combine(MachineRoot, "instances", "slot-1");

    /// <summary>A real one off the owner's machine, so the shape is observed rather than invented.</summary>
    private const string RetiredInterpreter = "python.old-006087698e3c4ab0b32640f334eefc0d";

    private static OwningDirectorState Idle => new(DirectorResolution.NotRunning, false);
    private static OwningDirectorState Own => new(DirectorResolution.NotRunning, true);

    private static SweepVerdict Classify(string candidate, OwningDirectorState? owner, bool masterAlive = true)
        => DirectorToolCopySweep.Classify(candidate, MachineRoot, masterAlive, owner);

    // ---------------------------------------------------------------------------------------------
    // The positives: what the sweep is FOR. Without these the tests below would pass on a classifier
    // that deletes nothing at all, which is the shape a lean-to-keep rule fails into.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("bin")]
    [InlineData("pyenv")]
    [InlineData("python")]
    [InlineData(RetiredInterpreter)]
    public void ACopyInsideAnIdleDirectorFolderIsDeleted(string name)
    {
        var verdict = Classify(Path.Combine(DirectorHome, name), Idle);

        Assert.Equal(SweepAction.Delete, verdict.Action);
        Assert.Contains("no Director is running there", verdict.Reason);
    }

    [Fact]
    public void TheSweepingDirectorsOwnCopyIsDeleted_AndTheReasonSaysWhichConditionAllowedIt()
    {
        // The one exception to condition 4, and it has to name itself: "no Director is running there"
        // would be a false sentence about the folder of a Director that is demonstrably running.
        var verdict = Classify(Path.Combine(DirectorHome, "bin"), Own);

        Assert.Equal(SweepAction.Delete, verdict.Action);
        Assert.Contains("it belongs to the Director doing the sweep", verdict.Reason);
    }

    [Fact]
    public void ARetiredInterpreterInTheMachineRootIsDeleted()
    {
        // It has no owning Director, so condition 4 does not apply - passing no state must not make it
        // survive, or the machine root's copies could never be reclaimed at all.
        var verdict = Classify(Path.Combine(MachineRoot, RetiredInterpreter), owner: null);

        Assert.Equal(SweepAction.Delete, verdict.Action);
        Assert.Contains("retired interpreter in the machine root", verdict.Reason);
    }

    [Fact]
    public void ANestedDirectorFoldersCopyIsDeleted()
    {
        // instances/default/instances/default is a Director folder BY SHAPE. It is reached by walking
        // instances folders, never by searching for anything named bin.
        var nested = Path.Combine(MachineRoot, "instances", "default", "instances", "default");
        Assert.True(CcStorage.IsDirectorInstanceHome(nested));

        var verdict = Classify(Path.Combine(nested, "bin"), Idle);

        Assert.Equal(SweepAction.Delete, verdict.Action);
    }

    // ---------------------------------------------------------------------------------------------
    // CONDITION 2 - the name allow-list. MUTATION 1: accept any directory name inside a Director
    // folder, and these go red.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("bin-old")]
    [InlineData("binaries")]
    [InlineData("pyenv2")]
    [InlineData("pythonista")]
    [InlineData("python.old-notthirtytwo")]
    [InlineData("python.old-zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("scripts")]
    public void ANearMissNameInsideADirectorFolderIsKept(string name)
    {
        // Every one of these is a directory a person could reasonably have put there. The retired
        // interpreter shape is 32 HEXADECIMAL characters: a name that is 32 letters and is not hex
        // survives, and that survival is the boundary working rather than leaking.
        var verdict = Classify(Path.Combine(DirectorHome, name), Idle);

        Assert.Equal(SweepAction.Keep, verdict.Action);
        Assert.Contains("the name is not bin, pyenv, python", verdict.Reason);
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("config")]
    [InlineData("logs")]
    [InlineData("vault")]
    [InlineData("secrets")]
    [InlineData("instances")]
    [InlineData("app")]
    [InlineData("launcher")]
    public void ADataOrProductFolderInsideADirectorFolderIsKept(string name)
    {
        var verdict = Classify(Path.Combine(DirectorHome, name), Idle);

        Assert.Equal(SweepAction.Keep, verdict.Action);
    }

    // ---------------------------------------------------------------------------------------------
    // CONDITION 1 - placement, and DIRECTLY. MUTATION 2: accept any descendant, and this goes red.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("instances", "default", "some-repo", "bin")]
    [InlineData("instances", "default", "app", "bin")]
    [InlineData("instances", "default", "a", "python")]
    [InlineData("logs", "keep", "bin")]
    [InlineData("vault", "bin")]
    public void AFolderThatIsNotDIRECTLYInsideADirectorFolderOrTheRootIsKept(params string[] segments)
    {
        // Two levels down is never a candidate. A bin inside a repository a session has checked out
        // under a Director's folder is the exact file set this must never reach.
        var verdict = Classify(Path.Combine(new[] { MachineRoot }.Concat(segments).ToArray()), Idle);

        Assert.Equal(SweepAction.Keep, verdict.Action);
        Assert.Contains("not directly inside a Director folder or the machine root", verdict.Reason);
    }

    // ---------------------------------------------------------------------------------------------
    // CONDITION 2 in the MACHINE ROOT. MUTATION 3: let the root's own bin/pyenv/python qualify, and
    // this goes red - with the master itself classified as Delete.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("bin")]
    [InlineData("pyenv")]
    [InlineData("python")]
    public void TheMachineRootsOwnToolFoldersAreTheMasterAndAreNeverCandidates(string name)
    {
        var verdict = Classify(Path.Combine(MachineRoot, name), owner: null);

        Assert.Equal(SweepAction.Keep, verdict.Action);
        Assert.Contains("master tools folder", verdict.Reason);
    }

    [Theory]
    [InlineData("python.old-notthirtytwo")]
    [InlineData("python.old-zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("instances")]
    [InlineData("app")]
    [InlineData("launcher")]
    [InlineData("sessions")]
    public void AMachineRootChildThatIsNotARetiredInterpreterIsKept(string name)
    {
        var verdict = Classify(Path.Combine(MachineRoot, name), owner: null);

        Assert.Equal(SweepAction.Keep, verdict.Action);
    }

    // ---------------------------------------------------------------------------------------------
    // CONDITION 4 - only NotRunning is permission. MUTATION 4: treat Unknown/Ambiguous as permission,
    // and these go red.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DirectorResolution.Running, "a Director is running there")]
    [InlineData(DirectorResolution.Ambiguous, "more than one live process claims")]
    [InlineData(DirectorResolution.NotSupervised, "is not the installed Director")]
    [InlineData(DirectorResolution.Unknown, "unknown is never permission")]
    public void OnlyNotRunningPermitsADeletion(DirectorResolution resolution, string expectedReason)
    {
        // A question nobody could answer must never read as a yes. Three of these four are exactly
        // that, and the fourth is a live Director whose own tools are in use.
        var verdict = Classify(Path.Combine(OtherDirectorHome, "bin"), new OwningDirectorState(resolution, false));

        Assert.Equal(SweepAction.Keep, verdict.Action);
        Assert.Contains(expectedReason, verdict.Reason);
    }

    [Fact]
    public void ACandidateInADirectorFolderWithNoStateAtAllIsKept()
    {
        // There is no branch a missing state can fall through. It is the same failure direction as
        // Unknown and it gets the same answer.
        var verdict = Classify(Path.Combine(DirectorHome, "bin"), owner: null);

        Assert.Equal(SweepAction.Keep, verdict.Action);
        Assert.Contains("was not established", verdict.Reason);
    }

    // ---------------------------------------------------------------------------------------------
    // CONDITION 3 - the master is alive. MUTATION 5: drop it, and these go red.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("instances", "default", "bin")]
    [InlineData("instances", "default", "pyenv")]
    [InlineData("instances", "default", RetiredInterpreter)]
    [InlineData(RetiredInterpreter)]
    public void WithAnEmptyMasterNothingIsDeletedAnywhere(params string[] segments)
    {
        // The condition that stops this ever becoming a machine with no tools at all. It is asked once,
        // before anything is looked at, so the answer is the same sentence for every candidate - which
        // is what the rig reads back.
        var verdict = Classify(Path.Combine(new[] { MachineRoot }.Concat(segments).ToArray()), Idle, masterAlive: false);

        Assert.Equal(SweepAction.Keep, verdict.Action);
        Assert.Equal("the master holds no cc-devthrottle; nothing was swept anywhere", verdict.Reason);
    }

    // ---------------------------------------------------------------------------------------------
    // The runner, on a throwaway directory. Fast: it creates about thirty small files and deletes some
    // of them. It belongs in the parallel suite because it holds no shared state and reads no clock -
    // every test gets a directory of its own.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheRunnerDeletesTheCopiesAndLeavesEveryBystanderByteForByte()
    {
        using var rig = new Rig();

        rig.Master();
        rig.Dir(rig.Root, "python.old-9f688e2d1fe748d8a9e8c055465eb3ed", "old.txt");
        rig.Dir(rig.Root, "python.old-notthirtytwo", "keep.txt");

        var own = rig.Instance("default");
        var idle = rig.Instance("slot-7");
        var live = rig.Instance("slot-1");
        var nested = rig.NestedInstance("default", "default");

        var result = rig.Sweep(sweepingHome: own, running: new[] { live });

        Assert.True(result.Swept);

        // The copies of an idle Director, the sweeping Director's own, and the nested leak.
        foreach (var gone in new[]
                 {
                     Path.Combine(own, "bin"), Path.Combine(own, "pyenv"), Path.Combine(own, "python"),
                     Path.Combine(idle, "bin"), Path.Combine(nested, "bin"),
                     Path.Combine(rig.Root, "python.old-9f688e2d1fe748d8a9e8c055465eb3ed"),
                 })
            Assert.False(Directory.Exists(gone), $"{gone} should have been removed");

        // The master, the running Director's copies, the near-miss name, and every bystander.
        foreach (var survives in new[]
                 {
                     Path.Combine(rig.Root, "bin"), Path.Combine(rig.Root, "pyenv"), Path.Combine(rig.Root, "python"),
                     Path.Combine(rig.Root, "python.old-notthirtytwo"),
                     Path.Combine(live, "bin"), Path.Combine(live, "pyenv"), Path.Combine(live, "python"),
                     Path.Combine(own, "bin-old"), Path.Combine(own, "binaries"), Path.Combine(own, "pyenv2"),
                     Path.Combine(own, "sessions"), Path.Combine(own, "config"), Path.Combine(own, "logs"),
                 })
            Assert.True(Directory.Exists(survives), $"{survives} should have survived");

        Assert.True(File.Exists(Path.Combine(own, "notes.txt")));
    }

    [Fact]
    public void TheRunnerReportsTheCONDITIONThatSavedARunningDirector_NotMerelyThatItSurvived()
    {
        // A folder surviving does not prove the guard that was meant to save it did the saving: it
        // survives identically when nobody looked at it. So the report has to name the condition, AND
        // the folder has to appear in the report at all.
        using var rig = new Rig();
        rig.Master();
        var own = rig.Instance("default");
        var live = rig.Instance("slot-1");

        var result = rig.Sweep(sweepingHome: own, running: new[] { live });

        var row = result.Looked.SingleOrDefault(d => d.Path == Path.Combine(live, "bin"));
        Assert.NotNull(row);
        Assert.Equal(SweepAction.Keep, row!.Action);
        Assert.Equal("a Director is running there", row.Reason);
    }

    [Fact]
    public void TheRunnerWithAnEmptyMasterDeletesNothingAndSaysWhy()
    {
        using var rig = new Rig();
        // No master written at all: the root has no bin, so nothing on this machine can answer.
        var own = rig.Instance("default");

        var result = rig.Sweep(sweepingHome: own, running: Array.Empty<string>());

        Assert.False(result.Swept);
        Assert.Contains("the master holds no cc-devthrottle; nothing was swept anywhere", result.Summary);
        Assert.Empty(result.Looked);
        Assert.True(Directory.Exists(Path.Combine(own, "bin")));
    }

    [Fact]
    public void TheRunnerDoesNothingWhenAnInstallHoldsTheLock()
    {
        using var rig = new Rig();
        rig.Master();
        var own = rig.Instance("default");

        // A name of this test's own, so nothing outside it can be affected and nothing outside it can
        // make this pass or fail.
        //
        // HELD ON ANOTHER THREAD, AND THAT IS NOT CEREMONY. A mutex is reentrant for the thread that
        // owns it, so a holder taken on this thread would be re-acquired by the sweep and this test
        // would pass while proving the opposite of what it claims. The wait is zero so the refusal is
        // immediate rather than a ten-second pause on a suite with a two-minute budget.
        var lockName = "Local\\cc-director-tool-copy-sweep-test-" + Guid.NewGuid().ToString("N");
        using var acquired = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, lockName, out _);
            mutex.WaitOne();
            acquired.Set();
            release.Wait();
            mutex.ReleaseMutex();
        }) { IsBackground = true };
        holder.Start();
        acquired.Wait();

        try
        {
            var result = rig.Sweep(sweepingHome: own, running: Array.Empty<string>(),
                mutexName: lockName, lockWait: TimeSpan.Zero);

            Assert.False(result.Swept);
            Assert.Contains("another install or repair holds the lock; nothing was swept", result.Summary);
            Assert.True(Directory.Exists(Path.Combine(own, "bin")));
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    /// <summary>A throwaway machine root shaped like the owner's, built and torn down per test.</summary>
    private sealed class Rig : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "cc-sweep-" + Guid.NewGuid().ToString("N"));

        public Rig() => Directory.CreateDirectory(Root);

        /// <summary>The master: a bin holding a runnable cc-devthrottle, plus pyenv and python beside it.</summary>
        public void Master()
        {
            var bin = Path.Combine(Root, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, OperatingSystem.IsWindows() ? "cc-devthrottle.cmd" : "cc-devthrottle"),
                "#!/bin/sh\necho master\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Combine(bin, "cc-devthrottle"),
                    UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
            Dir(Root, "pyenv", "bystander.txt");
            Dir(Root, "python", "bystander.txt");
        }

        /// <summary>A Director folder carrying the three copies, a retired interpreter, and bystanders.</summary>
        public string Instance(string name) => Populate(Path.Combine(Root, "instances", name));

        /// <summary>The nested leak: a Director folder inside a Director folder's own instances.</summary>
        public string NestedInstance(string outer, string inner) =>
            Populate(Path.Combine(Root, "instances", outer, "instances", inner));

        private string Populate(string home)
        {
            foreach (var copy in new[] { "bin", "pyenv", "python", "python.old-e510f5f175124e9aaa127e317582387d" })
                Dir(home, copy, "tool.txt");
            foreach (var bystander in new[] { "bin-old", "binaries", "pyenv2" })
                Dir(home, bystander, "mine.txt");
            Dir(home, "sessions", "s1.json");
            Dir(home, "config", "settings.json");
            Dir(home, "logs", "d.log");
            File.WriteAllText(Path.Combine(home, "notes.txt"), "a loose file that is nothing to do with the tools");
            return home;
        }

        public void Dir(string parent, string name, string file)
        {
            var dir = Path.Combine(parent, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, file), $"contents of {name}/{file}");
        }

        public ToolCopySweepResult Sweep(string sweepingHome, IReadOnlyCollection<string> running,
            string? mutexName = null, TimeSpan? lockWait = null)
            => DirectorToolCopySweep.Sweep(
                machineRoot: Root,
                masterBinDir: Path.Combine(Root, "bin"),
                sweepingDirectorInstanceHome: sweepingHome,
                heavyRepairMutexName: mutexName
                    ?? "Local\\cc-director-tool-copy-sweep-test-" + Guid.NewGuid().ToString("N"),
                lockWait: lockWait,
                resolveDirector: home => running.Any(r => string.Equals(
                    Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                    ? DirectorResolution.Running
                    : DirectorResolution.NotRunning);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* a leftover temp directory is not a test failure */ }
        }
    }
}
