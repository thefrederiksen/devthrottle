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
            ElapsedMs: 1000, NumberOfBuffers: 3, BufferMilliseconds: 50);

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
            ElapsedMs: 1000, NumberOfBuffers: 3, BufferMilliseconds: 50);

        Assert.Equal(0.0, h.DeficitFraction);
    }

    [Fact]
    public void DeficitFraction_NoExpectedBytes_IsZeroNotDivideByZero()
    {
        var h = new CaptureHealth(
            CapturedBytes: 0, ExpectedBytes: 0, CallbackCount: 0,
            MaxCallbackGapMs: 0, LongGapCount: 0, MaxHandlerMs: 0, TotalHandlerMs: 0,
            ElapsedMs: 0, NumberOfBuffers: 3, BufferMilliseconds: 50);

        Assert.Equal(0.0, h.DeficitFraction);
    }
}
