using CcRecorder.Recording;
using Xunit;

namespace CcRecorder.Tests;

public class RecordingUploadGateTests
{
    [Fact]
    public void StateAfterStop_WithSegments_IsQueued()
    {
        Assert.Equal(RecordingUploadGate.Queued, RecordingUploadGate.StateAfterStop(4));
    }

    [Fact]
    public void StateAfterStop_NoSegment_IsNoAudio()
    {
        // Stop pressed before the recorder delivered any audio: the only segment was dropped.
        Assert.Equal(RecordingUploadGate.NoAudio, RecordingUploadGate.StateAfterStop(0));
    }

    [Fact]
    public void NeedsUpload_NoAudio_IsFalse()
    {
        // A recording with nothing to send must not be revisited by every upload pass as "queued".
        Assert.False(RecordingUploadGate.NeedsUpload(RecordingUploadGate.NoAudio, completed: false));
    }
}
