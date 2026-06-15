# UI Automation helper for issue #436 proof. ASCII only.
# Usage: powershell -NoProfile -File uia-436.ps1 <command> [args]
# The Settings dialog is an owned Avalonia window; it shows in the UIA tree as a descendant of the
# Director main window (pid 36984), so everything is driven through that window's subtree.
param(
    [Parameter(Mandatory=$true)][string]$Command,
    [string]$Arg1,
    [string]$Arg2
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$DirectorExe = 'D:\ReposFred\cc-director-wt-436\local_builds\cc-director8.exe'

if (-not ([System.Management.Automation.PSTypeName]'Win32').Type) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@
}

function Get-DirectorPid {
    $p = Get-Process | Where-Object { $_.Path -eq $DirectorExe } | Select-Object -First 1
    if ($p) { return $p.Id } else { return 0 }
}

function Get-MainWindow {
    $pid0 = Get-DirectorPid
    if ($pid0 -eq 0) { return $null }
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $wins) { if ($w.Current.ProcessId -eq $pid0) { return $w } }
    return $null
}

function Get-Settings {
    $main = Get-MainWindow
    if (-not $main) { return $null }
    $dcond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'CC Director Settings')
    return $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $dcond)
}

function Find-ById($root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Id($root, [string]$id) {
    $el = Find-ById $root $id
    if (-not $el) { Write-Output "NOTFOUND:$id"; return $false }
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "INVOKED:$id"; return $true
}

function Select-Tab($root, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $el) { Write-Output "TAB-NOTFOUND:$name"; return }
    $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output "TAB-SELECTED:$name"
}

function Set-Combo($settings, [string]$comboId, [string]$value) {
    $combo = Find-ById $settings $comboId
    if (-not $combo) { Write-Output "COMBO-NOTFOUND:$comboId"; return }
    $pid0 = Get-DirectorPid
    $exp = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $exp.Expand(); Start-Sleep -Milliseconds 700
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $icond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $value)
    $items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $icond)
    $target = $null
    foreach ($it in $items) { if ($it.Current.ProcessId -eq $pid0) { $target = $it; break } }
    if (-not $target) { Write-Output "COMBO-ITEM-NOTFOUND:$value"; try { $exp.Collapse() } catch {}; return }
    $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output "COMBO-SET:$comboId=$value"
}

function Set-Text($settings, [string]$id, [string]$value) {
    $el = Find-ById $settings $id
    if (-not $el) { Write-Output "TEXT-NOTFOUND:$id"; return }
    $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    Write-Output "TEXT-SET:$id=$value"
}

function Read-Value($settings, [string]$id) {
    $el = Find-ById $settings $id
    if (-not $el) { Write-Output "READ-NOTFOUND:$id"; return }
    $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    Write-Output "VALUE:$id=$($vp.Current.Value)"
}

function Shot($el, [string]$path) {
    $r = $el.Current.BoundingRectangle
    if ($r.Width -le 0) { Write-Output "NO-BOUNDS"; return }
    try { [void][Win32]::SetForegroundWindow([IntPtr]$el.Current.NativeWindowHandle) } catch {}
    Start-Sleep -Milliseconds 500
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "SHOT:$path ($([int]$r.Width)x$([int]$r.Height))"
}

switch ($Command) {
    'open-settings'     { $m = Get-MainWindow; if (-not $m) { Write-Output 'MAIN-NOTFOUND'; exit 1 }; try { [void][Win32]::SetForegroundWindow([IntPtr]$m.Current.NativeWindowHandle) } catch {}; Start-Sleep -Milliseconds 300; Invoke-Id $m 'BtnSettings' }
    'select-agents'     { $s = Get-Settings; if (-not $s) { Write-Output 'DLG-NOTFOUND'; exit 1 }; Select-Tab $s 'Agents' }
    'shot-settings'     { $s = Get-Settings; if (-not $s) { Write-Output 'DLG-NOTFOUND'; exit 1 }; Shot $s $Arg1 }
    'set-preset'        { $s = Get-Settings; if (-not $s) { Write-Output 'DLG-NOTFOUND'; exit 1 }; Set-Combo $s $Arg1 $Arg2 }
    'set-text'          { $s = Get-Settings; if (-not $s) { Write-Output 'DLG-NOTFOUND'; exit 1 }; Set-Text $s $Arg1 $Arg2 }
    'read'              { $s = Get-Settings; if (-not $s) { Write-Output 'DLG-NOTFOUND'; exit 1 }; Read-Value $s $Arg1 }
    'invoke'            { $s = Get-Settings; if (-not $s) { Write-Output 'DLG-NOTFOUND'; exit 1 }; Invoke-Id $s $Arg1 }
    'shot-named'        {
        $pid0 = Get-DirectorPid
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Arg1)
        $matches = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
        $w = $null; foreach ($m in $matches) { if ($m.Current.ProcessId -eq $pid0) { $w = $m; break } }
        if (-not $w) { Write-Output "WIN-NOTFOUND:$Arg1"; exit 1 }
        Shot $w $Arg2
    }
    default { Write-Output "UNKNOWN:$Command" }
}
