using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// The upstream-gone probe answers from the local remote-tracking ref and never leaves the
/// machine. Each test builds a real bare "origin", a working clone, and a SECOND clone that
/// stands in for "someone else" changing the remote - so the working clone's tracking refs can
/// be genuinely stale, exactly as they are on a developer's machine between fetches.
///
/// Before 22 September 2026 the probe ran <c>git ls-remote</c> per branch per inventory. The
/// first and last tests fail against that code: one records every git command the probe runs
/// and refuses any that reaches for the network; the other points origin at a path that does
/// not exist, which the old probe reported as "could not inspect".
/// </summary>
public sealed class ConfiguredUpstreamProbeTests : IDisposable
{
    private readonly string _root;
    private readonly string _origin;
    private readonly string _work;
    private readonly string _elsewhere;

    public ConfiguredUpstreamProbeTests()
    {
        _root = TestTempRoot.For("ccd-upstream-probe-");
        Directory.CreateDirectory(_root);
        _origin = Path.Combine(_root, "origin.git");
        _work = Path.Combine(_root, "work");
        _elsewhere = Path.Combine(_root, "elsewhere");

        RunGit(_root, "-c", "init.defaultBranch=main", "init", "--bare", _origin);
        RunGit(_root, "-c", "init.defaultBranch=main", "clone", _origin, _work);
        ConfigureIdentity(_work);
        File.WriteAllText(Path.Combine(_work, "README.md"), "initial\n");
        RunGit(_work, "add", "-A");
        RunGit(_work, "commit", "-m", "initial commit");
        RunGit(_work, "branch", "-M", "main");
        RunGit(_work, "push", "-u", "origin", "main");

        RunGit(_root, "-c", "init.defaultBranch=main", "clone", _origin, _elsewhere);
        ConfigureIdentity(_elsewhere);
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch { Thread.Sleep(100); }
        }
    }

    // ------------------------------------------------------------------------------------------
    // The whole point: no network. A branch deleted on the remote by someone else is "gone" only
    // once this clone has fetched with prune - and finding that out never runs ls-remote.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public async Task UpstreamDeletedElsewhere_IsGoneAfterAFetchPrune_AndTheProbeNeverTouchesTheNetwork()
    {
        RunGit(_work, "checkout", "-b", "feature");
        RunGit(_work, "commit", "--allow-empty", "-m", "feature work");
        RunGit(_work, "push", "-u", "origin", "feature");

        // Someone else deletes the branch on the remote. This clone's tracking ref is now stale.
        RunGit(_elsewhere, "push", "origin", "--delete", "feature");

        var git = new RecordingGitRunner();

        var stale = await ConfiguredUpstreamProbe.ProbeAsync(git, _work, "feature", CancellationToken.None);
        Assert.True(stale.InspectionSucceeded);
        Assert.True(stale.HasConfiguredUpstream);
        // As fresh as the last fetch, by design: git itself still says the upstream exists.
        Assert.False(stale.UpstreamGone);

        RunGit(_work, "fetch", "--prune", "origin");

        var fresh = await ConfiguredUpstreamProbe.ProbeAsync(git, _work, "feature", CancellationToken.None);
        Assert.True(fresh.InspectionSucceeded);
        Assert.True(fresh.HasConfiguredUpstream);
        Assert.True(fresh.UpstreamGone);

        Assert.NotEmpty(git.Commands);
        Assert.DoesNotContain(git.Commands, c => c is "ls-remote" or "fetch" or "remote" or "pull" or "push");
    }

    [Fact]
    public async Task UpstreamStillOnTheRemote_IsNotGone()
    {
        RunGit(_work, "checkout", "-b", "kept");
        RunGit(_work, "commit", "--allow-empty", "-m", "kept work");
        RunGit(_work, "push", "-u", "origin", "kept");

        var verdict = await ConfiguredUpstreamProbe.ProbeAsync(new GitCommandRunner(), _work, "kept", CancellationToken.None);

        Assert.True(verdict.InspectionSucceeded);
        Assert.True(verdict.HasConfiguredUpstream);
        Assert.False(verdict.UpstreamGone);
    }

    [Fact]
    public async Task NeverPushedBranch_HasNoConfiguredUpstream_SoTheQuestionDoesNotApply()
    {
        RunGit(_work, "checkout", "-b", "local-only");

        var verdict = await ConfiguredUpstreamProbe.ProbeAsync(new GitCommandRunner(), _work, "local-only", CancellationToken.None);

        Assert.True(verdict.InspectionSucceeded);
        Assert.False(verdict.HasConfiguredUpstream);
        Assert.False(verdict.UpstreamGone);
    }

    // The upstream ref can carry a different name than the local branch. The probe must follow the
    // CONFIGURED name: the local name never existed on the remote, and reading that as "gone" would
    // mark a live branch merged.
    [Fact]
    public async Task UpstreamWithADifferentName_IsAnsweredForTheConfiguredRef()
    {
        RunGit(_work, "checkout", "-b", "local-name");
        RunGit(_work, "commit", "--allow-empty", "-m", "renamed work");
        RunGit(_work, "push", "-u", "origin", "local-name:refs/heads/remote-name");

        var live = await ConfiguredUpstreamProbe.ProbeAsync(new GitCommandRunner(), _work, "local-name", CancellationToken.None);
        Assert.True(live.HasConfiguredUpstream);
        Assert.False(live.UpstreamGone);

        RunGit(_elsewhere, "push", "origin", "--delete", "remote-name");
        RunGit(_work, "fetch", "--prune", "origin");

        var gone = await ConfiguredUpstreamProbe.ProbeAsync(new GitCommandRunner(), _work, "local-name", CancellationToken.None);
        Assert.True(gone.InspectionSucceeded);
        Assert.True(gone.HasConfiguredUpstream);
        Assert.True(gone.UpstreamGone);
    }

    [Fact]
    public async Task TwoConfiguredMergeValues_DoNotApply_RatherThanGuess()
    {
        RunGit(_work, "checkout", "-b", "octopus");
        RunGit(_work, "commit", "--allow-empty", "-m", "octopus work");
        RunGit(_work, "push", "-u", "origin", "octopus");
        RunGit(_work, "config", "--add", "branch.octopus.merge", "refs/heads/main");

        var verdict = await ConfiguredUpstreamProbe.ProbeAsync(new GitCommandRunner(), _work, "octopus", CancellationToken.None);

        Assert.True(verdict.InspectionSucceeded);
        Assert.False(verdict.HasConfiguredUpstream);
        Assert.False(verdict.UpstreamGone);
    }

    // ------------------------------------------------------------------------------------------
    // Offline, or a remote that cannot be reached, is not a failed inspection any more: the
    // answer was never on the remote. The old probe reported "could not inspect" here, and every
    // worktree on the machine went unknown whenever the hosting provider did.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public async Task UnreachableRemote_StillAnswersFromTheLocalRef()
    {
        RunGit(_work, "checkout", "-b", "offline");
        RunGit(_work, "commit", "--allow-empty", "-m", "offline work");
        RunGit(_work, "push", "-u", "origin", "offline");
        RunGit(_work, "remote", "set-url", "origin", Path.Combine(_root, "no-such-remote.git"));

        var verdict = await ConfiguredUpstreamProbe.ProbeAsync(new GitCommandRunner(), _work, "offline", CancellationToken.None);

        Assert.True(verdict.InspectionSucceeded);
        Assert.True(verdict.HasConfiguredUpstream);
        Assert.False(verdict.UpstreamGone);
    }

    // ------------------------------------------------------------------------------------------
    // The fail-closed cases the change introduced: each was answered by a live query before.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public async Task AnUpstreamConfiguredOnABranchWhoseOwnRefDoesNotExist_CannotBeInspected()
    {
        RunGit(_work, "config", "branch.phantom.remote", "origin");
        RunGit(_work, "config", "branch.phantom.merge", "refs/heads/phantom");

        var verdict = await ConfiguredUpstreamProbe.ProbeAsync(new GitCommandRunner(), _work, "phantom", CancellationToken.None);

        Assert.True(verdict.HasConfiguredUpstream);
        Assert.False(verdict.InspectionSucceeded);
        Assert.False(verdict.UpstreamGone);
    }

    [Fact]
    public async Task AnUpstreamGitCannotMapToATrackingRef_CannotBeInspected()
    {
        // A remote with an address but no fetch refspec: git has no refs/remotes/<name>/ to map into.
        RunGit(_work, "remote", "add", "bare-address", _origin);
        RunGit(_work, "config", "--unset-all", "remote.bare-address.fetch");
        RunGit(_work, "checkout", "-b", "unmapped");
        RunGit(_work, "config", "branch.unmapped.remote", "bare-address");
        RunGit(_work, "config", "branch.unmapped.merge", "refs/heads/unmapped");

        var verdict = await ConfiguredUpstreamProbe.ProbeAsync(new GitCommandRunner(), _work, "unmapped", CancellationToken.None);

        Assert.True(verdict.HasConfiguredUpstream);
        Assert.False(verdict.InspectionSucceeded);
        Assert.False(verdict.UpstreamGone);
    }

    [Fact]
    public async Task ABranchOnARemoteTheCallerCouldNotRefresh_CannotBeInspected()
    {
        RunGit(_work, "checkout", "-b", "on-origin");
        RunGit(_work, "commit", "--allow-empty", "-m", "work");
        RunGit(_work, "push", "-u", "origin", "on-origin");

        var trusted = await ConfiguredUpstreamProbe.ProbeAsync(new GitCommandRunner(), _work, "on-origin", CancellationToken.None);
        Assert.True(trusted.InspectionSucceeded);

        var untrusted = await ConfiguredUpstreamProbe.ProbeAsync(
            new GitCommandRunner(), _work, "on-origin", CancellationToken.None, unrefreshedRemotes: new[] { "origin" });

        Assert.True(untrusted.HasConfiguredUpstream);
        Assert.False(untrusted.InspectionSucceeded);
        Assert.False(untrusted.UpstreamGone);
    }

    // ------------------------------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------------------------------

    /// <summary>Forwards to real git and keeps the subcommand of every call, so a test can say what was NOT run.</summary>
    private sealed class RecordingGitRunner : GitCommandRunner
    {
        public List<string> Commands { get; } = new();

        public override Task<GitCommandResult> RunAsync(string workingDirectory, string[] args, CancellationToken ct = default)
        {
            lock (Commands) Commands.Add(args.Length > 0 ? args[0] : "");
            return base.RunAsync(workingDirectory, args, ct);
        }
    }

    private static void ConfigureIdentity(string repo)
    {
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "Test");
        RunGit(repo, "config", "commit.gpgsign", "false");
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}) in {cwd}:\n{stdout}\n{stderr}");
    }
}
