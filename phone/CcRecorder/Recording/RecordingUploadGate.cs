namespace CcRecorder.Recording;

/// <summary>
/// The pure decision logic for the background upload pass: given a recording's UPLOAD
/// state and whether its complete/notes call has been acknowledged by the server, decide
/// whether it still needs work, whether the audio-upload phase must run, whether it is
/// fully delivered, and whether it is safe to delete in the server-&gt;phone deletion sync.
///
/// These rules are what guarantee "the audio AND the notes always upload, no matter what
/// happens to transcription". The notes ride ONLY on the complete call, so a recording is
/// NOT done at State=="Uploaded" - it is done only once the complete call is acknowledged
/// (<c>completed</c>). Kept dependency-free (no MAUI/Android) so it is unit-tested
/// off-device and cannot silently regress (e.g. back to treating "Uploaded" as terminal).
/// </summary>
public static class RecordingUploadGate
{
    public const string Queued = "Queued";
    public const string Uploading = "Uploading";
    public const string Uploaded = "Uploaded";
    public const string Retry = "Retry";

    /// <summary>
    /// The recording still has upload work to do, so the background pass must process it.
    /// Either the audio bytes are not all on the server yet (Queued/Retry/Uploading), or the
    /// audio IS uploaded but the complete call - the only thing that delivers the NOTES and
    /// triggers server-side transcription - has not yet been acknowledged.
    /// </summary>
    public static bool NeedsUpload(string state, bool completed)
    {
        if (state is Queued or Retry or Uploading) return true; // audio not all up yet
        if (state == Uploaded && !completed) return true;        // notes not yet delivered
        return false;
    }

    /// <summary>
    /// The audio-upload phase must run unless the audio is already fully on the server. When
    /// the audio is up but the notes are not yet delivered, this is false so the pass resumes
    /// straight to the complete/notes call without re-sending any bytes.
    /// </summary>
    public static bool ShouldUploadAudio(string state) => state != Uploaded;

    /// <summary>
    /// Terminal: the recording is fully delivered - the audio is uploaded AND the complete/notes
    /// call was acknowledged. Only then does it drop out of the upload queue.
    /// </summary>
    public static bool IsFullyDelivered(string state, bool completed) => state == Uploaded && completed;

    /// <summary>
    /// Safe to delete in the server-&gt;phone deletion sync ONLY when fully delivered. A recording
    /// whose audio is not confirmed, or that still owes its notes, must never be removed locally.
    /// </summary>
    public static bool IsDeletable(string state, bool completed) => IsFullyDelivered(state, completed);

    /// <summary>
    /// The server's audio completeness gate (issue #586) refused the complete call because some
    /// segments are missing or hash-mismatched on the server, naming their indices. This is the pure
    /// decision for the gate-driven resume (issue #591): given the indices the gate reported and the
    /// segment indices this recording actually has locally, return exactly the locally-present indices
    /// that must be re-armed (their <c>Uploaded</c> flag cleared) so the next upload pass re-sends them
    /// - and nothing else. With zero audio loss: a segment the gate names but the phone never had is
    /// not invented here; only segments the phone holds are re-sent. De-duplicated and sorted.
    /// </summary>
    /// <param name="missingOrBadIndices">The indices the server gate reported as missing or bad.</param>
    /// <param name="localIndices">The segment indices this recording has on the phone.</param>
    public static IReadOnlyList<int> RequeueIndicesForResend(
        IEnumerable<int>? missingOrBadIndices, IEnumerable<int> localIndices)
    {
        if (missingOrBadIndices is null) return Array.Empty<int>();
        var local = new HashSet<int>(localIndices);
        return missingOrBadIndices
            .Where(local.Contains)
            .Distinct()
            .OrderBy(i => i)
            .ToList();
    }
}
