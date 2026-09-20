using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Avalonia.SmartRestart;
using CcDirector.ControlApi;
using CcDirector.ControlApi.SmartRestart;
using Xunit;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// THE START-UP ASK (mission 5.3 item 10): once the Director is connected to its Gateway, ask the
/// engine ONCE whether there is a record to offer, and show the window only if there is.
///
/// These are the three rules the mandate sets, each with a test that goes red if it is broken: once
/// ever, off the interface thread, and NOTHING shown unless the engine offers something.
///
/// WHAT THESE DO NOT COVER. They do not prove MainWindow calls this - that is one line in
/// MainWindow.axaml.cs with no test of its own - and they do not prove a real GatewayConnectionMonitor
/// raises Changed when it connects, because no Gateway is run here.
/// </summary>
public class WayUpStartUpAskTests
{
    private static WayUpRecord Record() => WayUp.Record(
        "ws-1", "A restart is available", "Shut down on 19 September 2026 at 17:50.",
        "No reason was given.", 1, "One session is waiting to be brought back.",
        WayUp.BringBackRow("r1", "Billing: Billing - Developer - the export",
            "Brings back one session, reading its own handover.", true,
            WayUp.Seat("r1", "Billing - Developer - the export", "It comes back reading its handover.")));

    private static WayUpStartUpAsk Build(
        Func<CancellationToken, Task<WayUpOffer>> ask, List<WayUpRecord> shown) =>
        new(ask, record =>
        {
            shown.Add(record);
            return Task.CompletedTask;
        });

    // ===== It waits for Connected =====

    /// <summary>
    /// Nothing is asked until the connection is Connected. A Director that is not configured, is still
    /// dialling, or has failed, asks nothing at all - so it shows nothing at all.
    /// </summary>
    [AvaloniaFact]
    public void OnConnectionChanged_EveryStateExceptConnected_AsksNothingAndShowsNothing()
    {
        var asks = 0;
        var shown = new List<WayUpRecord>();
        var ask = Build(_ =>
        {
            asks++;
            return Task.FromResult(new WayUpOffer(WayUpOfferState.Offered, "h", Record()));
        }, shown);

        foreach (var status in new[]
                 {
                     GatewayConnectionStatus.NotConfigured,
                     GatewayConnectionStatus.Connecting,
                     GatewayConnectionStatus.Failed,
                     GatewayConnectionStatus.NoTailnetIdentity,
                 })
        {
            Assert.False(ask.OnConnectionChanged(status));
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, asks);
        Assert.Empty(shown);
        Assert.Null(ask.Pending);
    }

    // ===== Once, ever =====

