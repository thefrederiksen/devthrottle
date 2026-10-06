param(
    [Parameter(Mandatory = $true)][ValidateSet('list', 'shot', 'tree', 'click', 'type')][string]$Verb,
    [string]$ProcessName = "cc-director5",
    [string]$Title = "",
    [string]$Out = "",
    [string]$Name = "",
    [string]$Text = "",
    [int]$Index = 0
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text; using System.Collections.Generic;
public static class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref PT p);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  public static List<IntPtr> Of(uint pid) { var l = new List<IntPtr>(); EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) l.Add(h); return true; }, IntPtr.Zero); return l; }
  public static string T(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
}
"@
$proc = Get-Process $ProcessName
$wins = [W]::Of([uint32]$proc.Id) | Where-Object { [W]::T($_) -ne "" }
if ($Title) { $wins = $wins | Where-Object { [W]::T($_) -like "*$Title*" } }
if ($Verb -eq 'list') { foreach ($h in $wins) { "{0}  '{1}'" -f $h, [W]::T($h) }; return }
$h = @($wins)[0]
if (-not $h) { throw "no window for $ProcessName matching '$Title'" }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
function Find-ByName([string]$n) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $n)
    $r = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c))
    if ($r.Count -eq 0) {
        $c2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $n)
        $r = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c2))
    }
    return $r
}
switch ($Verb) {
    'shot' {
        [W]::ShowWindow($h, 9) | Out-Null; [W]::SetForegroundWindow($h) | Out-Null
        [W]::SetCursorPos(5, 5) | Out-Null; Start-Sleep -Milliseconds 700
        $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $g.Dispose()
        $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); "saved $Out ($($bmp.Width)x$($bmp.Height)) from '$([W]::T($h))'"
    }
    'tree' {
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($e in $all) { $n = $e.Current.Name; if ($n -or $e.Current.AutomationId) { "{0} | {1} | {2}" -f $e.Current.ControlType.ProgrammaticName, $n, $e.Current.AutomationId } }
    }
    'click' {
        $els = Find-ByName $Name; if ($els.Count -le $Index) { throw "no element named '$Name' (found $($els.Count))" }
        $e = $els[$Index]
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $cand = $e
        for ($up = 0; $up -lt 4 -and $cand; $up++) {
            $pats = $cand.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }
            if ($pats -contains 'InvokePatternIdentifiers.Pattern') { $cand.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); "invoked '$Name' (ancestor $up, $($cand.Current.ControlType.ProgrammaticName))"; return }
            if ($pats -contains 'SelectionItemPatternIdentifiers.Pattern') { $cand.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); "selected '$Name' (ancestor $up)"; return }
            $cand = $walker.GetParent($cand)
        }
        try { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); "invoked '$Name'" }
        catch {
            try { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); "selected '$Name'" }
            catch {
                $b = $e.Current.BoundingRectangle
                $pt = New-Object W+PT; $pt.X = [int]($b.X + $b.Width / 2); $pt.Y = [int]($b.Y + $b.Height / 2)
                [W]::ScreenToClient($h, [ref]$pt) | Out-Null
                $lp = [IntPtr](($pt.Y -shl 16) -bor ($pt.X -band 0xFFFF))
                [W]::PostMessage($h, 0x200, [IntPtr]0, $lp) | Out-Null; Start-Sleep -Milliseconds 100
                [W]::PostMessage($h, 0x201, [IntPtr]1, $lp) | Out-Null; Start-Sleep -Milliseconds 80
                [W]::PostMessage($h, 0x202, [IntPtr]0, $lp) | Out-Null; "posted a click to '$Name' at client $($pt.X),$($pt.Y)"
            }
        }
    }
    'type' {
        $els = Find-ByName $Name; $e = $els[$Index]
        $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text); "set '$Name' to '$Text'"
    }
}
