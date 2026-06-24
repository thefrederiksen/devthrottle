using System.Security.Cryptography;

namespace CcDirectorClient.Recording;

/// <summary>
/// Pure (no MAUI/Android) recovery logic for a recording the app died mid-capture for.
///
/// A segment .m4a is written to disk by the recorder as it captures, but it only becomes a
/// <see cref="ChunkInfo"/> in the manifest when <c>FinalizeSegment</c> runs - on a one-minute
/// segment roll or on a clean stop. A process kill, crash, or swipe-away skips that step, so the
/// OPEN segment is on disk yet absent from <see cref="LocalManifest.Chunks"/>. A recording shorter
/// than one roll (or killed before its first roll) therefore has audio on disk but an EMPTY Chunks
/// list. The uploader walks Chunks, so such a recording could never upload and was stranded forever
/// showing "In Progress" - the gap left by the issue #687 recovery, which only looked at the
/// manifest's own chunk count.
///
/// Kept dependency-free so it is unit-tested off-device (the real Android recorder cannot run headless).
/// </summary>
public static class RecordingRecovery
{
    /// <summary>
    /// Rebuild <paramref name="manifest"/>'s <see cref="LocalManifest.Chunks"/> from the actual
    /// segment files in <paramref name="recordingFolder"/>, appending any NNNN.m4a segment that is
    /// present on disk but missing from the manifest. Segments already in the manifest are left
    /// exactly as they are - their <see cref="ChunkInfo.Uploaded"/> flag and measured durations are
    /// preserved, so a resume after a dropped connection still skips bytes already on the server.
    /// Zero-length files are ignored (an open segment that captured no bytes has nothing to save).
    /// Returns the number of segments added. After this runs, a non-empty Chunks list means there is
    /// real audio to upload, regardless of what the pre-kill manifest happened to list.
    /// </summary>
    public static int ReconstructChunksFromDisk(LocalManifest manifest, string recordingFolder)
    {
        if (!Directory.Exists(recordingFolder)) return 0;

        var known = new HashSet<int>(manifest.Chunks.Select(c => c.Index));
        int added = 0;

        foreach (var path in Directory.GetFiles(recordingFolder, "*.m4a"))
        {
            var name = Path.GetFileName(path);
            if (!TryParseSegmentIndex(name, out var index)) continue;
            if (known.Contains(index)) continue;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) continue; // an empty open segment has no audio to save

            manifest.Chunks.Add(new ChunkInfo
            {
                Index = index,
                File = name,
                // The kill skipped FinalizeSegment, so this segment was never measured. Its
                // duration is unknown (0); place it after the last measured segment so the timeline
                // stays monotonic. The bytes and their hash are exact - all the upload and the
                // server's per-segment completeness gate actually need.
                StartMs = NextStartMs(manifest),
                DurationMs = 0,
                Bytes = bytes.Length,
                Sha256 = Sha256Hex(bytes),
            });
            known.Add(index);
            added++;
        }

        if (added > 0)
            manifest.Chunks.Sort((a, b) => a.Index.CompareTo(b.Index));

        return added;
    }

    /// <summary>
    /// The end of the latest measured segment, used as the start offset for a reconstructed
    /// (unmeasured) segment so the timeline stays monotonic.
    /// </summary>
    private static long NextStartMs(LocalManifest manifest)
        => manifest.Chunks.Count == 0 ? 0 : manifest.Chunks.Max(c => c.StartMs + c.DurationMs);

    /// <summary>Parse the NNNN.m4a segment file name into its integer index.</summary>
    private static bool TryParseSegmentIndex(string fileName, out int index)
        => int.TryParse(Path.GetFileNameWithoutExtension(fileName), out index);

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
