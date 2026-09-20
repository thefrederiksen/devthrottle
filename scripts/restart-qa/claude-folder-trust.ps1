#Requires -Version 5.1
<#
.SYNOPSIS
    Mark folders as already trusted by Claude Code, and take the marks away again.

.DESCRIPTION
    WHY THIS EXISTS, AND WHY IT IS NOT A WORKAROUND HIDDEN IN A SCRIPT. Product issue 3210: a Claude
    Code session created in a folder Claude Code has not seen before opens on its "Quick safety
    check" modal, the Director types the session's first prompt INTO that modal, and the session then
    exits reported as "exited cleanly". The flag --dangerously-skip-permissions does not suppress it.

    A quality assurance run that creates fresh scratch folders meets this every time, so every Claude
    Code seat would be lost before the thing under test had started. This marks those folders
    trusted BEFORE the run and takes the marks away AFTER it, so the machine is left as it was found
    - the same thing phase 3 did, recorded the same way.

    It changes ONE file, the user's own ~/.claude.json, and only the entries it is given. It refuses
    to revoke an entry it did not create: the grant writes a record of exactly what it added.

.PARAMETER Command
    grant  - mark each folder trusted, writing a record of what was changed
    revoke - undo exactly what the record says was changed
    show   - print the current mark for each folder

.PARAMETER Folders
    The folders. Required for grant and show.

.PARAMETER RecordTo
    Where the grant writes its record and the revoke reads it.

.EXAMPLE
    .\claude-folder-trust.ps1 grant -Folders C:\temp\seat-a,C:\temp\seat-b -RecordTo .\trust.json
    .\claude-folder-trust.ps1 revoke -RecordTo .\trust.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('grant', 'revoke', 'show')]
    [string]$Command,
    [string[]]$Folders,
    [Parameter(Mandatory = $true)][string]$RecordTo,
    [string]$ConfigPath = (Join-Path $env:USERPROFILE ".claude.json")
)

$ErrorActionPreference = 'Stop'
function Say([string]$text) { Write-Host "[claude-folder-trust] $text" }

if (-not (Test-Path $ConfigPath)) { throw "no Claude Code configuration at $ConfigPath." }

# Claude Code keys its project entries on the folder path with forward slashes.
function Normalize([string]$path) {
    return ([System.IO.Path]::GetFullPath($path)).TrimEnd('\', '/').Replace('\', '/')
}

$json = Get-Content $ConfigPath -Raw
$config = $json | ConvertFrom-Json

switch ($Command) {
    'show' {
        if (-not $Folders) { throw "show needs -Folders." }
        foreach ($f in $Folders) {
            $key = Normalize $f
            $entry = $config.projects.PSObject.Properties[$key]
            if (-not $entry) { Say "$key : no entry at all" }
            else { Say "$key : hasTrustDialogAccepted=$($entry.Value.hasTrustDialogAccepted)" }
        }
    }

    'grant' {
        if (-not $Folders) { throw "grant needs -Folders." }
        $changed = @()
        foreach ($f in $Folders) {
            $key = Normalize $f
            $prop = $config.projects.PSObject.Properties[$key]
            if (-not $prop) {
                $config.projects | Add-Member -NotePropertyName $key -NotePropertyValue ([pscustomobject]@{ hasTrustDialogAccepted = $true })
                $changed += [pscustomobject]@{ folder = $key; wasAbsent = $true; previous = $null }
                Say "$key : entry ADDED with hasTrustDialogAccepted=true"
            } else {
                $previous = $null
                if ($prop.Value.PSObject.Properties['hasTrustDialogAccepted']) {
                    $previous = $prop.Value.hasTrustDialogAccepted
                    $prop.Value.hasTrustDialogAccepted = $true
                } else {
                    $prop.Value | Add-Member -NotePropertyName hasTrustDialogAccepted -NotePropertyValue $true
                }
                $changed += [pscustomobject]@{ folder = $key; wasAbsent = $false; previous = $previous }
                Say "$key : hasTrustDialogAccepted set to true (was $previous)"
            }
        }

        Copy-Item $ConfigPath "$ConfigPath.before-restart-qa" -Force
        $config | ConvertTo-Json -Depth 100 | Set-Content -Path $ConfigPath -Encoding utf8
        ([pscustomobject]@{
            grantedAtUtc = (Get-Date).ToUniversalTime().ToString("s") + "Z"
            configPath   = $ConfigPath
            changed      = $changed
        }) | ConvertTo-Json -Depth 6 | Set-Content -Path $RecordTo -Encoding ascii
        Say "record written to $RecordTo; a copy of the file as it was is at $ConfigPath.before-restart-qa"
    }

    'revoke' {
        if (-not (Test-Path $RecordTo)) { throw "no grant record at $RecordTo, so there is nothing this script can prove it added." }
        $record = Get-Content $RecordTo -Raw | ConvertFrom-Json
        foreach ($c in $record.changed) {
            $prop = $config.projects.PSObject.Properties[$c.folder]
            if (-not $prop) { Say "$($c.folder) : already gone"; continue }
            if ($c.wasAbsent) {
                $config.projects.PSObject.Properties.Remove($c.folder)
                Say "$($c.folder) : entry REMOVED, as it was before"
            } else {
                $prop.Value.hasTrustDialogAccepted = $c.previous
                Say "$($c.folder) : hasTrustDialogAccepted put back to $($c.previous)"
            }
        }
        $config | ConvertTo-Json -Depth 100 | Set-Content -Path $ConfigPath -Encoding utf8
        Say "the machine is as it was found."
    }
}
