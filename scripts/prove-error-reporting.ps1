#Requires -Version 5.1
<#
.SYNOPSIS
    Error Logging mission (issue #3675), step 8: prove that an error on EVERY surface reaches the central
    error store, by causing one and reading it back.

.DESCRIPTION
    For every component in ErrorReportLimits.Components - read from the contract by the driver, never from a
    list kept here - the script triggers a real error through that surface's real reporting path where one can
    be run from this machine, then reads the store back on the administrator token and shows the report
    arriving with its fields.

    HOW EACH COMPONENT IS TRIGGERED (the "triggered how" column says the same):
      director, launcher, gateway-app
                  The real ErrorReporter, started as that component on this machine's own Gateway credential.
                  An error line is logged inside an ErrorContext carrying the proof's correlation id, and the
                  reporter's own last-send flush delivers it. The running app is NOT exercised - only its
                  reporter. The Director's line also plants a credential-shaped value; the stored message must
                  not carry it (the scrubbing check).
      cockpit, mobile
                  The browser shells' report, field for field, POSTed to /client-errors on this machine's
                  credential. The screen that would have shown the error is NOT exercised.
      gateway     A prompt to a session id no Director holds, sent on this session's own key. The Gateway answers
                  404 and stores its OWN row (the phone's red-box path). The Gateway mints the correlation id -
                  by design no client can choose it - so the proof marker rides in the row's session id, and the
                  row is matched on the exact id the answer's X-Correlation-Id header carried.
      tool        cc-devthrottle, run from THIS checkout (the installed copy may predate the reporter), asked for
                  an out-of-range read so the Gateway refuses it with 400. The tool's shared failure hook reports
                  it. CC_SESSION_ID is set to the marker for that one run, so the row's session id carries it; the
                  correlation id is the Gateway's, from its refusal.
      install     The installer's real InstallFailureReporter, from a throwaway install root (so the machine's
                  own install id and its hourly allowance are untouched). An install report has no correlation
                  id; the marker rides in the step.
      website     NOT PROVEN from this machine: the website files its reports server-side with its own service
                  credential, which this machine does not hold.

    A component the script has no trigger for at all (one added to the contract after this script) is a FAILURE,
    not a skip: the proof must be extended before it can pass again.

    EVERY ROW THIS CREATES IS MARKED. The run's marker is errproof-<UTC time>-<random>; each component gets
    <marker>-<component>. It is in correlation_id where the sender chooses that id (director, launcher,
    gateway-app, cockpit, mobile), in session_id where the Gateway owns the correlation id (gateway, tool),
    and in step for install. A row whose correlation_id, session_id or step starts with "errproof-" is a proof
    row, never a real problem.

    THE READ IS PROVED BEFORE IT IS TRUSTED. A read that returns no rows would make every "did not arrive"
    meaningless, so the script first reads the store for any row at all and stops (exit 2) if none comes back.

    Exit codes: 0 every provable component arrived; 1 at least one provable component did not arrive (or the
    script has no trigger for a component); 2 the instrument or the set-up failed.

    Run it INSIDE a DevThrottle session (the gateway and tool triggers use that session's own key). The reads
    run as: cc-secrets run admin-service-token -- <driver> read ... ; the driver writes to a file, never to the
    console, because cc-secrets crashes when a child prints a character outside ASCII (issue #3262).

.PARAMETER Gateway
    The Gateway to prove. Default: the hosted Gateway. This machine must be signed in to it, and this session's
    key must belong to it; a surface whose credential belongs elsewhere is reported NOT PROVEN with that reason.

.PARAMETER WaitSeconds
    How long to keep reading for rows that have not arrived yet. Default 90.

.PARAMETER SkipBuild
    Reuse the driver already built (a re-run while iterating).

.EXAMPLE
    .\scripts\prove-error-reporting.ps1
#>

[CmdletBinding()]
param(
    [string]$Gateway = "https://gateway.devthrottle.com",
    [int]$WaitSeconds = 90,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$Gateway = $Gateway.TrimEnd('/')

$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddHHmmss")
$random = -join ((1..4) | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) })
$runId = "errproof-$stamp-$random"
$work = Join-Path ([System.IO.Path]::GetTempPath()) "prove-error-reporting\$runId"
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Write-Step([string]$text) { Write-Host "[prove-error-reporting] $text" }

function Fail-Setup([string]$text) {
    Write-Host "[prove-error-reporting] SET-UP FAILED: $text" -ForegroundColor Red
    exit 2
}

# --- The driver ---------------------------------------------------------------------------------------------------

$project = Join-Path $repo "scripts\prove-error-reporting\ProveErrorReporting.csproj"
$driverDir = Join-Path $repo "scripts\prove-error-reporting\bin\Release\net10.0"
$driver = Join-Path $driverDir "prove-error-reporting.exe"
if (-not $SkipBuild) {
    Write-Step "building the driver"
    & dotnet build $project -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail-Setup "the driver did not build (dotnet build exit $LASTEXITCODE)" }
}
if (-not (Test-Path $driver)) { Fail-Setup "the driver is not at $driver; run without -SkipBuild" }

# Run one driver verb; its answer is the JSON file it wrote.
function Invoke-Driver([string]$name, [string[]]$arguments, [hashtable]$environment = @{}, [switch]$AsAdmin) {
    $out = Join-Path $work "$name.json"
    $saved = @{}
    foreach ($k in $environment.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k)
        [Environment]::SetEnvironmentVariable($k, $environment[$k])
    }
    try {
        if ($AsAdmin) {
            & cc-secrets run admin-service-token -- $driver @arguments --out $out | Out-Host
        } else {
            & $driver @arguments --out $out | Out-Host
        }
        $code = $LASTEXITCODE
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
    if (-not (Test-Path $out)) { Fail-Setup "the driver verb '$name' wrote no answer (exit $code)" }
    $answer = Get-Content -Raw -Path $out | ConvertFrom-Json
    if ($answer.outcome -eq 'failed') { Fail-Setup "the driver verb '$name' failed: $($answer.error)" }
    return $answer
}

Write-Step "run $runId against $Gateway; working files in $work"

$components = @((Invoke-Driver "components" @("components")).components)
if ($components.Count -eq 0) { Fail-Setup "the contract returned no components - a broken instrument, not an empty proof" }
Write-Step "the contract lists $($components.Count) components: $($components -join ', ')"

# --- Prove the read can return a row, before trusting any "did not arrive" -----------------------------------------

$sanity = Invoke-Driver "sanity" @("sanity", "--target", $Gateway) -AsAdmin
if ($sanity.outcome -ne 'can-read-a-row') {
    Fail-Setup "the administrator read returned no row at all (HTTP $($sanity.http_status), returned $($sanity.returned)); a read that cannot return a row proves nothing"
}
Write-Step "the read works: it returned a $($sanity.first_component) row received $($sanity.first_received_utc) ($($sanity.total_matched) in the last 90 days)"

$since = (Get-Date).ToUniversalTime().AddMinutes(-2).ToString("yyyy-MM-ddTHH:mm:ssZ")

# --- Trigger each component ---------------------------------------------------------------------------------------

# One entry per component: how it was triggered, whether it should be provable from here, and how to find its row.
$plan = [ordered]@{}

function Add-Plan([string]$component, [string]$how, [bool]$provable, [string]$reason, [scriptblock]$match) {
    $plan[$component] = [pscustomobject]@{ How = $how; Provable = $provable; Reason = $reason; Match = $match }
}

$pythonExe = Join-Path $env:LOCALAPPDATA "cc-director\pyenv\Scripts\python.exe"

foreach ($component in $components) {
    $marker = "$runId-$component"
    switch ($component) {
        { $_ -in @('director', 'launcher', 'gateway-app') } {
            $r = Invoke-Driver "trigger-$component" @("device", "--component", $component, "--marker", $marker, "--target", $Gateway, "--logs", (Join-Path $work "logs"))
            if ($r.outcome -eq 'not-proven') { Add-Plan $component "ErrorReporter as $component" $false $r.reason $null; break }
            $m = $marker
            Add-Plan $component $r.triggered_how ($r.outcome -eq 'sent') "reporter sent $($r.sent), dropped $($r.dropped)" { param($row) $row.correlation_id -eq $m }.GetNewClosure()
        }
        { $_ -in @('cockpit', 'mobile') } {
            $r = Invoke-Driver "trigger-$component" @("browser", "--component", $component, "--marker", $marker, "--target", $Gateway)
            if ($r.outcome -eq 'not-proven') { Add-Plan $component "POST /client-errors" $false $r.reason $null; break }
            $m = $marker
            Add-Plan $component $r.triggered_how $true "answered HTTP $($r.http_status)" { param($row) $row.correlation_id -eq $m }.GetNewClosure()
        }
        'gateway' {
            $r = Invoke-Driver "trigger-gateway" @("gateway-refusal", "--marker", $marker, "--target", $Gateway)
            if ($r.outcome -eq 'not-proven') { Add-Plan $component "a refused prompt" $false $r.reason $null; break }
            $cid = $r.correlation_id
            $m = $marker
            Add-Plan $component $r.triggered_how $true "answered HTTP $($r.http_status), correlation $cid" { param($row) $row.correlation_id -eq $cid -and $row.session_id -eq $m }.GetNewClosure()
        }
        'tool' {
            if (-not $env:CC_GATEWAY_SESSION_KEY -or -not $env:CC_GATEWAY_URL) {
                Add-Plan $component "cc-devthrottle from this checkout" $false "needs a DevThrottle session's own key; run the script inside a session" $null; break
            }
            if ($env:CC_GATEWAY_URL.TrimEnd('/') -ne $Gateway) {
                Add-Plan $component "cc-devthrottle from this checkout" $false "this session's key belongs to $($env:CC_GATEWAY_URL), not $Gateway" $null; break
            }
            if (-not (Test-Path $pythonExe)) { Fail-Setup "the cc-* tools' Python is not at $pythonExe" }
            # The tool exactly as it is in this checkout: its package and the shared reporter, copied under the names
            # the installed bundle uses, so the code on trial is the code being merged.
            $importRoot = Join-Path $work "tool-import"
            New-Item -ItemType Directory -Force -Path $importRoot | Out-Null
            Copy-Item -Recurse -Force (Join-Path $repo "tools\cc-devthrottle\src") (Join-Path $importRoot "cc_devthrottle")
            Copy-Item -Recurse -Force (Join-Path $repo "tools\cc_shared") (Join-Path $importRoot "cc_shared")
            $toolOut = Join-Path $work "trigger-tool.txt"
            $savedPath = $env:PYTHONPATH; $savedSession = $env:CC_SESSION_ID
            # Windows PowerShell turns a native command's error output into a terminating error under Stop; the
            # tool's refusal on its error stream is the expected outcome here, and its exit code is what is checked.
            $savedPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $env:PYTHONPATH = $importRoot
                $env:CC_SESSION_ID = $marker
                # Too old a moment: the Gateway refuses the read with 400, and the tool exits 1 - a failure, not a
                # usage mistake (exit 2 is never reported). Its output goes to a file: it may print non-ASCII.
                & $pythonExe -c "from cc_devthrottle.cli import tool_main; tool_main()" errors list --since 2000-01-01T00:00:00Z *> $toolOut
                $toolExit = $LASTEXITCODE
            } finally {
                $env:PYTHONPATH = $savedPath; $env:CC_SESSION_ID = $savedSession
                $ErrorActionPreference = $savedPreference
            }
            $m = $marker
            Add-Plan $component "cc-devthrottle errors list with a refused moment, from this checkout, CC_SESSION_ID = marker" ($toolExit -eq 1) "the tool exited $toolExit" { param($row) $row.session_id -eq $m }.GetNewClosure()
        }
        'install' {
            $r = Invoke-Driver "trigger-install" @("install", "--marker", $marker, "--target", $Gateway, "--root", (Join-Path $work "install-root"))
            $installId = $r.install_id
            $m = $marker
            Add-Plan $component $r.triggered_how ($r.outcome -eq 'sent') "install id $installId" { param($row) $row.step -eq $m -and $row.device -eq $installId }.GetNewClosure()
        }
        'website' {
            Add-Plan $component "none" $false "the website files its reports server-side with its own service credential, which this machine does not hold" $null
        }
        default {
            # A component added to the contract after this script was written. Never a skip: the proof is incomplete.
            Add-Plan $component "none" $true "this script has NO TRIGGER for '$component'; extend scripts\prove-error-reporting.ps1" $null
        }
    }
}

