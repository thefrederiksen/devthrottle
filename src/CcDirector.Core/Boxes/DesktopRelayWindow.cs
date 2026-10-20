using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Boxes;

/// <summary>
/// The sending half of one relay stream's flow control: how many bytes of frames this side may still send before
/// the far side grants some back. See <see cref="DesktopRelayProtocol"/> for why the desktop leg carries its own
/// window instead of leaning on the tunnel's shared stream buffer.
///
/// One sender acquires (the pump's endpoint reader); the far side's Credit frames release. A release that would
/// take the window above its size is a protocol fault on the far side and throws, so a confused peer is loud.
/// </summary>
public sealed class DesktopRelayWindow
{
    private readonly object _gate = new();
    private readonly int _size;
    private int _available;
    private TaskCompletionSource? _waiter;

    public DesktopRelayWindow(int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size), "a window must hold at least one byte");
        _size = size;
        _available = size;
    }

    /// <summary>The bytes this side may send right now without waiting. For logs and tests.</summary>
    public int Available
    {
        get { lock (_gate) return _available; }
    }

    /// <summary>
    /// Wait until <paramref name="cost"/> bytes are free, then take them. Waits only on the far side's credit,
    /// never on a timer. A cost above the window size can never be met and throws at once.
    /// </summary>
    public async Task AcquireAsync(int cost, CancellationToken ct)
    {
        if (cost < 0) throw new ArgumentOutOfRangeException(nameof(cost));
        if (cost > _size) throw new ArgumentOutOfRangeException(nameof(cost), $"a frame costing {cost} bytes can never fit a {_size}-byte window");
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_available >= cost)
                {
                    _available -= cost;
                    return;
                }
                _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _waiter.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>The far side delivered <paramref name="credit"/> bytes: give them back to the sender.</summary>
    public void Release(int credit)
    {
        if (credit <= 0) throw new InvalidOperationException($"the far side granted {credit} bytes of credit; a grant must be positive");
        TaskCompletionSource? wake;
        lock (_gate)
        {
            if ((long)_available + credit > _size)
                throw new InvalidOperationException($"the far side granted {credit} bytes of credit with {_available} of {_size} already free - more than was ever sent");
            _available += credit;
            wake = _waiter;
            _waiter = null;
        }
        wake?.TrySetResult();
    }
}
