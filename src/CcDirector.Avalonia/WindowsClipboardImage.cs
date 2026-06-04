using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// Puts an image file on the Windows clipboard as an actual image (not a path string),
/// so it can be pasted into browsers (GitHub issues), Claude Code, Paint, etc.
///
/// Avalonia's IClipboard cannot write native image formats, so this goes through Win32
/// directly. Two formats are written: CF_DIB (the universal bitmap format every Windows
/// app reads) and, for PNG files, the registered "PNG" format (Chromium-based browsers
/// prefer it and keep the image lossless).
/// </summary>
internal static class WindowsClipboardImage
{
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int BitmapInfoHeaderSize = 40;
    private const int ClipboardOpenAttempts = 5;
    private const int ClipboardRetryDelayMs = 50;

    /// <summary>
    /// Copies the image file to the clipboard. Throws with a clear message when the
    /// file cannot be decoded or the clipboard cannot be opened (held by another app).
    /// </summary>
    public static void CopyImageFile(string filePath)
    {
        FileLog.Write($"[WindowsClipboardImage] CopyImageFile: {filePath}");
        if (!File.Exists(filePath))
            throw new InvalidOperationException($"File not found: {filePath}");

        var dib = BuildDib(filePath);
        var isPng = string.Equals(Path.GetExtension(filePath), ".png", StringComparison.OrdinalIgnoreCase);
        var pngBytes = isPng ? File.ReadAllBytes(filePath) : null;

        OpenClipboardWithRetry();
        try
        {
            if (!EmptyClipboard())
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed");

            SetClipboardBytes(CF_DIB, dib);
            if (pngBytes != null)
            {
                var pngFormat = RegisterClipboardFormat("PNG");
                if (pngFormat == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClipboardFormat(PNG) failed");
                SetClipboardBytes(pngFormat, pngBytes);
            }
        }
        finally
        {
            CloseClipboard();
        }
        FileLog.Write($"[WindowsClipboardImage] CopyImageFile: done, dib={dib.Length} bytes, png={pngBytes?.Length ?? 0} bytes");
    }

    /// <summary>
    /// Decodes the image via Avalonia and packs it as a CF_DIB block.
    /// </summary>
    private static byte[] BuildDib(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var bitmap = new Bitmap(stream);

        if (bitmap.Format != PixelFormats.Bgra8888)
            throw new InvalidOperationException(
                $"Unexpected pixel format {bitmap.Format} decoding {filePath} (expected Bgra8888)");

        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var stride = width * 4;
        var pixels = new byte[stride * height];

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return BuildDibFromBgraTopDown(pixels, width, height);
    }

    /// <summary>
    /// Packs top-down 32bpp BGRA pixels as a CF_DIB block: BITMAPINFOHEADER +
    /// pixel rows stored bottom-up (positive biHeight). Pure byte logic so it is
    /// unit-testable without an Avalonia platform or the Win32 clipboard.
    /// </summary>
    internal static byte[] BuildDibFromBgraTopDown(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"Invalid dimensions: {width}x{height}");
        if (pixels.Length != width * height * 4)
            throw new ArgumentException(
                $"Pixel buffer is {pixels.Length} bytes, expected {width * height * 4} for {width}x{height} BGRA");

        var stride = width * 4;
        var dib = new byte[BitmapInfoHeaderSize + pixels.Length];
        WriteInt32(dib, 0, BitmapInfoHeaderSize);   // biSize
        WriteInt32(dib, 4, width);                  // biWidth
        WriteInt32(dib, 8, height);                 // biHeight (positive = bottom-up)
        WriteInt16(dib, 12, 1);                     // biPlanes
        WriteInt16(dib, 14, 32);                    // biBitCount
        WriteInt32(dib, 16, 0);                     // biCompression = BI_RGB
        WriteInt32(dib, 20, pixels.Length);         // biSizeImage
        // biXPelsPerMeter / biYPelsPerMeter / biClrUsed / biClrImportant stay 0

        // Decoded rows are top-down; DIB with positive height wants bottom-up.
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * stride, dib, BitmapInfoHeaderSize + (height - 1 - y) * stride, stride);

        return dib;
    }

    /// <summary>
    /// The clipboard is a shared resource that another process may briefly hold open,
    /// so opening it legitimately needs a short retry loop before giving up.
    /// </summary>
    private static void OpenClipboardWithRetry()
    {
        for (var attempt = 1; attempt <= ClipboardOpenAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
                return;
            if (attempt < ClipboardOpenAttempts)
                Thread.Sleep(ClipboardRetryDelayMs);
        }
        throw new Win32Exception(Marshal.GetLastWin32Error(),
            "Could not open the clipboard (another application is holding it)");
    }

    /// <summary>
    /// Copies the bytes into a moveable HGLOBAL and hands it to the clipboard.
    /// On success the system owns the memory; it is only freed here on failure.
    /// </summary>
    private static void SetClipboardBytes(uint format, byte[] bytes)
    {
        var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (hGlobal == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalAlloc failed");

        try
        {
            var ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalLock failed");
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            GlobalUnlock(hGlobal);

            if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetClipboardData(format={format}) failed");
        }
        catch
        {
            GlobalFree(hGlobal);
            throw;
        }
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
