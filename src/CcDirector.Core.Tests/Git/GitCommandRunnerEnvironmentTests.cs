using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// <c>GIT_OPTIONAL_LOCKS=0</c> REACHES THE GIT PROCESS, read back out of the child itself.
///
/// WHY THIS IS NOT COVERED BY THE GUARD THAT ALREADY EXISTS.
/// <c>BackgroundGitLaunchesTakeNoOptionalLocksTests</c> scans the source of every file that starts
/// git and fails one that does not MENTION <c>GitOptionalLocks</c>. That catches a file which forgot
/// the setting. It cannot catch a file that mentions it and still does not deliver it - and that is
/// the mistake that was actually available here: <see cref="GitCommandRunner"/> used to set the
/// variable on its own <c>ProcessStartInfo</c>, and when it was routed through
/// <see cref="Core.Utilities.ProcessRunner"/> for devthrottle#4577 the setting had to be handed over
/// as the child's environment instead. Pass the wrong argument, or the overload without an
/// environment, and every reference to GitOptionalLocks still reads correctly while the variable
/// reaches nothing.
///
/// What it cost the last time it was missing (issue #2872): a killed <c>git status</c> cannot remove
/// the <c>index.lock</c> it holds, so a 0-byte lock file stayed behind for good and every later
/// commit, pull, checkout, rebase and worktree removal in that checkout failed with "File exists" -
/// while status itself kept working, so nothing in the Director showed it. Six weeks in one checkout.
///
/// So this test asks the CHILD what it was given. The executable seam the class already carries for
/// its missing-git tests is what makes that possible: a shell in git's place prints the variable's
/// value, and the value is the proof.
/// </summary>
public sealed class GitCommandRunnerEnvironmentTests
{
    [UnixOnlyFact("a shell standing in for git is how the child's own environment is read back; the source-text guard covers every platform.")]
    public async Task EveryGitCommand_IsGivenGitOptionalLocksOff()
    {
        var runner = new GitCommandRunner("/bin/sh");

        var result = await runner.RunAsync(
            Directory.GetCurrentDirectory(),
            new[] { "-c", $"echo \"[${GitOptionalLocks.VariableName}]\"" });

        Assert.True(result.Success, result.Error);
        // The brackets are what tell "the variable was 0" apart from "the variable was not set at
        // all", which an empty expansion would otherwise print identically.
        Assert.Equal("[0]", result.Output);
    }

    /// <summary>
    /// A LAUNCH FAILURE IS ONE SENTENCE, NOT TWO NESTED ONES.
    ///
    /// <see cref="GitLaunchFailure.Describe"/> writes "git could not be started: " and then the
    /// reason it was handed. When ProcessRunner's own failure text also introduced itself, the
    /// result read "git could not be started: /path/to/thing could not be started: Permission
    /// denied" - the same clause twice, in a sentence shown on the Worktrees page and by the branch
    /// services. Found in review of devthrottle#4577; ENOENT was the one code that escaped it,
    /// because Describe replaces the reason entirely for that one.
    ///
    /// A file that exists but cannot be executed is how a non-ENOENT launch failure is reached
    /// without depending on anything about the machine.
    /// </summary>
    [UnixOnlyFact("file modes and the EACCES launch failure are POSIX; Windows refuses for other reasons.")]
    public async Task ALaunchFailureThatIsNotAMissingFile_SaysItOnce()
    {
        var dir = Directory.CreateTempSubdirectory("ccd-git-noexec-");
        try
        {
            var notExecutable = Path.Combine(dir.FullName, "notgit");
            await File.WriteAllTextAsync(notExecutable, "#!/bin/sh\necho hello\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(notExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var result = await new GitCommandRunner(notExecutable)
                .RunAsync(Directory.GetCurrentDirectory(), new[] { "--version" });

            Assert.False(result.Success);
            Assert.Equal("git could not be started: Permission denied", result.Error);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
