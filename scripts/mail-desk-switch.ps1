#Requires -Version 5.1
<#
.SYNOPSIS
    Switch Mail Desk on or off for ONE account on a Gateway (the Mail Desk mission, issue #4213, phase 1), and read
    the decision back.

.DESCRIPTION
    Mail Desk is off for every account until an administrator switches one on. This is the one written command for
    doing that. It makes three requests, all on the administrator service token:

      1. GET  <gateway>/gateway/admin/accounts?email=<account>   - the account's identifier on that Gateway
      2. POST <gateway>/gateway/admin/mail-desk                  - { account, enabled, actor, reason }
      3. GET  <gateway>/gateway/admin/mail-desk?account=<id>     - the decision as the Gateway now holds it

    and prints the decision read back in step 3. The actor recorded is the person running the script on this
    machine (<user> on <machine>).

    WHERE IT READS ITS SECRETS. The Gateway address is the cc-secrets setting gateway-admin-url (or -Gateway). The
    token is the cc-secrets entry admin-service-token: the script runs itself again under
    "cc-secrets run admin-service-token", which hands the token to that second run in ADMIN_SERVICE_TOKEN and removes
    it from anything printed. The token is never an argument, never printed, and never written to a file.

    Nothing is changed by -DryRun: it prints the three requests without sending any of them.

    Exit codes: 0 the decision was recorded and read back as asked; 1 the Gateway refused or answered something
    unexpected (its own words are printed); 2 the command was used wrongly or a secret could not be read.

.PARAMETER Account
    The account's email address, as the Gateway recorded it.

.PARAMETER On
    Switch Mail Desk on for the account. Exactly one of -On and -Off.

.PARAMETER Off
    Switch Mail Desk off for the account. Exactly one of -On and -Off.

.PARAMETER Reason
    Why. Required; it is stored with the decision.

.PARAMETER DryRun
    Print the requests without sending them. The token is not printed.

.PARAMETER Gateway
    The Gateway's address. Default: the cc-secrets setting gateway-admin-url.

.PARAMETER TokenSupplied
    Used by the script itself: the token is already in ADMIN_SERVICE_TOKEN because cc-secrets started this run. Also
    how a test against a Gateway started on this machine supplies that Gateway's own test token. Never needed by hand
    against the hosted Gateway.

.EXAMPLE
    .\scripts\mail-desk-switch.ps1 -Account email@devthrottle.com -On -Reason "phase 6: the owner's account only"

.EXAMPLE
    .\scripts\mail-desk-switch.ps1 -Account email@devthrottle.com -Off -Reason "stopped for the night" -DryRun
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Account,
    [switch]$On,
    [switch]$Off,
    [Parameter(Mandatory = $true)][string]$Reason,
    [switch]$DryRun,
    [string]$Gateway = "",
    [switch]$TokenSupplied
)

$ErrorActionPreference = 'Stop'

function Write-Line([string]$text) { Write-Host "[mail-desk-switch] $text" }

function Stop-Usage([string]$text) {
    Write-Host "[mail-desk-switch] $text" -ForegroundColor Red
    exit 2
}

# --- What was asked ---------------------------------------------------------------------------------------------

if ($On -eq $Off) { Stop-Usage "say exactly one of -On or -Off" }
if ([string]::IsNullOrWhiteSpace($Account)) { Stop-Usage "-Account is required: the account's email address" }
if ([string]::IsNullOrWhiteSpace($Reason)) { Stop-Usage "-Reason is required: it is stored with the decision" }
$enabled = [bool]$On
$Account = $Account.Trim()
$actor = "$([Environment]::UserName) on $([Environment]::MachineName)"

if ([string]::IsNullOrWhiteSpace($Gateway)) {
    $Gateway = (& cc-secrets get gateway-admin-url 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Gateway)) {
        Stop-Usage "the cc-secrets setting gateway-admin-url could not be read (exit $LASTEXITCODE); add it, or pass -Gateway"
    }
}
$Gateway = $Gateway.TrimEnd('/')

$lookupUrl = "$Gateway/gateway/admin/accounts?email=$([Uri]::EscapeDataString($Account))"
$switchUrl = "$Gateway/gateway/admin/mail-desk"

function New-Body([string]$accountId) {
    return (@{ account = $accountId; enabled = $enabled; actor = $actor; reason = $Reason.Trim() } | ConvertTo-Json -Compress)
}

if ($DryRun) {
    Write-Line "DRY RUN - nothing is sent. Every request carries 'Authorization: Bearer <admin-service-token>'."
    Write-Line "1. GET  $lookupUrl"
    Write-Line "2. POST $switchUrl"
    Write-Line "        $(New-Body '<account id from request 1>')"
    Write-Line "3. GET  $switchUrl`?account=<account id from request 1>"
    exit 0
}

# --- Run again under cc-secrets, which supplies the token ---------------------------------------------------------

if (-not $TokenSupplied) {
    $shell = (Get-Process -Id $PID).Path
    $arguments = @('-NoProfile', '-File', $PSCommandPath, '-Account', $Account, '-Reason', $Reason, '-Gateway', $Gateway, '-TokenSupplied')
    $arguments += $(if ($On) { '-On' } else { '-Off' })
    & cc-secrets run admin-service-token -- $shell @arguments
    exit $LASTEXITCODE
}

$token = $env:ADMIN_SERVICE_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) {
    Stop-Usage "-TokenSupplied was given but ADMIN_SERVICE_TOKEN is empty; run without it, so cc-secrets supplies the token"
}

# --- The three requests -------------------------------------------------------------------------------------------

Add-Type -AssemblyName System.Net.Http
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds(30)
$client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $token)

function Send([string]$method, [string]$url, [string]$json) {
    $request = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::new($method)), $url
    if ($json) { $request.Content = New-Object System.Net.Http.StringContent $json, ([Text.Encoding]::UTF8), 'application/json' }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Write-Line "$method $url -> HTTP $([int]$response.StatusCode)"
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Text = $text }
}

function Stop-Refused($answer, [string]$what) {
    Write-Host "[mail-desk-switch] $what was refused: HTTP $($answer.Status) $($answer.Text)" -ForegroundColor Red
    exit 1
}

try {
    $lookup = Send 'GET' $lookupUrl $null
    if ($lookup.Status -ne 200) { Stop-Refused $lookup "the account lookup" }
    $accountId = ($lookup.Text | ConvertFrom-Json).account
    if ([string]::IsNullOrWhiteSpace($accountId)) { Stop-Refused $lookup "the account lookup (it named no account)" }
    Write-Line "account $Account is $accountId"

    $set = Send 'POST' $switchUrl (New-Body $accountId)
    if ($set.Status -ne 200) { Stop-Refused $set "the switch" }

    $read = Send 'GET' "$switchUrl`?account=$([Uri]::EscapeDataString($accountId))" $null
    if ($read.Status -ne 200) { Stop-Refused $read "the read-back" }
    $decision = $read.Text | ConvertFrom-Json
    Write-Line "read back: $($read.Text)"
    if ([bool]$decision.enabled -ne $enabled) {
        Write-Host "[mail-desk-switch] the Gateway reads enabled=$($decision.enabled) after being asked for $enabled" -ForegroundColor Red
        exit 1
    }
    Write-Line "Mail Desk is $(if ($enabled) { 'ON' } else { 'OFF' }) for $Account ($accountId), recorded by $($decision.decision.actor): $($decision.decision.reason)"
    exit 0
}
finally {
    $client.Dispose()
}
