# One QA step against the agent-brain-panel: optionally set prompt text, invoke a
# button, wait until the panel's status line matches a regex (its own "done" signal),
# then screenshot. Exits 1 on timeout so the QA loop notices.
# Usage:
#   qa-step.ps1 -PanelPid 123 -Invoke AskButton -SetPrompt "..." -WaitStatus "Reply received" -Shot qa4.png
param(
    [Parameter(Mandatory = $true)][int]$PanelPid,
    [string]$SetPrompt,
    [string]$Invoke,
    [string]$Toggle,
    [Parameter(Mandatory = $true)][string]$WaitStatus,
    [int]$TimeoutSec = 120,
    [string]$Shot
)

$ErrorActionPreference = "Stop"
$scripts = Join-Path $PSScriptRoot "..\..\..\scripts"

if ($SetPrompt) {
    & powershell -NoProfile -File (Join-Path $scripts "ui-drive.ps1") -TargetPid $PanelPid -SetTextAutomationId PromptBox -Text $SetPrompt
}
if ($Toggle) {
    & powershell -NoProfile -File (Join-Path $scripts "ui-drive.ps1") -TargetPid $PanelPid -ToggleAutomationId $Toggle
}
if ($Invoke) {
    & powershell -NoProfile -File (Join-Path $scripts "ui-drive.ps1") -TargetPid $PanelPid -InvokeAutomationId $Invoke
}

$deadline = (Get-Date).AddSeconds($TimeoutSec)
$status = ""
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $out = & powershell -NoProfile -File (Join-Path $scripts "ui-drive.ps1") -TargetPid $PanelPid -ReadAutomationId StatusText
    $line = ($out | Select-String -Pattern "READ StatusText:").Line
    if ($line) { $status = $line.Substring("READ StatusText: ".Length) }
    if ($status -match $WaitStatus) {
        Write-Output "STATUS-OK: $status"
        if ($Shot) {
            & powershell -NoProfile -File (Join-Path $scripts "capture-window.ps1") -TargetPid $PanelPid -OutPath $Shot
        }
        exit 0
    }
    if ($status -match "failed") {
        Write-Output "STATUS-FAILED: $status"
        exit 1
    }
}
Write-Output "STATUS-TIMEOUT: last=[$status] wanted=[$WaitStatus]"
exit 1
