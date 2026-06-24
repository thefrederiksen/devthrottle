using CcDirectorClient.Recording;
using Xunit;

namespace CcDirectorClient.Tests;

/// <summary>
/// Tests for <see cref="RecordingUploadGate"/> - the rules that guarantee the audio AND the
/// notes always upload, no matter what happens to transcription. The crux of the fix: a
/// recording is NOT finished at State=="Uploaded"; the notes ride on the complete call, so it
/// stays in the upload queue until that call is acknowledged (<c>completed</c>). These tests
/// pin that down so it can never silently regress back to treating "Uploaded" as terminal.
/// </summary>
public sealed class RecordingUploadGateTests
{
    // ===== NeedsUpload: the queue-eligibility rule ==========================

    [Theory]
    [InlineData("Queued")]
    [InlineData("Retry")]
    [InlineData("Uploading")]
    public void NeedsUpload_AudioNotYetUploaded_IsTrue(string state)
    {
        // Audio bytes are not all on the server yet - obviously still needs the pass.
        Assert.True(RecordingUploadGate.NeedsUpload(state, completed: false));
        // Even if some stale "completed" flag were set, an un-uploaded recording still needs work.
        Assert.True(RecordingUploadGate.NeedsUpload(state, completed: true));
    }

    [Fact]
    public void NeedsUpload_AudioUploadedButNotCompleted_IsTrue()
    {
        // THE regression guard for stranded notes: the audio is on the server, but the
        // complete/notes call has not been acknowledged. The recording MUST stay in the queue
        // so the complete call (which carries the notes) is retried.
        Assert.True(RecordingUploadGate.NeedsUpload("Uploaded", completed: false));
    }

    [Fact]
    public void NeedsUpload_FullyDelivered_IsFalse()
    {
        // Audio uploaded AND complete acknowledged: nothing left to do, drops out of the queue.
        Assert.False(RecordingUploadGate.NeedsUpload("Uploaded", completed: true));
    }

    [Theory]
    [InlineData("Recording")] // still being recorded
    [InlineData("")]          // unknown/blank
    public void NeedsUpload_NotAnUploadState_IsFalse(string state)
    {
        Assert.False(RecordingUploadGate.NeedsUpload(state, completed: false));
    }

    // ===== ShouldUploadAudio: skip the byte transfer when it is already done =

    [Theory]
    [InlineData("Queued", true)]
    [InlineData("Retry", true)]
    [InlineData("Uploading", true)]
    [InlineData("Uploaded", false)] // bytes already on the server - resume straight to complete
    public void ShouldUploadAudio_OnlyWhenNotYetUploaded(string state, bool expected)
    {
        Assert.Equal(expected, RecordingUploadGate.ShouldUploadAudio(state));
    }

    [Fact]
    public void ShouldUploadAudio_UploadedButNotCompleted_SkipsAudioPhase()
    {
        // The exact resume case: a prior pass got the bytes up but the complete call had not
        // landed. The next pass must NOT re-send any audio - it goes straight to the complete
        // call. Combined with NeedsUpload==true, this is what retries ONLY the notes delivery.
        Assert.False(RecordingUploadGate.ShouldUploadAudio("Uploaded"));
        Assert.True(RecordingUploadGate.NeedsUpload("Uploaded", completed: false));
    }

    // ===== IsFullyDelivered / IsDeletable: terminal + deletion safety =======

    [Fact]
    public void IsFullyDelivered_RequiresBothUploadedAndCompleted()
    {
        Assert.True(RecordingUploadGate.IsFullyDelivered("Uploaded", completed: true));
        Assert.False(RecordingUploadGate.IsFullyDelivered("Uploaded", completed: false));
        Assert.False(RecordingUploadGate.IsFullyDelivered("Retry", completed: true));
    }

    [Fact]
    public void IsDeletable_NeverDeletesARecordingStillOwingItsNotes()
    {
        // The deletion sync may remove a local recording only once it is FULLY delivered.
        // A recording whose notes have not been delivered (completed=false) must never be
        // eligible for deletion, even if its audio is already on the server.
        Assert.False(RecordingUploadGate.IsDeletable("Uploaded", completed: false));
        Assert.False(RecordingUploadGate.IsDeletable("Retry", completed: false));
        Assert.True(RecordingUploadGate.IsDeletable("Uploaded", completed: true));
    }

    // ===== NeedsRecovery: rescue interrupted recordings (issue #687) =========

    [Fact]
    public void NeedsRecovery_InterruptedWithAudio_IsTrue()
    {
        // The bug at the heart of #687: a recording the app died mid-capture for is reported
        // "Recording" (EndedAt==null) but already has audio segments on disk. It must be
        // recovered into the upload path, NOT left stranded and silently lost.
        Assert.True(RecordingUploadGate.NeedsRecovery("Recording", hasAudioSegments: true));
    }

