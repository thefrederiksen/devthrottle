using System.Runtime.InteropServices;
using CcDirector.Core.UnixPty;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

/// <summary>
/// The CONTRACT <see cref="ProcessRunner"/> has always promised, re-proved on the posix_spawn path
/// that now carries it on macOS and Linux (devthrottle#4577).
///
/// The fork counter next door proves the MECHANISM changed. It says nothing about whether the new
/// mechanism still does the job, and a rewrite of how a process is started can break things a fork
/// counter cannot see: an exit code read out of the wrong field, a pipe that deadlocks at 64 KB, a
/// child left as a zombie because nobody collected it, a descriptor leaked on every call until the
/// Director runs out of them. Each test here is one of those.
///
/// NOT ONE OF THESE WAITS ON THE CLOCK. Every answer is read from a process that has finished, or
/// from the operating system's own tables - a test that decided on a sleep would be measuring the
/// machine, and on a busy machine would fail for reasons that are not defects.
/// </summary>
public sealed class ProcessRunnerPosixSpawnContractTests
{
    private const string Sh = "/bin/sh";

    /// <summary>This process's own process group, so the child's can be shown to differ from it.</summary>
    [DllImport("libc")]
    private static extern int getpgrp();

    private static Task<ProcessRunner.Result> RunAsync(string file, string[] args, string? cwd = null, CancellationToken ct = default)
        => ProcessRunner.RunAsync(file, args, cwd, ct);

    [UnixOnlyFact("the posix_spawn path is what this proves; Windows is unchanged and still uses the Process class.")]
    public async Task AZeroExit_IsReportedWithItsOutput()
    {
        var r = await RunAsync("/bin/echo", new[] { "hello world" });

        Assert.True(r.Started);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("hello world", r.StandardOutput.Trim());
        Assert.Equal("", r.StandardError);
    }

    /// <summary>
    /// The exit code is the child's own number, not a pass/fail verdict. Commands like
    /// <c>git merge-base --is-ancestor</c> answer through the exit status, so losing the number
    /// loses the answer.
    /// </summary>
    [UnixOnlyFact("the posix_spawn path is what this proves; Windows is unchanged.")]
    public async Task ANonZeroExit_KeepsTheChildsOwnNumber()
    {
        var r = await RunAsync(Sh, new[] { "-c", "exit 7" });

        Assert.True(r.Started);
        Assert.Equal(7, r.ExitCode);
    }

    /// <summary>
    /// A signalled child reports 128 plus the signal, which is what the Process class reported on
    /// Unix and what a shell shows for the same thing. Read out of the wrong half of the wait
    /// status this comes back as 0 - a killed child reported as a success.
    /// </summary>
    [UnixOnlyFact("the posix_spawn path is what this proves; Windows is unchanged.")]
    public async Task AChildKilledBySignal_Reports128PlusTheSignal()
    {
        var r = await RunAsync(Sh, new[] { "-c", "kill -9 $$" });

        Assert.True(r.Started);
        Assert.Equal(137, r.ExitCode);
    }

    [UnixOnlyFact("the posix_spawn path is what this proves; Windows is unchanged.")]
    public async Task StandardErrorAndStandardOutput_ArriveSeparately()
    {
        var r = await RunAsync(Sh, new[] { "-c", "echo out; echo err >&2" });

        Assert.Equal("out", r.StandardOutput.Trim());
        Assert.Equal("err", r.StandardError.Trim());
    }

    /// <summary>
    /// HAZARD 1 OF ISSUE 516, on the new path. A pipe holds about 64 KB. Reading one stream to end
    /// before the other lets the child block writing to the second while the parent waits on the
    /// first, and neither can move. Both streams here carry 200 KB AT THE SAME TIME, so a drain
    /// that is not concurrent does not fail this test - it hangs it, and the watchdog names it.
    /// </summary>
    [UnixOnlyFact("the posix_spawn path is what this proves; Windows is unchanged.")]
    public async Task BothPipesFarPastTheBufferSize_DoNotDeadlock()
    {
        const int bytes = 200_000;
        using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        var r = await RunAsync(Sh, new[]
        {
            "-c",
            $"head -c {bytes} /dev/zero | tr '\\0' 'o' & head -c {bytes} /dev/zero | tr '\\0' 'e' >&2; wait",
        }, cwd: null, watchdog.Token);

        Assert.True(r.Started);
        Assert.Equal(bytes, r.StandardOutput.Length);
        Assert.Equal(bytes, r.StandardError.Length);
        // And neither stream picked up the other's bytes, which a crossed dup2 would show as a mix.
        Assert.DoesNotContain('e', r.StandardOutput);
        Assert.DoesNotContain('o', r.StandardError);
    }

