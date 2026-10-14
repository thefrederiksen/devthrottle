# Factory Definition, phase 0, proof (c): factory skills from a folder OUTSIDE both the checkout and
# the user's home reach ONE session only, in a git worktree and in a non-repository folder, while the
# fleet skills under the user's home still reach every session.
#
# The kit is a scratch folder holding one factory skill, fd-proof-skill, whose body carries a secret
# word. For each start folder (a git worktree, and a plain folder that is no repository) two sessions
# run on the Director's line: one given the kit, one not. The fleet skill checked is fleet-naming,
# which the Director installs under the user's home for every agent.
#
# CLAUDE CODE: the kit is a plugin folder passed with --plugin-dir, on the Director's real line
#     claude --dangerously-skip-permissions --session-id <guid> --settings <hooks-settings.json>
# plus -p, --output-format stream-json --verbose and --model sonnet (as in proof a). Evidence: the
# skills list in the session's init event, and the session asked to load the skill and quote its
# secret word.
#
# CODEX: the line is the Director's (codex exec, both bypass flags) against the scripted model
# endpoint of proof (b), because the sign-in on this machine is at its usage limit. Skills are given
# to the model in the first request's instructions, which the endpoint logs, so the evidence is the
# skill list Codex itself sent. Two routes are tried:
#     skills-config - -c skills.config=[{path='<kit skill>/SKILL.md',enabled=true}]
#     codex-home    - CODEX_HOME pointed at a per-session folder holding skills/fd-proof-skill
# A per-session CODEX_HOME also moves the sign-in (auth.json) and the user's config and hooks; that
# consequence is checked separately with `codex login status`.
#
# Usage:  powershell -NoProfile -File c-factory-skills.ps1
# Output: c-output\claude-<folder>-<kit|no-kit>\..., c-output\codex-<folder>-<route>\..., summary.txt

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$outRoot = Join-Path $here 'c-output'
New-Item -ItemType Directory -Force $outRoot | Out-Null
$realHooks = Join-Path $env:LOCALAPPDATA 'cc-director\instances\default\claude-hooks\hooks-settings.json'
if (-not (Test-Path -LiteralPath $realHooks)) { throw "The Director's hook settings file is not at $realHooks." }