    [Fact]
    public void NeedsRecovery_InterruptedButEmpty_IsFalse()
    {
        // A "Recording" shell with no captured segments yet has nothing to recover - there is
        // no audio to save, so it is left for the normal lifecycle (it is not silent data loss).
        Assert.False(RecordingUploadGate.NeedsRecovery("Recording", hasAudioSegments: false));
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Uploading")]
    [InlineData("Uploaded")]
    [InlineData("Retry")]
    [InlineData("")]
    public void NeedsRecovery_AlreadyFinalizedStates_IsFalse(string state)
    {
        // Any recording that was cleanly stopped (EndedAt set, so state is a real upload state)
        // is already in the normal path - recovery must not touch it, even with audio present.
        Assert.False(RecordingUploadGate.NeedsRecovery(state, hasAudioSegments: true));
    }

    [Fact]
    public void Recovery_RecoveredOrphan_IsNotDeletableUntilDelivered()
    {
        // The deletion-safety guard for #687 (AC5): once an interrupted recording is recovered
        // it becomes "Queued". That state is NOT fully delivered and NOT deletable, so the
        // server->phone deletion sync can never remove recovered audio before it is on the server.
        Assert.True(RecordingUploadGate.NeedsRecovery("Recording", hasAudioSegments: true));
        // After recovery the recorder sets State="Queued" with completed=false:
        Assert.True(RecordingUploadGate.NeedsUpload("Queued", completed: false));
        Assert.False(RecordingUploadGate.IsFullyDelivered("Queued", completed: false));
        Assert.False(RecordingUploadGate.IsDeletable("Queued", completed: false));
    }

    // ===== the lifecycle the fix protects ==================================

    [Fact]
    public void Lifecycle_NotesSurviveACompleteCallThatFailsThenSucceeds()
    {
        // 1. Fresh recording, audio not yet up: needs the pass, audio phase runs.
        Assert.True(RecordingUploadGate.NeedsUpload("Queued", completed: false));
        Assert.True(RecordingUploadGate.ShouldUploadAudio("Queued"));

        // 2. Audio uploaded, but the complete/notes call dropped (or the app was killed before
        //    it ran). The recording is NOT done - it stays eligible and will retry ONLY the
        //    complete call. This is precisely where notes used to be stranded forever.
        Assert.True(RecordingUploadGate.NeedsUpload("Uploaded", completed: false));
        Assert.False(RecordingUploadGate.ShouldUploadAudio("Uploaded")); // no re-send of audio
        Assert.False(RecordingUploadGate.IsFullyDelivered("Uploaded", completed: false));
        Assert.False(RecordingUploadGate.IsDeletable("Uploaded", completed: false));

        // 3. The retried complete call is acknowledged: notes delivered. Now, and only now, the
        //    recording is finished and may be cleaned up.
        Assert.False(RecordingUploadGate.NeedsUpload("Uploaded", completed: true));
        Assert.True(RecordingUploadGate.IsFullyDelivered("Uploaded", completed: true));
        Assert.True(RecordingUploadGate.IsDeletable("Uploaded", completed: true));
    }

    // ===== RequeueIndicesForResend: the gate-driven resume (issue #591) ======

    [Fact]
    public void RequeueIndicesForResend_NamesOnlyLocallyPresentMissingIndices()
    {
        // The server gate reported indices 1 and 3 as missing/bad; the phone holds 0..3. Exactly
        // those two are re-armed for re-send - nothing else is touched.
        var resend = RecordingUploadGate.RequeueIndicesForResend(
            missingOrBadIndices: new[] { 3, 1 }, localIndices: new[] { 0, 1, 2, 3 });
        Assert.Equal(new[] { 1, 3 }, resend); // sorted + de-duplicated
    }

    [Fact]
    public void RequeueIndicesForResend_NeverInventsASegmentThePhoneNeverHad()
    {
        // Zero audio loss the other way: the gate names index 5, but the phone only ever had 0..2.
        // The phone cannot re-send what it does not have, so 5 is dropped from the resend set - it
        // is never fabricated.
        var resend = RecordingUploadGate.RequeueIndicesForResend(
            missingOrBadIndices: new[] { 1, 5 }, localIndices: new[] { 0, 1, 2 });
        Assert.Equal(new[] { 1 }, resend);
    }

    [Fact]
    public void RequeueIndicesForResend_NullOrEmpty_IsEmpty()
    {
        Assert.Empty(RecordingUploadGate.RequeueIndicesForResend(null, new[] { 0, 1 }));
        Assert.Empty(RecordingUploadGate.RequeueIndicesForResend(Array.Empty<int>(), new[] { 0, 1 }));
    }

    [Fact]
    public void RequeueIndicesForResend_DeDuplicatesRepeatedIndices()
    {
        var resend = RecordingUploadGate.RequeueIndicesForResend(
            missingOrBadIndices: new[] { 2, 2, 0, 0 }, localIndices: new[] { 0, 1, 2 });
        Assert.Equal(new[] { 0, 2 }, resend);
    }
}
