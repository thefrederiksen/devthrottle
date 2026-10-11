using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (up-stream): the Gateway's registry of live up-streams, keyed by the
/// fresh stream id the Gateway minted when it opened the stream over the tunnel. It maps a stream id to the
/// browser-facing <see cref="IStreamSink"/>, and its <see cref="ConsumeAsync"/> is what the hub's StreamUp
/// method calls to pump the Director's frames into that sink.
///
/// Three of the Architect's four binding refinements live here (the fourth, bounded frames, lives in the
/// producer and the contract):
///  1. Backpressure (ruling 1): <see cref="ConsumeAsync"/> is pull-then-forward - it awaits the sink write of
///     one frame BEFORE pulling the next frame from the Director's up-stream. A slow sink therefore stalls the
///     pull, which (with a small SignalR StreamBufferCapacity) blocks the Director's producer. Bounded memory
///     end to end; this is the whole reason for native streaming, not an optimization.
///  3. close-stream / lifecycle races (ruling 3): a StreamUp that arrives after its sink is already gone is a
///     safe no-op (nothing to pump into); a StreamUp that never arrives after an open is torn down by a
///     timeout so a sink is never left waiting forever. Stream ids are fresh Guids and never reused, so no
///     late frame can alias a later stream.
/// </summary>
public sealed class GatewayStreamRegistry
{
    private sealed class Entry
    {
        public required IStreamSink Sink { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        /// <summary>
        /// Issue #1923: the identity allowed to stream frames into this sink - the tenant that opened the
        /// stream and the Director the open command was sent to. Recorded at registration; checked on every
        /// claim/write. Without it the bare stream id was the only key, so any authenticated caller that
        /// learned or guessed another account's id could WRITE into that account's terminal, claim the stream
        /// before the real Director, or tear it down.
        /// </summary>
        public required StreamOwner Owner { get; init; }
        public ITimer? OpenTimeout { get; set; }
        public int Claimed;
    }

    private readonly ConcurrentDictionary<string, Entry> _streams = new();

    /// <summary>How long a registered sink waits for its StreamUp before it is torn down (ruling 3).</summary>
    private readonly TimeSpan _openTimeout;

    /// <summary>The clock the open timeout runs on.</summary>
    private readonly TimeProvider _time;

