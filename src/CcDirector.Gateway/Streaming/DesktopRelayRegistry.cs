using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Factory Boxes (issue #4258): the Gateway's open desktop relay streams, keyed by the fresh stream id the Gateway
/// minted. The browser-facing leg registers a stream before it sends the open command; the Director then calls the
/// hub's <see cref="DesktopRelayProtocol.HubMethod"/>, which lands in <see cref="Attach"/> and hands the leg the
/// Director's frames. The frames the leg writes into <see cref="DesktopRelayPending.Down"/> are what the hub returns
/// to the Director.
///
/// The same ownership rule as the terminal's up-streams (issue #1923): a stream records the tenant that opened it
/// and the Director the open went to, and only that Director, in that tenant, may attach to it. Anyone else is
/// refused loudly.
/// </summary>
public sealed class DesktopRelayRegistry
{
    private readonly ConcurrentDictionary<string, DesktopRelayPending> _streams = new(StringComparer.Ordinal);

    /// <summary>The streams registered and not yet removed. For diagnostics and tests.</summary>
    public int LiveStreamCount => _streams.Count;

    /// <summary>Register a stream the Gateway is about to open. A duplicate id fails loudly (ids are fresh Guids).</summary>
    public DesktopRelayPending Register(string streamId, StreamOwner owner)
    {
        if (string.IsNullOrEmpty(streamId)) throw new ArgumentException("streamId is required", nameof(streamId));
        if (!owner.Tenant.IsValid) throw new ArgumentException("stream owner tenant is required", nameof(owner));
        if (string.IsNullOrWhiteSpace(owner.DirectorId)) throw new ArgumentException("stream owner directorId is required", nameof(owner));
        var pending = new DesktopRelayPending(streamId, owner);
        if (!_streams.TryAdd(streamId, pending))
            throw new InvalidOperationException($"desktop stream id already registered (ids must be fresh and unique): {streamId}");
        FileLog.Write($"[DesktopRelayRegistry] registered stream {streamId} ({owner.ToLogString()}, live={_streams.Count})");
        return pending;
    }

    /// <summary>Forget a stream (the leg is done with it). Idempotent. Ends the frames going down if they are still open.</summary>
    public void Remove(string streamId)
    {
        if (!_streams.TryRemove(streamId, out var pending)) return;
        pending.Down.Writer.TryComplete();
        pending.Abandon();
        FileLog.Write($"[DesktopRelayRegistry] removed stream {streamId} (live={_streams.Count})");
    }

    /// <summary>
    /// The Director's call, from the hub: attach its frames (<paramref name="up"/>) to the registered stream and
    /// return the frames going down to it. <paramref name="hubCancellation"/> fires when the Director's connection
    /// ends, which is how the browser learns its Director disconnected.
    ///
    /// A stream id nobody registered (the browser already left) gets one Closed frame and the Director's frames are
    /// read to their end and dropped, so nothing sits unread on the shared connection. A caller that does not own the
    /// stream is REFUSED with <see cref="StreamOwnershipDeniedException"/> and the stream is left untouched.
    /// </summary>
    public IAsyncEnumerable<DesktopRelayFrame> Attach(string streamId, StreamOwner caller, IAsyncEnumerable<DesktopRelayFrame> up, CancellationToken hubCancellation)
    {
        if (up is null) throw new ArgumentNullException(nameof(up));
        if (!_streams.TryGetValue(streamId, out var pending))
        {
            FileLog.Write($"[DesktopRelayRegistry] Attach for unknown or closed stream {streamId} ({caller.ToLogString()}); answering Closed and draining");
            _ = DrainAsync(streamId, up, hubCancellation);
            return OneClosed("the Gateway is no longer waiting for this stream");
        }
        if (!pending.Owner.Matches(caller))
        {
            FileLog.Write($"[DesktopRelayRegistry] Attach REFUSED for stream {streamId}: caller ({caller.ToLogString()}) does not own it (owner {pending.Owner.ToLogString()})");
            throw new StreamOwnershipDeniedException($"desktop stream {streamId} is not owned by the calling Director");
        }
        if (!pending.TryAttach(up, hubCancellation))
        {
            FileLog.Write($"[DesktopRelayRegistry] Attach REFUSED for stream {streamId}: it is already attached");
            throw new InvalidOperationException($"desktop stream {streamId} is already attached");
        }
        FileLog.Write($"[DesktopRelayRegistry] attached stream {streamId} ({caller.ToLogString()})");
        return pending.Down.Reader.ReadAllAsync(hubCancellation);
    }

    private static async Task DrainAsync(string streamId, IAsyncEnumerable<DesktopRelayFrame> up, CancellationToken ct)
    {
        try
        {
            await foreach (var _ in up.WithCancellation(ct).ConfigureAwait(false)) { }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DesktopRelayRegistry] drain of unknown stream {streamId} ended: {ex.Message}");
        }
    }

    private static async IAsyncEnumerable<DesktopRelayFrame> OneClosed(string reason)
    {
        await Task.CompletedTask;
        yield return new DesktopRelayFrame { Kind = DesktopRelayFrameKind.Closed, CloseStatus = 1001, Reason = reason };
    }
}

/// <summary>One registered desktop relay stream, waiting for (or holding) the Director's frames.</summary>
public sealed class DesktopRelayPending
{
    private readonly TaskCompletionSource<DesktopRelayAttachment> _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DesktopRelayPending(string streamId, StreamOwner owner)
    {
        StreamId = streamId;
        Owner = owner;
    }

    public string StreamId { get; }
    public StreamOwner Owner { get; }

    /// <summary>The frames going down to the Director. The leg's pump writes them; the hub returns them.</summary>
    public Channel<DesktopRelayFrame> Down { get; } = Channel.CreateUnbounded<DesktopRelayFrame>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Completes when the Director attaches. Cancelled when the stream is removed first.</summary>
    public Task<DesktopRelayAttachment> Attached => _attached.Task;

    internal bool TryAttach(IAsyncEnumerable<DesktopRelayFrame> up, CancellationToken hubCancellation) =>
        _attached.TrySetResult(new DesktopRelayAttachment(up, hubCancellation));

    internal void Abandon() => _attached.TrySetCanceled();
}

/// <summary>The Director's side of an attached stream: its frames, and the token that fires when its connection ends.</summary>
public sealed record DesktopRelayAttachment(IAsyncEnumerable<DesktopRelayFrame> Up, CancellationToken HubCancellation);