# --- Read the store back ------------------------------------------------------------------------------------------

$expected = @($plan.Keys | Where-Object { $plan[$_].Provable -and $plan[$_].Match })
$found = @{}
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$attempt = 0
do {
    $attempt++
    $read = Invoke-Driver "read-$attempt" @("read", "--target", $Gateway, "--since", $since, "--components", ($components -join ',')) -AsAdmin
    foreach ($entry in $read.reads) {
        $component = $entry.component
        if (-not $plan.Contains($component) -or -not $plan[$component].Match) { continue }
        if ($found.ContainsKey($component)) { continue }
        $hit = @($entry.errors | Where-Object { & $plan[$component].Match $_ }) | Select-Object -First 1
        if ($hit) { $found[$component] = $hit }
    }
    $missing = @($expected | Where-Object { -not $found.ContainsKey($_) })
    if ($missing.Count -eq 0) { break }
    Write-Step "read $attempt : waiting for $($missing -join ', ')"
    Start-Sleep -Seconds 10
} while ((Get-Date) -lt $deadline)

# --- The table ----------------------------------------------------------------------------------------------------

$fieldNames = @('user_visible', 'surface', 'action', 'correlation_id', 'http_status', 'error_code', 'session_id', 'fingerprint')
$rows = foreach ($component in $plan.Keys) {
    $p = $plan[$component]
    $row = $found[$component]
    if (-not $p.Provable) {
        $arrived = 'NOT PROVEN'
        $fields = $p.Reason
    } elseif (-not $p.Match) {
        $arrived = 'FAIL'
        $fields = $p.Reason
    } elseif ($row) {
        $arrived = 'yes'
        $seen = @($fieldNames | Where-Object { $null -ne $row.$_ -and "$($row.$_)" -ne '' })
        $fields = $seen -join ' '
        if ($component -eq 'install') { $fields = "step device $fields".Trim() }
    } else {
        $arrived = 'NO'
        $fields = $p.Reason
    }
    [pscustomobject]@{ Component = $component; TriggeredHow = $p.How; Arrived = $arrived; FieldsSeen = $fields }
}

