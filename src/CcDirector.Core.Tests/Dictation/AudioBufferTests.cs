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
}