    /// <summary>
    /// Standard input exists so a caller can hand a child something that must never sit in an
    /// argument list or an environment variable, where any process on the machine can read it. The
    /// write end must also be CLOSED afterwards, or the child waits for input that is not coming -
    /// which is why this test runs <c>cat</c>, a program that only ends when its input does.
    /// </summary>
    [UnixOnlyFact("the posix_spawn path is what this proves; Windows is unchanged.")]
    public async Task StandardInput_IsDeliveredAndThenClosed()
    {
        var r = await ProcessRunner.RunAsync(
            "/bin/cat", Array.Empty<string>(), workingDirectory: null,
            standardInput: "a secret\nsecond line\n", CancellationToken.None);

        Assert.True(r.Started);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("a secret\nsecond line", r.StandardOutput.TrimEnd('\n'));
    }

    /// <summary>
    /// Asked by its CONTENTS rather than by its path. macOS hands out temp directories beneath a
    /// symlinked /var, so the name the test was given and the name the child prints for the same
    /// directory are different strings - comparing them would fail on a correct implementation.
    /// A file only this directory holds answers the real question, on every platform.
    /// </summary>
    [UnixOnlyFact("the posix_spawn path is what this proves; Windows is unchanged.")]
    public async Task TheWorkingDirectory_IsTheChildsWorkingDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("ccd-spawn-cwd-");
        try
        {
            var marker = "marker-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(dir.FullName, marker), "");

            var r = await RunAsync("/bin/ls", Array.Empty<string>(), dir.FullName);

            Assert.True(r.Started);
            Assert.Equal(0, r.ExitCode);
            Assert.Equal(marker, r.StandardOutput.Trim());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// THE CLOSE-ON-EXEC FLAG IS REALLY SET. A regression test for a defect found while writing
    /// these, and the reason <see cref="UnixNativeMethods.SetCloseOnExec"/> exists as its own
    /// function: <c>fcntl</c> is a VARIADIC C function, and declared the plain way its third
    /// argument never reaches it on Apple Silicon, where a variadic argument has to arrive on the
    /// stack rather than in a register. fcntl then set the descriptor's flags from whatever it
    /// found on the stack and RETURNED SUCCESS, so nothing anywhere reported a problem - while
    /// every child went on inheriting the parent's pipe ends, the write end of its own standard
    /// input among them. A child whose standard input is still held open by its parent never sees
    /// end of input: <c>cat</c> ran forever and the call never returned.
    ///
    /// This asks the operating system to read the flag back, which is the only answer that cannot
    /// be faked by a call that merely did not fail. F_GETFD takes no third argument at all, so
    /// reading is not exposed to the hazard that broke writing.
    /// </summary>
    [UnixOnlyFact("close-on-exec and fcntl are POSIX; Windows inherits handles by an unrelated mechanism.")]
    public void SetCloseOnExec_ActuallyMarksTheDescriptor()
    {
        const int F_GETFD = 1;
        const int FD_CLOEXEC = 1;

        var fds = new[] { -1, -1 };
        Assert.Equal(0, UnixNativeMethods.pipe(fds));
        try
        {
            // A fresh pipe is NOT close-on-exec, which is what makes the next assertion mean
            // something: it shows the flag changed rather than having been set all along.
            Assert.Equal(0, fcntl(fds[0], F_GETFD) & FD_CLOEXEC);

            Assert.True(UnixNativeMethods.SetCloseOnExec(fds[0]));

            Assert.Equal(FD_CLOEXEC, fcntl(fds[0], F_GETFD) & FD_CLOEXEC);
        }
        finally
        {
            UnixNativeMethods.close(fds[0]);
            UnixNativeMethods.close(fds[1]);
        }
    }

    /// <summary>F_GETFD takes two arguments, so this declaration is exact rather than variadic.</summary>
    [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int fcntl(int fd, int cmd);

    /// <summary>
    /// posix_spawn REPLACES the child's environment rather than adding to it, so the merge has to
    /// happen in the parent - and a merge written the other way round hands the child only the
    /// caller's few variables and no PATH at all. Both halves are asserted: the variable the caller
    /// added, and one it never mentioned that the child must still have.
    /// </summary>
    [UnixOnlyFact("the posix_spawn path is what this proves; Windows is unchanged.")]
    public async Task TheCallersEnvironment_IsAddedToTheInheritedOne_NotSubstitutedForIt()
    {
        var r = await ProcessRunner.RunAsync(
            Sh, new[] { "-c", "echo \"$CCD_SPAWN_PROBE|$PATH\"" }, workingDirectory: null,
            standardInput: null,
            environment: new Dictionary<string, string> { ["CCD_SPAWN_PROBE"] = "set-by-the-caller" },
            CancellationToken.None);

        Assert.True(r.Started);
        var parts = r.StandardOutput.Trim().Split('|');
        Assert.Equal("set-by-the-caller", parts[0]);
        Assert.False(string.IsNullOrWhiteSpace(parts[1]));
        // And this process's own environment was not changed by starting that child.
        Assert.Null(Environment.GetEnvironmentVariable("CCD_SPAWN_PROBE"));
    }

    /// <summary>
    /// A PROGRAM ON A FOLDER ADDED TO PATH AT RUN TIME IS STILL FOUND.
    ///
    /// posix_spawnp searches the C library's PATH - and that is NOT the PATH this process thinks it
    /// has. <c>Environment.SetEnvironmentVariable</c> writes only the managed copy; .NET never
    /// pushes it down to the native environment. So spawning by bare name through posix_spawnp
    /// cannot see a folder added that way, while the Process class it replaced resolved the name in
    /// managed code and always could.
    ///
    /// The Director does exactly this: <c>GitHubSetup.AddFoundFoldersToProcessPath</c> adds the
    /// folders git and gh were found in after an install, and FleetToolPathRepair, ToolShimPath and
    /// BrowserHarnessInstaller rewrite PATH the same way. Without resolving in the parent, every
    /// caller passing a bare name reported "git is not installed on this machine" on a Director
    /// whose git lived only in such a folder - which is the Finder-launched Mac. Measured during
    /// review of devthrottle#4577: this test fails with code 2 against posix_spawnp, and passes
    /// against the Process class it replaced.
    /// </summary>
    [UnixOnlyFact("the posix_spawn path is what this proves; Windows is unchanged.")]
    public async Task ABareNameOnAFolderAddedToPathAtRunTime_IsStillFound()
    {
        var dir = Directory.CreateTempSubdirectory("ccd-spawn-path-");
        var name = "ccd-path-probe-4577";
        try
        {
            var script = Path.Combine(dir.FullName, name);
            await File.WriteAllTextAsync(script, "#!/bin/sh\necho found-on-the-added-folder\n");
            // The attribute keeps this off Windows, but the compiler checks the call site rather
            // than the attribute, so the guard has to be written out.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var pathBefore = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", dir.FullName + Path.PathSeparator + pathBefore);
            try
            {
                var r = await RunAsync(name, Array.Empty<string>());

                Assert.True(r.Started, $"not started, code {r.StartErrorCode}: {r.StandardError}");
                Assert.Equal("found-on-the-added-folder", r.StandardOutput.Trim());
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", pathBefore);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A machine with no git is a supported machine (devthrottle_internal issue #1048). The code
    /// must be ENOENT - the one number that means the same thing on every platform - because the
    /// callers that say "not installed" read the code and never the message text.
    /// </summary>
    [UnixOnlyFact("the posix_spawn path is what this proves; Windows is unchanged.")]
    public async Task AMissingExecutable_IsNotStartedAndCarriesEnoent()
    {
        var r = await RunAsync("/no/such/executable-ccd-4577", new[] { "--version" });

        Assert.False(r.Started);
        Assert.Equal(-1, r.ExitCode);
        Assert.Equal(2, r.StartErrorCode);

        // THE OPERATING SYSTEM'S REASON, AND NOTHING WRAPPED AROUND IT. Callers put this inside a
        // sentence of their own, so a sentence here too reads as a stutter - see the GitCommandRunner
        // test below, which is the regression guard for having done exactly that.
        Assert.Equal("No such file or directory", r.StandardError);
    }

    /// <summary>
    /// HAZARD 2 OF ISSUE 516, proved as a PROPERTY rather than as a race. What has to be true is
    /// that cancelling a call can reach a GRANDCHILD - a stuck credential prompt, a stalled network
    /// read - and not just the child. The runner achieves that by making the child the leader of a
    /// process group of its own and signalling the whole group, and POSIX then guarantees the rest:
    /// a process inherits its parent's process group, so every descendant is in the group that one
    /// kill reaches.
    ///
    /// So this asserts the guarantee's PRECONDITION, which is a fact about a process that has
    /// already finished: the child's process group id is its own pid, a grandchild is in that same
    /// group, and the group is NOT this test process's own - which is what it would be if the flag
    /// had silently not been applied. Watching a grandchild die instead would mean sleeping and
    /// polling, and would decide a pass on how busy the machine was.
    /// </summary>
    [UnixOnlyFact("process groups and posix_spawn are POSIX; Windows is unchanged.")]
    public async Task TheChildLeadsItsOwnProcessGroup_AndItsGrandchildrenAreInIt()
    {
        // Line 1: the child's own pid and process group. Line 2: a real grandchild's process group.
        var r = await RunAsync(Sh, new[]
        {
            "-c", "ps -o pid=,pgid= -p $$; sleep 30 & ps -o pgid= -p $!; kill $! 2>/dev/null",
        });

        Assert.True(r.Started);
        var lines = r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        var childFields = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var childPid = int.Parse(childFields[0]);
        var childGroup = int.Parse(childFields[1]);
        var grandchildGroup = int.Parse(lines[1].Trim());

        Assert.Equal(childPid, childGroup);          // the child LEADS the group
        Assert.Equal(childPid, grandchildGroup);     // and the grandchild is in it
        Assert.NotEqual(getpgrp(), childGroup);      // and it is not merely this process's group
    }

    /// <summary>
    /// Cancelling throws and does not hang. The token here is cancelled BEFORE the call, so the
    /// cancellation lands at a known point every time rather than depending on how long a child
    /// happened to live - the child is still started, still signalled, and still collected.
    /// </summary>
    [UnixOnlyFact("the posix_spawn path is what this proves; Windows is unchanged.")]
    public async Task ACancelledCall_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(Sh, new[] { "-c", "sleep 30" }, cwd: null, cts.Token));
    }

    /// <summary>
    /// Taking over how a process is STARTED means taking over how it ENDS. The Process class
    /// collected its children's exit statuses; code that spawns directly has to do that itself, and
    /// if it does not, every single call leaves a zombie in the process table for the life of the
    /// Director - thousands a day on the git path alone, and the crash this fix is for happened on
    /// a machine already short of memory.
    /// </summary>
    [UnixOnlyFact("the posix_spawn path is what this proves; Windows is unchanged.")]
    public async Task TwoHundredChildren_LeaveNoZombiesBehind()
    {
        for (var i = 0; i < 200; i++)
            Assert.Equal(0, (await RunAsync("/bin/echo", new[] { i.ToString() })).ExitCode);

        var mine = Environment.ProcessId.ToString();
        var ps = await RunAsync("/bin/ps", new[] { "-A", "-o", "ppid=,stat=" });
        Assert.True(ps.Started);

        var zombies = ps.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length >= 2 && f[0] == mine && f[1].StartsWith('Z'))
            .ToList();

        Assert.Empty(zombies);
    }

    /// <summary>
    /// Every start creates three pipes, six descriptors. Any one of them neither handed to a stream
    /// nor closed is a leak on every call, and a process that runs out of descriptors fails in ways
    /// that look like anything but this. Fifty starts would leak at least a hundred, which is past
    /// the default per-process limit on macOS - so a leak here does not merely raise a number, it
    /// stops the loop.
    /// </summary>
    [UnixOnlyFact("/dev/fd and /proc/self/fd are how a Unix process counts its own descriptors.")]
    public async Task FiftyChildren_LeakNoFileDescriptors()
    {
        var before = OpenDescriptorCount();
        Assert.True(before > 0, "could not count this process's open descriptors");

        for (var i = 0; i < 50; i++)
            Assert.Equal(0, (await RunAsync("/bin/echo", new[] { "x" })).ExitCode);

        var after = OpenDescriptorCount();

        // Not an exact match: the runtime opens and closes things of its own between the two reads.
        // The margin is nowhere near the hundred-plus that a per-call leak would show.
        Assert.True(after - before <= 4, $"open descriptors went from {before} to {after}");
    }

    private static int OpenDescriptorCount()
    {
        var dir = OperatingSystem.IsMacOS() ? "/dev/fd" : "/proc/self/fd";
        return Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir).Length : -1;
    }
}