# The scrubbing check: the Director's proof line planted a credential-shaped value.
$scrub = 'not checked (the director row did not arrive)'
if ($found.ContainsKey('director')) {
    $message = "$($found['director'].message)"
    $scrub = if ($message -match 'PROOFplanted0secret0value') { 'FAIL: the planted credential reached the store' } else { 'ok: the planted credential was scrubbed' }
}

Write-Host ""
Write-Host "Error reporting proof - run $runId - $Gateway"
$rows | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Host
Write-Host "Scrubbing: $scrub"
foreach ($component in $found.Keys) {
    $r = $found[$component]
    Write-Host ("  {0,-12} correlation_id={1} session_id={2} fingerprint={3}" -f $component, $r.correlation_id, $r.session_id, $r.fingerprint)
}

$summary = [pscustomobject]@{ run = $runId; gateway = $Gateway; rows = $rows; scrubbing = $scrub }
$summary | ConvertTo-Json -Depth 5 | Set-Content -Encoding ascii -Path (Join-Path $work "summary.json")
Write-Host "Summary: $(Join-Path $work 'summary.json')"

$failed = @($rows | Where-Object { $_.Arrived -in @('NO', 'FAIL') })
if ($scrub -like 'FAIL*') { $failed += 'scrubbing' }
if ($failed.Count -gt 0) {
    Write-Host "RESULT: FAIL - $($failed.Count) provable check(s) did not pass" -ForegroundColor Red
    exit 1
}
$notProven = @($rows | Where-Object { $_.Arrived -eq 'NOT PROVEN' }).Count
Write-Host "RESULT: PASS - every provable component arrived ($notProven not provable from this machine, each with its reason)" -ForegroundColor Green
exit 0
