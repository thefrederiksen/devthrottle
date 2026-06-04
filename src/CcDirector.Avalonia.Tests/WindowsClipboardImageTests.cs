using CcDirector.Avalonia;
using Xunit;

namespace CcDirector.Avalonia.Tests;

public class WindowsClipboardImageTests
{
    private const int HeaderSize = 40;

    [Fact]
    public void BuildDibFromBgraTopDown_WritesBitmapInfoHeaderFields()
    {
        var pixels = new byte[2 * 3 * 4];

        var dib = WindowsClipboardImage.BuildDibFromBgraTopDown(pixels, width: 2, height: 3);

        Assert.Equal(HeaderSize + pixels.Length, dib.Length);
        Assert.Equal(HeaderSize, ReadInt32(dib, 0));        // biSize
        Assert.Equal(2, ReadInt32(dib, 4));                 // biWidth
        Assert.Equal(3, ReadInt32(dib, 8));                 // biHeight (positive = bottom-up)
        Assert.Equal(1, ReadInt16(dib, 12));                // biPlanes
        Assert.Equal(32, ReadInt16(dib, 14));               // biBitCount
        Assert.Equal(0, ReadInt32(dib, 16));                // biCompression = BI_RGB
        Assert.Equal(pixels.Length, ReadInt32(dib, 20));    // biSizeImage
    }

    [Fact]
    public void BuildDibFromBgraTopDown_FlipsRowsBottomUp()
    {
        // 1x3 image: rows marked 1, 2, 3 top-down in the blue channel.
        var pixels = new byte[]
        {
            1, 0, 0, 255,   // top row
            2, 0, 0, 255,   // middle row
            3, 0, 0, 255,   // bottom row
        };

        var dib = WindowsClipboardImage.BuildDibFromBgraTopDown(pixels, width: 1, height: 3);

        // DIB rows are stored bottom-up: first stored row is the image's bottom row.
        Assert.Equal(3, dib[HeaderSize + 0]);
        Assert.Equal(2, dib[HeaderSize + 4]);
        Assert.Equal(1, dib[HeaderSize + 8]);
    }

    [Fact]
    public void BuildDibFromBgraTopDown_PreservesPixelBytesWithinRow()
    {
        // 2x1 image: two distinct BGRA pixels in one row.
        var pixels = new byte[]
        {
            10, 20, 30, 40,
            50, 60, 70, 80,
        };

        var dib = WindowsClipboardImage.BuildDibFromBgraTopDown(pixels, width: 2, height: 1);

        Assert.Equal(pixels, dib[HeaderSize..]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void BuildDibFromBgraTopDown_InvalidDimensions_Throws(int width, int height)
    {
        Assert.Throws<ArgumentException>(
            () => WindowsClipboardImage.BuildDibFromBgraTopDown(Array.Empty<byte>(), width, height));
    }

    [Fact]
    public void BuildDibFromBgraTopDown_BufferSizeMismatch_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => WindowsClipboardImage.BuildDibFromBgraTopDown(new byte[7], width: 2, height: 2));

        Assert.Contains("expected 16", ex.Message);
    }

    private static int ReadInt32(byte[] buffer, int offset) =>
        buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);

    private static short ReadInt16(byte[] buffer, int offset) =>
        (short)(buffer[offset] | (buffer[offset + 1] << 8));
}
