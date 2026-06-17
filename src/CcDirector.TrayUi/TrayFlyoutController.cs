using Avalonia.Threading;

namespace CcDirector.TrayUi;

/// <summary>
/// Owns the single live <see cref="TrayFlyout"/> and turns a tray-icon LEFT-CLICK into an
/// open/close toggle. Builds a fresh model on each open (so the panel shows current state) via the
/// supplied factory.
///
/// The tricky bit is the toggle: clicking the tray icon while the flyout is open first deactivates
/// the flyout (which closes it) AND then raises the icon's Clicked - so a naive handler would close
/// then immediately reopen. A short post-close debounce swallows that reopen, giving a clean toggle.
/// </summary>
public sealed class TrayFlyoutController
{
    private readonly Func<TrayFlyoutModel> _build;
    private TrayFlyout? _current;
    private DateTime _lastClosedUtc = DateTime.MinValue;

    public TrayFlyoutController(Func<TrayFlyoutModel> build)
        => _build = build ?? throw new ArgumentNullException(nameof(build));

    /// <summary>Open the flyout if closed, close it if open. Safe to call from the tray Clicked handler.</summary>
    public void Toggle() => Dispatcher.UIThread.Post(() =>
    {
        if (_current is not null)
        {
            _current.Close();
            return;
        }

        // A click that just deactivated+closed the flyout also fires Clicked; don't reopen on it.
        if ((DateTime.UtcNow - _lastClosedUtc).TotalMilliseconds < 300)
            return;

        var flyout = new TrayFlyout(_build());
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, flyout)) _current = null;
            _lastClosedUtc = DateTime.UtcNow;
        };
        _current = flyout;
        flyout.Show();
        flyout.Activate();
    });

    /// <summary>Close the flyout if it is open (e.g. on app shutdown).</summary>
    public void Close() => Dispatcher.UIThread.Post(() => _current?.Close());
}
