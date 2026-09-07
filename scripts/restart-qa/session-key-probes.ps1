#Requires -Version 5.1
<#
.SYNOPSIS
    The negative cases that need a SESSION KEY, run from inside a session (issue 2719, Phase 7).

.DESCRIPTION
    A session key is minted per session and stamped into that one session's environment; the
    Director never logs it and nothing outside the session can read it. So the only honest way to
    show what a session key can and cannot do is to BE a session: a RawCli seat runs this script, it
    inherits CC_GATEWAY_URL, CC_GATEWAY_SESSION_KEY and CC_SESSION_ID exactly as an agent would, and
    it writes what the Gateway answered to -Out. The key itself is never written anywhere.

    The cases, each recorded as expected / observed / verdict:

      direct-restart-refused   POST /machines/<made-up machine>/director/restart with the session key
                               must answer 403 session_key_out_of_scope. The machine name is made up
                               on purpose: even on a Gateway with no guard at all, nothing could be
                               restarted, so this probe is safe to run against production.
      control-reaches-route    POST /machines/<the same made-up machine>/sessions with the same key
                               must answer 400 "repoPath is required" - the route is reached and the
                               guard is what refused above, not machine resolution. Without this
                               control the 403 could be anything.
      request-route            (Phase 6) POST the restart REQUEST route with the session key. Until
                               Phase 6 lands the route is unknown; the case records
                               "route not yet defined" and is NOT a pass.

.PARAMETER Out
    The JSON file the results are written to. Written last, in one go, so a half-written file is
    never read as a result.

.PARAMETER Machine
    The made-up machine name. Defaults to one that cannot exist.

.PARAMETER RequestRoute
    (Phase 6) The path of the restart request route, once it exists, for example
    "/machines/<machine>/director/restart-request". Empty means not yet defined.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Out,
    [string]$Machine = "no-such-machine-phase7-probe",
    [string]$RequestRoute = ""
)

$ErrorActionPreference = 'Stop'
$gateway = $env:CC_GATEWAY_URL
$key     = $env:CC_GATEWAY_SESSION_KEY
$self    = $env:CC_SESSION_ID
if (-not $gateway) { throw "CC_GATEWAY_URL is not set - this must run inside a session" }
if (-not $key)     { throw "CC_GATEWAY_SESSION_KEY is not set - this must run inside a session" }

function Probe([string]$name, [string]$method, [string]$path, [string]$body, [int]$expectStatus, [string]$expectText) {
    $url = $gateway.TrimEnd('/') + $path
    $status = 0; $text = ""
    try {
        $r = Invoke-WebRequest -Method $method -Uri $url -Headers @{ Authorization = "Bearer $key" } `
            -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 30
        $status = [int]$r.StatusCode; $text = [string]$r.Content
    } catch {
        $resp = $_.Exception.Response
        if ($resp) {
            $status = [int]$resp.StatusCode
            $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
            $text = $reader.ReadToEnd()
        } else {
            $status = -1; $text = "no response: " + $_.Exception.Message
        }
    }
    $verdict = if ($status -eq $expectStatus -and ($expectText -eq "" -or $text.Contains($expectText))) { "PASS" } else { "FAIL" }
    return [ordered]@{
        case = $name; method = $method; path = $path; body = $body
        expectedStatus = $expectStatus; expectedText = $expectText
        observedStatus = $status; observedBody = $text
        verdict = $verdict
    }
}

$results = @()
$results += Probe "direct-restart-refused" "POST" "/machines/$Machine/director/restart" '{"confirmProtected":true}' 403 "session_key_out_of_scope"
$results += Probe "control-reaches-route" "POST" "/machines/$Machine/sessions" '{}' 400 "repoPath"
if ($RequestRoute) {
    $results += Probe "request-route" "POST" $RequestRoute '{"reason":"phase 7 probe"}' 0 ""
} else {
    $results += [ordered]@{ case = "request-route"; verdict = "NOT RUN"; observedBody = "route not yet defined - Phase 6 has not landed; this is not a pass" }
}

$doc = [ordered]@{
    ranAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    gateway = $gateway
    probeSessionId = $self
    keyPresent = $true
    machineUsed = $Machine
    results = $results
}
$json = $doc | ConvertTo-Json -Depth 6
$tmp = "$Out.tmp"
[System.IO.File]::WriteAllText($tmp, $json, [System.Text.Encoding]::ASCII)
Move-Item -Force $tmp $Out
Write-Host "session-key probes written to $Out"
foreach ($r in $results) { Write-Host ("  {0,-24} {1}  (status {2})" -f $r.case, $r.verdict, $r.observedStatus) }
