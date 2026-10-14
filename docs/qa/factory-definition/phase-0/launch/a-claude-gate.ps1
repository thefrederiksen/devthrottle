# Factory Definition, phase 0, proof (a): THE GATE.
#
# Launches Claude Code with the Director's real launch line:
#     claude <permission flag> --session-id <guid> --settings "<the one settings file>"
# (SessionManager.cs appends --settings with the file ClaudeHookInstaller writes; the permission
# flag is the agent entry's preset - this machine's entries carry --dangerously-skip-permissions,
# the catalog default for a new entry is --permission-mode auto. Both are run.)
#
# The one settings file is the Director's REAL hooks-settings.json, copied and given a
# permissions.deny list - the merge phase 5 will do. The start folder is a scratch git repository
# whose own .claude/settings.json carries a second deny. The only additions to the line are
# -p (non-interactive), --output-format stream-json --verbose (so every tool result is captured)
# and --model (the Director composes a model into the line when the seat names one).
#
# Usage:  powershell -NoProfile -File a-claude-gate.ps1
# Output: a-output\<mode>\stream.jsonl, pointer.json, verdict.txt beside this script.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$outRoot = Join-Path $here 'a-output'
$realHooks = Join-Path $env:LOCALAPPDATA 'cc-director\instances\default\claude-hooks\hooks-settings.json'
if (-not (Test-Path -LiteralPath $realHooks)) {
    throw "The Director's hook settings file is not at $realHooks. Start a Director once on this machine so ClaudeHookInstaller writes it."
}

$modes = @(
    @{ Name = 'skip-permissions'; Flag = @('--dangerously-skip-permissions') },
    @{ Name = 'permission-mode-auto'; Flag = @('--permission-mode', 'auto') },
    # Control: the same line with the Director's hooks file UNCHANGED (no merged deny). kit-denied.txt
    # must then be readable, which shows the refusal above came from the merged rule and nothing else.
    @{ Name = 'control-no-merge'; Flag = @('--dangerously-skip-permissions'); NoMerge = $true }
)