    /// <param name="openTimeout">
    /// The StreamUp-never-arrives teardown window. Null uses ten seconds. A test seam; production passes null.
    /// </param>
    /// <param name="time">The clock the open timeout runs on. Null is the system clock; tests pass a fake one and advance it.</param>
    public GatewayStreamRegistry(TimeSpan? openTimeout = null, TimeProvider? time = null)
    {
        _openTimeout = openTimeout ?? TimeSpan.FromSeconds(10);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The number of live (registered, not yet torn down) streams. For diagnostics and tests.</summary>
    public int LiveStreamCount => _streams.Count;

    /// <summary>
    /// Register the sink for a stream the Gateway just opened over the tunnel (Phase 2 callers). Arms the
    /// StreamUp-never-arrives timeout. Returns a token that fires when the stream is torn down (a browser
    /// disconnect via <see cref="Close"/>, the timeout, or natural completion) so the caller can stop waiting.
    /// A duplicate stream id is a fail-loud error (ids are fresh Guids and must be unique).
    ///
    /// Issue #1923: the caller MUST supply the <paramref name="owner"/> - the tenant whose request opened this
    /// stream and the Director the open command is being sent to. That pair is recorded on the entry and is
    /// what <see cref="ConsumeAsync"/> authorizes every incoming StreamUp against, so a stream can only ever be
    /// claimed, written, or ended by the Director it was opened on, inside the account that opened it.
    /// </summary>
    public CancellationToken Register(string streamId, StreamOwner owner, IStreamSink sink)
    {
        if (string.IsNullOrEmpty(streamId)) throw new ArgumentException("streamId is required", nameof(streamId));
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        // Fail loud on an unusable owner rather than recording one that would authorize nobody (or, worse,
        // anybody). An invalid tenant or a blank Director id means the caller does not actually know who owns
        // this stream, and a stream whose owner is unknown must never be opened.
        if (!owner.Tenant.IsValid) throw new ArgumentException("stream owner tenant is required", nameof(owner));
        if (string.IsNullOrWhiteSpace(owner.DirectorId)) throw new ArgumentException("stream owner directorId is required", nameof(owner));

        var entry = new Entry { Sink = sink, Cts = new CancellationTokenSource(), Owner = owner };
        if (!_streams.TryAdd(streamId, entry))
            throw new InvalidOperationException($"stream id already registered (ids must be fresh and unique): {streamId}");

        entry.OpenTimeout = _time.CreateTimer(_ => TimeoutUnclaimed(streamId), null, _openTimeout, Timeout.InfiniteTimeSpan);
        FileLog.Write($"[GatewayStreamRegistry] registered stream {streamId} ({owner.ToLogString()}, live={_streams.Count})");
        return entry.Cts.Token;
    }

    // StreamUp-never-arrives (ruling 3): if nothing claimed this stream within the window, tear the sink down
    // so it is not left waiting forever (the Director died mid-open, or the open never produced).
    private void TimeoutUnclaimed(string streamId)
    {
        if (!_streams.TryGetValue(streamId, out var entry)) return;
        if (Interlocked.CompareExchange(ref entry.Claimed, 0, 0) == 1) return; // already claimed - fine
        FileLog.Write($"[GatewayStreamRegistry] stream {streamId} never received its StreamUp within the open window; tearing down");
        Teardown(streamId, "stream did not start");
    }

    /// <summary>
    /// Consume a Director up-stream into its registered sink (called by the hub's StreamUp method). Pull-then-
    /// forward: awaits the sink write of each frame before pulling the next (the backpressure invariant). Ends
    /// on the Closed frame, natural completion, cancellation (a browser disconnect via <see cref="Close"/>), or
    /// the hub connection aborting. If no sink is registered for this id the frames are dropped and the method
    /// returns at once (StreamUp-after-sink-gone: the browser is already gone).
    ///
    /// Issue #1923 - AUTHORIZATION, not just authentication. <paramref name="caller"/> is the identity the hub
    /// resolved for the connection sending these frames (its bound tenant and its bound Director id). It is
    /// checked against the owner recorded at <see cref="Register"/>, and a mismatch REFUSES the call by
    /// throwing - the frames never reach the sink, the stream is not claimed, and it is not torn down. Proving
    /// WHO the caller is (which the hub's Hello binding already does) is not proving the caller owns THIS
    /// stream; this method is where the second question is answered.
    /// </summary>
    public async Task ConsumeAsync(string streamId, StreamOwner caller, IAsyncEnumerable<DirectorStreamFrame> frames, CancellationToken hubCancellation)
    {
        if (frames is null) throw new ArgumentNullException(nameof(frames));

        if (!_streams.TryGetValue(streamId, out var entry))
        {
            // StreamUp-after-sink-gone (ruling 3): the browser disconnected before the frames arrived. There
            // is nothing to pump into; return immediately so the Director's producer is not consumed further.
            FileLog.Write($"[GatewayStreamRegistry] StreamUp for unknown/closed stream {streamId}; dropping (sink already gone)");
            return;
        }

        if (!entry.Owner.Matches(caller))
        {
            // REFUSE - loudly. A silent drop here would be indistinguishable from the legitimate no-op above,
            // which is exactly what would let a cross-account injection attempt look like an ordinary race in
            // the log. Nothing is claimed and nothing is torn down: the real owner's stream is untouched, so a
            // wrong caller cannot deny it service either.
            FileLog.Write($"[GatewayStreamRegistry] StreamUp REFUSED for stream {streamId}: caller ({caller.ToLogString()}) does not own it (owner {entry.Owner.ToLogString()})");
            throw new StreamOwnershipDeniedException($"stream {streamId} is not owned by the calling Director");
        }

        Interlocked.Exchange(ref entry.Claimed, 1);
        entry.OpenTimeout?.Dispose();
        entry.OpenTimeout = null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cts.Token, hubCancellation);
        string? reason = null;
        try
        {
            await foreach (var frame in frames.WithCancellation(linked.Token))
            {
                // Pull-then-forward (ruling 1): await this frame's delivery BEFORE the loop pulls the next one.
                await entry.Sink.WriteFrameAsync(frame, linked.Token);
                if (frame.Kind == DirectorStreamFrameType.Closed)
                {
                    reason = frame.Reason;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected teardown: the browser disconnected (Close) or the hub connection aborted. Not a fault -
            // the finally tears the stream down normally.
            reason ??= "cancelled";
        }
        finally
        {
            Teardown(streamId, reason);
        }
    }

    /// <summary>
    /// Tear a stream down from the BROWSER-FACING side (the browser disconnected). Idempotent.
    ///
    /// Issue #1923 - why this one takes no owner. Every caller of this method is the Gateway leg that called
    /// <see cref="Register"/> for that very stream id, inside the same request (see
    /// <c>TunnelStreamLegs</c>); the id never leaves that method's local scope on this side. No Director,
    /// device, or browser can reach it - the Director-facing surface is the hub, whose only stream method is
    /// StreamUp, and the Director's own "close-stream" travels the OTHER way (Gateway to Director) and never
    /// re-enters this registry. So there is no untrusted caller to authorize here. The teardown that IS
    /// reachable by a Director runs in <see cref="ConsumeAsync"/>'s finally, behind the ownership check above.
    /// If a caller-supplied close is ever added, it must carry and check an owner exactly as ConsumeAsync does.
    /// </summary>
    public void Close(string streamId) => Teardown(streamId, "closed");

    private void Teardown(string streamId, string? reason)
    {
        if (!_streams.TryRemove(streamId, out var entry)) return;
        entry.OpenTimeout?.Dispose();
        try { entry.Cts.Cancel(); } catch { /* already disposed */ }
        _ = SafeCompleteAsync(entry.Sink, reason);
        entry.Cts.Dispose();
        FileLog.Write($"[GatewayStreamRegistry] torn down stream {streamId} (reason={reason ?? "none"}, live={_streams.Count})");
    }

    private static async Task SafeCompleteAsync(IStreamSink sink, string? reason)
    {
        try { await sink.CompleteAsync(reason); }
        catch (Exception ex) { FileLog.Write($"[GatewayStreamRegistry] sink CompleteAsync failed: {ex.Message}"); }
    }
}
