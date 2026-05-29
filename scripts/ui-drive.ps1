# Drive the CC Director Avalonia UI via Windows UI Automation (DPI-robust, no coordinates).
# Avalonia exposes each named control's x:Name as its AutomationId, and a Button's text as
# its Name. Supports: select first session in SessionList, invoke a tab/button by AutomationId.
# Usage:
#   ui-drive.ps1 -TargetPid 123 -SelectFirstSession
#   ui-drive.ps1 -TargetPid 123 -InvokeAutomationId WingmanTabButton
#   ui-drive.ps1 -TargetPid 123 -ToggleAutomationId WingmanVoicePreviewToggle
param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [switch]$SelectFirstSession,
    [int]$SelectSessionIndex = -1,
    [string]$InvokeAutomationId,
    [string]$InvokeName,
    [string]$ToggleAutomationId
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Get-Window([int]$procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    for ($i = 0; $i -lt 20; $i++) {
        $w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst($TS::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Find-ByAutomationId($root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst($TS::Descendants, $cond)
}

$win = Get-Window $TargetPid
if (-not $win) { Write-Output "NO_WINDOW for pid $TargetPid"; exit 1 }

if ($SelectFirstSession) {
    $list = Find-ByAutomationId $win "SessionList"
    if (-not $list) { Write-Output "SessionList not found"; exit 1 }
    # First selectable item (ListBoxItem) under the list.
    $itemCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $items = $list.FindAll($TS::Descendants, $itemCond)
    if ($items.Count -eq 0) { Write-Output "No session items in SessionList"; exit 1 }
    $first = $items.Item(0)
    $sel = $first.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sel.Select()
    Write-Output "SELECTED session item 0 (of $($items.Count)); name=[$($first.Current.Name)]"
}

if ($InvokeAutomationId) {
    $el = Find-ByAutomationId $win $InvokeAutomationId
    if (-not $el) { Write-Output "AutomationId '$InvokeAutomationId' not found"; exit 1 }
    $inv = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $inv.Invoke()
    Write-Output "INVOKED $InvokeAutomationId"
}

if ($SelectSessionIndex -ge 0) {
    $list = Find-ByAutomationId $win "SessionList"
    if (-not $list) { Write-Output "SessionList not found"; exit 1 }
    $itemCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $items = $list.FindAll($TS::Descendants, $itemCond)
    if ($SelectSessionIndex -ge $items.Count) { Write-Output "index $SelectSessionIndex out of range ($($items.Count) items)"; exit 1 }
    $it = $items.Item($SelectSessionIndex)
    $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output "SELECTED session item $SelectSessionIndex of $($items.Count); name=[$($it.Current.Name)]"
}

if ($InvokeName) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $InvokeName)
    $el = $win.FindFirst($TS::Descendants, $cond)
    if (-not $el) { Write-Output "Element named '$InvokeName' not found"; exit 1 }
    $inv = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $inv.Invoke()
    Write-Output "INVOKED by name '$InvokeName'"
}

if ($ToggleAutomationId) {
    $el = Find-ByAutomationId $win $ToggleAutomationId
    if (-not $el) { Write-Output "AutomationId '$ToggleAutomationId' not found"; exit 1 }
    $tog = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $tog.Toggle()
    Write-Output "TOGGLED $ToggleAutomationId"
}

Write-Output "OK"
