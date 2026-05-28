namespace CcDirectorClient.Voice;

/// <summary>
/// 9-bar audio-level equalizer that bounces with the live microphone input
/// from an <see cref="IUtteranceRecorder"/>. Shared by both dictation dialog
/// modes (the FIFO voice dialog and the speak-into-textbox dialog) so the
/// visual confirmation "the mic is hearing you" looks identical everywhere.
///
/// The recorder only exposes a single peak amplitude per read, not a real
/// spectrum, so the bars are decorative: each bar is offset from the shared
/// peak so neighbours dance at slightly different heights and the whole
/// thing reads as alive instead of a single flat line going up and down.
/// </summary>
public partial class BouncingBarsView : ContentView
{
    // Tick fast enough to feel responsive (smooth animation) without burning
    // battery. ~16 fps is plenty for a level meter and easy on the dispatcher.
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(60);

    // Visual cap: the well is 92px tall (matches the XAML) and a bar shorter
    // than this is the "floor" idle state when the room is silent.
    private const double WellHeight = 92;
    private const double FloorHeight = 6;

    // Per-bar phase offset so neighbouring bars never have identical heights -
    // the whole row would otherwise look like a single fat bar going up and down.
    // The numbers are arbitrary multipliers in 0.4..1.0; the centre bars trend
    // louder than the edges so the equaliser feels naturally centre-heavy.
    private static readonly double[] BarOffsets =
        { 0.55, 0.70, 0.85, 0.95, 1.00, 0.95, 0.85, 0.70, 0.55 };

    private readonly Border[] _bars;
    private readonly double[] _smoothed = new double[9];
    private IDispatcherTimer? _timer;
    private IUtteranceRecorder? _recorder;

    public BouncingBarsView()
    {
        InitializeComponent();
        _bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8 };
    }

    /// <summary>
    /// Start animating against <paramref name="recorder"/>. Safe to call
    /// repeatedly - the existing timer is stopped first. The recorder does
    /// not have to be recording yet; bars will simply sit at the floor until
    /// audio comes in.
    /// </summary>
    public void Start(IUtteranceRecorder recorder)
    {
        Stop();
        _recorder = recorder;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TickInterval;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Stop animating and reset every bar back to the floor.</summary>
    public void Stop()
    {
        if (_timer is not null)
        {
            _timer.Tick -= OnTick;
            _timer.Stop();
            _timer = null;
        }
        _recorder = null;
        for (var i = 0; i < _bars.Length; i++)
        {
            _smoothed[i] = 0;
            _bars[i].HeightRequest = FloorHeight;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // ReadLevel is the recorder's peak amplitude since the last call, already
        // mapped to 0..1 with a sqrt curve so the meter looks linear to the ear.
        var peak = _recorder?.ReadLevel() ?? 0;
        for (var i = 0; i < _bars.Length; i++)
        {
            // Target height for this bar: peak shaped by the per-bar offset, with
            // a small floor so even silence shows the structure (not zero bars).
            var target = Math.Clamp(peak * BarOffsets[i], 0, 1);

            // Exponential smoothing so the bar settles instead of strobing. Rising
            // is faster than falling so loud peaks are visible without a long lag.
            var alpha = target > _smoothed[i] ? 0.55 : 0.30;
            _smoothed[i] = _smoothed[i] + (target - _smoothed[i]) * alpha;

            var h = FloorHeight + _smoothed[i] * (WellHeight - FloorHeight);
            _bars[i].HeightRequest = h;
        }
    }
}
