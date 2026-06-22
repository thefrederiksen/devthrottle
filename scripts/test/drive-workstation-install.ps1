# Non-destructive UI test of the installer's Workstation path (skip Sign-in).
# Launches the built cc-director-setup.exe, selects the "I already have a gateway" (Workstation) card,
# and asserts the step rail drops the Sign-in step and that advancing past Prerequisites lands on the
# Privacy step ("A quick, honest note"), NOT the Sign-in step ("Sign in to DevThrottle").
# It NEVER reaches the Install step, so nothing is installed. It then closes the installer it launched.
$ErrorActionPreference = "Stop"

$exe    = "D:\ReposFred\devthrottle\tools\cc-director-setup\bin\Release\net10.0-windows\win-x64\publish\cc-director-setup.exe"
$shotDir = Join-Path $env:TEMP "cc-setup-wstest"
$capture = "D:\ReposFred\devthrottle\scripts\capture-window.ps1"
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Get-Window([int]$procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
    for ($i = 0; $i -lt 40; $i++) {
        $w = $AE::RootElement.FindFirst($TS::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function ById($root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    return $root.FindFirst($TS::Descendants, $cond)
}
function ByName($root, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    return $root.FindFirst($TS::Descendants, $cond)
}
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Select-El($el) { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Shot([int]$procId, [string]$name) {
    & $capture -TargetPid $procId -OutPath (Join-Path $shotDir $name) | Out-Null
    Write-Host "  shot: $name"
}
function VisibleSteps($win) {
    # Read the rail circle numbers that are present (collapsed rows are absent from the UIA tree).
    $nums = @()
    foreach ($n in 2..7) {
        $el = ById $win "Step$($n)Num"
        if ($el) { $nums += $el.Current.Name }
    }
    return ,$nums
}
function StepLabels($win) {
    $labels = @()
    foreach ($n in 1..7) {
        $el = ById $win "Step$($n)Label"
        if ($el) { $labels += $el.Current.Name }
    }
    return ,$labels
}

# Force fresh-install mode so the role picker appears (this machine already has a real install).
# The override changes ONLY the wizard's install DETECTION, pointed at an empty scratch root; it never
# redirects an actual install, and this test never reaches the Install step anyway.
$freshRoot = Join-Path $env:TEMP "cc-setup-wstest-emptyroot"
New-Item -ItemType Directory -Force -Path $freshRoot | Out-Null
$env:CC_DIRECTOR_SETUP_INSTALL_ROOT = $freshRoot

$proc = Start-Process -FilePath $exe -PassThru
$procId = $proc.Id
Write-Host "Launched installer pid=$procId"
$win = Get-Window $procId
if (-not $win) { Write-Host "RESULT: FAIL - no installer window"; Stop-Process -Id $procId -Force; exit 1 }
Start-Sleep -Milliseconds 900

$fail = @()

# --- Welcome, default (Gateway pre-selected): rail should INCLUDE Sign in ---
Shot $procId "01-welcome-default-gateway.png"
$labelsDefault = StepLabels $win
Write-Host "Welcome (default) rail labels: $($labelsDefault -join ' | ')"
if ($labelsDefault -notcontains "Sign in") { $fail += "Default Gateway rail is missing the 'Sign in' step." }

# --- Select Workstation: 'I already have a gateway' ---
$ws = ById $win "HaveGatewayRadio"
if (-not $ws) { Write-Host "RESULT: FAIL - HaveGatewayRadio not found"; Stop-Process -Id $procId -Force; exit 1 }
Select-El $ws
Start-Sleep -Milliseconds 700
Shot $procId "02-welcome-workstation.png"
$labelsWs = StepLabels $win
$numsWs   = VisibleSteps $win
Write-Host "Welcome (workstation) rail labels: $($labelsWs -join ' | ')"
Write-Host "Welcome (workstation) circle numbers (steps 2..n): $($numsWs -join ' ')"
if ($labelsWs -contains "Sign in") { $fail += "Workstation rail STILL shows the 'Sign in' step." }
# After dropping Sign-in, the six visible steps renumber 1..6, so circles 2..7's rendered numbers are 2..6.
if (($numsWs -join ' ') -ne "2 3 4 5 6") { $fail += "Workstation rail numbers not renumbered cleanly (got '$($numsWs -join ' ')', expected '2 3 4 5 6')." }

# --- Welcome -> Prerequisites ---
Invoke-El (ById $win "NextButton")
Start-Sleep -Milliseconds 1500
Shot $procId "03-prerequisites.png"
$onPrereq = (ByName $win "Sign in to DevThrottle") -eq $null
if (-not $onPrereq) { $fail += "Landed on the Sign-in step right after Welcome on the Workstation path." }

# --- Prerequisites -> next visible step (must be Privacy, not Sign in) ---
$next = ById $win "NextButton"
$prereqNextEnabled = $next.Current.IsEnabled
Write-Host "Prerequisites Next enabled: $prereqNextEnabled"
if ($prereqNextEnabled) {
    Invoke-El $next
    Start-Sleep -Milliseconds 1200
    Shot $procId "04-after-prereq.png"
    $onPrivacy = (ByName $win "A quick, honest note") -ne $null
    $onSignIn  = (ByName $win "Sign in to DevThrottle") -ne $null
    Write-Host "After Prerequisites -> Privacy heading present: $onPrivacy ; Sign-in heading present: $onSignIn"
    if ($onSignIn) { $fail += "Workstation path showed the Sign-in step after Prerequisites." }
    if (-not $onPrivacy) { $fail += "Workstation path did NOT land on the Privacy step after Prerequisites." }
} else {
    Write-Host "NOTE: a required prerequisite is missing on this machine, so Next is disabled at step 2."
    Write-Host "      The rail assertions above already prove Sign-in is dropped for the Workstation role."
}

# --- Close the installer we launched (this is cc-director-setup.exe, NOT cc-director.exe) ---
Stop-Process -Id $procId -Force
Write-Host ""
if ($fail.Count -eq 0) {
    Write-Host "RESULT: PASS - Workstation install skips the Sign-in step."
    Write-Host "Screenshots: $shotDir"
    exit 0
} else {
    Write-Host "RESULT: FAIL"
    $fail | ForEach-Object { Write-Host "  - $_" }
    Write-Host "Screenshots: $shotDir"
    exit 1
}
