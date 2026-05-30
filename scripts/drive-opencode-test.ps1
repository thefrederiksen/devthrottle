# One-off: drive the New Session dialog to start an OpenCode session, for verifying
# the OpenCode launch fix in a real Director. Selects the OpenCode agent radio, picks
# the first repo (which enables Start), and clicks Start.
param([Parameter(Mandatory = $true)][int]$TargetPid)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Find-ById($root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    return $root.FindFirst($TS::Descendants, $cond)
}

# All top-level windows for the process.
function Get-ProcWindows([int]$procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
    return $AE::RootElement.FindAll($TS::Children, $cond)
}

# Find the window (main or dialog) that currently contains the given AutomationId.
function Find-WindowWith([int]$procId, [string]$id) {
    for ($i = 0; $i -lt 30; $i++) {
        foreach ($w in Get-ProcWindows $procId) {
            $el = Find-ById $w $id
            if ($el) { return @($w, $el) }
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

# 1. Open the New Session dialog.
$mainPair = Find-WindowWith $TargetPid "BtnNewSession"
if (-not $mainPair) { Write-Output "FAIL: BtnNewSession not found"; exit 1 }
$btnNew = $mainPair[1]
$btnNew.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output "Opened New Session dialog"
Start-Sleep -Milliseconds 800

# 2. Find the dialog via its OpenCode radio.
$dlgPair = Find-WindowWith $TargetPid "AgentRadioOpenCode"
if (-not $dlgPair) { Write-Output "FAIL: AgentRadioOpenCode not found"; exit 1 }
$dlg = $dlgPair[0]
$radio = $dlgPair[1]

# 3. Select the OpenCode agent.
$radio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Write-Output "Selected OpenCode agent"
Start-Sleep -Milliseconds 300

# 4. Select the first repo in RepoList (sets the path and enables Start).
$repoList = Find-ById $dlg "RepoList"
if (-not $repoList) { Write-Output "FAIL: RepoList not found"; exit 1 }
$itemCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $repoList.FindAll($TS::Descendants, $itemCond)
if ($items.Count -eq 0) { Write-Output "FAIL: no repos in RepoList"; exit 1 }
$first = $items.Item(0)
$first.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Write-Output "Selected repo: [$($first.Current.Name)]"
Start-Sleep -Milliseconds 400

# 5. Click Start.
$btnAction = Find-ById $dlg "BtnAction"
if (-not $btnAction) { Write-Output "FAIL: BtnAction not found"; exit 1 }
if (-not $btnAction.Current.IsEnabled) { Write-Output "FAIL: BtnAction is disabled (no path?)"; exit 1 }
$btnAction.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output "Clicked Start - OpenCode session launching"
