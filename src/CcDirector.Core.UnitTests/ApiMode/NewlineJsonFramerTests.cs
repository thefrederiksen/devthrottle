using System.Text;
using CcDirector.Core.ApiMode;
using Xunit;

namespace CcDirector.Core.Tests.ApiMode;

public sealed class NewlineJsonFramerTests
{
    private static string Text(byte[] line) => Encoding.UTF8.GetString(line);

    [Fact]
    public void Append_LoneCarriageReturnInsideString_StaysInOneRecord()
    {
        // Arrange: a raw carriage return byte inside a JSON string, where a StreamReader would cut the line.
        var framer = new NewlineJsonFramer();
        var bytes = Encoding.UTF8.GetBytes("{\"text\":\"before\rafter\"}\n");

        // Act
        var lines = framer.Append(bytes);

        // Assert
        var line = Assert.Single(lines);
        Assert.Equal("{\"text\":\"before\rafter\"}", Text(line));
    }

    [Fact]
    public void Append_LineAndParagraphSeparatorsInsideString_StayInOneRecord()
    {
        // Arrange
        var framer = new NewlineJsonFramer();
        var record = "{\"text\":\"one\u2028two\u2029three\"}";

        // Act
        var lines = framer.Append(Encoding.UTF8.GetBytes(record + "\n"));

        // Assert
        Assert.Equal(record, Text(Assert.Single(lines)));
    }

    [Fact]
    public void Append_RecordSplitAcrossManyReads_ReturnsOneRecordWhenTheLineFeedArrives()
    {
        // Arrange
        var framer = new NewlineJsonFramer();
        var bytes = Encoding.UTF8.GetBytes("{\"type\":\"message_update\",\"delta\":\"OTTER-58\"}\n");
        var collected = new List<byte[]>();

        // Act: one byte per read.
        for (var i = 0; i < bytes.Length; i++)
        {
            var lines = framer.Append(bytes.AsSpan(i, 1));
            if (i < bytes.Length - 1)
                Assert.Empty(lines);
            collected.AddRange(lines);
        }

        // Assert
        Assert.Equal("{\"type\":\"message_update\",\"delta\":\"OTTER-58\"}", Text(Assert.Single(collected)));
        Assert.Equal(0, framer.PendingByteCount);
    }

    [Fact]
    public void Append_MultiByteCharacterSplitAcrossReads_DecodesCorrectly()
    {
        // Arrange: a four-byte character (an emoji) and a three-byte one, each cut in the middle.
        var framer = new NewlineJsonFramer();
        var record = "{\"text\":\"\U0001F600 €\"}";
        var bytes = Encoding.UTF8.GetBytes(record + "\n");
        var emojiStart = Array.IndexOf(bytes, (byte)0xF0);

        // Act
        var first = framer.Append(bytes.AsSpan(0, emojiStart + 2));
        var second = framer.Append(bytes.AsSpan(emojiStart + 2, 4));
        var third = framer.Append(bytes.AsSpan(emojiStart + 6));

        // Assert
        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Equal(record, Text(Assert.Single(third)));
    }

    [Fact]
    public void Append_ThreeMegabyteLine_ReturnsTheWholeRecord()
    {
        // Arrange
        var framer = new NewlineJsonFramer();
        var payload = new string('x', 3 * 1024 * 1024);
        var bytes = Encoding.UTF8.GetBytes("{\"text\":\"" + payload + "\"}\n");
        var collected = new List<byte[]>();

        // Act: in 64-kilobyte reads, as the transport reads.
        for (var offset = 0; offset < bytes.Length; offset += 65536)
            collected.AddRange(framer.Append(bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset))));

        // Assert
        var line = Assert.Single(collected);
        Assert.Equal(bytes.Length - 1, line.Length);
    }

    [Fact]
    public void Append_CarriageReturnLineFeed_StripsExactlyOneCarriageReturn()
    {
        // Arrange
        var framer = new NewlineJsonFramer();

        // Act
        var lines = framer.Append(Encoding.UTF8.GetBytes("{\"a\":1}\r\n{\"b\":2}\r\r\n"));

        // Assert
        Assert.Equal(2, lines.Count);
        Assert.Equal("{\"a\":1}", Text(lines[0]));
        Assert.Equal("{\"b\":2}\r", Text(lines[1]));
    }

    [Fact]
    public void Append_SeveralRecordsInOneRead_ReturnsThemInOrder()
    {
        // Arrange
        var framer = new NewlineJsonFramer();

        // Act
        var lines = framer.Append(Encoding.UTF8.GetBytes("{\"n\":1}\n{\"n\":2}\n{\"n\":3"));

        // Assert
        Assert.Equal(["{\"n\":1}", "{\"n\":2}"], lines.Select(Text));
        Assert.Equal(6, framer.PendingByteCount);
    }

    [Fact]
    public void Append_LineLongerThanTheLimit_ThrowsInvalidData()
    {
        // Arrange
        var framer = new NewlineJsonFramer(maximumLineBytes: 16);
        framer.Append(new byte[10]);

        // Act
        var ex = Assert.Throws<InvalidDataException>(() => framer.Append(new byte[10]));

        // Assert
        Assert.Contains("16 bytes", ex.Message);
    }

    [Fact]
    public void TakeUnterminated_BytesAfterTheLastLineFeed_ReturnsThemAndEmptiesTheFramer()
    {
        // Arrange
        var framer = new NewlineJsonFramer();
        framer.Append(Encoding.UTF8.GetBytes("{\"n\":1}\n{\"cut"));

        // Act
        var tail = framer.TakeUnterminated();

        // Assert
        Assert.NotNull(tail);
        Assert.Equal("{\"cut", Text(tail));
        Assert.Null(framer.TakeUnterminated());
    }

    [Fact]
    public void TakeUnterminated_StreamEndedOnALineFeed_ReturnsNull()
    {
        // Arrange
        var framer = new NewlineJsonFramer();
        framer.Append(Encoding.UTF8.GetBytes("{\"n\":1}\n"));

        // Act
        var tail = framer.TakeUnterminated();

        // Assert
        Assert.Null(tail);
    }
}
