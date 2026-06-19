using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using CcClick.Helpers;

namespace CcClick.Commands;

public static class ScreenshotCommand
{
    // PrintWindow with PW_RENDERFULLCONTENT captures the target window's OWN pixels,
    // even when it is occluded or not in the foreground, and even for GPU/DirectComposition
    // (Avalonia) windows. This is critical: a plain screen-region grab of a background
    // window's rectangle would capture whatever window is visually on top of it (e.g. a
    // different CC Director). Keying capture to the window handle makes that impossible.
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public static int Execute(AutomationBase automation, string? windowTitle, int? pid, string output)
    {
        var fullPath = Path.GetFullPath(output);
        int width, height;

        if ((pid is int p && p > 0) || !string.IsNullOrEmpty(windowTitle))
        {
            var window = WindowFinder.Resolve(automation, windowTitle, pid);
            var hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("Resolved window has no native handle; cannot capture it.");

            if (!GetWindowRect(hwnd, out var r))
                throw new InvalidOperationException("GetWindowRect failed for the target window.");
            width = r.Right - r.Left;
            height = r.Bottom - r.Top;
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException($"Target window has a non-positive size ({width}x{height}).");

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try
                {
                    var ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                    if (!ok) throw new InvalidOperationException("PrintWindow returned false for the target window.");
                }
                finally { g.ReleaseHdc(hdc); }
            }
            bmp.Save(fullPath, ImageFormat.Png);
        }
        else
        {
            var capture = Capture.MainScreen();
            capture.ToFile(fullPath);
            width = capture.Bitmap.Width;
            height = capture.Bitmap.Height;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            saved = fullPath,
            width,
            height
        }, JsonOptions.Default));
        return 0;
    }
}