$scratch = Join-Path $env:TEMP ("fd-p0-c-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$started = New-Object System.Collections.Generic.List[object]
$summary = New-Object System.Collections.Generic.List[string]

function Sanitize([string]$s) {
    $s.Replace($scratch.Replace('\', '\\'), '<scratch>').Replace($scratch.Replace('\', '/'), '<scratch>').Replace($scratch, '<scratch>').Replace($scratch.ToLowerInvariant(), '<scratch>')
}

function Get-SkillsFolderState {
    # The entry NAMES in the user's skill folders, read only. Not write times: the Director rewrites
    # every fleet skill under ~/.agents/skills when it refreshes them from the Gateway, which it may
    # do in the middle of a run (observed 14 October 2026), and that is not something a run wrote.
    $dirs = @((Join-Path $env:USERPROFILE '.claude\skills'), (Join-Path $env:USERPROFILE '.agents\skills'), (Join-Path $env:USERPROFILE '.codex\skills'))
    ($dirs | ForEach-Object { if (Test-Path $_) { Get-ChildItem -LiteralPath $_ -Force | ForEach-Object { $_.FullName } } }) -join "`n"
}

function Test-FactorySkillInUserFolders {
    $dirs = @((Join-Path $env:USERPROFILE '.claude\skills'), (Join-Path $env:USERPROFILE '.agents\skills'), (Join-Path $env:USERPROFILE '.codex\skills'))
    $hits = foreach ($d in $dirs) { if (Test-Path $d) { Get-ChildItem -LiteralPath $d -Recurse -Filter 'SKILL.md' -ErrorAction SilentlyContinue | Where-Object { (Get-Content -Raw -LiteralPath $_.FullName) -match 'OCELOT-4471' } } }
    @($hits).Count
}

function Clear-SessionEnvironment {
    $saved = @{}
    foreach ($k in @(Get-ChildItem env: | Where-Object { $_.Name -like 'CC_*' -or $_.Name -eq 'CODEX_HOME' } | ForEach-Object Name)) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $null)
    }
    $saved
}

function Restore-SessionEnvironment($saved) {
    foreach ($k in @('CC_SESSION_POINTER_FILE', 'CC_SESSION_PREAMBLE_FILE', 'CODEX_HOME')) { [Environment]::SetEnvironmentVariable($k, $null) }
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
}

try {
    $before = Get-SkillsFolderState

    # ---- The kit: one factory skill, as a Claude Code plugin and as a bare skill folder. ----
    $kit = Join-Path $scratch 'kit'
    $skillBody = @'
---
name: fd-proof-skill
description: Factory Definition phase 0 proof skill. Use when asked for the factory proof secret word.
---

The factory proof secret word is OCELOT-4471.
'@
    $pluginRoot = Join-Path $kit 'claude-plugin'
    New-Item -ItemType Directory -Force (Join-Path $pluginRoot '.claude-plugin'), (Join-Path $pluginRoot 'skills\fd-proof-skill') | Out-Null
    Set-Content -LiteralPath (Join-Path $pluginRoot '.claude-plugin\plugin.json') -Encoding ascii -Value '{ "name": "factory-kit", "version": "0.0.1", "description": "Factory skills for one session (phase 0 proof)." }'
    Set-Content -LiteralPath (Join-Path $pluginRoot 'skills\fd-proof-skill\SKILL.md') -Encoding ascii -Value $skillBody
    $codexSkill = Join-Path $kit 'skills\fd-proof-skill'
    New-Item -ItemType Directory -Force $codexSkill | Out-Null
    Set-Content -LiteralPath (Join-Path $codexSkill 'SKILL.md') -Encoding ascii -Value $skillBody
    $codexHome = Join-Path $kit 'codex-home'
    New-Item -ItemType Directory -Force (Join-Path $codexHome 'skills\fd-proof-skill') | Out-Null
    Set-Content -LiteralPath (Join-Path $codexHome 'skills\fd-proof-skill\SKILL.md') -Encoding ascii -Value $skillBody

    # ---- The two start folders. ----
    $mainRepo = Join-Path $scratch 'main-repo'
    New-Item -ItemType Directory -Force $mainRepo | Out-Null
    & git -C $mainRepo init -q
    & git -C $mainRepo -c user.name=t -c user.email=t@example.invalid commit -q --allow-empty -m init
    $worktree = Join-Path $scratch 'worktree'
    & git -C $mainRepo worktree add -q -b proof $worktree
    $plain = Join-Path $scratch 'plain-folder'
    New-Item -ItemType Directory -Force $plain | Out-Null
    $folders = @(@{ Name = 'worktree'; Path = $worktree }, @{ Name = 'plain-folder'; Path = $plain })

    # ---- Claude Code. ----
    foreach ($f in $folders) {
        foreach ($withKit in @($true, $false)) {
            $n = "claude-$($f.Name)-" + $(if ($withKit) { 'kit' } else { 'no-kit' })
            Write-Host "=== $n ==="
            $outDir = Join-Path $outRoot $n
            New-Item -ItemType Directory -Force $outDir | Out-Null
            $sessionDir = Join-Path $scratch $n
            New-Item -ItemType Directory -Force $sessionDir | Out-Null
            $prompt = 'This is an automated test. Use the Skill tool to load the skill named fd-proof-skill (it may carry a plugin prefix, such as factory-kit:fd-proof-skill). If it loads, answer with the secret word it contains. If no such skill exists, answer NOT AVAILABLE and quote the error. Do not search the disk for it.'
            $claudeArgs = @('--dangerously-skip-permissions', '--session-id', [guid]::NewGuid().ToString(), '--settings', $realHooks)
            if ($withKit) { $claudeArgs += @('--plugin-dir', $pluginRoot) }
            $claudeArgs += @('-p', $prompt, '--output-format', 'stream-json', '--verbose', '--model', 'sonnet')
            $line = 'claude ' + (($claudeArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
            Set-Content -LiteralPath (Join-Path $outDir 'launch-line.txt') -Value (Sanitize $line) -Encoding ascii
            $raw = Join-Path $sessionDir 'stream-raw.jsonl'
            $saved = Clear-SessionEnvironment
            $env:CC_SESSION_POINTER_FILE = Join-Path $sessionDir 'pointer.json'
            Push-Location $f.Path
            try { & claude @claudeArgs | Set-Content -LiteralPath $raw -Encoding ascii }
            finally { Pop-Location; Restore-SessionEnvironment $saved }

            $v = New-Object System.Collections.Generic.List[string]
            $trimmed = foreach ($l in Get-Content -LiteralPath $raw) {
                if (-not $l.Trim()) { continue }
                $o = $l | ConvertFrom-Json
                if ($o.type -eq 'system' -and $o.subtype -eq 'init') {
                    $proofSkills = @($o.skills | Where-Object { "$_" -match 'fd-proof' })
                    $fleetSkills = @($o.skills | Where-Object { "$_" -match '^fleet-naming$' })
                    $v.Add("INIT cwd=$($o.cwd)")
                    $v.Add("INIT factory skill names in the session: [$($proofSkills -join ', ')]")
                    $v.Add("INIT fleet skill fleet-naming in the session: [$($fleetSkills -join ', ')]")
                    $v.Add("INIT plugins: $(@($o.plugins | Where-Object { "$($_.name)" -eq 'factory-kit' }) | ConvertTo-Json -Compress)")
                    $o = [pscustomobject]@{ type = 'system'; subtype = 'init'; cwd = $o.cwd; permissionMode = $o.permissionMode; factory_skills = $proofSkills; fleet_skill = $fleetSkills; factory_plugin = @($o.plugins | Where-Object { "$($_.name)" -eq 'factory-kit' }); note = 'other init fields omitted' }
                }
                if ($o.type -eq 'system' -and $o.subtype -eq 'hook_response' -and "$($o.output)" -ne '') {
                    $o.output = '<hook output omitted>'; $o.stdout = '<omitted>'; $o.stderr = '<omitted>'
                }
                if ($o.type -eq 'assistant') { foreach ($c in $o.message.content) { if ($c.type -eq 'tool_use') { $v.Add("TOOL_USE $($c.name): $($c.input | ConvertTo-Json -Compress)") } } }
                if ($o.type -eq 'user') { foreach ($c in $o.message.content) { if ($c.type -eq 'tool_result') {
                    $text = if ($c.content -is [string]) { $c.content } else { ($c.content | ForEach-Object { $_.text }) -join ' ' }
                    $v.Add("TOOL_RESULT is_error=$($c.is_error): $text") } } }
                if ($o.type -eq 'result') { $v.Add("FINAL ANSWER: $($o.result)") }
                Sanitize ($o | ConvertTo-Json -Compress -Depth 30)
            }
            $trimmed | Set-Content -LiteralPath (Join-Path $outDir 'stream.jsonl') -Encoding ascii
            $v = $v | ForEach-Object { Sanitize $_ }
            $v | Set-Content -LiteralPath (Join-Path $outDir 'verdict.txt') -Encoding ascii
            $v | ForEach-Object { Write-Host $_ }
            $summary.Add("[$n]"); $v | Where-Object { $_ -like 'INIT factory*' -or $_ -like 'INIT fleet*' -or $_ -like 'FINAL*' } | ForEach-Object { $summary.Add("  $_") }
        }
    }

    # ---- Codex. ----
    $routes = @('skills-config', 'codex-home', 'none')
    foreach ($f in $folders) {
        foreach ($route in $routes) {
            $n = "codex-$($f.Name)-$route"
            Write-Host "=== $n ==="
            $outDir = Join-Path $outRoot $n
            New-Item -ItemType Directory -Force $outDir | Out-Null
            $sessionDir = Join-Path $scratch $n
            New-Item -ItemType Directory -Force $sessionDir | Out-Null
            $commandsFile = Join-Path $sessionDir 'commands.json'
            Set-Content -LiteralPath $commandsFile -Encoding ascii -Value '[]'
            $requestLog = Join-Path $sessionDir 'requests.jsonl'
            $portFile = Join-Path $sessionDir 'port.txt'
            $server = Start-Process -FilePath 'python' -NoNewWindow -PassThru -ArgumentList ('"' + (Join-Path $here 'scripted_model.py') + '" --commands-json "' + $commandsFile + '" --log "' + $requestLog + '" --port-file "' + $portFile + '"')
            $started.Add($server)
            $deadline = (Get-Date).AddSeconds(20)
            while (-not (Test-Path -LiteralPath $portFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
            if (-not (Test-Path -LiteralPath $portFile)) { throw 'The scripted model endpoint did not start within 20 seconds.' }
            $port = (Get-Content -LiteralPath $portFile).Trim()

            $codexArgs = @('exec', '--dangerously-bypass-approvals-and-sandbox', '--dangerously-bypass-hook-trust', '--json', '--ephemeral',
                '-m', 'scripted', '-c', "model_provider='scripted'", '-c', "model_providers.scripted.name='scripted'",
                '-c', "model_providers.scripted.base_url='http://127.0.0.1:$port/v1'", '-c', "model_providers.scripted.wire_api='responses'")
            if ($f.Name -eq 'plain-folder') { $codexArgs += @('--skip-git-repo-check') }
            if ($route -eq 'skills-config') {
                # Built first: inside @( ) the comma binds tighter than +, which splits the value.
                $skillsValue = "skills.config=[{path='" + (Join-Path $codexSkill 'SKILL.md').Replace('\', '/') + "',enabled=true}]"
                $codexArgs += @('-c', $skillsValue)
            }
            $codexArgs += @('Say which skills you have.')
            $line = ($(if ($route -eq 'codex-home') { 'CODEX_HOME=<kit>\codex-home ' } else { '' })) + 'codex ' + (($codexArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
            Set-Content -LiteralPath (Join-Path $outDir 'launch-line.txt') -Value (Sanitize $line) -Encoding ascii

            $emptyIn = Join-Path $sessionDir 'empty-stdin.txt'
            Set-Content -LiteralPath $emptyIn -Value $null -NoNewline
            $saved = Clear-SessionEnvironment
            if ($route -eq 'codex-home') { $env:CODEX_HOME = $codexHome }
            try {
                # Every argument quoted: an unquoted -c value carrying a comma is split by the Windows
                # command-line parser and codex refuses the half it does not recognise.
                $argString = ($codexArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
                $p = Start-Process -FilePath 'codex' -ArgumentList $argString -NoNewWindow -Wait -PassThru -WorkingDirectory $f.Path `
                    -RedirectStandardInput $emptyIn -RedirectStandardOutput (Join-Path $sessionDir 'events.jsonl') -RedirectStandardError (Join-Path $sessionDir 'stderr.txt')
                Write-Host "codex exit code: $($p.ExitCode)"
            }
            finally { Restore-SessionEnvironment $saved }
            if (-not $server.HasExited) { Stop-Process -Id $server.Id }

            $v = New-Object System.Collections.Generic.List[string]
            $v.Add("CODEX EXIT CODE: $($p.ExitCode)")
            $errText = (Get-Content -LiteralPath (Join-Path $sessionDir 'stderr.txt') -ErrorAction SilentlyContinue | Where-Object { $_ -notmatch 'Reading additional input from stdin' }) -join ' '
            if ($errText) { $v.Add("CODEX STDERR: $errText") }
            if (Test-Path -LiteralPath $requestLog) {
                foreach ($l in (& python (Join-Path $here 'scripted_model.py') summarize $requestLog --needle 'fd-proof-skill' --needle 'fleet-naming')) { $v.Add($l) }
            } else {
                $v.Add('REQUESTS: none - Codex never reached the model endpoint')
            }
            foreach ($l in Get-Content -LiteralPath (Join-Path $sessionDir 'events.jsonl')) {
                if ($l -match '"type":"error"' -or $l -match 'turn.failed') { $v.Add("EVENT: $l") }
            }
            $v = $v | ForEach-Object { Sanitize $_ }
            $v | Set-Content -LiteralPath (Join-Path $outDir 'verdict.txt') -Encoding ascii
            $v | ForEach-Object { Write-Host $_ }
            $summary.Add("[$n]"); $v | Where-Object { $_ -like 'NEEDLE*' -or $_ -like 'CODEX EXIT*' -or $_ -like 'CODEX STDERR*' -or $_ -like 'REQUESTS: none*' } | ForEach-Object { $summary.Add("  " + ($_ -replace ' \.\.\. .*$', '')) }
        }
    }

    # ---- What a per-session CODEX_HOME does to the sign-in. ----
    $saved = Clear-SessionEnvironment
    try {
        $real = (cmd /c 'codex login status 2>&1' | Out-String).Trim()
        $env:CODEX_HOME = $codexHome
        $perSession = (cmd /c 'codex login status 2>&1' | Out-String).Trim()
    }
    finally { Restore-SessionEnvironment $saved }
    $summary.Add("[codex login status, the user's own CODEX_HOME] $real")
    $summary.Add("[codex login status, CODEX_HOME = a per-session kit folder] $perSession")

    # ---- Nothing written into the checkout or the user's skill folders. ----
    $status = (& git -C $worktree status --porcelain --untracked-files=all | Out-String).Trim()
    $summary.Add("[git status --porcelain of the worktree after every run] '" + $status + "'")
    $after = Get-SkillsFolderState
    $summary.Add("[user's skill folders (~/.claude/skills, ~/.agents/skills, ~/.codex/skills): same entries before and after the runs] $($before -eq $after)")
    $summary.Add("[SKILL.md files in those folders carrying the factory skill's secret word] $(Test-FactorySkillInUserFolders)")
    $summary | ForEach-Object { Sanitize $_ } | Set-Content -LiteralPath (Join-Path $outRoot 'summary.txt') -Encoding ascii
    Write-Host '=== SUMMARY ==='
    $summary | ForEach-Object { Write-Host (Sanitize $_) }
}
finally {
    foreach ($sp in $started) { if (-not $sp.HasExited) { Stop-Process -Id $sp.Id } }
    if (Test-Path -LiteralPath $scratch) {
        $wt = Join-Path $scratch 'worktree'
        if (Test-Path -LiteralPath $wt) { & git -C (Join-Path $scratch 'main-repo') worktree remove --force $wt }
        Remove-Item -Recurse -Force -LiteralPath $scratch
    }
}