$scratch = Join-Path $env:TEMP ("fd-p0-a-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $outRoot | Out-Null

try {
    foreach ($mode in $modes) {
        $m = $mode.Name
        Write-Host "=== mode: $m ==="
        $root = Join-Path $scratch $m
        $repo = Join-Path $root 'repo'
        $kit = Join-Path $root 'kit'
        New-Item -ItemType Directory -Force $repo, $kit, (Join-Path $repo '.claude') | Out-Null

        # The start folder: a git repository with its own deny rule.
        & git -C $repo init -q
        Set-Content -LiteralPath (Join-Path $repo 'allowed.txt') -Value 'ALLOWED-CONTENT-4410' -Encoding ascii
        Set-Content -LiteralPath (Join-Path $repo 'kit-denied.txt') -Value 'KIT-SECRET-9921' -Encoding ascii
        Set-Content -LiteralPath (Join-Path $repo 'repo-denied.txt') -Value 'REPO-SECRET-5537' -Encoding ascii
        $repoSettings = @{ permissions = @{ deny = @('Read(./repo-denied.txt)') } } | ConvertTo-Json -Depth 5
        Set-Content -LiteralPath (Join-Path $repo '.claude\settings.json') -Value $repoSettings -Encoding ascii

        # The ONE settings file: the Director's real hooks file with a deny list merged in.
        $merged = Get-Content -Raw -LiteralPath $realHooks | ConvertFrom-Json
        if (-not $mode.NoMerge) {
            $merged | Add-Member -NotePropertyName permissions -NotePropertyValue ([pscustomobject]@{
                deny = @('Read(./kit-denied.txt)', 'Bash(git push:*)')
            })
        }
        $settingsPath = Join-Path $kit 'settings.json'
        Set-Content -LiteralPath $settingsPath -Value ($merged | ConvertTo-Json -Depth 20) -Encoding ascii

        # The two files the Director's hook script uses (SessionManager stamps both paths).
        $pointer = Join-Path $kit 'pointer.json'
        $preamble = Join-Path $kit 'preamble.json'
        $envelope = @{ hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = 'PREAMBLE-MARKER-6083: this text was injected by the Director SessionStart hook.' } } | ConvertTo-Json -Depth 5
        Set-Content -LiteralPath $preamble -Value $envelope -Encoding ascii

        $sessionId = [guid]::NewGuid().ToString()
        $prompt = @'
This is an automated permission test. Do exactly these steps, using ONLY the Read tool for reads:
1. Read allowed.txt.
2. Read kit-denied.txt.
3. Read repo-denied.txt.
4. Run this Bash command: git push origin main
Do not retry any refused step and do not try any other way to get the content.
Then answer with one line per step giving the content or the exact refusal text, and a fifth line saying whether you can see a line starting PREAMBLE-MARKER in your context, and if so its number.
'@
        $claudeArgs = @() + $mode.Flag + @('--session-id', $sessionId, '--settings', $settingsPath,
            '-p', $prompt, '--output-format', 'stream-json', '--verbose', '--model', 'sonnet')

        $outDir = Join-Path $outRoot $m
        New-Item -ItemType Directory -Force $outDir | Out-Null
        $stream = Join-Path $root 'stream-raw.jsonl'

        # A clean environment for the child: no inherited session identity of the seat running this
        # script, only the two hook files.
        $saved = @{}
        foreach ($k in @(Get-ChildItem env: | Where-Object { $_.Name -like 'CC_*' } | ForEach-Object Name)) {
            $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $null)
        }
        $env:CC_SESSION_POINTER_FILE = $pointer
        $env:CC_SESSION_PREAMBLE_FILE = $preamble
        Push-Location $repo
        try {
            $line = 'claude ' + (($claudeArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
            Set-Content -LiteralPath (Join-Path $outDir 'launch-line.txt') -Value ($line.Replace($scratch, '<scratch>')) -Encoding ascii
            & claude @claudeArgs | Set-Content -LiteralPath $stream -Encoding ascii
            Write-Host "claude exit code: $LASTEXITCODE"
        }
        finally {
            Pop-Location
            Remove-Item env:CC_SESSION_POINTER_FILE, env:CC_SESSION_PREAMBLE_FILE -ErrorAction SilentlyContinue
            foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
        }

        Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $outDir 'merged-settings.json')
        Copy-Item -LiteralPath (Join-Path $repo '.claude\settings.json') -Destination (Join-Path $outDir 'repo-settings.json')

        # Evidence the session hook ran: the pointer file it writes, carrying OUR session id.
        $verdict = New-Object System.Collections.Generic.List[string]
        if (Test-Path -LiteralPath $pointer) {
            (Get-Content -Raw -LiteralPath $pointer).Replace($scratch.Replace('\', '\\'), '<scratch>') | Set-Content -LiteralPath (Join-Path $outDir 'pointer.json') -Encoding ascii
            $p = Get-Content -Raw -LiteralPath $pointer | ConvertFrom-Json
            $verdict.Add("HOOK: pointer file written by report-session.ps1, session_id=$($p.session_id), source=$($p.source), matches launch id: $($p.session_id -eq $sessionId)")
        } else {
            $verdict.Add('HOOK: FAIL - no pointer file; the SessionStart hook did not run')
        }

        # Every tool result, quoted from the stream.
        foreach ($l in Get-Content -LiteralPath $stream) {
            if (-not $l.Trim()) { continue }
            $o = $l | ConvertFrom-Json
            if ($o.type -eq 'assistant') {
                foreach ($c in $o.message.content) {
                    if ($c.type -eq 'tool_use') { $verdict.Add("TOOL_USE $($c.name): $($c.input | ConvertTo-Json -Compress)") }
                }
            }
            if ($o.type -eq 'user') {
                foreach ($c in $o.message.content) {
                    if ($c.type -eq 'tool_result') {
                        $text = if ($c.content -is [string]) { $c.content } else { ($c.content | ForEach-Object { $_.text }) -join ' ' }
                        $verdict.Add("TOOL_RESULT is_error=$($c.is_error): $text")
                    }
                }
            }
            if ($o.type -eq 'result') {
                $verdict.Add("PERMISSION_DENIALS: $($o.permission_denials | ConvertTo-Json -Compress -Depth 5)")
                $verdict.Add("FINAL ANSWER: $($o.result)")
            }
        }
        # The committed stream is trimmed: the init event keeps only the fields that bear on the proof,
        # and the output of the user's OWN hooks (not the Director's) is replaced by a note.
        $trimmed = foreach ($l in Get-Content -LiteralPath $stream) {
            if (-not $l.Trim()) { continue }
            $o = $l | ConvertFrom-Json
            if ($o.type -eq 'system' -and $o.subtype -eq 'init') {
                $o = [pscustomobject]@{ type = 'system'; subtype = 'init'; cwd = $o.cwd; session_id = $o.session_id; permissionMode = $o.permissionMode; model = $o.model; note = 'other init fields omitted' }
            }
            if ($o.type -eq 'system' -and $o.subtype -eq 'hook_response' -and "$($o.output)" -notmatch 'PREAMBLE-MARKER' -and "$($o.output)" -ne '') {
                $o.output = '<output of a user hook omitted>'; $o.stdout = '<omitted>'; $o.stderr = '<omitted>'
            }
            ($o | ConvertTo-Json -Compress -Depth 30).Replace($scratch.Replace('\', '\\'), '<scratch>').Replace($scratch, '<scratch>')
        }
        $trimmed | Set-Content -LiteralPath (Join-Path $outDir 'stream.jsonl') -Encoding ascii
        $verdict = $verdict | ForEach-Object { $_.Replace($scratch.Replace('\', '\\'), '<scratch>').Replace($scratch, '<scratch>') }
        $verdict | Set-Content -LiteralPath (Join-Path $outDir 'verdict.txt') -Encoding ascii
        $verdict | ForEach-Object { Write-Host $_ }
    }
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -Recurse -Force -LiteralPath $scratch }
}
