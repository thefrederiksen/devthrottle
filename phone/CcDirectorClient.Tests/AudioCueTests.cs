using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

/// <summary>
/// Tests for <see cref="NullAudioCue"/>. The null implementation must satisfy the
/// <see cref="IAudioCue"/> contract without throwing and without producing any audio.
/// </summary>
public class AudioCueTests
{
    [Fact]
    public void NullAudioCue_ImplementsIAudioCue()
    {
        // The cast is the assertion: compile-time check that NullAudioCue satisfies the contract.
        IAudioCue cue = new NullAudioCue();
        Assert.NotNull(cue);
    }

    [Fact]
    public void PlayStart_DoesNotThrow()
    {
        var cue = new NullAudioCue();
        // Must complete silently; any exception would be a contract violation.
        cue.PlayStart();
    }

    [Fact]
    public void PlayStop_DoesNotThrow()
    {
        var cue = new NullAudioCue();
        cue.PlayStop();
    }

    [Fact]
    public void PlayError_DoesNotThrow()
    {
        var cue = new NullAudioCue();
        cue.PlayError();
    }

    [Fact]
    public void AllMethods_CalledMultipleTimes_NeverThrow()
    {
        // Verify re-entrant / repeated calls are safe (no state machine that could fault
        // on a second call without a prior start).
        var cue = new NullAudioCue();
        for (var i = 0; i < 10; i++)
        {
            cue.PlayStart();
            cue.PlayStop();
            cue.PlayError();
        }
    }
}
