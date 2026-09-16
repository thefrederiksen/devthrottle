using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Avalonia.HostedAi;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Modal dialog for desktop dictation. Whole-audio batch (issue #589) with a
/// Pause checkpoint restored on top of it. See the canonical contract in
/// docs/architecture/dictation/DICTATION_UX_SPEC.md.
///
/// The microphone captures the whole segment locally; NO text appears while the
/// user is speaking (there is no live partial preview and no realtime streaming
/// socket). Text is produced only at a CHECKPOINT - Pause, Insert, or Send - by
/// sending the whole captured segment ONCE through the shared
/// <see cref="BatchDictationRecorder"/> batch pipeline, which transcribes via the
/// user-selected method and applies the dictionary corrector only.
///
/// Pause is a checkpoint, not an ending: it transcribes the current segment,
/// appends it to the accumulated transcript, and shows it (editable) without
/// ending the turn. Resume is disabled while that transcription runs ("you cannot
/// resume until it has been transcribed"); afterwards Resume starts a fresh
/// segment that appends to the (possibly edited) text. The visible transcript is
/// the accumulation of every transcribed segment.
///
/// Commit actions:
///
///   Cancel - close, no text. An interrupted/cancelled turn produces no transcript.
///   Insert - transcribe the current segment (if recording), append, close with
///            <see cref="ResultText"/> populated and <see cref="ShouldSubmit"/>
///            FALSE so the caller inserts the text at the caret without auto-submit.
///   Send   - same as Insert but closes with <see cref="ShouldSubmit"/> TRUE so the
///            caller auto-submits the prompt.
///   Pause  - transcribe the current segment, append, stay in the dialog showing
///            PAUSED (editable); Resume to keep talking, or Insert/Send to commit.
///
/// All audio capture and transcription happen in-process via
/// <see cref="BatchDictationRecorder"/>. No browser, no localhost WebSocket
/// roundtrip, no realtime socket.
/// </summary>
public partial class SpeakDialog : Window
{
    private enum Stage { Connecting, Recording, Transcribing, Paused, Failed }

    private readonly AgentOptions _options;
    private readonly Border[] _bars;
    private readonly double[] _barTargets = new double[9];

    // Plays the water-drop "ready" cue the instant the mic goes live. Best-effort:
    // a failed sound never disrupts the dictation turn.
    private readonly DesktopAudioCue _audioCue = new();

    // Backstop for the GETTING READY state: if the selected mic never delivers a
    // first audio buffer, we must not strand the user on "GETTING READY" forever.
    // On expiry we fail loud with a clear "check your microphone" message instead of
    // silently pretending to record. Cancelled the moment real audio arrives.
    private readonly DispatcherTimer _readyTimeout;

    // Decaying peak of the raw int16 input RMS, tracked while recording.
    private double _recentPeakRms;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _eqTimer;

    // Recording-time accumulator. _t0 is the start of the CURRENT segment's
    // capture; _elapsedBeforeSegment holds the total capture time of all prior
    // segments. The displayed elapsed time is
    // _elapsedBeforeSegment + (now - _t0) while recording, then frozen.
    private DateTime _t0;
    private TimeSpan _elapsedBeforeSegment = TimeSpan.Zero;

    // The dialog opens into GETTING READY and holds there until the microphone
    // actually delivers its first audio buffer; only then does it flip to RECORDING
    // (and play the ready cue). So neither the red state nor the sound ever claims
    // the mic is live before audio is really flowing.
    private Stage _stage = Stage.Connecting;
    private BatchDictationRecorder? _service;

    /// <summary>
    /// Set the moment the window closes, BEFORE the async teardown runs, and checked after every
    /// suspension point in startup. A recorder owns a live NAudio capture thread that roots the whole
    /// object graph, so one built after the dialog has gone can never be collected and never stops
    /// recording. Startup must therefore refuse to open - or refuse to publish - a recorder once this
    /// is true, because nothing will come along later to dispose it.
    /// </summary>
    private volatile bool _closed;

    /// <summary>
    /// TEST SEAM: builds the recorder, so a test can hold construction at a barrier and control the
    /// exact interleaving with <see cref="Window.Close"/>.
    ///
    /// It exists because the close race cannot otherwise be tested deterministically. Without it a
    /// test drives the REAL recorder and a real microphone, so on a machine with no capture device
    /// <c>StartAsync</c> throws and self-disposes - and the regression test then passes even with the
    /// close guards removed. That is a test that cannot fail, which is worse than no test.
    /// Null in production, where the recorder is constructed directly.
    /// </summary>
    internal Func<MicDevice, Task<BatchDictationRecorder>>? RecorderFactoryForTests;

    /// <summary>
    /// TEST SEAM: plays the ready cue in place of the speakers, receiving the "playback finished" and
    /// "playback failed" callbacks, so a test can prove the dialog hands the cue's outcome to the recorder it
    /// was played into (issue #2925, ruling L1) without an audio device. Null in production, where
    /// <see cref="DesktopAudioCue"/> plays.
    /// </summary>
    internal Action<Action?, Action<string>?>? ReadyCueForTests;

    /// <summary>
    /// TEST SEAMS for the device queries (issue #2929): resolve the saved microphone name to a device
    /// number and its display name, and list the devices for the selector. Both run OFF the interface thread; a test holds
    /// them at a barrier to prove the dialog shows GETTING READY and opens the microphone without waiting
    /// for the list. Null in production, where <see cref="MicDevices"/> answers.
    /// </summary>
    internal Func<MicDevice>? ResolveMicForTests;
    internal Func<IReadOnlyList<MicDevice>>? EnumerateMicsForTests;

    /// <summary>TEST SEAM: the saved microphone name, in place of reading config.json. Null in production.</summary>
    internal Func<string?>? PersistedMicNameForTests;

    // Inspection two, finding 3: device resolution can outlast the GETTING READY window. While it is
    // outstanding these say so, so the timeout can name what it was waiting for. Set and read on the
    // interface thread, except the saved name, which the background resolution writes.
    private bool _resolvingSavedMic;
    private long _resolveStartedAt;
    private volatile string? _savedMicBeingResolved;

    /// <summary>
    /// TEST SEAM for the start-time history (issue #2928): receives each measured start (device name,
    /// milliseconds) in place of writing it to config.json. Null in production.
    /// </summary>
    internal Action<string, int>? RecordMicStartForTests;

    /// <summary>TEST SEAM: fires the GETTING READY backstop now, as its timer does after six seconds with no
    /// audio. The headless test platform does not run dispatcher timers.</summary>
    internal void RaiseReadyTimeoutForTests() => OnReadyTimeout();

    // When the user asked for the current microphone (open, Resume, or a device switch), as a Stopwatch
    // timestamp. Handed to each recorder so its first-audio log line can say how long the click waited.
    private long _micRequestedAt;

    // The accumulated, dictionary-corrected transcript across every checkpointed
    // segment so far. Grows on each Pause / commit-from-recording. Never populated
    // during recording (no live preview). While PAUSED the user may edit the box,
    // and Resume re-seeds this from the box so edits are preserved, not rewritten.
    private string _accumulatedText = "";

    // WaveIn device number the current recorder captures from. Defaults to
    // the Windows default mic; overridden by the persisted choice on open and by
    // the user via the mic selector. _suppressMicChange guards the programmatic
    // selection we make while populating the ComboBox from firing a restart.
    private int _selectedDeviceNumber = MicDevices.DefaultDeviceNumber;
    // The selected device's display name, resolved off the interface thread with the number (issue #2929) and
    // handed to every recorder, so building one never queries Windows on the interface thread.
    private string _selectedDeviceDescription = "";
    private bool _suppressMicChange;

    // Typical wake-up figures measured while this dialog is open, by device name (inspection three, finding 2).
    // The selector's list is read once at opening, so a figure recorded afterwards is shown by refreshing that
    // entry - and kept here so a list that arrives after the measurement still carries it.
    private readonly Dictionary<string, int> _measuredStartMs = new(StringComparer.Ordinal);

    /// <summary>
    /// The text the user accepted (dictionary-corrected). Null if cancelled.
    /// </summary>
    public string? ResultText { get; private set; }

    /// <summary>
    /// True when the dialog closed via Send; the caller should auto-submit the
    /// prompt. False when closed via Insert or Cancel.
    /// </summary>
    public bool ShouldSubmit { get; private set; }

    /// <summary>
    /// Opt in to fire-and-forget Send (spec section 10). When true, pressing Send while RECORDING
    /// hands the still-capturing recorder to the caller and closes the dialog IMMEDIATELY instead of
    /// blocking on transcription - the caller then transcribes + submits in the background while the
    /// session shows orange "Transcribing...". Default false keeps the blocking behavior for callers
    /// that cannot run a background send (e.g. the Wingman surface; they read <see cref="ResultText"/>).
    /// </summary>
    public bool EnableBackgroundSend { get; set; }

    /// <summary>True when the dialog closed for a fire-and-forget Send: the caller must transcribe
    /// <see cref="BackgroundRecorder"/> in the background (joining onto <see cref="BackgroundPrefix"/>)
    /// and submit the result. <see cref="ResultText"/> is null in this case.</summary>
    public bool IsBackgroundSend { get; private set; }

    /// <summary>The still-capturing recorder handed to the caller for a fire-and-forget Send. The
    /// caller owns it: it calls <c>TranscribeAsync()</c> (which stops the mic) then disposes it. Null
    /// unless <see cref="IsBackgroundSend"/> is true.</summary>
    public BatchDictationRecorder? BackgroundRecorder { get; private set; }

    /// <summary>The already-transcribed text from earlier Pause/Resume segments, to prepend to the
    /// background segment's transcript. Empty in the common "just talk and Send" case.</summary>
    public string BackgroundPrefix { get; private set; } = "";

    public SpeakDialog(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        InitializeComponent();
        _bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8 };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => UpdateTimer();

        // Decay the equalizer bars at a steady rate so they fall smoothly.
        // OnAudioBands sets per-bar target heights; this timer animates toward them.
        _eqTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _eqTimer.Tick += (_, _) => StepEqualizer();

        // One-shot backstop for the GETTING READY state (restarted on each entry).
        // A healthy mic delivers its first buffer in well under 100 ms; six seconds
        // with nothing means the device is not capturing, so fail loud rather than
        // hang on GETTING READY.
        _readyTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _readyTimeout.Tick += (_, _) => OnReadyTimeout();

        Opened += async (_, _) => await OnDialogOpenedAsync();
        // The latch is set SYNCHRONOUSLY, before the async teardown starts. Startup is an async
        // sequence with real suspension points, so the window can close while it is mid-flight; the
        // teardown then finds no recorder to dispose and finishes, and the startup continuation
        // afterwards happily builds one and publishes it to a dialog that no longer exists. There is
        // no second Closed event to catch it, so that recorder - and the live NAudio capture thread
        // rooting it - would never be released. See _closed.
        Closed += (_, _) =>
        {
            _closed = true;
            _ = OnDialogClosedAsync();
        };

        // Window-level Enter = Send (insert transcript + auto-submit). Lets the
        // user complete the whole dictation flow with the keyboard only.
        // Escape = Cancel. Registered on the TUNNEL phase so the window decides
        // BEFORE the editable paused TextBox: while reviewing in the focused box
        // we let Enter tunnel through to insert a newline instead of sending.
        AddHandler(KeyDownEvent, SpeakDialog_KeyDown, RoutingStrategies.Tunnel);
    }

    private void SpeakDialog_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;

        if (e.Key == Key.Enter)
        {
            // While reviewing edits in the focused transcript box, Enter inserts
            // a newline (let it tunnel to the TextBox) rather than sending.
            if (_stage == Stage.Paused && TranscriptText.IsFocused)
                return;

            // Only fire when Send is actually actionable. During Transcribing
            // PrimaryButton is disabled; during the error path it is hidden.
            if (PrimaryButton.IsVisible && PrimaryButton.IsEnabled)
            {
                FileLog.Write("[SpeakDialog] Enter -> PrimaryButton (Send)");
                e.Handled = true;
                PrimaryButton_Click(this, new RoutedEventArgs());
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            FileLog.Write("[SpeakDialog] Escape -> CancelButton");
            e.Handled = true;
            CancelButton_Click(this, new RoutedEventArgs());
        }
    }

    private async Task OnDialogOpenedAsync()
    {
        _micRequestedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _t0 = DateTime.UtcNow;
        _timer.Start();
        _eqTimer.Start();
        try
        {
            // Pre-flight (issue #940): do NOT record into a dead feature. If hosted AI is not ready
            // (out of credits, or no bring-your-own key), close without recording and show the ONE
            // shared "add credits / add a key" dialog over the owner instead of a raw failure later.
            var state = await DesktopHostedAiGate.CheckAsync();
            // The preflight is a real await; the user can close the dialog while it is outstanding.
            // Teardown has then already run and found nothing, so continuing here would open a
            // microphone nobody will ever close.
            if (_closed)
            {
                FileLog.Write("[SpeakDialog] closed during the hosted-AI preflight; not starting a recorder");
                return;
            }
            if (state != HostedAiState.Ready)
            {
                FileLog.Write($"[SpeakDialog] pre-flight not ready ({state}); not recording");
                await DesktopHostedAiGate.ShowAsync(this, state);
                Close();
                return;
            }

            // Show GETTING READY immediately (responsive). The device queries behind it - the saved
            // choice resolved to a device number, and the list for the selector - cost 57 to 243 ms
            // per open on the interface thread (issue #2929), so both now run in the background.
            SwitchToConnecting();

            // Resolve the saved mic choice to a current device number BEFORE the first capture
            // starts, so we record from the right device from the very first frame. The list is only
            // for the selector: start it at the same time and fill the selector whenever it arrives,
            // never holding the microphone for it.
            _resolvingSavedMic = true;
            _resolveStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var resolveTask = Task.Run(ResolveSavedMic);
            var devicesTask = Task.Run(EnumerateMics);
            _ = FillMicSelectorWhenListedAsync(devicesTask, resolveTask);

            // The window can close during this await; StartNewServiceAsync refuses to build a recorder then.
            MicDevice resolved;
            try
            {
                resolved = await resolveTask;
            }
            finally
            {
                _resolvingSavedMic = false;
            }

            // The ready window can run out while resolution is still outstanding. The dialog is then already
            // showing the failure, and a microphone opened now would record into it until it closes.
            if (_stage != Stage.Connecting)
            {
                FileLog.Write($"[SpeakDialog] microphone resolved after the dialog left GETTING READY (stage={_stage}); "
                    + "not opening it");
                return;
            }
            _selectedDeviceNumber = resolved.Number;
            _selectedDeviceDescription = resolved.Name;

            // Open the mic now. The flip to RECORDING + ready cue happens later, when real audio arrives.
            await StartNewServiceAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] StartAsync FAILED: {ex.Message}");
            SwitchToFailed("Failed to start recording: " + ex.Message);
        }
    }

    private async Task OnDialogClosedAsync()
    {
        _timer.Stop();
        _eqTimer.Stop();
        _readyTimeout.Stop();
        await DisposeServiceAsync();
    }

    /// <summary>
    /// Build and start a recorder for the current device, and take ownership of it in
    /// <see cref="_service"/>.
    ///
    /// THE RECORDER IS NEVER ORPHANED. A <see cref="BatchDictationRecorder"/> owns a NAudio
    /// <c>WaveInEvent</c> whose capture thread roots the whole object graph, so one that is dropped
    /// without being disposed can never be collected AND keeps appending audio forever. Every caller
    /// of this method (dialog open, Resume, mic switch) wraps it in a try/catch that leaves the dialog
    /// in a recoverable state - so before this fix, a throw anywhere after construction left a live,
    /// unreachable recorder behind while the dialog carried on. A 68-hour Director was measured with
    /// 87 of them, nine still recording, holding 12.69 GB.
    ///
    /// <see cref="BatchDictationRecorder.StartAsync"/> disposes itself if the mic fails to open, but
    /// it raises <c>OnCaptureStarted</c> AFTER capture is live - and that handler runs dialog code that
    /// can throw. The catch here covers that window and anything else added later.
    /// </summary>
    private async Task StartNewServiceAsync()
    {
        // Refuse to build one at all if the window has already gone - nothing would dispose it.
        if (_closed)
        {
            FileLog.Write("[SpeakDialog] dialog already closed; not building a recorder");
            return;
        }

        var svc = RecorderFactoryForTests is null
            ? new BatchDictationRecorder(_options, _selectedDeviceNumber, _selectedDeviceDescription)
            : await RecorderFactoryForTests(new MicDevice(_selectedDeviceNumber, _selectedDeviceDescription));
        svc.OnAudioBands += OnAudioBands;
        svc.OnInputRms += OnInputRms;
        svc.OnCaptureStarted += OnServiceCaptureStarted;
        svc.OnCaptureLive += OnServiceCaptureLive;
        svc.OnTranscriptionProgress += OnServiceTranscriptionProgress;
        svc.RequestedAtTimestamp = _micRequestedAt;
        try
        {
            await svc.StartAsync("default");
        }
        catch
        {
            // Dispose the LOCAL - _service is still whatever it was, so DisposeServiceAsync would not
            // reach this instance. DisposeAsync is idempotent, so double-disposing after StartAsync's
            // own cleanup is safe.
            try { await svc.DisposeAsync(); }
            catch (Exception disposeEx) { FileLog.Write($"[SpeakDialog] failed-start dispose error: {disposeEx.Message}"); }
            throw;
        }

        // StartAsync is awaited, so the window can have closed while the microphone was opening.
        // Teardown has already run and seen a null _service, so publishing now would strand a LIVE,
        // recording instance with no owner. Dispose the one we built instead.
        if (_closed)
        {
            FileLog.Write("[SpeakDialog] closed while the microphone was starting; disposing the recorder we just built");
            try { await svc.DisposeAsync(); }
            catch (Exception disposeEx) { FileLog.Write($"[SpeakDialog] closed-during-start dispose error: {disposeEx.Message}"); }
            return;
        }

        _service = svc;
    }


    private async Task DisposeServiceAsync()
    {
        var svc = _service;
        _service = null;
        if (svc is null) return;
        try { await svc.DisposeAsync(); }
        catch (Exception ex) { FileLog.Write($"[SpeakDialog] dispose error: {ex.Message}"); }
    }

    private MicDevice ResolveSavedMic()
    {
        var saved = PersistedMicNameForTests is not null ? PersistedMicNameForTests() : LoadPersistedMicName();
        _savedMicBeingResolved = saved ?? "(Windows default)";
        return ResolveMicForTests is not null ? ResolveMicForTests() : MicDevices.ResolveWithDescription(saved);
    }

    private IReadOnlyList<MicDevice> EnumerateMics()
    {
        var devices = EnumerateMicsForTests is not null ? EnumerateMicsForTests() : MicDevices.Enumerate();
        // Each device's measured wake-up time, for the selector (issue #2928). Read in the background with
        // the list, from the same config file the persisted choice lives in.
        return MicStartTimes.Decorate(devices, MicStartTimes.Read(CcDirectorConfigService.ReadRaw()));
    }

    /// <summary>
    /// Wait for the background device list and fill the selector with it on the interface thread. A
    /// failure to list devices leaves the selector empty and is logged; it never stops the recording,
    /// which does not depend on the list.
    /// </summary>
    private async Task FillMicSelectorWhenListedAsync(Task<IReadOnlyList<MicDevice>> devicesTask, Task<MicDevice> resolveTask)
    {
        IReadOnlyList<MicDevice> devices;
        int recordingFrom;
        try
        {
            devices = await devicesTask;
            // The selection marks the device being recorded from, so wait for that to be known too.
            // Read from the task, not the field: this continuation can run before the opening path's.
            recordingFrom = (await resolveTask).Number;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] listing microphones FAILED, selector left empty: {ex.Message}");
            return;
        }
        if (_closed) return;
        PopulateMicSelector(devices, recordingFrom);
        FileLog.Write($"[SpeakDialog] microphone selector filled: {devices.Count} entries");
    }

    /// <summary>Fill the mic selector with available devices and select the active one.</summary>
    private void PopulateMicSelector(IReadOnlyList<MicDevice> devices, int selectedDeviceNumber)
    {
        devices = devices
            .Select(d => _measuredStartMs.TryGetValue(d.Name, out var ms) ? d with { TypicalStartMs = ms } : d)
            .ToList();
        _suppressMicChange = true;
        MicSelector.ItemsSource = devices;
        int idx = 0;
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i].Number == selectedDeviceNumber) { idx = i; break; }
        }
        MicSelector.SelectedIndex = idx;
        _suppressMicChange = false;
    }

    private async void MicSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Ignore the programmatic selection PopulateMicSelector makes, and any
        // no-op re-selection of the device already in use.
        if (_suppressMicChange) return;
        if (MicSelector.SelectedItem is not MicDevice device) return;
        if (device.Number == _selectedDeviceNumber) return;

        try
        {
            // Persist the device NAME (indices reorder across replugs); the
            // Windows-default entry is stored as empty so it keeps tracking the
            // OS default rather than pinning to whatever it maps to today.
            PersistMicName(device.Number == MicDevices.DefaultDeviceNumber ? null : device.Name);
            await ChangeDeviceAsync(device);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] MicSelector_SelectionChanged FAILED: {ex.Message}");
            SwitchToFailed("Could not switch microphone: " + ex.Message);
        }
    }

    /// <summary>
    /// Switch the live capture to a different device. Tears down the current
    /// service and starts a fresh one on the new device. The current segment's
    /// audio is discarded (mixing two devices' audio into one clip is not
    /// meaningful), but the already-accumulated transcript from earlier segments
    /// is kept. The new segment's capture restarts the segment timer. The device's name comes from the list
    /// that was enumerated in the background - no device query runs here on the interface thread (issue #2929).
    /// </summary>
    private async Task ChangeDeviceAsync(MicDevice device)
    {
        _micRequestedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _selectedDeviceNumber = device.Number;
        _selectedDeviceDescription = device.Name;
        FileLog.Write($"[SpeakDialog] ChangeDevice: {device.Number} ({device.Name})");
        await DisposeServiceAsync();
        // Fresh device = fresh segment capture and fresh segment timer origin. Show
        // GETTING READY until the new device delivers audio, then flip + cue.
        _t0 = DateTime.UtcNow;
        SwitchToConnecting();
        await StartNewServiceAsync();
    }

    /// <summary>
    /// Terminal error state: recording or transcription failed. Freezes the timer,
    /// parks the equalizer gray, hides every action except Close.
    /// </summary>
    private void SwitchToFailed(string message)
    {
        _readyTimeout.Stop();
        _stage = Stage.Failed;
        TranscriptText.Text = message;
        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        TranscriptText.IsReadOnly = true;
        StatusLabel.Text = "ERROR";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
        PrimaryButton.IsVisible = false;
        StopButton.IsVisible = false;
        PauseButton.IsVisible = false;
        MicSelector.IsEnabled = false;
        CancelButton.Content = "Close";
        // Park the bars: nothing is being captured, the meter must not dance.
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 8.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
        LevelHint.Text = "";
    }

    private static string? LoadPersistedMicName()
    {
        var config = CcDirectorConfigService.ReadRaw();
        var name = config["dictation"]?["mic_device_name"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static void PersistMicName(string? name)
    {
        var patch = new JsonObject
        {
            ["dictation"] = new JsonObject { ["mic_device_name"] = name ?? "" },
        };
        CcDirectorConfigService.MergePatch(patch);
        FileLog.Write($"[SpeakDialog] PersistMicName: '{name ?? "(default)"}'");
    }

    private void UpdateTimer()
    {
        // Ticks only while audio is being captured (Recording). Frozen in every
        // other stage, most importantly Failed: a timer counting up next to an
        // ERROR label reads as "still recording" when nothing is (issue #189).
        // Shows the TOTAL capture across all segments so the user sees how long
        // they have dictated, not just the current segment.
        if (_stage != Stage.Recording) return;
        var elapsed = _elapsedBeforeSegment + (DateTime.UtcNow - _t0);
        var s = (int)elapsed.TotalSeconds;
        var tenths = elapsed.Milliseconds / 100;
        TimerLabel.Text = $"{s / 60}:{(s % 60):D2}.{tenths}";
    }

    private void OnAudioBands(double[] bands)
    {
        // Driven from NAudio's worker thread. Each band drives its own bar so
        // the bars move independently (real spectrum) rather than as one hill.
        // Update targets on the UI thread; the eqTimer animates toward them.
        Dispatcher.UIThread.Post(() =>
        {
            const double maxH = 92.0;
            const double minH = 8.0;
            int n = Math.Min(_barTargets.Length, bands.Length);
            for (int i = 0; i < n; i++)
            {
                double level = Math.Clamp(bands[i], 0.0, 1.0);
                _barTargets[i] = minH + (maxH - minH) * level;
            }
        });
    }

    private void OnInputRms(double rms)
    {
        // From NAudio's worker thread. Track a decaying peak on the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            _recentPeakRms = Math.Max(rms, _recentPeakRms * 0.97);
        });
    }

    private void StepEqualizer()
    {
        // Ease current height toward target. Faster up than down so loud beats
        // pop and quiet stretches decay smoothly.
        for (int i = 0; i < _bars.Length; i++)
        {
            var current = _bars[i].Height;
            var target = _barTargets[i];
            var diff = target - current;
            double step = diff >= 0 ? diff * 0.7 : diff * 0.32;
            var next = current + step;
            if (next < 8.0) next = 8.0;
            _bars[i].Height = next;
        }
    }

    /// <summary>
    /// Fired by the service the instant the microphone actually starts capturing.
    /// Re-anchors the current segment's elapsed-time origin so the displayed timer
    /// tracks REAL capture, not the dialog-open-to-capture (or resume) setup. The
    /// prior segments' time is preserved in <see cref="_elapsedBeforeSegment"/>.
    /// </summary>
    private void OnServiceCaptureStarted()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_stage == Stage.Recording)
                _t0 = DateTime.UtcNow;
        });
    }

    /// <summary>
    /// Fired the instant the microphone delivers its FIRST real audio buffer - the
    /// honest "ready to speak" moment. This is where the dialog flips from GETTING
    /// READY to RECORDING, anchors the timer to the true first-audio instant, and
    /// plays the water-drop ready cue - all together, so neither the red state nor the
    /// sound ever precedes real audio. Arrives on NAudio's worker thread. Guarded on
    /// the Connecting stage so it is a no-op if the state has already moved on (e.g. a
    /// mic switch mid-warmup) - and the recorder raises it only once regardless.
    ///
    /// The microphone is open while the cue plays, so the cue is recorded (issue #2925). The recorder is
    /// told the cue was handed to the device, so a Send inside the cue keeps capturing until the device
    /// reports an outcome. Only a report that the cue FINISHED lets the recorder search its audio for the
    /// cue and blank what it finds (ruling L1); a failure report, or none, blanks nothing. The recorder is captured here, not read later, so a cue that finishes
    /// after a mic switch or a background Send still reaches the recorder it was played into (a stopped or
    /// disposed recorder ignores it).
    /// </summary>
    private void OnServiceCaptureLive()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_stage != Stage.Connecting) return;
            _readyTimeout.Stop();
            _t0 = DateTime.UtcNow;
            SwitchToRecording();
            var recorder = _service;
            if (recorder is null)
            {
                FileLog.Write("[SpeakDialog] capture live with no published recorder; the cue will not be blanked");
                PlayReadyCue(null, null);
                return;
            }
            recorder.NoteReadyCuePlaying();
            PlayReadyCue(recorder.NoteReadyCueFinished, recorder.NoteReadyCueFailed);
            RecordMicStart(recorder);
        });
    }

    /// <summary>
    /// Add this start's measured wake-up time to the device's history (issue #2928), in the background -
    /// it is a config file write and must not hold the interface thread at the moment recording begins.
    /// A failure to save is logged; it never touches the recording.
    /// </summary>
    private void RecordMicStart(BatchDictationRecorder recorder)
    {
        if (recorder.StartToFirstAudioMs is not { } ms || string.IsNullOrWhiteSpace(recorder.DeviceDescription))
        {
            FileLog.Write("[SpeakDialog] capture live without a measured start time; nothing recorded for the selector");
            return;
        }
        var device = recorder.DeviceDescription!;
        if (RecordMicStartForTests is not null)
        {
            RecordMicStartForTests(device, ms);
            return;
        }
        _ = Task.Run(() =>
        {
            int typicalMs;
            try { typicalMs = MicStartTimes.Record(device, ms); }
            catch (Exception ex)
            {
                FileLog.Write($"[SpeakDialog] recording the microphone start time FAILED: {ex.Message}");
                return;
            }
            Dispatcher.UIThread.Post(() => ShowMeasuredStartTime(device, typicalMs));
        });
    }

    /// <summary>
    /// Show a just-recorded typical start time in the open selector (inspection three, finding 2): that
    /// device's entry is replaced with one carrying the new figure, the current selection is kept, and the
    /// change is suppressed so it never switches the microphone. If the list has not arrived yet the figure
    /// is kept and applied when it does.
    /// </summary>
    private void ShowMeasuredStartTime(string device, int typicalMs)
    {
        if (_closed) return;
        _measuredStartMs[device] = typicalMs;
        if (MicSelector.ItemsSource is not IReadOnlyList<MicDevice> devices)
        {
            FileLog.Write($"[SpeakDialog] measured start for \"{device}\" ({typicalMs} ms) kept until the selector is filled");
            return;
        }
        if (!devices.Any(d => d.Name == device))
        {
            FileLog.Write($"[SpeakDialog] measured start for \"{device}\" ({typicalMs} ms): no selector entry by that name");
            return;
        }
        var selectedIndex = MicSelector.SelectedIndex;
        _suppressMicChange = true;
        MicSelector.ItemsSource = devices
            .Select(d => d.Name == device ? d with { TypicalStartMs = typicalMs } : d)
            .ToList();
        MicSelector.SelectedIndex = selectedIndex;
        _suppressMicChange = false;
        FileLog.Write($"[SpeakDialog] selector entry for \"{device}\" now shows typicalMs={typicalMs}");
    }

    private void PlayReadyCue(Action? onPlaybackFinished, Action<string>? onPlaybackFailed)
    {
        if (ReadyCueForTests is not null)
            ReadyCueForTests(onPlaybackFinished, onPlaybackFailed);
        else
            _audioCue.PlayReady(onPlaybackFinished, onPlaybackFailed);
    }

    /// <summary>
    /// Backstop: the microphone never delivered a first audio buffer within the
    /// GETTING READY window. Rather than strand the user on GETTING READY, fail loud
    /// with a specific "check your microphone" message so they know what to fix. A
    /// no-op if audio has since arrived and the stage moved on.
    /// </summary>
    private void OnReadyTimeout()
    {
        _readyTimeout.Stop();
        if (_stage != Stage.Connecting) return;
        FileLog.Write("[SpeakDialog] microphone delivered no audio within the ready window");
        // One per-start line with the device and the timings (issue #2928), for the start most in need of it.
        if (_resolvingSavedMic)
        {
            // The microphone was never asked to start: finding which device it is took the whole window.
            var elapsedMs = (int)Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(_resolveStartedAt).TotalMilliseconds);
            FileLog.Write($"[SpeakDialog] ready window ran out while the saved microphone was still being resolved: "
                + $"elapsedMs={elapsedMs}, savedDevice=\"{_savedMicBeingResolved ?? "(not read yet)"}\"");
        }
        else if (_service is { } recorder)
        {
            recorder.LogNoAudioWithinReadyWindow();
        }
        else
        {
            // The recorder never finished starting, so it cannot speak for itself; the dialog knows the device
            // it asked for and when the user clicked.
            var clickMs = (int)Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(_micRequestedAt).TotalMilliseconds);
            FileLog.Write($"[BatchDictationRecorder] no first audio: device=\"{_selectedDeviceDescription}\", "
                + $"clickToTimeoutMs={clickMs}, startRecordingToTimeoutMs=unknown");
        }
        SwitchToFailed("The microphone did not start capturing. Check that it is connected, "
            + "not muted, and that DevThrottle is allowed to use it, then try again.");
    }

    /// <summary>
    /// Fired as the segment transcribes. A long segment is split into several bounded transcription
    /// requests, so show which part is running instead of a silent "Transcribing..." wait. A short
    /// segment reports a single part and keeps the plain message. May arrive off the UI thread.
    /// </summary>
    private void OnServiceTranscriptionProgress(int completed, int total)
    {
        if (total <= 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_stage != Stage.Transcribing) return;
            LevelHint.Text = completed >= total
                ? "Transcribing... finishing up"
                : $"Transcribing... part {completed + 1} of {total}";
        });
    }

    private async void PrimaryButton_Click(object? sender, RoutedEventArgs e)
    {
        // Send. From RECORDING with fire-and-forget enabled (spec section 10), hand the recorder to
        // the caller and close NOW so the screen is released immediately; the caller transcribes and
        // submits in the background. Without it (the Wingman surface), transcribe-then-close (blocking).
        // From PAUSED the text already exists, so it commits instantly either way.
        if (_stage == Stage.Recording)
        {
            if (EnableBackgroundSend)
                StartBackgroundSend();
            else
                await FinalizeFromRecordingAsync(submitOnClose: true);
        }
        else if (_stage == Stage.Paused)
        {
            // Use the (possibly edited) text from the box so the user's
            // corrections are what gets sent.
            CommitPausedText(submitOnClose: true);
        }
    }

    private async void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        // Insert: transcribe the current segment (if recording) and append, then
        // close with ShouldSubmit=false. The caller inserts the text at the caret
        // without auto-submitting so the user can review/edit in the prompt.
        if (_stage == Stage.Recording)
        {
            await FinalizeFromRecordingAsync(submitOnClose: false);
        }
        else if (_stage == Stage.Paused)
        {
            CommitPausedText(submitOnClose: false);
        }
    }

    private async void PauseButton_Click(object? sender, RoutedEventArgs e)
    {
        // Pause (while recording) is the checkpoint; Resume (while paused) starts
        // a fresh segment. The button is disabled while transcribing.
        if (_stage == Stage.Recording)
        {
            await PauseAsync();
        }
        else if (_stage == Stage.Paused)
        {
            await ResumeAsync();
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        // Cancel/Close: no text. An interrupted or cancelled turn produces no transcript.
        ResultText = null;
        ShouldSubmit = false;
        Close();
    }

    /// <summary>
    /// Fire-and-forget Send (spec section 10): hand the still-capturing recorder to the caller and
    /// close IMMEDIATELY, without waiting for transcription. The caller transcribes it in the
    /// background (joining onto <see cref="BackgroundPrefix"/>), submits, and marks the session orange
    /// "Transcribing..." meanwhile. We detach the recorder from this dialog first (null out
    /// <c>_service</c> and drop our event subscriptions) so the close handler does NOT dispose it and
    /// its capture callbacks stop touching the closing dialog - the caller owns the recorder now.
    /// </summary>
    private void StartBackgroundSend()
    {
        var svc = _service;
        if (svc is null)
        {
            // Nothing captured (no active recorder) - close with no result, like a cancel.
            ResultText = null;
            ShouldSubmit = false;
            Close();
            return;
        }
        _service = null;
        svc.OnAudioBands -= OnAudioBands;
        svc.OnInputRms -= OnInputRms;
        svc.OnCaptureStarted -= OnServiceCaptureStarted;
        svc.OnCaptureLive -= OnServiceCaptureLive;
        svc.OnTranscriptionProgress -= OnServiceTranscriptionProgress;
        BackgroundRecorder = svc;
        BackgroundPrefix = _accumulatedText;
        IsBackgroundSend = true;
        ResultText = null;
        ShouldSubmit = false;
        FileLog.Write("[SpeakDialog] Send: handing recorder to background transcribe-and-submit, closing now");
        Close();
    }

    /// <summary>
    /// Stop the mic, transcribe the current segment ONCE through the shared batch
    /// pipeline, append it to the accumulated transcript, and close the dialog with
    /// the result. Used by Send and Insert directly from recording.
    /// </summary>
    private async Task FinalizeFromRecordingAsync(bool submitOnClose)
    {
        SwitchToTranscribing();
        try
        {
            var segment = await TranscribeSegmentAsync();
            _accumulatedText = DictationText.Join(_accumulatedText, segment);
            ResultText = string.IsNullOrWhiteSpace(_accumulatedText) ? null : _accumulatedText;
            ShouldSubmit = submitOnClose && ResultText is not null;
            Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] FinalizeFromRecording FAILED: {ex.Message}");
            if (await TryShowOutOfCreditsAsync(ex)) return;
            SwitchToFailed(ex.Message);
        }
    }

    /// <summary>
    /// If the transcription failed because hosted AI ran out of credits (a 402 surfaced as
    /// <see cref="InsufficientCreditsException"/>), show the ONE shared "add credits" dialog and close -
    /// instead of dumping the raw "Transcription returned 402: ..." string into the transcript box
    /// (issue #940). Returns true when it handled the exception. Any other error falls through to the
    /// existing failed state.
    /// </summary>
    private async Task<bool> TryShowOutOfCreditsAsync(Exception ex)
    {
        if (ex is InsufficientCreditsException credits)
        {
            await DesktopHostedAiGate.ShowAsync(this, HostedAiErrorMapper.MapCode(credits.Code));
            Close();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checkpoint while recording: freeze the timer, transcribe the current segment
    /// ONCE, append it to the accumulated transcript, and stay in the dialog showing
    /// PAUSED (editable). Resume starts a fresh segment; Insert/Send commit. Resume
    /// is disabled for the duration of the transcription.
    /// </summary>
    private async Task PauseAsync()
    {
        FileLog.Write("[SpeakDialog] PauseAsync");
        // Freeze the segment time first so the displayed elapsed time stops at the
        // moment the user clicked Pause, not when transcription finishes, and so
        // the next segment adds onto this total.
        _elapsedBeforeSegment += DateTime.UtcNow - _t0;
        SwitchToTranscribing();
        try
        {
            var segment = await TranscribeSegmentAsync();
            _accumulatedText = DictationText.Join(_accumulatedText, segment);
            SwitchToPaused();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] PauseAsync FAILED: {ex.Message}");
            if (await TryShowOutOfCreditsAsync(ex)) return;
            SwitchToFailed("Pause failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Resume from PAUSED: re-seed the accumulator from the (possibly edited) box so
    /// the user's corrections survive, then start a fresh recording segment that
    /// will append onto it.
    /// </summary>
    private async Task ResumeAsync()
    {
        FileLog.Write("[SpeakDialog] ResumeAsync");
        _micRequestedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        // Re-seed the accumulator from the (possibly edited) text box so new
        // speech appends onto the edited text rather than the pre-edit transcript.
        _accumulatedText = TranscriptText.Text ?? "";

        // Anchor the new segment's origin BEFORE capture starts; OnServiceCaptureLive
        // re-anchors it to the real capture instant once the mic delivers audio.
        _t0 = DateTime.UtcNow;

        try
        {
            // Hold GETTING READY until the resumed mic is actually capturing again.
            SwitchToConnecting();
            await StartNewServiceAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SpeakDialog] ResumeAsync FAILED: {ex.Message}");
            // NOT SwitchToFailed: the accumulated text is still good. Fall back to
            // Paused so Send/Insert keep working and Resume can be retried. The
            // error must NOT go into the text box - in the Paused stage the box
            // content IS what Send submits, so writing the error there would send
            // the error message as the prompt.
            SwitchToPaused();
            StatusLabel.Text = "ERROR - could not resume";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            LevelHint.Text = "Could not resume - your text is kept. Try Resume again, or Send what you have.";
        }
    }

    /// <summary>
    /// Run the single batch transcription on the current segment's captured clip.
    /// The service is consumed (stopped) and disposed here, so a segment cannot be
    /// transcribed twice. Returns the dictionary-corrected transcript for the segment.
    /// </summary>
    private async Task<string> TranscribeSegmentAsync()
    {
        var svc = _service;
        if (svc is null)
            throw new InvalidOperationException("No active recording to transcribe.");

        var result = await svc.TranscribeAsync();
        await DisposeServiceAsync();
        FileLog.Write($"[SpeakDialog] segment transcribed: corrected={result.DictionaryWordsCorrected} words, len={result.CleanedTranscript.Length}");
        return result.CleanedTranscript;
    }

    /// <summary>Close with the (possibly edited) text currently in the box.</summary>
    private void CommitPausedText(bool submitOnClose)
    {
        var reviewed = TranscriptText.Text;
        ResultText = string.IsNullOrWhiteSpace(reviewed) ? null : reviewed;
        ShouldSubmit = submitOnClose && ResultText is not null;
        Close();
    }

    private void SwitchToTranscribing()
    {
        _readyTimeout.Stop();
        _stage = Stage.Transcribing;
        StatusLabel.Text = "TRANSCRIBING";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        // Show the text accumulated from prior segments (if any) while the current
        // segment transcribes; the new segment's text is not available until the
        // batch call completes. Read-only - no editing mid-transcription.
        TranscriptText.Text = _accumulatedText;
        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        TranscriptText.IsReadOnly = true;
        PrimaryButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        // Resume stays disabled here: "you cannot resume until it has been transcribed".
        PauseButton.IsEnabled = false;
        MicSelector.IsEnabled = false;
        LevelHint.Text = "Transcribing what you have said so far...";
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 34.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
    }

    private void SwitchToPaused()
    {
        _readyTimeout.Stop();
        _stage = Stage.Paused;
        StatusLabel.Text = "PAUSED - reviewing";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TranscriptText.Text = _accumulatedText;
        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        // Editable for review: let the user fix mis-heard words before committing
        // or resuming. Park the caret at the end so appended typing / Resume
        // continues naturally.
        TranscriptText.IsReadOnly = false;
        TranscriptText.CaretIndex = TranscriptText.Text?.Length ?? 0;
        TranscriptText.Focus();
        // Pause becomes Resume; commit actions stay available.
        PauseButton.Content = "Resume";
        PauseButton.IsEnabled = true;
        PrimaryButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        MicSelector.IsEnabled = true;
        // Park the equalizer bars at a low resting height while paused.
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 8.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
        LevelHint.Text = "";
    }

    /// <summary>
    /// The warm-up state shown from the moment the dialog opens (or a segment resumes /
    /// the mic is switched) until the microphone actually delivers audio. Yellow
    /// "GETTING READY", a still timer at zero, and idle gray bars - deliberately NOT the
    /// red RECORDING look and NO cue, because the mic is not confirmed live yet. Commit
    /// actions are disabled (there is nothing captured to send); the mic selector stays
    /// enabled so the user can pick a different device while it warms up. Starts the
    /// no-audio backstop timer.
    /// </summary>
    private void SwitchToConnecting()
    {
        _stage = Stage.Connecting;
        StatusLabel.Text = "GETTING READY";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TimerLabel.Text = "0:00.0";
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        TranscriptText.Text = "";
        TranscriptText.IsReadOnly = true;
        // Nothing is captured yet, so the commit/checkpoint actions are not actionable.
        PrimaryButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        PauseButton.Content = BuildPauseIcon();
        PauseButton.IsEnabled = false;
        MicSelector.IsEnabled = true;
        // Idle gray bars: the meter must not dance before any audio is flowing.
        for (int i = 0; i < _barTargets.Length; i++) _barTargets[i] = 8.0;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
        LevelHint.Text = "";
        _recentPeakRms = 0.0;
        // Restart the backstop for this warm-up window.
        _readyTimeout.Stop();
        _readyTimeout.Start();
    }

    private void SwitchToRecording()
    {
        _readyTimeout.Stop();
        _stage = Stage.Recording;
        StatusLabel.Text = "RECORDING";
        StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        TimerLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        // Read-only and empty while recording: NO live preview. The transcript
        // box stays blank (its explanatory watermark shows through) until the next
        // checkpoint. Prior-segment text is restored only at the next Pause/commit.
        TranscriptText.Text = "";
        TranscriptText.IsReadOnly = true;
        // Restore the two-bar Pause glyph (it may currently read "Resume").
        PauseButton.Content = BuildPauseIcon();
        PauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        PrimaryButton.IsEnabled = true;
        MicSelector.IsEnabled = true;
        foreach (var bar in _bars) bar.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
        // Fresh segment: re-evaluate loudness from scratch.
        _recentPeakRms = 0.0;
        LevelHint.Text = "";
    }

    /// <summary>
    /// Two-bar pause glyph as Avalonia shapes. Avoids the Unicode pause symbol per
    /// the project-wide no-Unicode rule. Matches the markup-built glyph in
    /// SpeakDialog.axaml so the button looks identical whether the content came
    /// from XAML (initial) or from here (after a Resume).
    /// </summary>
    private static Control BuildPauseIcon()
    {
        var fill = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        var sp = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            Spacing = 5,
        };
        sp.Children.Add(new global::Avalonia.Controls.Shapes.Rectangle { Width = 4, Height = 14, Fill = fill });
        sp.Children.Add(new global::Avalonia.Controls.Shapes.Rectangle { Width = 4, Height = 14, Fill = fill });
        return sp;
    }
}
