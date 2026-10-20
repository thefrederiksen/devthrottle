using System.Threading.Channels;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Boxes;

/// <summary>
/// Moves one relay stream's frames between an endpoint on this side (the browser on the Gateway, the box on the
/// Director) and the far side of the tunnel. The same pump runs at both ends; only the endpoint differs.
///
/// <para><b>Three loops.</b> The <i>peer reader</i> takes frames from the far side and never waits on anything:
/// a Credit frame releases this side's send window at once, and every other frame goes into this stream's own
/// queue, which always has room because the far side may never have more than
/// <see cref="DesktopRelayLimits.WindowBytes"/> outstanding here. That is what keeps the shared tunnel moving: the
/// Gateway's hub reads the connection only as fast as each stream takes its frames, and this stream always takes
/// them at once. The <i>endpoint writer</i> delivers the queued frames to the endpoint and grants their bytes
/// back to the far side as Credit. The <i>endpoint reader</i> reads the endpoint, waits for the rate cap (on the
/// Director, toward the browser) and for send window, and sends. A slow browser therefore holds back only its own
/// desktop: credit stops coming, this side stops reading the box, and Selkies' acknowledgements slow its encoder.</para>
///
/// <para><b>Ending.</b> A Closed frame either way is the end. Whichever side sees its endpoint close sends Closed and
/// completes its outgoing frames; the side that receives Closed closes its endpoint and completes its own. If the
/// far side vanishes without a Closed (the Director disconnected, the tunnel dropped) the endpoint is closed with
/// a reason saying so. The peer reader keeps reading after the end until the far side completes its frames, so a
/// late frame never sits unread in the tunnel.</para>
/// </summary>
public sealed class DesktopRelayPump
{
    private readonly IDesktopRelayEndpoint _endpoint;
    private readonly ChannelWriter<DesktopRelayFrame> _toPeer;
    private readonly DesktopRelayUsage _usage;
    private readonly DesktopRelayBoxTally _tally;
    private readonly bool _endpointIsBrowser;
    private readonly TimeProvider _time;
    private readonly TimeSpan? _idleTimeout;
    private readonly DesktopRateLimiter? _outboundLimit;
    private readonly string _peerGoneReason;
    private readonly string _endpointGoneReason;

    private readonly DesktopRelayWindow _window = new(DesktopRelayLimits.WindowBytes);
    // Not marked single-reader: the writer loop asks how many frames are waiting, which only the general channel answers.
    private readonly Channel<DesktopRelayFrame> _inbox = Channel.CreateUnbounded<DesktopRelayFrame>();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<string> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _inboxOutstanding;
    private long _lastActivity;
    private int _finishing;
    private volatile bool _peerSentClosed;

