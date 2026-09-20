#Requires -Version 5.1
<#
.SYNOPSIS
    Send a prompt to seats created by populate-sessions.ps1, and say for each one whether it landed.

.DESCRIPTION
    A long turn is only a long turn while it is running, so the work cannot be started when the seats
    are created - it would be over before a shutdown began. This sends the prompts at the moment a run
    needs them, reads back what each session's activity state became, and reports both.

    IT REPORTS A REFUSAL AS A RESULT, NOT AS A FAILURE. A wedged seat cannot take a prompt, and that
    is one of the shapes being proved: the report names the seat, the words the Gateway used, and the
    activity state afterwards. Nothing here retries a refusal into a success.

    It knows no rig: a Gateway address, a credential and the report populate-sessions.ps1 wrote.

.PARAMETER SeatsReport
    The report populate-sessions.ps1 wrote. Seats are addressed by their key.

.PARAMETER Keys
    Which seats to prompt. The default prompts every seat that has a prompt named for it.

.PARAMETER PromptName
    Which prompt from the specification's "prompts" section to send. When given, it is sent to every
    seat in -Keys.

.PARAMETER Text
    A prompt written out here instead of taken from the specification.

.PARAMETER Spec
    The specification holding the prompts. Defaults to session-shapes.json beside this script.

.PARAMETER ReportTo
    Where to write what happened, as JSON.

.EXAMPLE
    .\prompt-sessions.ps1 -GatewayUrl http://127.0.0.1:7911 -TokenFile ... `
        -SeatsReport .\run-1-seats.json -Keys alpha-long-turn -PromptName longTurn -ReportTo .\run-1-prompts.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GatewayUrl,
    [string]$Token,
    [string]$TokenFile,
    [Parameter(Mandatory = $true)][string]$SeatsReport,
    [string[]]$Keys,
    [string]$PromptName,
    [string]$Text,
    [string]$Spec = (Join-Path $PSScriptRoot "session-shapes.json"),
    [Parameter(Mandatory = $true)][string]$ReportTo,
    [int]$SettleSeconds = 20
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot "gateway-common.ps1")

function Say([string]$text) { Write-Host "[prompt] $text" }

if (-not (Test-Path $SeatsReport)) { throw "no seats report at $SeatsReport - run populate-sessions.ps1 first." }
$seats = (Get-Content $SeatsReport -Raw | ConvertFrom-Json).seats
$gateway = New-GatewaySession -GatewayUrl $GatewayUrl -Token $Token -TokenFile $TokenFile

if (-not $PromptName -and -not $Text) { throw "give -PromptName or -Text." }
if ($PromptName -and $Text) { throw "give -PromptName or -Text, not both." }

$prompt = $Text
if ($PromptName) {
    if (-not (Test-Path $Spec)) { throw "no specification at $Spec." }
    $shapes = Get-Content $Spec -Raw | ConvertFrom-Json
    $prop = $shapes.prompts.PSObject.Properties[$PromptName]
    if (-not $prop) { throw "the specification has no prompt called '$PromptName'." }
    $prompt = $prop.Value
}

$targets = if ($Keys) { @($seats | Where-Object { $Keys -contains $_.key }) } else { @($seats) }
if ($targets.Count -eq 0) { throw "no seat in $SeatsReport matches the keys given." }

$results = @()
foreach ($seat in $targets) {
    Say "sending to $($seat.key) ($($seat.sessionId))"
    $landed = $false
    $detail = $null
    try {
        $answer = Invoke-GatewayApi -Gateway $gateway -Method POST -Path "/sessions/$($seat.sessionId)/prompt" `
            -Body @{ text = $prompt; appendEnter = $true } -TimeoutSec 180
        $landed = $true
        $detail = ($answer | ConvertTo-Json -Depth 4 -Compress)
        Say "  landed"
    } catch {
        $detail = $_.Exception.Message
        Say "  DID NOT LAND: $detail"
    }
    $results += [pscustomobject]@{
        key = $seat.key; sessionId = $seat.sessionId; name = $seat.name
        landed = $landed; detail = $detail
    }
}

if ($SettleSeconds -gt 0) {
    Say "waiting ${SettleSeconds}s so the activity states below describe the sessions AFTER the prompt"
    Start-Sleep -Seconds $SettleSeconds
}

$live = Get-ApiList (Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/sessions") "sessions"

foreach ($r in $results) {
    $s = @($live | Where-Object { $_.sessionId -ieq $r.sessionId })
    $state = if ($s.Count -gt 0) { $s[0].activityState } else { "(not listed)" }
    $r | Add-Member -NotePropertyName activityStateAfter -NotePropertyValue $state
    Say ("  {0,-22} {1}" -f $r.key, $state)
}

$dir = Split-Path -Parent $ReportTo
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
([pscustomobject]@{
    sentAtUtc = (Get-Date).ToUniversalTime().ToString("s") + "Z"
    promptName = $PromptName
    promptLength = $prompt.Length
    results = $results
}) | ConvertTo-Json -Depth 6 | Set-Content -Path $ReportTo -Encoding ascii
Say "report: $ReportTo"
