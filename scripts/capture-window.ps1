# Capture a top-level window to PNG by PID, using Win32 PrintWindow with
# PW_RENDERFULLCONTENT (works for GPU/composited windows like Avalonia).
# Finds the largest VISIBLE top-level window owned by the PID (Avalonia main window
# is often not reported via .NET MainWindowHandle, so we enumerate directly).
# Usage: capture-window.ps1 -TargetPid 12345 -OutPath C:\tmp\shot.png
param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [Parameter(Mandatory = $true)][string]$OutPath
)

Add-Type @"
using System;
using System.Drawing;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WinCap {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr FindMainWindow(uint targetPid) {
        IntPtr best = IntPtr.Zero; int bestArea = 0;
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != targetPid) return true;
            if (!IsWindowVisible(h)) return true;
            RECT r; GetWindowRect(h, out r);
            int area = (r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static Bitmap Capture(IntPtr hWnd) {
        RECT r; GetWindowRect(hWnd, out r);
        int w = r.Right - r.Left; int h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;
        Bitmap bmp = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc();
            PrintWindow(hWnd, hdc, 0x2); // PW_RENDERFULLCONTENT
            g.ReleaseHdc(hdc);
        }
        return bmp;
    }
}
"@ -ReferencedAssemblies System.Drawing

$h = [WinCap]::FindMainWindow([uint32]$TargetPid)
if ($h -eq [IntPtr]::Zero) { Write-Output "NO_WINDOW: no visible top-level window for pid $TargetPid"; exit 1 }
[WinCap]::ShowWindow($h, 9) | Out-Null   # SW_RESTORE
[WinCap]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 500
$bmp = [WinCap]::Capture($h)
if (-not $bmp) { Write-Output "CAPTURE_FAILED"; exit 1 }
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$dim = "$($bmp.Width)x$($bmp.Height)"
$bmp.Dispose()
Write-Output "SAVED: $OutPath ($dim) hwnd=$($h.ToInt64()) pid=$TargetPid"