    /// <param name="endpoint">This side's end: the browser on the Gateway, the box on the Director.</param>
    /// <param name="toPeer">Where this side's frames go toward the far side. The pump completes it when the stream ends.</param>
    /// <param name="usage">This stream's byte counts.</param>
    /// <param name="tally">The process's per-box totals.</param>
    /// <param name="endpointIsBrowser">True on the Gateway (bytes read from the endpoint travel toward the box).</param>
    /// <param name="time">The clock for the idle timeout and the periodic usage line.</param>
    /// <param name="idleTimeout">Close the stream after this long with nothing moving either way; null for none.</param>
    /// <param name="outboundLimit">The rate cap on bytes read from the endpoint, or null for none.</param>
    /// <param name="peerGoneReason">What the endpoint is told when the far side vanishes without closing.</param>
    /// <param name="endpointGoneReason">What the far side is told when this side's endpoint goes away.</param>
    public DesktopRelayPump(IDesktopRelayEndpoint endpoint, ChannelWriter<DesktopRelayFrame> toPeer,
        DesktopRelayUsage usage, DesktopRelayBoxTally tally, bool endpointIsBrowser, TimeProvider time,
        TimeSpan? idleTimeout, DesktopRateLimiter? outboundLimit, string peerGoneReason, string endpointGoneReason)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _toPeer = toPeer ?? throw new ArgumentNullException(nameof(toPeer));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _tally = tally ?? throw new ArgumentNullException(nameof(tally));
        _endpointIsBrowser = endpointIsBrowser;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _idleTimeout = idleTimeout;
        _outboundLimit = outboundLimit;
        _peerGoneReason = peerGoneReason;
        _endpointGoneReason = endpointGoneReason;
    }

    /// <summary>The bytes this side may still send to the far side without waiting for credit. For tests.</summary>
    public int SendWindowAvailable => _window.Available;

    /// <summary>
    /// Run the stream until it ends, and return why it ended.
    /// </summary>
    /// <param name="fromPeer">The far side's frames.</param>
    /// <param name="peerGone">Fires when the far side is gone (the hub connection aborted, or the tunnel dropped).</param>
    /// <param name="endpointGone">Fires when this side's endpoint is gone (the browser's request aborted).</param>
    public async Task<string> RunAsync(IAsyncEnumerable<DesktopRelayFrame> fromPeer, CancellationToken peerGone, CancellationToken endpointGone)
    {
        FileLog.Write($"[DesktopRelayPump] RunAsync: box={_usage.BoxId} stream={_usage.StreamId} side={_usage.Side} idleTimeout={_idleTimeout?.ToString() ?? "none"} rateCap={(_outboundLimit is null ? "none" : "on")}");
        Touch();
        using var tick = _time.CreateTimer(_ => OnTick(), null, DesktopRelayLimits.UsageLogInterval, DesktopRelayLimits.UsageLogInterval);
        using var peerRegistration = peerGone.Register(() => Fire(_peerGoneReason, sendClosed: false, DesktopRelayEndpointClose.GoingAway));
        using var endpointRegistration = endpointGone.Register(() => Fire(_endpointGoneReason, sendClosed: true, DesktopRelayEndpointClose.GoingAway));

        var peerReader = ReadPeerAsync(fromPeer, peerGone);
        var endpointReader = ReadEndpointAsync();
        var endpointWriter = WriteEndpointAsync();

        var reason = await _finished.Task.ConfigureAwait(false);
        await Task.WhenAll(endpointReader, endpointWriter).ConfigureAwait(false);
        // The peer reader drains whatever the far side still sends until it completes its frames.
        _ = peerReader;

        FileLog.Write($"[DesktopRelayUsage] {_usage.Describe("closed")} reason={reason}");
        _tally.Log(_usage.BoxId, _usage.Side);
        return reason;
    }

    // ------------------------------------------------------------------------------ the three loops ----

    private async Task ReadPeerAsync(IAsyncEnumerable<DesktopRelayFrame> fromPeer, CancellationToken peerGone)
    {
        try
        {
            await foreach (var frame in fromPeer.WithCancellation(peerGone).ConfigureAwait(false))
            {
                Touch();
                if (frame.Kind == DesktopRelayFrameKind.Credit)
                {
                    if (Volatile.Read(ref _finishing) == 1) continue;
                    try
                    {
                        _window.Release(frame.Credit);
                    }
                    catch (InvalidOperationException ex)
                    {
                        await FinishAsync($"the far side broke the relay protocol: {ex.Message}", sendClosed: true, DesktopRelayEndpointClose.InternalError).ConfigureAwait(false);
                    }
                    continue;
                }
                if (Volatile.Read(ref _finishing) == 1) continue; // draining after the end

                var outstanding = Interlocked.Add(ref _inboxOutstanding, DesktopRelayLimits.CostOf(frame));
                if (outstanding > DesktopRelayLimits.WindowBytes)
                {
                    await FinishAsync($"the far side sent {outstanding} bytes against a {DesktopRelayLimits.WindowBytes}-byte window",
                        sendClosed: true, DesktopRelayEndpointClose.InternalError).ConfigureAwait(false);
                    continue;
                }
                if (frame.Kind == DesktopRelayFrameKind.Closed)
                    _peerSentClosed = true;
                _inbox.Writer.TryWrite(frame);
            }
            if (!_peerSentClosed)
                await FinishAsync(_peerGoneReason, sendClosed: false, DesktopRelayEndpointClose.GoingAway).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(_peerGoneReason, sendClosed: false, DesktopRelayEndpointClose.GoingAway).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The endpoint is closed below with the reason the person is owed.
            // not-an-error: a dropped connection - a Director disconnect - ends the far side's frames with an error.
            FileLog.Write($"[DesktopRelayPump] stream={_usage.StreamId}: the far side's frames ended with an error: {ex.Message}");
            await FinishAsync(_peerGoneReason, sendClosed: false, DesktopRelayEndpointClose.GoingAway).ConfigureAwait(false);
        }
    }

    private async Task ReadEndpointAsync()
    {
        try
        {
            await foreach (var frame in _endpoint.ReadAsync(_stop.Token).ConfigureAwait(false))
            {
                Touch();
                if (frame.Kind == DesktopRelayFrameKind.Closed)
                {
                    await FinishAsync(frame.Reason ?? "closed", sendClosed: true, frame.CloseStatus, closedFrame: frame).ConfigureAwait(false);
                    return;
                }
                var bytes = frame.Data?.Length ?? 0;
                if (bytes > DesktopRelayLimits.MaxFrameBytes)
                    throw new InvalidOperationException($"an endpoint produced a {bytes}-byte frame; the most is {DesktopRelayLimits.MaxFrameBytes}");
                if (_outboundLimit is not null)
                    await _outboundLimit.AcquireAsync(bytes, _stop.Token).ConfigureAwait(false);
                await _window.AcquireAsync(DesktopRelayLimits.CostOf(frame), _stop.Token).ConfigureAwait(false);
                CountFromEndpoint(bytes);
                _toPeer.TryWrite(frame);
            }
            // The endpoint ended this direction without closing (an HTTP request body is complete); the stream stays
            // open for the other direction.
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The stream ended elsewhere.
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DesktopRelayPump] stream={_usage.StreamId}: reading the {EndpointName} FAILED: {ex}");
            await FinishAsync($"the {EndpointName} end failed: {ex.Message}", sendClosed: true, DesktopRelayEndpointClose.InternalError).ConfigureAwait(false);
        }
    }

    private async Task WriteEndpointAsync()
    {
        var pendingCredit = 0;
        try
        {
            await foreach (var frame in _inbox.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (frame.Kind == DesktopRelayFrameKind.Closed)
                {
                    await FinishAsync(frame.Reason ?? "closed by the far side", sendClosed: false, frame.CloseStatus).ConfigureAwait(false);
                    return;
                }
                await _endpoint.DeliverAsync(frame, _stop.Token).ConfigureAwait(false);
                CountToEndpoint(frame.Data?.Length ?? 0);

                var cost = DesktopRelayLimits.CostOf(frame);
                Interlocked.Add(ref _inboxOutstanding, -cost);
                pendingCredit += cost;
                // Grant in batches while frames keep coming, and at once when the queue runs dry, so the far side is
                // never left waiting on credit for bytes already delivered.
                if (pendingCredit >= DesktopRelayLimits.WindowBytes / 8 || _inbox.Reader.Count == 0)
                {
                    _toPeer.TryWrite(new DesktopRelayFrame { Kind = DesktopRelayFrameKind.Credit, Credit = pendingCredit });
                    pendingCredit = 0;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The stream ended elsewhere.
        }
        catch (DesktopRelayMessageTooBigException ex)
        {
            await FinishAsync(ex.Message, sendClosed: true, DesktopRelayEndpointClose.MessageTooBig).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DesktopRelayPump] stream={_usage.StreamId}: delivering to the {EndpointName} FAILED: {ex}");
            await FinishAsync($"delivering to the {EndpointName} failed: {ex.Message}", sendClosed: true, DesktopRelayEndpointClose.InternalError).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------------- the end ----

    // From a timer or a token registration: there is no caller to hand a failure to, so it is logged.
    private void Fire(string reason, bool sendClosed, int closeStatus)
    {
        _ = FireAsync();

        async Task FireAsync()
        {
            try
            {
                await FinishAsync(reason, sendClosed, closeStatus).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DesktopRelayPump] stream={_usage.StreamId}: ending the stream FAILED: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// End the stream once: tell the far side (unless it is the one that ended it or is gone), complete this side's
    /// frames, stop the endpoint loops, and close the endpoint with the status and reason.
    /// </summary>
    private async Task FinishAsync(string reason, bool sendClosed, int closeStatus, DesktopRelayFrame? closedFrame = null)
    {
        if (Interlocked.Exchange(ref _finishing, 1) == 1) return;
        FileLog.Write($"[DesktopRelayPump] stream={_usage.StreamId} box={_usage.BoxId} side={_usage.Side} ending: status={closeStatus}, reason={reason}, tellFarSide={sendClosed}");
        if (sendClosed)
            _toPeer.TryWrite(closedFrame ?? new DesktopRelayFrame { Kind = DesktopRelayFrameKind.Closed, CloseStatus = closeStatus, Reason = reason });
        _toPeer.TryComplete();
        // Close BEFORE stopping the loops: cancelling a pending WebSocket receive aborts the socket, and an aborted
        // socket can no longer carry the close status and reason the person is owed.
        await _endpoint.CloseAsync(closeStatus, reason).ConfigureAwait(false);
        _stop.Cancel();
        _finished.TrySetResult(reason);
    }

    private void OnTick()
    {
        FileLog.Write($"[DesktopRelayUsage] {_usage.Describe("open")}");
        _tally.Log(_usage.BoxId, _usage.Side);
        if (_idleTimeout is not { } idle) return;
        var quiet = _time.GetElapsedTime(Interlocked.Read(ref _lastActivity));
        if (quiet >= idle)
            Fire($"nothing moved for {quiet.TotalMinutes:F0} minutes, so the desktop connection was closed", sendClosed: true, DesktopRelayEndpointClose.GoingAway);
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivity, _time.GetTimestamp());

    private string EndpointName => _endpointIsBrowser ? "browser" : "box";

    private void CountFromEndpoint(int bytes)
    {
        if (bytes == 0) return;
        if (_endpointIsBrowser) { _usage.AddToBox(bytes); _tally.AddToBox(_usage.BoxId, bytes); }
        else { _usage.AddToBrowser(bytes); _tally.AddToBrowser(_usage.BoxId, bytes); }
    }

    private void CountToEndpoint(int bytes)
    {
        if (bytes == 0) return;
        if (_endpointIsBrowser) { _usage.AddToBrowser(bytes); _tally.AddToBrowser(_usage.BoxId, bytes); }
        else { _usage.AddToBox(bytes); _tally.AddToBox(_usage.BoxId, bytes); }
    }
}
