using Avalonia.Controls;
using Avalonia.Threading;
using CcDirector.ControlApi;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// THE START-UP ASK (mission "Smart Director Restart", 5.3 item 10): once this Director is connected to
/// its Gateway, ask the engine ONCE whether there is a record to offer, and show
/// <see cref="WayUpOfferWindow"/> only if there is.
///
/// IT LIVES HERE AND NOT IN MainWindow.axaml.cs. That file is seven thousand lines, several missions
/// are editing it at once, and phase 2 is holding a branch that changes its File menu and its OnClosing.
/// What lands there is one call.
///
/// THE THREE RULES IT KEEPS, all from the mandate:
///  - ONCE, EVER. The Gateway connection goes up and down all day; the offer is a start-up question and
///    asking it again on every reconnection would put a window in front of the owner while he works;
///  - OFF THE INTERFACE THREAD. The engine reads records from the Gateway one at a time, so asking it
///    on the interface thread would freeze the Director for as long as that takes (CLAUDE.md rule 1);
///  - NOTHING IS SHOWN unless the engine says there is something to offer. A Director with no Gateway,
///    or one whose engine refuses, shows NO dialog, NO error box and NO notification at start-up: a
///    start-up that interrupts the owner to say it could not check is a worse product than one that
///    stays quiet. The reason goes in the log, and the answer is there in File, Restart history when
///    he asks for it.
/// </summary>
public sealed class WayUpStartUpAsk
{
    private readonly Func<CancellationToken, Task<WayUpOffer>> _ask;
    private readonly Func<WayUpRecord, Task> _show;
    private int _asked;

    /// <param name="ask">Asks the engine. Called on a thread pool thread, never on the interface thread.</param>
    /// <param name="show">Shows the offer. Called on the interface thread, with the engine's record.</param>
    public WayUpStartUpAsk(Func<CancellationToken, Task<WayUpOffer>> ask, Func<WayUpRecord, Task> show)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(show);
        _ask = ask;
        _show = show;
    }

    /// <summary>
    /// The ask that is in flight, or has finished. Null until the first Connected. A test awaits it; the
    /// Director never does.
    /// </summary>
    public Task? Pending { get; private set; }

    /// <summary>
    /// Called on every change of the Gateway connection. Does nothing at all until the status is
    /// <see cref="GatewayConnectionStatus.Connected"/>, and then does its work exactly once for the life
    /// of this Director.
    /// </summary>
    /// <param name="status">What the Gateway connection monitor now reports.</param>
    /// <returns>True when this call started the ask. False every other time, including every later
    /// Connected.</returns>
    public bool OnConnectionChanged(GatewayConnectionStatus status)
    {
        if (status != GatewayConnectionStatus.Connected) return false;
        if (Interlocked.Exchange(ref _asked, 1) != 0) return false;

        FileLog.Write("[WayUpStartUpAsk] Connected for the first time; asking the way up engine");
        Pending = AskAsync();
        return true;
    }

    private async Task AskAsync()
    {
        WayUpOffer offer;
        try
        {
            // Task.Run puts the whole read on a thread pool thread. The engine answers a refusal rather
            // than throwing, but a seam behind it could still throw, and a throw here must not take the
            // Director's start-up with it.
            offer = await Task.Run(() => _ask(CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WayUpStartUpAsk] The way up engine could not be asked, so nothing is shown: {ex}");
            return;
        }

        if (offer.State != WayUpOfferState.Offered || offer.Record is null)
        {
            // Nothing waiting, or refused. Both stay quiet at start-up; only the log says which.
            FileLog.Write($"[WayUpStartUpAsk] Nothing shown: state={offer.State}, message={offer.Message}");
            return;
        }

        FileLog.Write($"[WayUpStartUpAsk] Offering: workspace={offer.Record.WorkspaceId}, owed={offer.Record.SeatsOwed}");
        var record = offer.Record;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await _show(record);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WayUpStartUpAsk] Showing the offer FAILED: {ex}");
            }
        });
    }

    /// <summary>
    /// THE ONE CALL THE MAIN WINDOW MAKES. Builds the ask from this Director's host, follows the host's
    /// own Gateway connection monitor, and shows the offer over <paramref name="owner"/>.
    ///
    /// The returned object is kept alive by the subscription it makes, so the caller need not hold it;
    /// it is returned so a caller that wants to wait for the ask can.
    /// </summary>
    /// <param name="host">This Director's host. Its monitor is followed and its engine is asked.</param>
    /// <param name="owner">The window the offer opens over.</param>
    public static WayUpStartUpAsk WatchForRestartOffer(ControlApiHost host, Window owner)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(owner);

        var ask = new WayUpStartUpAsk(
            ct => host.CreateDirectorWayUp().FindOfferAsync(ct),
            record => new WayUpOfferWindow(new WayUpOfferViewModel(record, host.CreateDirectorWayUp()))
                .ShowForAnswerAsync(owner));

        host.GatewayMonitor.Changed += () => ask.OnConnectionChanged(host.GatewayMonitor.Status);

        // The monitor may already be Connected by the time the window attaches to it, and a change that
        // has already happened raises no event.
        ask.OnConnectionChanged(host.GatewayMonitor.Status);

        FileLog.Write($"[WayUpStartUpAsk] Watching for a restart offer: status={host.GatewayMonitor.Status}");
        return ask;
    }
}
