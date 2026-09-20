#Requires -Version 5.1
<#
.SYNOPSIS
    Ask a machine's launcher, through its Gateway, to start the Director - and wait until it is there.

.DESCRIPTION
    A smart shutdown started from the window's close button leaves the Director CLOSED, which is what
    mission ruling 10.4 says it must do. The next case then needs it back. This is the product's own
    door for that - the same route the Cockpit and the phone use - so getting the Director back is
    not a thing the harness does behind the product's back.

    It knows no rig: a Gateway address, a credential and a machine name.

    The wait is on an ARTIFACT, not on a feeling: the Gateway listing a Director for that machine.

.PARAMETER Machine
    The machine whose launcher is asked.

.PARAMETER WaitSeconds
    How long to wait for the Gateway to list a Director on that machine.

.EXAMPLE
    .\start-director.ps1 -GatewayUrl http://127.0.0.1:7911 -TokenFile ... -Machine SOREN_NORTH
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GatewayUrl,
    [string]$Token,
    [string]$TokenFile,
    [Parameter(Mandatory = $true)][string]$Machine,
    [int]$WaitSeconds = 180
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot "gateway-common.ps1")

function Say([string]$text) { Write-Host "[start-director] $text" }
$gateway = New-GatewaySession -GatewayUrl $GatewayUrl -Token $Token -TokenFile $TokenFile

# What is waited for is a NEWER PROCESS, not a bigger list. A restarted Director keeps its identifier
# and its row, so counting rows waits for ever: the first attempt at this sat through three minutes
# while the Director it was waiting for had been up the whole time. The row's own process id and start
# time are what change.
$before = @(Get-ApiList (Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/directors") "directors" |
            Where-Object { $_.machineName -ieq $Machine })
$beforePids = @($before | ForEach-Object { $_.pid })
Say "the Gateway lists $($before.Count) Director(s) on ${Machine} before the ask (process ids: $($beforePids -join ', '))"

$answer = Invoke-GatewayApi -Gateway $gateway -Method POST -Path "/machines/$Machine/director/start" -Body @{ } -TimeoutSec 60
Say "director/start -> $($answer | ConvertTo-Json -Depth 4 -Compress)"

$ok = Wait-ForCondition -What "the Gateway to list a Director on $Machine in a process it did not list before" -Seconds $WaitSeconds -Test {
    $now = @(Get-ApiList (Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/directors") "directors" |
             Where-Object { $_.machineName -ieq $Machine })
    return (@($now | Where-Object { $beforePids -notcontains $_.pid }).Count -gt 0)
}

$after = @(Get-ApiList (Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/directors") "directors" |
           Where-Object { $_.machineName -ieq $Machine })
foreach ($d in $after) { Say "Director on $($d.machineName): id $($d.directorId) process $($d.pid) started $($d.startedAt) version $($d.version)" }
if (-not $ok) { Say "the Director did not appear within ${WaitSeconds}s. Nothing was forced; say so where this is used." }
