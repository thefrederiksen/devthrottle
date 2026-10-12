using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Issue 516: a superseded scan must be able to cancel its git children, and git commands must
/// drain both pipes and honor cancellation. These prove the token is threaded through and honored,
/// and that a cancelled git command kills its child process tree rather than orphaning it.
///
/// MARKED FOR QUARANTINE (Reliable Tests, 11 October 2026): it runs real git processes, which gate tests may no longer
/// do, but it holds the issue 516 regression tests, among them the only test of GitCommandRunner's own kill of a child process tree, so it is kept for a run of its own whose result never decides a release. The gate's filter on this
/// trait comes with a later change; until it does, the class still runs in the release gate (-Parked).
/// </summary>
[Trait("Gate", "Quarantine")]
public sealed class GitProcessCancellationTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;

    public GitProcessCancellationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-gitcancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _repo = Path.Combine(_root, "repo");
        RunGit(_root, "-c", "init.defaultBranch=main", "init", _repo);
        RunGit(_repo, "config", "user.email", "test@cc-director.local");
        RunGit(_repo, "config", "user.name", "CC Director Test");
        RunGit(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "init\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "initial");
    }

    public void Dispose()
    {
        // The fixture holds a real git repository, whose objects are read-only; the shared delete clears the
        // attribute first and reports a failure instead of retrying and swallowing it, which leaked a folder per
        // test for as long as the swallow stood.
        TestTempRoot.DeleteTree(_root);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): cancelling GitCommandRunner's process wait must KILL the child tree -
    // disposing the Process does not. A git command whose editor child sleeps then writes a marker
    // is cancelled mid-sleep; the marker must never appear, proving the child was killed.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task GitCommandRunner_Cancelled_KillsTheChildProcessTree()
    {
        var marker = Path.Combine(_root, "hook-ran.txt").Replace('\\', '/');
        // A pre-commit hook that sleeps, then touches the marker. Hooks run under git's own shell,
        // so this is a reliable long-running child of the git process. git commit runs it and waits.
        var hook = Path.Combine(_repo, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\nsleep 5\ntouch \"" + marker + "\"\n".Replace("\r\n", "\n"));
        // MAKE THE HOOK EXECUTABLE, OR THIS TEST PROVES NOTHING OUTSIDE WINDOWS. git on macOS and Linux
        // SKIPS a hook without the executable bit - it says so on stderr - so the commit finished
        // instantly, the cancellation had nothing to cancel, and the test failed on the missing exception
        // rather than on the behaviour it was written to pin. git for Windows runs the hook regardless of
        // the bit, which is why this went unnoticed there.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        using var cts = new CancellationTokenSource();
        var run = new GitCommandRunner().RunAsync(_repo, new[] { "commit", "--allow-empty", "-m", "x" }, cts.Token);

        await Task.Delay(1500); // git has launched the hook and it is in its sleep
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);

        // Past the hook's own sleep: had the tree NOT been killed, the marker would exist by now.
        await Task.Delay(5000);
        Assert.False(File.Exists(marker.Replace('/', Path.DirectorySeparatorChar)),
            "the hook child must have been killed before it could write the marker");
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): the status count probe now accepts and honors a cancellation token -
    // the old signature took none, so a superseded scan could not stop it.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task GitStatusProvider_GetCountAsync_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new GitStatusProvider().GetCountAsync(_repo, cts.Token));
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): the sync-status probe now accepts and honors a cancellation token,
    // and (via ProcessRunner) drains stderr - the old code took no token and never drained stderr.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task GitSyncStatusProvider_GetSyncStatusAsync_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new GitSyncStatusProvider().GetSyncStatusAsync(_repo, cts.Token));
    }

    private static void RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
    }
}
