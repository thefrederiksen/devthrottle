# Live proof of the skill-folder rule (devthrottle_internal#2311, finding F7): two Directors on one computer,
# one personal and one team, sharing ONE user profile's ~/.agents/skills and ~/.claude/skills.
#
# Each Director is a real storage home (its own CC_DIRECTOR_ROOT, config.json and team file) driven by
# SkillsRig, which runs the real SkillStoreRefresh and the real SkillDirectoryInstaller.InstallFor(ClaudeCode)
# from that home. The user profile is a folder under this worktree, never the owner's.
#
# The stub Gateways listen on localhost:7811 and localhost:7812, each only while its Director run lasts;
# the script refuses to start if either port is already in use.
#
# Run from the worktree root:  powershell -NoProfile -File docs\proof\teams-2311-skills\rig\run-live-proof.ps1
# It starts nothing that outlives it: each Director run is one foreground process that exits.
$ErrorActionPreference = 'Stop'

$worktree = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$rig = Join-Path $worktree '.skills-rig'
$rigDll = Join-Path $PSScriptRoot 'SkillsRig\bin\Debug\net10.0\SkillsRig.dll'
if (-not (Test-Path $rigDll)) { throw "Build the rig first: dotnet build $PSScriptRoot\SkillsRig\SkillsRig.csproj" }

# ---- This process only: drop every CC_* variable and point the profile into the rig. Checked again by the
# child before it does anything, and written as the first lines of each Director's log.
foreach ($name in @(Get-ChildItem env: | Where-Object { $_.Name -like 'CC_*' } | ForEach-Object Name)) {
    Remove-Item "env:$name"
}
$home_ = Join-Path $rig 'home'
$env:USERPROFILE = $home_
$env:HOME = $home_
$env:LOCALAPPDATA = Join-Path $home_ 'AppData\Local'
$env:APPDATA = Join-Path $home_ 'AppData\Roaming'
$env:RIG_ROOT = $rig

$shared = Join-Path $home_ '.agents\skills'
$claude = Join-Path $home_ '.claude\skills'
$directors = @{
    # Two stub Gateways, each served on its own local port by the Director run that uses it.
    P = @{ Root = (Join-Path $rig 'director-personal'); Url = 'http://localhost:7811'; Key = 'personal-key'; Team = $null }
    T = @{ Root = (Join-Path $rig 'director-team');     Url = 'http://localhost:7812'; Key = 'team-key';     Team = 'team-a' }
}

function Write-Library($personal, $team) {
    # Each stub Gateway names its library on the register, as the real one does: its own Gateway id, the tenant,
    # and the team when the tenant is one.
    $lib = @{
        'personal-key' = @{ skills = $personal; gatewayId = 'rig-gateway-7811'; tenantId = 'tenant-person'; teamId = $null }
        'team-key'     = @{ skills = $team;     gatewayId = 'rig-gateway-7812'; tenantId = 'team-a';        teamId = 'team-a' }
    }
    $lib | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $rig 'gateways.json')
}

function New-Rig {
    if (Test-Path $rig) { Remove-Rig }
    New-Item -ItemType Directory -Force $shared, $claude, $env:LOCALAPPDATA, $env:APPDATA | Out-Null

    foreach ($d in $directors.Values) {
        $cfg = Join-Path $d.Root 'config'
        New-Item -ItemType Directory -Force (Join-Path $cfg 'director') | Out-Null
        @{ gateway = @{ url = $d.Url; token = $d.Key } } | ConvertTo-Json |
            Set-Content -Encoding utf8 (Join-Path $cfg 'config.json')
        if ($d.Team) {
            @{ teamId = $d.Team; teamName = 'Team A' } | ConvertTo-Json |
                Set-Content -Encoding utf8 (Join-Path $cfg 'director\gateway-team.json')
        }
    }

    # What a person's folders hold today: a skill written by hand (no marker) in both folders, and a skill the
    # CURRENT release installed (the old three-line marker, a junction into the shared copy).
    foreach ($root in $shared, $claude) {
        New-Item -ItemType Directory -Force (Join-Path $root 'my-own-skill') | Out-Null
        Set-Content -Encoding utf8 (Join-Path $root 'my-own-skill\SKILL.md') "# my-own-skill`n`nwritten by hand"
    }
    $old = Join-Path $shared 'old-personal'
    New-Item -ItemType Directory -Force $old | Out-Null
    Set-Content -Encoding utf8 (Join-Path $old 'SKILL.md') "# old-personal`n`ninstalled by the current release"
    [IO.File]::WriteAllText((Join-Path $old '.devthrottle-skill'), "old-personal`n2`nold-hash`n")
    cmd /c mklink /J "$(Join-Path $claude 'old-personal')" "$old" | Out-Null
}

