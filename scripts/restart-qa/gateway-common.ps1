#Requires -Version 5.1
<#
.SYNOPSIS
    The few things every script in this harness needs: how to reach a Gateway, and how to wait.

.DESCRIPTION
    Dot-source this from a harness script. It knows NOTHING about any particular Gateway, Director,
    port or storage root - every caller supplies those. That is what lets the same harness drive the
    isolated quality assurance rig today and a real Director on another machine tomorrow.

    A failure here is reported and thrown. Nothing in this harness has a second path that hides a
    first one: if the Gateway does not answer, that IS the result, and the caller says so.
#>

function New-GatewaySession {
    <#
    .SYNOPSIS
        One Gateway to talk to: its address and the credential to present.
    .PARAMETER GatewayUrl
        The Gateway's base address, e.g. http://127.0.0.1:7911 or https://gateway.example.com.
    .PARAMETER Token
        The bearer token. Give this or -TokenFile, never neither.
    .PARAMETER TokenFile
        A file holding the bearer token, which is how a self-hosted Gateway stores it.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$GatewayUrl,
        [string]$Token,
        [string]$TokenFile
    )

    if (-not $Token -and -not $TokenFile) {
        throw "a Gateway needs a credential: pass -Token or -TokenFile."
    }
    if (-not $Token) {
        if (-not (Test-Path $TokenFile)) { throw "no token file at $TokenFile." }
        $Token = (Get-Content $TokenFile -Raw).Trim()
    }
    if (-not $Token) { throw "the token is empty." }

    return [pscustomobject]@{
        Url   = $GatewayUrl.TrimEnd('/')
        Token = $Token
    }
}

function Invoke-GatewayApi {
    <#
    .SYNOPSIS
        One call to a Gateway. Returns the parsed body, or throws with what the Gateway said.
    .PARAMETER Gateway
        From New-GatewaySession.
    .PARAMETER Method
        GET, POST, PUT, DELETE.
    .PARAMETER Path
        The route, starting with a slash.
    .PARAMETER Body
        An object, sent as JSON. Omit for a call with no body.
    .PARAMETER TimeoutSec
        How long to wait for the answer.
    #>
    param(
        [Parameter(Mandatory = $true)]$Gateway,
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        $Body,
        [int]$TimeoutSec = 60
    )

    $args = @{
        Method          = $Method
        Uri             = ($Gateway.Url + $Path)
        Headers         = @{ Authorization = "Bearer $($Gateway.Token)" }
        UseBasicParsing = $true
        TimeoutSec      = $TimeoutSec
    }
    if ($null -ne $Body) {
        $args.Body        = ($Body | ConvertTo-Json -Depth 12)
        $args.ContentType = 'application/json'
    }

    try {
        $resp = Invoke-WebRequest @args
    } catch {
        $detail = $_.Exception.Message
        $r = $_.Exception.Response
        if ($r) {
            try {
                $reader = New-Object System.IO.StreamReader($r.GetResponseStream())
                $text = $reader.ReadToEnd()
                $reader.Dispose()
                if ($text) { $detail = "$detail - $text" }
            } catch { }
        }
        throw "$Method $Path failed: $detail"
    }

    if (-not $resp.Content) { return $null }
    try { return ($resp.Content | ConvertFrom-Json) } catch { return $resp.Content }
}

function Get-ApiList {
    <#
    .SYNOPSIS
        A Gateway answer as an ARRAY, whatever PowerShell did to it on the way here.
    .DESCRIPTION
        PowerShell unrolls a collection returned from a function, so a route that answered with a
        list of ONE arrives as a single object with no Count and no indexer. Every caller that then
        asked "is this an array" got false and read the list as empty - which is a list of zero and
        a list of one being indistinguishable, in a harness whose whole job is counting sessions.
        Everything that reads a list goes through here so that cannot happen once.
    .PARAMETER Result
        What Invoke-GatewayApi gave back.
    .PARAMETER Property
        The property holding the items when the route wraps them in an object.
    #>
    param($Result, [string]$Property)

    if ($null -eq $Result) { return ,@() }
    if ($Result -is [System.Array]) { return ,$Result }
    if ($Property -and $Result.PSObject.Properties[$Property]) { return ,@($Result.$Property) }
    return ,@($Result)
}

function Wait-ForCondition {
    <#
    .SYNOPSIS
        Wait in the foreground until a condition is true. Returns true, or false when time ran out.
    .PARAMETER What
        What is being waited for, for the log line.
    .PARAMETER Test
        A script block returning true when the wait is over.
    .PARAMETER Seconds
        How long to wait in all.
    .PARAMETER PollMs
        How often to look.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$What,
        [Parameter(Mandatory = $true)][scriptblock]$Test,
        [int]$Seconds = 60,
        [int]$PollMs = 750
    )

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Test) { return $true }
        Start-Sleep -Milliseconds $PollMs
    }
    Write-Host "[harness] TIMEOUT after ${Seconds}s waiting for: $What"
    return $false
}
