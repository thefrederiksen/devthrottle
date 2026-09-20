#Requires -Version 5.1
<#
.SYNOPSIS
    Put the session shapes a smart shutdown must be proved against onto a Director, and report what
    it made.

.DESCRIPTION
    Mission document "Smart Director Restart", section 3: at least six sessions across at least two
    missions, one a lead with a session under it, one mid-turn on a long turn, one idle and never
    answering - plus the two shapes section 7 adds, a session with a question box open and a session
    too wedged to take a prompt. The shapes themselves are DATA (session-shapes.json), so a different
    run can prove a different set without editing a script.

    IT KNOWS NO RIG. A Gateway address, a credential, a machine name and a Director id are all it
    takes, so the same script drives the isolated quality assurance rig today and a real Director on
    another machine tomorrow. Nothing here reads a storage root, a port or a scheduled task.

    Each seat gets its own empty working folder under -WorkRoot. Nothing outside -WorkRoot is
    written, and the folders are left in place afterwards: a handover names the folder it was
    written about, so deleting them would destroy half the evidence.

    WHAT IT DOES NOT DO. It does not start the work. A long turn started here would be over by the
    time a shutdown began, so the prompts live in the same specification file and are sent by
    prompt-sessions.ps1 at the moment the run needs them.

.PARAMETER GatewayUrl
    The Gateway the Director is connected to, e.g. http://127.0.0.1:7911.

.PARAMETER Token
    The bearer token. Give this or -TokenFile.

.PARAMETER TokenFile
    A file holding the bearer token, which is how a self-hosted Gateway keeps it.

.PARAMETER DirectorId
    The Director to create the sessions on.

.PARAMETER Machine
    The machine that Director runs on. Recorded in the report so a report read later names the
    machine it was taken on.

.PARAMETER Spec
    The shapes to create. Defaults to session-shapes.json beside this script.

.PARAMETER WorkRoot
    Where each seat's working folder is made.

.PARAMETER ReportTo
    Where to write the report of what was made, as JSON. Every later step reads this rather than
    being told session ids by hand.

.PARAMETER Only
    Create only the seats whose keys are given. The default creates every seat in the specification.

.EXAMPLE
    .\populate-sessions.ps1 -GatewayUrl http://127.0.0.1:7911 `
        -TokenFile $env:LOCALAPPDATA\...\gateway-token.txt `
        -DirectorId 3be6c633-c4c1-41a7-8f45-ff49e4d2e8fe -Machine SOREN_NORTH `
        -WorkRoot $env:TEMP\restart-qa-seats -ReportTo .\run-1-seats.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GatewayUrl,
    [string]$Token,
    [string]$TokenFile,
    [Parameter(Mandatory = $true)][string]$DirectorId,
    [Parameter(Mandatory = $true)][string]$Machine,
    [string]$Spec = (Join-Path $PSScriptRoot "session-shapes.json"),
    [Parameter(Mandatory = $true)][string]$WorkRoot,
    [Parameter(Mandatory = $true)][string]$ReportTo,
    [string[]]$Only
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot "gateway-common.ps1")

function Say([string]$text) { Write-Host "[populate] $text" }

if (-not (Test-Path $Spec)) { throw "no specification at $Spec." }
$shapes = Get-Content $Spec -Raw | ConvertFrom-Json
$gateway = New-GatewaySession -GatewayUrl $GatewayUrl -Token $Token -TokenFile $TokenFile

