using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Storage;

/// <summary>
/// Tests for <see cref="ScreenshotCaptureWatcher"/> against real temporary directories - the
/// watcher's whole job is observing the filesystem, so a seam would test nothing.
/// </summary>
public sealed class ScreenshotCaptureWatcherTests : IDisposable
{
    private readonly string _root;

    public ScreenshotCaptureWatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "shots-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Cancellation_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        var watcher = new ScreenshotCaptureWatcher();

        var wait = watcher.WaitForNewScreenshotAsync(new[] { _root }, cts.Token);
        cts.Cancel();

        Assert.Null(await wait);
    }

    [Fact]
    public async Task NoWatchableRoots_ReturnsNull()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var watcher = new ScreenshotCaptureWatcher();

        var missing = Path.Combine(Path.GetTempPath(), "gone-" + Guid.NewGuid().ToString("N"));
        Assert.Null(await watcher.WaitForNewScreenshotAsync(new[] { missing }, cts.Token));
    }

    [Fact]
    public void WatchRoots_OnThisPlatform_ExistAndAreNotNested()
    {
        var roots = ScreenshotCaptureWatcher.WatchRoots();

        foreach (var root in roots)
            Assert.True(Directory.Exists(root), $"watch root does not exist: {root}");

        // No root may be inside another - the parent's recursive watch already covers it, and
        // double-watching doubles the event storm.
        for (var i = 0; i < roots.Count; i++)
            for (var j = 0; j < roots.Count; j++)
            {
                if (i == j) continue;
                Assert.False(
                    roots[i].StartsWith(roots[j].TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    $"nested watch roots: {roots[i]} inside {roots[j]}");
            }
    }
}