    /// <summary>
    /// ONCE, EVER. The Gateway connection goes up and down all day; the offer is a start-up question,
    /// and asking it again on every reconnection would put a window in front of the owner while he is
    /// working. Ten Connected events, one ask, one window.
    /// </summary>
    [AvaloniaFact]
    public async Task OnConnectionChanged_ConnectedTenTimes_AsksOnceAndShowsOnce()
    {
        var asks = 0;
        var shown = new List<WayUpRecord>();
        var ask = Build(_ =>
        {
            Interlocked.Increment(ref asks);
            return Task.FromResult(new WayUpOffer(WayUpOfferState.Offered, "h", Record()));
        }, shown);

        var started = 0;
        for (var i = 0; i < 10; i++)
            if (ask.OnConnectionChanged(GatewayConnectionStatus.Connected))
                started++;

        await ask.Pending!;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, started);
        Assert.Equal(1, asks);
        Assert.Equal("ws-1", Assert.Single(shown).WorkspaceId);
    }

    /// <summary>And a reconnection AFTER a disconnection is still not a second ask.</summary>
    [AvaloniaFact]
    public async Task OnConnectionChanged_DisconnectedThenConnectedAgain_StillAsksOnlyOnce()
    {
        var asks = 0;
        var shown = new List<WayUpRecord>();
        var ask = Build(_ =>
        {
            Interlocked.Increment(ref asks);
            return Task.FromResult(new WayUpOffer(WayUpOfferState.Offered, "h", Record()));
        }, shown);

        ask.OnConnectionChanged(GatewayConnectionStatus.Connected);
        await ask.Pending!;
        ask.OnConnectionChanged(GatewayConnectionStatus.Failed);
        Assert.False(ask.OnConnectionChanged(GatewayConnectionStatus.Connected));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, asks);
        Assert.Single(shown);
    }

    // ===== Off the interface thread =====

    /// <summary>
    /// OFF THE INTERFACE THREAD. The engine reads records from the Gateway one at a time, so asking it
    /// on the interface thread would freeze the Director for as long as that took (CLAUDE.md rule 1).
    /// The window that is then shown IS on the interface thread, because that is where windows open.
    /// </summary>
    [AvaloniaFact]
    public async Task OnConnectionChanged_Connected_AsksOffTheInterfaceThreadAndShowsOnIt()
    {
        var askedOnTheInterfaceThread = true;
        var shownOnTheInterfaceThread = false;
        var ask = new WayUpStartUpAsk(
            _ =>
            {
                askedOnTheInterfaceThread = Dispatcher.UIThread.CheckAccess();
                return Task.FromResult(new WayUpOffer(WayUpOfferState.Offered, "h", Record()));
            },
            _ =>
            {
                shownOnTheInterfaceThread = Dispatcher.UIThread.CheckAccess();
                return Task.CompletedTask;
            });

        ask.OnConnectionChanged(GatewayConnectionStatus.Connected);
        await ask.Pending!;
        Dispatcher.UIThread.RunJobs();

        Assert.False(askedOnTheInterfaceThread);
        Assert.True(shownOnTheInterfaceThread);
    }

    // ===== Nothing is shown unless the engine offers something =====

    /// <summary>
    /// A Director with nothing waiting shows NOTHING at start-up: no dialog, no error box, no
    /// notification.
    /// </summary>
    [AvaloniaFact]
    public async Task Connected_NothingWaiting_ShowsNothingAtAll()
    {
        var shown = new List<WayUpRecord>();
        var ask = Build(
            _ => Task.FromResult(new WayUpOffer(WayUpOfferState.NothingWaiting, "nothing waiting", null)),
            shown);

        ask.OnConnectionChanged(GatewayConnectionStatus.Connected);
        await ask.Pending!;
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(shown);
    }

    /// <summary>
    /// AND A REFUSAL SHOWS NOTHING EITHER. This is the one the mandate is most explicit about: a
    /// start-up that interrupts the owner to say it could not check is a worse product than one that
    /// stays quiet. The reason goes in the log, and File, Restart history has the answer when he asks.
    /// </summary>
    [AvaloniaFact]
    public async Task Connected_TheEngineRefuses_ShowsNothingAtAll()
    {
        var shown = new List<WayUpRecord>();
        var ask = Build(
            _ => Task.FromResult(new WayUpOffer(
                WayUpOfferState.Refused,
                "The records of what this Director shut down could not be read (the Gateway did not answer).",
                null)),
            shown);

        ask.OnConnectionChanged(GatewayConnectionStatus.Connected);
        await ask.Pending!;
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(shown);
    }

    /// <summary>
    /// And a seam behind the engine that THROWS takes nothing with it. Start-up survives, and still
    /// shows nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task Connected_TheEngineThrows_StartUpSurvivesAndShowsNothing()
    {
        var shown = new List<WayUpRecord>();
        var ask = Build(
            _ => throw new InvalidOperationException("this Director is not connected to a Gateway."),
            shown);

        ask.OnConnectionChanged(GatewayConnectionStatus.Connected);
        await ask.Pending!;
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(shown);
    }

    /// <summary>
    /// An answer that says Offered but carries no record is not an offer. It shows nothing rather than
    /// opening a window with nothing in it.
    /// </summary>
    [AvaloniaFact]
    public async Task Connected_OfferedWithNoRecord_ShowsNothingRatherThanAnEmptyWindow()
    {
        var shown = new List<WayUpRecord>();
        var ask = Build(
            _ => Task.FromResult(new WayUpOffer(WayUpOfferState.Offered, "A restart is available", null)),
            shown);

        ask.OnConnectionChanged(GatewayConnectionStatus.Connected);
        await ask.Pending!;
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(shown);
    }

    /// <summary>A window that throws on the way up does not take the Director's start-up with it.</summary>
    [AvaloniaFact]
    public async Task Connected_ShowingTheWindowThrows_StartUpSurvives()
    {
        var ask = new WayUpStartUpAsk(
            _ => Task.FromResult(new WayUpOffer(WayUpOfferState.Offered, "h", Record())),
            _ => throw new InvalidOperationException("the window could not be opened"));

        ask.OnConnectionChanged(GatewayConnectionStatus.Connected);
        await ask.Pending!;
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(ask.Pending);
        Assert.True(ask.Pending!.IsCompletedSuccessfully);
    }

    // ===== It refuses to be built wrong =====

    [Fact]
    public void Constructor_NoAskOrNoShow_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new WayUpStartUpAsk(null!, _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() =>
            new WayUpStartUpAsk(_ => Task.FromResult(new WayUpOffer(WayUpOfferState.NothingWaiting, "", null)), null!));
    }
}
