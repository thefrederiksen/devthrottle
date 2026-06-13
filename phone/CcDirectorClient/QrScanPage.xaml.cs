using CcDirectorClient.Voice;
using ZXing.Net.Maui;

namespace CcDirectorClient;

/// <summary>
/// Modal camera page that scans the Gateway "Connect a phone" pairing QR (issue #386). It is
/// pushed by <see cref="TalkPage"/> after the CAMERA permission is granted; on the first decoded
/// barcode it pops itself and resolves <see cref="ScannedAsync"/> with the raw QR text (which the
/// caller hands to <see cref="PairingLink.Parse"/>). Cancelling or popping resolves with null.
///
/// The reader is configured for QR only (the pairing payload is a QR) and stops detecting the
/// moment one is read so the same code is not fired twice while the page tears down.
/// </summary>
public partial class QrScanPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Guards the single-shot return: the camera fires BarcodesDetected continuously, so the first
    // hit must claim the result and every later one is ignored.
    private bool _handled;

    public QrScanPage()
    {
        InitializeComponent();
        Reader.Options = new BarcodeReaderOptions
        {
            Formats = BarcodeFormats.TwoDimensional,
            AutoRotate = true,
            Multiple = false,
        };
    }

    /// <summary>
    /// Awaitable result of the scan: the raw decoded QR text, or null if the user cancelled or
    /// navigated away. Resolves exactly once.
    /// </summary>
    public Task<string?> ScannedAsync => _tcs.Task;

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_handled) return;
        var value = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(value)) return;

        _handled = true;
        Reader.IsDetecting = false;
        // The event is raised off the UI thread; pop + resolve on the main thread.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _tcs.TrySetResult(value);
            await Navigation.PopModalAsync();
        });
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        if (_handled) return;
        _handled = true;
        Reader.IsDetecting = false;
        _tcs.TrySetResult(null);
        await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        // Hardware/system back == cancel: resolve null so the caller never blocks on the await.
        _tcs.TrySetResult(null);
        return base.OnBackButtonPressed();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Whatever the exit path, never leave the camera running or the awaiter hanging.
        Reader.IsDetecting = false;
        _tcs.TrySetResult(null);
    }
}
