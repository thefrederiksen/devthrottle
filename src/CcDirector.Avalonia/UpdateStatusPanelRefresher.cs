using CcDirector.Core.Update;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// Reads the update status off the window's thread and hands the result back to it, one read at a
/// time.
///
/// <see cref="UpdateStatusBoard.Current"/> is not free: it loads the updater's state file, reads the
/// launcher's discovery record and looks the launcher up in the process table. Until 22 September
/// 2026 the panel called it straight from a 20-second <c>DispatcherTimer</c> tick and from every
/// update event, so two file reads and a process lookup ran on the thread that paints the terminal,
/// three times a minute, for the life of the window. Now the read runs on the pool and only the
/// painting stays on the window's thread. A refresh asked for while one is already running does not
/// start a second read; it is remembered and one trailing read runs when the current one finishes,
/// so nothing a caller asked for is dropped and nothing is read twice for the same reason.
/// </summary>
public sealed class UpdateStatusPanelRefresher
{
    private readonly Func<UpdateStatusView?> _read;
    private readonly Action<UpdateStatusView?> _render;
    private readonly Func<Func<UpdateStatusView?>, Task<UpdateStatusView?>> _offTheWindowThread;
    private readonly Action<Action> _onTheWindowThread;
    private readonly object _gate = new();
    private bool _reading;
    private bool _askedAgain;

    /// <summary>How many reads have been started. For tests and the log.</summary>
    public int ReadsStarted { get; private set; }

    /// <param name="read">Produces the status. Runs OFF the window's thread.</param>
    /// <param name="render">Paints the status. Runs ON the window's thread.</param>
    /// <param name="offTheWindowThread">Where the read runs; the pool by default. A test seam.</param>
    /// <param name="onTheWindowThread">Where the render runs; the dispatcher by default. A test seam.</param>
    public UpdateStatusPanelRefresher(
        Func<UpdateStatusView?> read,
        Action<UpdateStatusView?> render,
        Func<Func<UpdateStatusView?>, Task<UpdateStatusView?>>? offTheWindowThread = null,
        Action<Action>? onTheWindowThread = null)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _offTheWindowThread = offTheWindowThread ?? (work => Task.Run(work));
        _onTheWindowThread = onTheWindowThread ?? (work => global::Avalonia.Threading.Dispatcher.UIThread.Post(work));
    }

    /// <summary>Ask for a fresh status. Returns at once; the panel repaints when the read lands.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (_reading)
            {
                _askedAgain = true;
                return;
            }
            _reading = true;
            ReadsStarted++;
        }
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var status = await _offTheWindowThread(_read).ConfigureAwait(false);
            _onTheWindowThread(() => _render(status));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateStatusPanelRefresher] refresh FAILED: {ex.Message}");
        }
        finally
        {
            bool again;
            lock (_gate)
            {
                _reading = false;
                again = _askedAgain;
                _askedAgain = false;
            }
            if (again)
                Refresh();
        }
    }
}