# The Director must be there before anything is asked of it: a create sent to a Director that is not
# connected comes back as a relay failure, which reads like a bad request.
$items = Get-ApiList (Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/directors") "directors"
$mine = @($items | Where-Object { $_.directorId -ieq $DirectorId })
if ($mine.Count -eq 0) { throw "the Gateway at $GatewayUrl does not list Director $DirectorId." }
Say "Director $DirectorId is listed by the Gateway (name: $($mine[0].name))"

New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null

# ---------------------------------------------------------------------------
# The missions. A mission is the Gateway's, so a session can only be attached to one that exists.
# An existing mission of the same name is REUSED rather than duplicated, so this script can be run
# twice against one Gateway without leaving two missions with one name.
$existingList = Get-ApiList (Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/missions") "missions"


$missionIds = @{}
foreach ($m in $shapes.missions) {
    $match = @($existingList | Where-Object { $_.missionName -ieq $m.name })
    if ($match.Count -gt 0) {
        $missionIds[$m.key] = $match[0].missionId
        Say "mission '$($m.name)' already exists: $($match[0].missionId)"
    } else {
        $made = Invoke-GatewayApi -Gateway $gateway -Method POST -Path "/missions" -Body @{ missionName = $m.name }
        $missionIds[$m.key] = $made.missionId
        Say "mission '$($m.name)' created: $($made.missionId)"
    }
}

# ---------------------------------------------------------------------------
# The seats, in the order the specification lists them, because a seat naming a controller needs that
# controller to exist already.
$made = @()
foreach ($seat in $shapes.seats) {
    if ($Only -and ($Only -notcontains $seat.key)) { Say "skipped $($seat.key) (not in -Only)"; continue }

    $folder = Join-Path $WorkRoot $seat.key
    New-Item -ItemType Directory -Force -Path $folder | Out-Null

    # A seat may bring files with it - a shape that needs a program to stand in for an agent cannot be
    # written as a command line without quoting it three times over. The file is DATA in the
    # specification, so the shape stays readable and this script stays general.
    if ($seat.PSObject.Properties['seedFiles']) {
        foreach ($f in $seat.seedFiles.PSObject.Properties) {
            $target = Join-Path $folder $f.Name
            $text = if ($f.Value -is [array]) { $f.Value -join "`r`n" } else { [string]$f.Value }
            Set-Content -Path $target -Value $text -Encoding ascii
            Say "  seeded $target ($($text.Length) characters)"
        }
    }

    # {folder} in a command line is this seat's working folder. It is the only substitution there is.
    function Expand-Folder([string]$value) { return $value.Replace("{folder}", $folder) }

    $body = @{
        repoPath      = $folder
        name          = $seat.name
        agent         = $seat.agent
        origin        = "agent"
        originSurface = "api"
        missionId     = $missionIds[$seat.mission]
    }
    if ($seat.PSObject.Properties['role'] -and $seat.role) { $body.role = $seat.role }
    if ($seat.PSObject.Properties['command'] -and $seat.command) { $body.command = Expand-Folder $seat.command }
    if ($seat.PSObject.Properties['commandArgs'] -and $seat.commandArgs) { $body.commandArgs = Expand-Folder $seat.commandArgs }
    if ($seat.PSObject.Properties['args'] -and $seat.args) { $body.args = Expand-Folder $seat.args }
    if ($seat.PSObject.Properties['bypassPermissions']) { $body.bypassPermissions = [bool]$seat.bypassPermissions }
    if ($seat.PSObject.Properties['controller'] -and $seat.controller) {
        $owner = @($made | Where-Object { $_.key -eq $seat.controller })
        if ($owner.Count -eq 0) { throw "seat '$($seat.key)' reports to '$($seat.controller)', which has not been created - order the specification so a controller comes first." }
        $body.controllerSessionId = $owner[0].sessionId
        $body.parentSessionId     = $owner[0].sessionId
    }

    Say "creating $($seat.key): agent=$($seat.agent) folder=$folder"
    $dto = Invoke-GatewayApi -Gateway $gateway -Method POST -Path "/directors/$DirectorId/sessions" -Body $body -TimeoutSec 180
    if (-not $dto.sessionId) { throw "the create of '$($seat.key)' came back with no session id: $($dto | ConvertTo-Json -Depth 5)" }

    $made += [pscustomobject]@{
        key        = $seat.key
        sessionId  = $dto.sessionId
        name       = $seat.name
        agent      = $seat.agent
        mission    = $seat.mission
        missionId  = $missionIds[$seat.mission]
        controller = $(if ($seat.PSObject.Properties['controller']) { $seat.controller } else { $null })
        folder     = $folder
        why        = $seat.why
    }
    Say "  created $($dto.sessionId)"
}

# ---------------------------------------------------------------------------
# What the Director really holds afterwards, read back from the Gateway rather than assumed from the
# creates: a create that answered and a session that is present are two different facts.
$live = Get-ApiList (Invoke-GatewayApi -Gateway $gateway -Method GET -Path "/sessions") "sessions"
$onDirector = @($live | Where-Object { $_.directorId -ieq $DirectorId })

$report = [pscustomobject]@{
    takenAtUtc     = (Get-Date).ToUniversalTime().ToString("s") + "Z"
    gatewayUrl     = $gateway.Url
    machine        = $Machine
    directorId     = $DirectorId
    specification  = (Resolve-Path $Spec).Path
    workRoot       = (Resolve-Path $WorkRoot).Path
    seats          = $made
    liveOnDirector = @($onDirector | ForEach-Object {
        [pscustomobject]@{ sessionId = $_.sessionId; name = $_.name; agent = $_.agent; activityState = $_.activityState }
    })
}

$dir = Split-Path -Parent $ReportTo
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $ReportTo -Encoding ascii

Say ""
Say "MADE $($made.Count) seat(s) across $($shapes.missions.Count) mission(s)"
foreach ($s in $made) { Say ("  {0,-22} {1}  {2}" -f $s.key, $s.sessionId, $s.name) }
Say "the Gateway lists $($onDirector.Count) session(s) on Director $DirectorId"
Say "report: $ReportTo"
