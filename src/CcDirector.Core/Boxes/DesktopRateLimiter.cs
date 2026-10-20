using CcDirector.Core.Utilities;

namespace CcDirector.Core.Boxes;

/// <summary>
/// The ceiling on how fast one box's bytes travel toward the browser (Factory Boxes, issue #4258). A token
/// bucket: it fills at <c>bytesPerSecond</c> up to <c>burstBytes</c>, and a send takes its size out of it. When
/// the bucket is empty the caller waits - on the injected clock, never the real one in a test - so the relay reads
/// the box more slowly. A box cannot talk its way past it: it is not a setting the box or the page can change.
///
/// One instance is shared by every socket and request of one box, so opening more sockets does not buy more rate.
/// A send larger than what is in the bucket is let through as soon as the bucket is above zero and drives it
/// negative, so one large frame waits its turn instead of never fitting.
/// </summary>
public sealed class DesktopRateLimiter
{
    private readonly object _gate = new();
    private readonly double _bytesPerSecond;
    private readonly double _burstBytes;
    private readonly TimeProvider _time;
    private double _tokens;
    private long _lastRefillTimestamp;

    /// <summary>How many times this limiter has made a caller wait. For logs and tests.</summary>
    public long ThrottledWaits { get; private set; }

    public DesktopRateLimiter(long bytesPerSecond, long burstBytes, TimeProvider time)
    {
        if (bytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        if (burstBytes <= 0) throw new ArgumentOutOfRangeException(nameof(burstBytes));
        _bytesPerSecond = bytesPerSecond;
        _burstBytes = burstBytes;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _tokens = burstBytes;
        _lastRefillTimestamp = time.GetTimestamp();
    }

    /// <summary>Wait until the bucket allows <paramref name="bytes"/> more bytes, then take them.</summary>
    public async Task AcquireAsync(int bytes, CancellationToken ct)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes == 0) return;
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                Refill();
                if (_tokens > 0)
                {
                    _tokens -= bytes;
                    return;
                }
                // Wait until the bucket is back above zero, plus one millisecond so the next look finds it there.
                wait = TimeSpan.FromSeconds(-_tokens / _bytesPerSecond) + TimeSpan.FromMilliseconds(1);
                ThrottledWaits++;
            }
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_lastRefillTimestamp, now);
        _lastRefillTimestamp = now;
        if (elapsed <= TimeSpan.Zero) return;
        _tokens = Math.Min(_burstBytes, _tokens + elapsed.TotalSeconds * _bytesPerSecond);
    }

    /// <summary>
    /// Build the limiter one box gets: <paramref name="bytesPerSecond"/> with one second's worth as the burst, so one
    /// keyframe passes whole. Production passes <see cref="Gateway.Contracts.DesktopRelayLimits.MaxBoxBytesPerSecond"/>.
    /// </summary>
    public static DesktopRateLimiter ForBox(long bytesPerSecond, TimeProvider time)
    {
        FileLog.Write($"[DesktopRateLimiter] ForBox: ceiling={bytesPerSecond} bytes a second, burst={bytesPerSecond}");
        return new DesktopRateLimiter(bytesPerSecond, bytesPerSecond, time);
    }
}
