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
