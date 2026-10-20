using CcDirector.Avalonia.Voice;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Unit coverage for the capture-health math (issue #863). The instrumentation that
/// drives audio-loss diagnosis is behaviour-neutral, but its two pure pieces - the
/// expected-bytes yardstick and the deficit fraction - are exact and must stay exact,
/// because the whole "is this a local stall or upstream under-delivery" decision is
/// read off them.
/// </summary>
public sealed class MicAudioCaptureDiagnosticsTests
{
    [Theory]
    [InlineData(1000.0, 48000)]  // 1s @ 24 kHz, 16-bit, mono = 48000 bytes
    [InlineData(500.0, 24000)]   // half a second
    [InlineData(0.0, 0)]         // no elapsed time -> no expected bytes
    public void ExpectedBytes_MatchesPcmRate(double elapsedMs, long expected)
        => Assert.Equal(expected, MicAudioCapture.ExpectedBytes(elapsedMs));

    [Fact]
    public void DeficitFraction_ReportsMissingAudio()
    {
        // The real incident: 32002 captured against 48000 expected over one second.
        var h = new CaptureHealth(
            CapturedBytes: 32000, ExpectedBytes: 48000, CallbackCount: 0,
            MaxCallbackGapMs: 0, LongGapCount: 0, MaxHandlerMs: 0, TotalHandlerMs: 0,
            ElapsedMs: 1000, NumberOfBuffers: 3, BufferMilliseconds: 50, FirstAudioMs: 0);

        Assert.Equal(1.0 - 32000.0 / 48000.0, h.DeficitFraction, 5);
    }

    [Fact]
    public void DeficitFraction_NoLoss_ClampsTheDrainTailToZero()
    {
        // The drain tail can push captured slightly above the stopwatch-derived expected;
        // that is not a negative deficit, it is zero loss.
        var h = new CaptureHealth(
            CapturedBytes: 48200, ExpectedBytes: 48000, CallbackCount: 0,
            MaxCallbackGapMs: 0, LongGapCount: 0, MaxHandlerMs: 0, TotalHandlerMs: 0,
            ElapsedMs: 1000, NumberOfBuffers: 3, BufferMilliseconds: 50, FirstAudioMs: 0);

        Assert.Equal(0.0, h.DeficitFraction);
    }

    [Fact]
    public void DeficitFraction_NoExpectedBytes_IsZeroNotDivideByZero()
    {
        var h = new CaptureHealth(
            CapturedBytes: 0, ExpectedBytes: 0, CallbackCount: 0,
            MaxCallbackGapMs: 0, LongGapCount: 0, MaxHandlerMs: 0, TotalHandlerMs: 0,
            ElapsedMs: 0, NumberOfBuffers: 3, BufferMilliseconds: 50, FirstAudioMs: -1);

        Assert.Equal(0.0, h.DeficitFraction);
    }

    [Theory]
    [InlineData(1000.0, 0.0, 48000)]     // delivering from the first millisecond
    [InlineData(1000.0, 500.0, 24000)]   // half the clock went on opening the device
    [InlineData(1000.0, 1000.0, 0)]      // the first buffer arrived as the clock stopped
    [InlineData(1000.0, 1500.0, 0)]      // never negative, however the two clocks land
    [InlineData(1000.0, -1.0, 0)]        // no buffer ever arrived: expect nothing, not a whole recording
    public void ExpectedBytesWhileDelivering_CountsOnlyTheWindowTheDeviceWasDelivering(
        double elapsedMs, double firstAudioMs, long expected)
        => Assert.Equal(expected, MicAudioCapture.ExpectedBytesWhileDelivering(elapsedMs, firstAudioMs));

    /// <summary>
    /// The measurement that prompted this: a first dictation on macOS, where the microphone
    /// permission prompt sat in front of the capture. Against the whole clock it read as a 23%
    /// deficit - a figure that means "words are missing" - with no gap between callbacks and
    /// nothing actually dropped. Against the window the device was delivering, it is no loss.
    /// </summary>
    [Fact]
    public void DeficitFraction_SlowDeviceOpen_IsNotReportedAsLostAudio()
    {
        const double elapsedMs = 9797;
        // The log recorded the first CALLBACK at 2282 ms, carrying a 50 ms buffer, so that
        // buffer's audio began at 2232 ms - which is what the backends now store.
        double firstAudioMs = MicAudioCapture.FirstAudioStartMs(2282, 2400);
        const long capturedBytes = 362252;

        var againstTheWholeClock = new CaptureHealth(
            CapturedBytes: capturedBytes, ExpectedBytes: MicAudioCapture.ExpectedBytes(elapsedMs),
            CallbackCount: 174, MaxCallbackGapMs: 64, LongGapCount: 0, MaxHandlerMs: 1, TotalHandlerMs: 10,
            ElapsedMs: elapsedMs, NumberOfBuffers: 3, BufferMilliseconds: 50, FirstAudioMs: firstAudioMs);
        Assert.True(againstTheWholeClock.DeficitFraction > 0.20,
            "the old yardstick is what reported a loss that never happened - if this no longer holds, "
            + "the measurement this test is built on has changed and the test is lying about its subject");

        var health = new CaptureHealth(
            CapturedBytes: capturedBytes,
            ExpectedBytes: MicAudioCapture.ExpectedBytesWhileDelivering(elapsedMs, firstAudioMs),
            CallbackCount: 174, MaxCallbackGapMs: 64, LongGapCount: 0, MaxHandlerMs: 1, TotalHandlerMs: 10,
            ElapsedMs: elapsedMs, NumberOfBuffers: 3, BufferMilliseconds: 50, FirstAudioMs: firstAudioMs);

        Assert.Equal(2232.0, firstAudioMs, 3);
        Assert.Equal(7565.0, health.DeliveringMs, 3);
        // Not exactly zero now that the first buffer's own 50 ms is inside the window, which is
        // the point: its bytes were always counted, so its time has to be too. A quarter of one
        // per cent is the device's ordinary rate wobble, not a loss worth a line in a log.
        Assert.True(health.DeficitFraction < 0.005,
            $"expected effectively no loss, got {health.DeficitFraction:P2}");
    }

    [Fact]
    public void DeliveringMs_NoAudioEverArrived_IsZero()
    {
        var h = new CaptureHealth(
            CapturedBytes: 0, ExpectedBytes: 0, CallbackCount: 0,
            MaxCallbackGapMs: 0, LongGapCount: 0, MaxHandlerMs: 0, TotalHandlerMs: 0,
            ElapsedMs: 4000, NumberOfBuffers: 3, BufferMilliseconds: 50, FirstAudioMs: -1);

        Assert.Equal(0.0, h.DeliveringMs);
        Assert.Equal(0.0, h.DeficitFraction);
    }

    /// <summary>
    /// The reviewer's objection to measuring only the delivering window, answered in the data rather
    /// than in prose: excluding the wait for the first buffer would otherwise ABSORB a genuinely late
    /// start, because a slow device open and audio the device never delivered look identical from
    /// here. So the wait is reported in its own right, as the portion of the clip with nothing behind
    /// it. No threshold: a flag that read "normal" below some cutoff would hand back a clean answer
    /// for an unexplained wait just under it, which only moves the blind spot.
    /// </summary>
    [Theory]
    [InlineData(1000.0, 0.0, 0.0)]        // delivering from the first millisecond
    [InlineData(1000.0, -1.0, 1.0)]       // no buffer ever arrived: the WHOLE clip has nothing behind it
    [InlineData(1000.0, 400.0, 0.40)]     // a wait no threshold would have flagged is still reported
    [InlineData(9797.0, 2232.0, 0.2278)]  // the microphone permission prompt, from the log this came from
    [InlineData(1000.0, 4000.0, 1.0)]     // never over the whole clip, however the two clocks land
    public void UnaccountedAtStartFraction_ReportsTheWaitWithoutJudgingIt(
        double elapsedMs, double firstAudioMs, double expected)
    {
        var h = new CaptureHealth(
            CapturedBytes: 48000, ExpectedBytes: 48000, CallbackCount: 20,
            MaxCallbackGapMs: 0, LongGapCount: 0, MaxHandlerMs: 0, TotalHandlerMs: 0,
            ElapsedMs: elapsedMs, NumberOfBuffers: 3, BufferMilliseconds: 50, FirstAudioMs: firstAudioMs);

        Assert.Equal(expected, h.UnaccountedAtStartFraction, 3);
    }

    [Fact]
    public void CleanDeliveringWindowAfterALateStart_ReportsBothFacts()
    {
        // Nothing lost once the device was delivering, and 2232 ms before it that this layer cannot
        // account for. Both have to survive into the log line, or the second one is lost - which is
        // exactly what measuring the deficit over the delivering window would otherwise cost.
        const double elapsedMs = 9797;
        // The log recorded the first CALLBACK at 2282 ms, carrying a 50 ms buffer, so that
        // buffer's audio began at 2232 ms - which is what the backends now store.
        double firstAudioMs = MicAudioCapture.FirstAudioStartMs(2282, 2400);
        var h = new CaptureHealth(
            CapturedBytes: 362252,
            ExpectedBytes: MicAudioCapture.ExpectedBytesWhileDelivering(elapsedMs, firstAudioMs),
            CallbackCount: 174, MaxCallbackGapMs: 64, LongGapCount: 0, MaxHandlerMs: 1, TotalHandlerMs: 10,
            ElapsedMs: elapsedMs, NumberOfBuffers: 3, BufferMilliseconds: 50, FirstAudioMs: firstAudioMs);

        Assert.True(h.DeficitFraction < 0.005, $"expected effectively no loss, got {h.DeficitFraction:P2}");
        Assert.Equal(0.228, h.UnaccountedAtStartFraction, 3);

        // The old whole-clock yardstick reported that same 23% as LOST audio. The two numbers now say
        // different things, and the sum of what they describe is still the whole recording.
        var oldWay = h with { ExpectedBytes = MicAudioCapture.ExpectedBytes(elapsedMs) };
        Assert.Equal(h.UnaccountedAtStartFraction, oldWay.DeficitFraction, 2);
    }

    /// <summary>
    /// A capture that produced not one byte must not report itself clean. It has no delivering window,
    /// so the deficit has nothing to measure and reads zero - which means the unaccounted-at-start
    /// figure is the only one left that can say anything, and for this recording it has to say all of it.
    /// Raised in review: with both reading zero, the pair said "nothing unaccounted and nothing missing"
    /// about silence, where the old whole-clock deficit had said a hundred per cent.
    /// </summary>
    [Fact]
    public void NoBufferEverArrived_IsNotReportedAsACleanRecording()
    {
        var h = new CaptureHealth(
            CapturedBytes: 0,
            ExpectedBytes: MicAudioCapture.ExpectedBytesWhileDelivering(4000, -1),
            CallbackCount: 0, MaxCallbackGapMs: 0, LongGapCount: 0, MaxHandlerMs: 0, TotalHandlerMs: 0,
            ElapsedMs: 4000, NumberOfBuffers: 3, BufferMilliseconds: 50, FirstAudioMs: -1);

        Assert.Equal(0.0, h.DeliveringMs);
        Assert.Equal(1.0, h.UnaccountedAtStartFraction);
    }

    /// <summary>
    /// A callback timestamp marks DELIVERY, not recording. Raised in review: the first buffer's bytes
    /// were already in the captured count while its duration was left out of the expected window, so
    /// every recording got a whole buffer of free headroom - 50 milliseconds here - and a loss at the
    /// very start of a clip could hide inside it.
    /// </summary>
    [Theory]
    [InlineData(2282.0, 2400, 2232.0)]  // a 50 ms buffer delivered at 2282 ms began at 2232 ms
    [InlineData(114.0, 2400, 64.0)]     // an ordinary open, measured on macOS
    [InlineData(20.0, 2400, 0.0)]       // the first buffer arrived sooner than it could have been recorded
    [InlineData(1000.0, 0, 1000.0)]     // an empty chunk carries no audio and moves nothing
    public void FirstAudioStartMs_BacksUpOverTheAudioTheFirstBufferCarried(
        double callbackMs, long chunkBytes, double expected)
        => Assert.Equal(expected, MicAudioCapture.FirstAudioStartMs(callbackMs, chunkBytes), 3);

    [Theory]
    [InlineData(2400, 50.0)]    // one 50 ms buffer at 24 kHz, 16-bit, mono
    [InlineData(48000, 1000.0)]
    [InlineData(0, 0.0)]
    public void MillisecondsOfBytes_MatchesPcmRate(long bytes, double expected)
        => Assert.Equal(expected, MicAudioCapture.MillisecondsOfBytes(bytes), 6);
}
