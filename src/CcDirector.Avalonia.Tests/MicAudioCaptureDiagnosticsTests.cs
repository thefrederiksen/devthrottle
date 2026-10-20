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
        const double elapsedMs = 9797, firstAudioMs = 2282;
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

        Assert.Equal(7515.0, health.DeliveringMs, 3);
        Assert.Equal(0.0, health.DeficitFraction);
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
}
