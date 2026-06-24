using System.Security.Cryptography;
using CcDirectorClient.Recording;
using Xunit;

namespace CcDirectorClient.Tests;

/// <summary>
/// Tests for <see cref="RecordingRecovery.ReconstructChunksFromDisk"/> - the rebuild that rescues a
/// recording the app died mid-capture for. The crux: the OPEN segment is on disk but, because the
/// kill skipped FinalizeSegment, it is absent from the manifest's Chunks. A recording shorter than
/// one segment roll therefore had audio on disk but an EMPTY Chunks list, so it could never upload
/// and was stranded forever showing "In Progress". These tests pin down that the open segment is
/// rebuilt from disk (so it uploads), that already-listed segments are never disturbed, and that an
/// empty shell stays empty.
/// </summary>
public sealed class RecordingRecoveryTests : IDisposable
{
    private readonly string _dir;

    public RecordingRecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "recrecov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteSegment(int index, byte[] bytes)
    {
        var name = $"{index:D4}.m4a";
        File.WriteAllBytes(Path.Combine(_dir, name), bytes);
        return name;
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public void Reconstruct_OpenSegmentMissingFromManifest_IsRebuiltSoItCanUpload()
    {
        // THE bug: the app was killed before the first roll, so the only segment (0000.m4a) is on
        // disk but the manifest lists no chunks. Without rebuilding, the uploader (which walks
        // Chunks) sees nothing and the recording is stranded forever showing "In Progress".
        var audio = new byte[] { 1, 2, 3, 4, 5 };
        WriteSegment(0, audio);
        var m = new LocalManifest { EndedAt = null }; // interrupted: Chunks empty

        var added = RecordingRecovery.ReconstructChunksFromDisk(m, _dir);

        Assert.Equal(1, added);
        var chunk = Assert.Single(m.Chunks);
        Assert.Equal(0, chunk.Index);
        Assert.Equal("0000.m4a", chunk.File);
        Assert.Equal(audio.Length, chunk.Bytes);
        Assert.Equal(Sha256Hex(audio), chunk.Sha256);
        Assert.False(chunk.Uploaded); // not yet on the server
    }

    [Fact]
    public void Reconstruct_AppendsOnlyTheOpenSegment_LeavingFinalizedOnesUntouched()
    {
        // A longer recording: segments 0 and 1 were rolled cleanly (in the manifest, segment 0
        // already uploaded), then the app died mid-segment-2. Recovery must add ONLY segment 2 and
        // must not disturb the resume state of the already-listed segments.
        WriteSegment(0, new byte[] { 10 });
        WriteSegment(1, new byte[] { 20, 21 });
        var open = new byte[] { 30, 31, 32 };
        WriteSegment(2, open);

        var m = new LocalManifest { EndedAt = null };
        m.Chunks.Add(new ChunkInfo { Index = 0, File = "0000.m4a", Bytes = 1, DurationMs = 1000, StartMs = 0, Uploaded = true });
        m.Chunks.Add(new ChunkInfo { Index = 1, File = "0001.m4a", Bytes = 2, DurationMs = 1000, StartMs = 1000, Uploaded = false });

        var added = RecordingRecovery.ReconstructChunksFromDisk(m, _dir);

        Assert.Equal(1, added);
        Assert.Equal(3, m.Chunks.Count);
        // Existing segments are preserved exactly (resume state intact).
        Assert.True(m.Chunks[0].Uploaded);
        Assert.False(m.Chunks[1].Uploaded);
        // The open segment is appended with exact bytes/hash and placed last on the timeline.
        var rebuilt = m.Chunks[2];
        Assert.Equal(2, rebuilt.Index);
        Assert.Equal(open.Length, rebuilt.Bytes);
        Assert.Equal(Sha256Hex(open), rebuilt.Sha256);
        Assert.Equal(2000, rebuilt.StartMs); // after segment 1 ends (1000 + 1000)
    }

    [Fact]
    public void Reconstruct_IsIdempotent_DoesNotDuplicateAlreadyListedSegments()
    {
        var audio = new byte[] { 7, 7, 7 };
        WriteSegment(0, audio);
        var m = new LocalManifest { EndedAt = null };

        var firstPass = RecordingRecovery.ReconstructChunksFromDisk(m, _dir);
        var secondPass = RecordingRecovery.ReconstructChunksFromDisk(m, _dir);

        Assert.Equal(1, firstPass);
        Assert.Equal(0, secondPass); // nothing new on the second pass
        Assert.Single(m.Chunks);
    }

    [Fact]
    public void Reconstruct_IgnoresZeroLengthOpenSegment_NoAudioToSave()
    {
        // The app died the instant recording started: the open file exists but captured no bytes.
        // There is nothing to save, so no chunk is created (the caller then leaves it as an empty
        // shell rather than recovering a zero-byte recording).
        WriteSegment(0, Array.Empty<byte>());
        var m = new LocalManifest { EndedAt = null };

        var added = RecordingRecovery.ReconstructChunksFromDisk(m, _dir);

        Assert.Equal(0, added);
        Assert.Empty(m.Chunks);
    }

    [Fact]
    public void Reconstruct_MissingFolder_ReturnsZero()
    {
        var m = new LocalManifest { EndedAt = null };
        var added = RecordingRecovery.ReconstructChunksFromDisk(m, Path.Combine(_dir, "does-not-exist"));
        Assert.Equal(0, added);
        Assert.Empty(m.Chunks);
    }

    [Fact]
    public void Reconstruct_IgnoresNonSegmentFiles()
    {
        // manifest.json and any other non NNNN.m4a file must never be mistaken for a segment.
        File.WriteAllText(Path.Combine(_dir, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "hello");
        var m = new LocalManifest { EndedAt = null };

        var added = RecordingRecovery.ReconstructChunksFromDisk(m, _dir);

        Assert.Equal(0, added);
        Assert.Empty(m.Chunks);
    }

    [Fact]
    public void Reconstruct_RebuildsAllSegments_WhenManifestLostEntirely()
    {
        // Belt and braces: even if the manifest's Chunks were lost completely, every segment on
        // disk is rebuilt in index order so the whole recording uploads.
        WriteSegment(2, new byte[] { 3 });
        WriteSegment(0, new byte[] { 1 });
        WriteSegment(1, new byte[] { 2 });
        var m = new LocalManifest { EndedAt = null };

        var added = RecordingRecovery.ReconstructChunksFromDisk(m, _dir);

        Assert.Equal(3, added);
        Assert.Equal(new[] { 0, 1, 2 }, m.Chunks.Select(c => c.Index).ToArray());
    }
}
