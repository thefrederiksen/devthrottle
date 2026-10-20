using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Boxes;

/// <summary>
/// The bytes one relay stream (one desktop socket or one request) carried, in each direction. Every byte toward
/// the browser leaves Azure as paid outbound traffic, so the relay writes these to the normal log while a socket
/// is open and again when it closes (Factory Boxes, issue #4258, the Delivery Lead's spend requirement).
/// </summary>
public sealed class DesktopRelayUsage
{
    private long _toBrowser;
    private long _toBox;

    public DesktopRelayUsage(string boxId, string streamId, string side)
    {
        BoxId = boxId;
        StreamId = streamId;
        Side = side;
    }

    public string BoxId { get; }
    public string StreamId { get; }

    /// <summary>"gateway" or "director": which end of the tunnel counted these bytes.</summary>
    public string Side { get; }

    public long BytesToBrowser => Interlocked.Read(ref _toBrowser);
    public long BytesToBox => Interlocked.Read(ref _toBox);

    public void AddToBrowser(long bytes) => Interlocked.Add(ref _toBrowser, bytes);
    public void AddToBox(long bytes) => Interlocked.Add(ref _toBox, bytes);

    /// <summary>One log line for this stream, with <paramref name="when"/> saying whether it is a periodic or a closing line.</summary>
    public string Describe(string when) =>
        $"box={BoxId} stream={StreamId} side={Side} {when}: toBrowser={BytesToBrowser} bytes, toBox={BytesToBox} bytes";
}

/// <summary>
/// The bytes relayed per box since this process started, across all its sockets and requests, in each direction.
/// One per Gateway or Director process. A stream adds its bytes to it as they move, so the box line is current
/// whenever it is written.
/// </summary>
public sealed class DesktopRelayBoxTally
{
    private sealed class Totals
    {
        public long ToBrowser;
        public long ToBox;
    }

    private readonly ConcurrentDictionary<string, Totals> _boxes = new(StringComparer.Ordinal);

    public void AddToBrowser(string boxId, long bytes) => Interlocked.Add(ref _boxes.GetOrAdd(boxId, _ => new Totals()).ToBrowser, bytes);
    public void AddToBox(string boxId, long bytes) => Interlocked.Add(ref _boxes.GetOrAdd(boxId, _ => new Totals()).ToBox, bytes);

    /// <summary>The totals for one box (zero for a box never seen).</summary>
    public (long ToBrowser, long ToBox) For(string boxId) =>
        _boxes.TryGetValue(boxId, out var t) ? (Interlocked.Read(ref t.ToBrowser), Interlocked.Read(ref t.ToBox)) : (0, 0);

    /// <summary>Write the box's running totals to the log.</summary>
    public void Log(string boxId, string side)
    {
        var (toBrowser, toBox) = For(boxId);
        FileLog.Write($"[DesktopRelayUsage] box={boxId} side={side} total since start: toBrowser={toBrowser} bytes, toBox={toBox} bytes");
    }
}