function Remove-Rig {
    # Links first, as links, so nothing is deleted through one.
    Get-ChildItem $rig -Recurse -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
        ForEach-Object { [IO.Directory]::Delete($_.FullName, $false) }
    Remove-Item $rig -Recurse -Force
}

function Show-Folders($title) {
    "---- $title"
    foreach ($root in $shared, $claude) {
        "  $($root.Substring($rig.Length + 1))"
        foreach ($entry in Get-ChildItem $root -Directory -Force | Sort-Object Name) {
            $kind = if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { 'link' } else { 'folder' }
            $marker = Join-Path $entry.FullName '.devthrottle-skill'
            $owner = if (-not (Test-Path $marker)) { 'no marker' }
                     else {
                         $lines = Get-Content $marker
                         $acct = ($lines | Where-Object { $_ -like 'account=*' })
                         $gw = ($lines | Where-Object { $_ -like 'gateway-id=*' })
                         if ($acct -and $gw) { "$acct $($gw.Substring(11))" } else { 'old marker, no source' }
                     }
            $skillMd = Join-Path $entry.FullName 'SKILL.md'
            $body = if (Test-Path $skillMd) { (Get-Content $skillMd | Where-Object { $_.Trim() } | Select-Object -Last 1) } else { '(unreadable)' }
            "    {0,-14} {1,-6} {2,-45} {3}" -f $entry.Name, $kind, $owner, $body
        }
    }
}

function Invoke-Director($which, $label) {
    $d = $directors[$which]
    $env:CC_DIRECTOR_ROOT = $d.Root
    "==== $label"
    & dotnet $rigDll $label
    if ($LASTEXITCODE -ne 0) { throw "$label exited $LASTEXITCODE" }
    Remove-Item env:CC_DIRECTOR_ROOT
    $log = Get-ChildItem (Join-Path $d.Root 'logs\director') -Filter *.log | Sort-Object LastWriteTime | Select-Object -Last 1
    "  first log lines of $($log.FullName.Substring($rig.Length + 1)):"
    Get-Content $log.FullName -TotalCount 12 | ForEach-Object { "    $_" }
    Show-Folders "after $label"
}

$personalSkills = @{ 'dev-throttle' = 'from the personal library'; 'fleet-comms' = 'from the personal library';
                     'browsers' = 'from the personal library'; 'demo-mode' = 'from the personal library' }
$teamSkills     = @{ 'dev-throttle' = 'from the team library'; 'fleet-comms' = 'from the team library';
                     'team-runbook' = 'from the team library'; 'team-style' = 'from the team library' }

foreach ($port in 7811, 7812) {
    if (Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue) { throw "Port $port is in use - refusing to start." }
}
"RIG $rig  ($(Get-Date -Format o))"
"ports 7811 and 7812: nothing listening (checked with Get-NetTCPConnection)"
"USERPROFILE=$env:USERPROFILE HOME=$env:HOME LOCALAPPDATA=$env:LOCALAPPDATA APPDATA=$env:APPDATA"
"CC_* in this process: $(@(Get-ChildItem env: | Where-Object { $_.Name -like 'CC_*' }).Count)"

"`n######## Scenario 1: the personal Director starts first"
New-Rig
Write-Library $personalSkills $teamSkills
Show-Folders 'before either Director'
Invoke-Director P '1.1 personal Director'
Invoke-Director T '1.2 team Director'
Invoke-Director P '1.3 personal Director again'
Invoke-Director T '1.4 team Director again'
$team2 = $teamSkills.Clone(); $team2.Remove('team-style')
Write-Library $personalSkills $team2
Invoke-Director T '1.5 team Director, after its Gateway withdrew team-style'
$personal2 = $personalSkills.Clone(); $personal2.Remove('demo-mode')
Write-Library $personal2 $team2
Invoke-Director P '1.6 personal Director, after its Gateway withdrew demo-mode'
# The partial enrollment (review finding SK-F2): the team key and Gateway address are saved, the team file is not.
$teamFile = Join-Path $directors.T.Root 'config\director\gateway-team.json'
Move-Item $teamFile "$teamFile.aside"
Invoke-Director T '1.7 team Director with its team file missing - must change nothing'
Move-Item "$teamFile.aside" $teamFile

"`n######## Scenario 2: the team Director starts first"
New-Rig
Write-Library $personalSkills $teamSkills
Show-Folders 'before either Director'
Invoke-Director T '2.1 team Director'
Invoke-Director P '2.2 personal Director'
Invoke-Director T '2.3 team Director again'
Invoke-Director P '2.4 personal Director again'

Remove-Rig
"`nRig removed. Every process this script started was a foreground run that exited."
