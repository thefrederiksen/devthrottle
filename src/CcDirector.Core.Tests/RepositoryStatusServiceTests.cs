using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class RepositoryStatusClassifyTests
{
    [Theory]
    [InlineData("https://github.com/foo/bar.git", RepoProvider.GitHub, "foo")]
    [InlineData("git@github.com:foo/bar.git", RepoProvider.GitHub, "foo")]
    [InlineData("https://github.com/thefrederiksen/devthrottle", RepoProvider.GitHub, "thefrederiksen")]
    [InlineData("https://mindzie@dev.azure.com/mindzie/mindzieStudio1/_git/mindzieWeb", RepoProvider.AzureDevOps, "mindzie")]
    [InlineData("git@ssh.dev.azure.com:v3/mindzie/mindzieStudio1/mindzieWeb", RepoProvider.AzureDevOps, "mindzie")]
    [InlineData("https://gitlab.com/foo/bar.git", RepoProvider.Other, null)]
    [InlineData("", RepoProvider.None, null)]
    [InlineData(null, RepoProvider.None, null)]
    public void ClassifyRemote_MapsUrlToProviderAndOrg(string? url, RepoProvider provider, string? org)
    {
        var (p, o) = RepositoryStatusService.ClassifyRemote(url);
        Assert.Equal(provider, p);
        Assert.Equal(org, o);
    }
}

/// <summary>
/// REGRESSION (inspection finding F7): the size walk honors cancellation during enumeration and
/// never stores a partial (lying) measurement. No file caps or byte budgets exist - the walk
/// either finishes or is cancelled cleanly.
/// </summary>
public sealed class WorktreeSizeMeasurementTests : IDisposable
{
    private readonly string _dir;

    public WorktreeSizeMeasurementTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccd-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), new string('x', 10));
    }

    public void Dispose()
    {
        // Loud: the folder is created in the constructor and holds plain files; a delete that fails is a defect.
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Measure_Cancelled_Throws_AndStoresNoPartialResult()
    {
        var stamp = new DateTime(2026, 07, 23, 10, 0, 0, DateTimeKind.Utc);
        var w = new WorktreeInfo { Path = _dir, Branch = "b", LastActivityUtc = stamp };

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => RepositoryStatusService.MeasureWorktreeBytes(w, cts.Token));

        // A partial measurement must not have been cached: the uncancelled walk returns the
        // FULL size (a cached partial with the same activity stamp would short-circuit here).
        Assert.Equal(50, RepositoryStatusService.MeasureWorktreeBytes(w, CancellationToken.None));
    }

    [Fact]
    public void Measure_Uncancelled_ReturnsTheFullSize_AndCachesIt()
    {
        var stamp = new DateTime(2026, 07, 23, 11, 0, 0, DateTimeKind.Utc);
        var w = new WorktreeInfo { Path = _dir, Branch = "b", LastActivityUtc = stamp };

        Assert.Equal(50, RepositoryStatusService.MeasureWorktreeBytes(w, CancellationToken.None));
        // Second call with the same stamp hits the cache, cancellation not consulted before it.
        Assert.Equal(50, RepositoryStatusService.MeasureWorktreeBytes(w, CancellationToken.None));
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F8): a completed scan evicts cached sizes for worktrees
    // the scan did not see, so reaped, moved, and deleted worktrees do not grow the
    // process-wide cache forever.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task CompletedScan_EvictsCachedSizes_ForWorktreesNoLongerPresent()
    {
        var goneDir = Path.Combine(Path.GetTempPath(), "ccd-size-gone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(goneDir);
        File.WriteAllText(Path.Combine(goneDir, "g.txt"), "gone");
        try
        {
            var stamp = new DateTime(2026, 07, 23, 12, 0, 0, DateTimeKind.Utc);
            RepositoryStatusService.MeasureWorktreeBytes(new WorktreeInfo { Path = _dir, Branch = "b", LastActivityUtc = stamp }, CancellationToken.None);
            RepositoryStatusService.MeasureWorktreeBytes(new WorktreeInfo { Path = goneDir, Branch = "g", LastActivityUtc = stamp }, CancellationToken.None);
            Assert.True(RepositoryStatusService.SizeCacheContains(_dir));
            Assert.True(RepositoryStatusService.SizeCacheContains(goneDir));

            // A scan whose model only contains the surviving worktree.
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { "/r/a" },
                compute: (p, _, _) => Task.FromResult(new RepositoryStatus
                {
                    Path = p,
                    Name = "a",
                    IsClean = true,
                    Success = true,
                    Worktrees = new[] { new WorktreeInfo { Path = _dir, Branch = "b" } },
                }))
            {
                LiveSessionsProvider = _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>()),
            };
            await monitor.RescanAsync(new[] { "/r" });

            Assert.True(RepositoryStatusService.SizeCacheContains(_dir));    // still present - kept
            Assert.False(RepositoryStatusService.SizeCacheContains(goneDir)); // unseen - evicted
        }
        finally
        {
            if (Directory.Exists(goneDir)) Directory.Delete(goneDir, recursive: true);
        }
    }
}

/// <summary>
/// Integration tests over real repositories: a repo's status folds in its remote provider, its
/// uncommitted count, and its worktree summary.
///
/// QUARANTINED (Reliable Tests, 11 October 2026): it runs real git processes, which gate tests may no longer do,
/// but it holds the regression test that a failed git probe never reads as verified clean, so it is kept and run on
/// its own: test-local.ps1 leaves it out of every gate run and runs it under -Quarantine, whose result never
/// decides a release.
/// </summary>
[Trait("Gate", "Quarantine")]
public sealed class RepositoryStatusServiceTests : IDisposable
{
    private readonly string _root;

    public RepositoryStatusServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-repostatus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // The fixture holds a real git repository, whose objects are read-only; the shared delete clears the
        // attribute first and reports a failure instead of retrying and swallowing it, which leaked a folder per
        // test for as long as the swallow stood.
        TestTempRoot.DeleteTree(_root);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): a failed git probe must be reported as UNKNOWN, never folded into a
    // non-provisional "verified clean" row eligible for fleet reporting and recommendations. A real
    // directory that is not a git repository makes the status, sync, and worktree probes all fail;
    // the aggregate must come back Success=false, not Success=true with IsClean=true.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task GetStatusAsync_WhenGitProbesFail_ReportsUnknown_NotVerifiedClean()
    {
        var notARepo = Path.Combine(_root, "not-a-repo");
        Directory.CreateDirectory(notARepo);

        var status = await new RepositoryStatusService().GetStatusAsync(notARepo, fetchPrune: false);

        Assert.False(status.Success, "a failed git probe must report unknown, never a verified-clean row");
        Assert.False(status.IsClean);
    }

    // ----- helpers -----
}
