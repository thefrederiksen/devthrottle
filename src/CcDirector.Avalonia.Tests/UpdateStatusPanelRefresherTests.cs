using CcDirector.Avalonia;
using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The update status panel reads its facts off the window's thread and paints them on it, one read
/// at a time. The thread seams are injected, so these tests need no window: the "window thread" is
/// the test's own thread and the "pool" is a task the test controls.
/// </summary>
public sealed class UpdateStatusPanelRefresherTests
{
    private static UpdateStatusView AView(string headline) => UpdateStatusFold.Fold(new UpdateStatusFacts(
        CurrentVersion: "1.0.0",
        AutomaticUpdatesEnabled: true,
        State: new UpdaterState(),
        Live: null,
        RunningSessionCount: 0,
        LauncherRunning: false,
        Now: DateTimeOffset.UtcNow)) with { Headline = headline };

    [Fact]
    public async Task TheRead_RunsOffTheCallersThread_AndTheRender_RunsWhereTheWindowThreadIs()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        int readThread = -1, renderThread = -1;
        var rendered = new TaskCompletionSource<UpdateStatusView?>();
        var refresher = new UpdateStatusPanelRefresher(
            read: () => { readThread = Environment.CurrentManagedThreadId; return AView("read"); },
            render: v => { renderThread = Environment.CurrentManagedThreadId; rendered.TrySetResult(v); },
            offTheWindowThread: work => Task.Run(work),
            onTheWindowThread: work => work()); // "the window thread" is whichever thread hands the result over

        refresher.Refresh();
        var status = await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("read", status!.Headline);
        Assert.NotEqual(callerThread, readThread);
        Assert.Equal(1, refresher.ReadsStarted);
    }

    [Fact]
    public async Task ARefreshAskedForWhileOneIsRunning_DoesNotStartASecondRead_ButRunsOneAfterwards()
    {
        var release = new TaskCompletionSource();
        var renders = 0;
        var secondRender = new TaskCompletionSource();
        var refresher = new UpdateStatusPanelRefresher(
            read: () => { release.Task.Wait(TimeSpan.FromSeconds(10)); return AView("x"); },
            render: _ => { if (Interlocked.Increment(ref renders) == 2) secondRender.TrySetResult(); },
            offTheWindowThread: work => Task.Run(work),
            onTheWindowThread: work => work());

        refresher.Refresh();
        refresher.Refresh();
        refresher.Refresh();
        Assert.Equal(1, refresher.ReadsStarted); // the two extra asks were remembered, not started

        release.SetResult();
        await secondRender.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, refresher.ReadsStarted); // exactly one trailing read for the asks that arrived mid-read
        Assert.Equal(2, renders);
    }

    [Fact]
    public async Task AReadThatThrows_IsLogged_AndTheNextRefreshStillRuns()
    {
        var calls = 0;
        var rendered = new TaskCompletionSource();
        var refresher = new UpdateStatusPanelRefresher(
            read: () => { if (Interlocked.Increment(ref calls) == 1) throw new IOException("state file locked"); return AView("ok"); },
            render: _ => rendered.TrySetResult(),
            offTheWindowThread: work => Task.Run(work),
            onTheWindowThread: work => work());

        refresher.Refresh();
        await Task.Delay(200);
        refresher.Refresh();
        await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, refresher.ReadsStarted);
    }

    [Fact]
    public async Task ANullStatus_IsHandedToTheRender_SoThePanelCanHideItself()
    {
        var rendered = new TaskCompletionSource<UpdateStatusView?>();
        var refresher = new UpdateStatusPanelRefresher(
            read: () => null,
            render: v => rendered.TrySetResult(v),
            offTheWindowThread: work => Task.Run(work),
            onTheWindowThread: work => work());

        refresher.Refresh();

        Assert.Null(await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
