using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

public sealed class AudioBufferTests
{
    [Fact]
    public void NewBuffer_IsEmpty()
    {
        var buf = new AudioBuffer();
        Assert.True(buf.IsEmpty);
        Assert.Equal(0, buf.BytesBuffered);
        Assert.Equal(0, buf.ChunkCount);
        Assert.False(buf.Overflowed);
    }

    [Fact]
    public void Ctor_NonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBuffer(-1));
    }

    [Fact]
    public void Append_NullChunk_Throws()
    {
        var buf = new AudioBuffer();
        Assert.Throws<ArgumentNullException>(() => buf.Append(null!));
    }

    [Fact]
    public void Append_EmptyChunk_IsIgnored()
    {
        var buf = new AudioBuffer();
        buf.Append(Array.Empty<byte>());
        Assert.True(buf.IsEmpty);
    }

    [Fact]
    public void Append_TracksBytesAndCount()
    {
        var buf = new AudioBuffer(capacityBytes: 1000);
        buf.Append(new byte[] { 1, 2, 3 });
        buf.Append(new byte[] { 4, 5 });
        Assert.Equal(2, buf.ChunkCount);
        Assert.Equal(5, buf.BytesBuffered);
    }

    [Fact]
    public void DrainAll_ReturnsChunksInOrderAndEmptiesBuffer()
    {
        var buf = new AudioBuffer(capacityBytes: 1000);
        buf.Append(new byte[] { 1 });
        buf.Append(new byte[] { 2, 3 });
        buf.Append(new byte[] { 4 });

        var drained = buf.DrainAll();

        Assert.Equal(3, drained.Count);
        Assert.Equal(new byte[] { 1 }, drained[0]);
        Assert.Equal(new byte[] { 2, 3 }, drained[1]);
        Assert.Equal(new byte[] { 4 }, drained[2]);
        Assert.True(buf.IsEmpty);
    }

    [Fact]
    public void DrainAll_EmptyBuffer_ReturnsEmpty()
    {
        var buf = new AudioBuffer();
        Assert.Empty(buf.DrainAll());
    }

    [Fact]
    public void Append_BeyondCapacity_DropsOldestAndLatchesOverflow()
    {
        var buf = new AudioBuffer(capacityBytes: 5);
        buf.Append(new byte[] { 1, 2 });
        buf.Append(new byte[] { 3, 4 });
        buf.Append(new byte[] { 5, 6, 7 });
        // Total 7 bytes exceeds 5; oldest [1,2] should have been dropped.

        Assert.True(buf.Overflowed);
        var drained = buf.DrainAll();
        Assert.Equal(2, drained.Count);
        Assert.Equal(new byte[] { 3, 4 }, drained[0]);
        Assert.Equal(new byte[] { 5, 6, 7 }, drained[1]);
    }

    [Fact]
    public void Append_SingleHugeChunk_StaysInBuffer()
    {
        // A single chunk larger than capacity is preserved (we never drop
        // the only chunk we have, even if it overflows).
        var buf = new AudioBuffer(capacityBytes: 5);
        var big = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        buf.Append(big);
        Assert.False(buf.Overflowed);
        Assert.Equal(1, buf.ChunkCount);
        Assert.Equal(10, buf.BytesBuffered);
    }

    [Fact]
    public void Clear_ResetsEverythingIncludingOverflowFlag()
    {
        var buf = new AudioBuffer(capacityBytes: 5);
        buf.Append(new byte[] { 1, 2, 3 });
        buf.Append(new byte[] { 4, 5, 6, 7 });
        Assert.True(buf.Overflowed);

        buf.Clear();

        Assert.True(buf.IsEmpty);
        Assert.False(buf.Overflowed);
        Assert.Equal(0, buf.BytesBuffered);
    }

    // ===== Phase 4: disk spill ==============================================

    private static string FreshSpillDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-dictate-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SpillEnabled_Overflow_PreservesChunksOnDisk()
    {
        var dir = FreshSpillDir();
        try
        {
            using var buf = new AudioBuffer(capacityBytes: 5, spillDirectory: dir);
            buf.Append(new byte[] { 1, 2 });   // 2 bytes in memory
            buf.Append(new byte[] { 3, 4 });   // 4 bytes
            buf.Append(new byte[] { 5, 6, 7 }); // 7 > cap 5: spill [1,2] to disk

            Assert.False(buf.Overflowed);
            Assert.True(buf.Spilled);
            Assert.Equal(1, buf.SpilledChunkCount);
            Assert.Equal(7L, buf.BytesBuffered);
            // Memory should now hold [3,4] (2 bytes) + [5,6,7] (3 bytes) = 5 bytes
            Assert.Equal(5L, buf.BytesInMemory);

            var drained = buf.DrainAll();
            Assert.Equal(3, drained.Count);
            Assert.Equal(new byte[] { 1, 2 }, drained[0]);
            Assert.Equal(new byte[] { 3, 4 }, drained[1]);
            Assert.Equal(new byte[] { 5, 6, 7 }, drained[2]);
            Assert.True(buf.IsEmpty);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SpillEnabled_ManyChunks_PreservesOrder()
    {
        var dir = FreshSpillDir();
        try
        {
            using var buf = new AudioBuffer(capacityBytes: 10, spillDirectory: dir);
            for (int i = 1; i <= 20; i++)
                buf.Append(new byte[] { (byte)i });

            Assert.True(buf.Spilled);
            Assert.False(buf.Overflowed);
            // 20 chunks at 1 byte = 20 bytes total. Capacity 10 -> ~10 spilled.
            Assert.True(buf.SpilledChunkCount > 0);
            Assert.Equal(20L, buf.BytesBuffered);
            Assert.True(buf.BytesInMemory <= 10);

            var drained = buf.DrainAll();
            Assert.Equal(20, drained.Count);
            for (int i = 0; i < 20; i++)
                Assert.Equal(new byte[] { (byte)(i + 1) }, drained[i]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SpillEnabled_DrainDeletesSpillFiles()
    {
        var dir = FreshSpillDir();
        try
        {
            using var buf = new AudioBuffer(capacityBytes: 4, spillDirectory: dir);
            buf.Append(new byte[] { 1, 2 });
            buf.Append(new byte[] { 3, 4 });
            buf.Append(new byte[] { 5, 6 });   // spills [1,2]
            buf.Append(new byte[] { 7, 8 });   // spills [3,4]

            Assert.True(Directory.EnumerateFiles(dir, "*.bin").Any());

            var _ = buf.DrainAll();

            // After drain, spill files for drained chunks should be gone.
            Assert.False(Directory.EnumerateFiles(dir, "*.bin").Any());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Clear_WithSpill_DeletesAllSpillFiles()
    {
        var dir = FreshSpillDir();
        try
        {
            using var buf = new AudioBuffer(capacityBytes: 4, spillDirectory: dir);
            buf.Append(new byte[] { 1, 2 });
            buf.Append(new byte[] { 3, 4 });
            buf.Append(new byte[] { 5, 6 });   // spills [1,2]
            Assert.True(buf.Spilled);

            buf.Clear();

            Assert.True(buf.IsEmpty);
            Assert.False(buf.Spilled);
            Assert.False(Directory.EnumerateFiles(dir, "*.bin").Any());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Dispose_DeletesSpillFiles()
    {
        var dir = FreshSpillDir();
        try
        {
            var buf = new AudioBuffer(capacityBytes: 4, spillDirectory: dir);
            buf.Append(new byte[] { 1, 2 });
            buf.Append(new byte[] { 3, 4 });
            buf.Append(new byte[] { 5, 6 });   // spills
            Assert.True(Directory.EnumerateFiles(dir, "*.bin").Any());

            buf.Dispose();

            Assert.False(Directory.EnumerateFiles(dir, "*.bin").Any());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var dir = FreshSpillDir();
        try
        {
            var buf = new AudioBuffer(spillDirectory: dir);
            buf.Append(new byte[] { 1, 2, 3 });
            buf.Dispose();
            buf.Dispose();   // second call must not throw
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void OperationsAfterDispose_Throw()
    {
        var dir = FreshSpillDir();
        try
        {
            var buf = new AudioBuffer(spillDirectory: dir);
            buf.Dispose();

            Assert.Throws<ObjectDisposedException>(() => buf.Append(new byte[] { 1 }));
            Assert.Throws<ObjectDisposedException>(() => buf.DrainAll());
            Assert.Throws<ObjectDisposedException>(() => buf.Clear());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
