param(
  [Parameter(Mandatory=$true)][int]$ProcessId,
  [int]$Width = 390,
  [int]$Height = 844,
  [int]$X = 100,
  [int]$Y = 50
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder t, int c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc p, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);
}
"@

$script:foundHandle = [IntPtr]::Zero
$script:targetPid = [uint32]$ProcessId

$cb = [W+EnumWindowsProc]{
  param($h, $lp)
  [uint32]$wpid = 0
  [W]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
  if ($wpid -eq $script:targetPid -and [W]::IsWindowVisible($h)) {
    $sb = New-Object System.Text.StringBuilder 256
    [W]::GetWindowText($h, $sb, 256) | Out-Null
    if ($sb.Length -gt 0) {
      $script:foundHandle = $h
      return $false
    }
  }
  return $true
}

[W]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null

if ($script:foundHandle -ne [IntPtr]::Zero) {
  [W]::MoveWindow($script:foundHandle, $X, $Y, $Width, $Height, $true) | Out-Null
  "Resized HWND {0} (PID {1}) to {2}x{3} at ({4},{5})" -f $script:foundHandle, $ProcessId, $Width, $Height, $X, $Y
} else {
  "No visible window found for PID $ProcessId"
  exit 1
}
