using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.UnitTests.Git;

/// <summary>
/// The repository monitor asked its live-session provider on every repository it computed, and on the owner's
/// machine that provider fetches the whole fleet roster from the Gateway - 75 fetches per five-minute rescan,
/// about 80 MB an hour of the hosted Gateway's data out. These pin the cache that stops it: one fetch serves a
/// whole rescan, an answer is reused only for its maximum age, and a failure is never held.
///
/// Revert-proof: make <see cref="LiveSessionsCache.GetAsync"/> always fetch and
/// <see cref="RescanOf75Repositories_FetchesTheRosterOnce"/> counts 75 and goes red; drop the age check and
/// <see cref="GetAsync_AfterMaxAge_FetchesAgain"/> goes red. NOT covered here: the one line in MainWindow that
/// puts the cache in front of the Director's real provider - that window cannot be built in a unit test.
/// </summary>
public sealed class LiveSessionsCacheTests
{
    private sealed class Clock
    {
        public TimeSpan Now = TimeSpan.FromHours(1);
    }

    private sealed class CountingFetch
    {
        public int Calls;
        public IReadOnlyList<LiveSessionRef> Answer = new[] { new LiveSessionRef { RepoPath = "/r/a", Label = "one" } };

        public Task<IReadOnlyList<LiveSessionRef>> FetchAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Answer);
        }
    }

    private static RepositoryStatus Status(string path) => new()
    {
        Path = path,
        Name = Path.GetFileName(path),
        Provider = RepoProvider.GitHub,
        Branch = "main",
        IsClean = true,
        Success = true,
    };

    [Fact]
    public async Task RescanOf75Repositories_FetchesTheRosterOnce()
    {
        var fetch = new CountingFetch();
        var clock = new Clock();
        var cache = new LiveSessionsCache(fetch.FetchAsync, TimeSpan.FromSeconds(30), () => clock.Now);
        var paths = Enumerable.Range(0, 75).Select(i => $"/r/repo{i}").ToList();
        var sessionsSeen = new List<IReadOnlyList<LiveSessionRef>?>();
        var monitor = new RepositoryMonitor(
            enumerate: _ => paths,
            compute: (p, sessions, _) =>
            {
                lock (sessionsSeen) sessionsSeen.Add(sessions);
                return Task.FromResult(Status(p));
            })
        { LiveSessionsProvider = cache.GetAsync };

        await monitor.RescanAsync(new[] { "/r" });

        Assert.Equal(1, fetch.Calls);
        Assert.Equal(75, sessionsSeen.Count);
        Assert.All(sessionsSeen, s => Assert.Same(fetch.Answer, s));
    }

    [Fact]
    public async Task GetAsync_WithinMaxAge_ReusesTheAnswer()
    {
        var clock = new Clock();
        var fetch = new CountingFetch();
        var cache = new LiveSessionsCache(fetch.FetchAsync, TimeSpan.FromSeconds(30), () => clock.Now);

        var first = await cache.GetAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(29);
        var second = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(1, fetch.Calls);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetAsync_AfterMaxAge_FetchesAgain()
    {
        var clock = new Clock();
        var fetch = new CountingFetch();
        var cache = new LiveSessionsCache(fetch.FetchAsync, TimeSpan.FromSeconds(30), () => clock.Now);

        await cache.GetAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(30);
        fetch.Answer = new[] { new LiveSessionRef { RepoPath = "/r/b", Label = "two" } };
        var later = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(2, fetch.Calls);
        Assert.Equal("/r/b", Assert.Single(later).RepoPath);
    }

    [Fact]
    public async Task GetAsync_FetchThrows_NothingHeld_NextCallFetchesAgain()
    {
        var calls = 0;
        var cache = new LiveSessionsCache(_ =>
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("gateway down");
            return Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(CancellationToken.None));
        var answer = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Empty(answer);
    }

    [Fact]
    public async Task GetAsync_ConcurrentCallers_ShareOneFetch()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        var cache = new LiveSessionsCache(async _ =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return (IReadOnlyList<LiveSessionRef>)Array.Empty<LiveSessionRef>();
        });

        var callers = Enumerable.Range(0, 5).Select(_ => cache.GetAsync(CancellationToken.None)).ToList();
        release.SetResult();
        await Task.WhenAll(callers);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetAsync_CancelledWhileWaiting_DoesNotReleaseTheGateItNeverHeld()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        var cache = new LiveSessionsCache(async _ =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return (IReadOnlyList<LiveSessionRef>)Array.Empty<LiveSessionRef>();
        });

        var holder = cache.GetAsync(CancellationToken.None);
        using var gaveUp = new CancellationTokenSource();
        var waiter = cache.GetAsync(gaveUp.Token);
        gaveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        // Had the cancelled waiter released the gate, this caller would start a second fetch while the first runs.
        var third = cache.GetAsync(CancellationToken.None);
        Assert.Equal(1, calls);
        release.SetResult();
        await Task.WhenAll(holder, third);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetAsync_FetchCancelled_ReleasesTheGate_HoldsNothing()
    {
        var calls = 0;
        using var midFetch = new CancellationTokenSource();
        var cancelling = new LiveSessionsCache(ct =>
        {
            calls++;
            midFetch.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.GetAsync(midFetch.Token));

        var callsBefore = calls;
        var answer = await cancelling.GetAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(answer);
        Assert.Equal(callsBefore + 1, calls);
    }

    [Fact]
    public void Constructor_NonPositiveMaxAge_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LiveSessionsCache(_ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>()), TimeSpan.Zero));
}
