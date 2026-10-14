# Factory Definition, phase 0, proof (b): rules for a Codex session at launch.
#
# The Director's Codex line (SessionManager.cs + AgentToolCatalog.cs on origin/main):
#     codex --dangerously-bypass-approvals-and-sandbox --dangerously-bypass-hook-trust [prompt]
# with its SessionStart hook merged into the user's ~/.codex/hooks.json (CodexHookInstaller) and the
# preamble path in CC_SESSION_PREAMBLE_FILE.
#
# Codex has no --settings equivalent: it reads .rules files and hooks only from folders beside a
# config layer (the user's ~/.codex, a trusted project's .codex). This script tests what CAN carry
# a factory rule at launch without writing into the checkout or the user's home:
#   KIT HOOK   - a PreToolUse hook supplied on the command line with -c, whose script and rules
#                file live in the kit folder outside the checkout.
#   REPO RULE  - the start folder's own .codex/rules/repo.rules with decision "forbidden".
# Both under the Director's two bypass flags. The user's own hooks.json (with the Director's
# SessionStart hook) loads as it always does; it is read, never written.
#
# Deviation from the Director's line, and why it does not weaken the proof: `codex exec` (the
# non-interactive form of the same CLI, which takes the same two flags) instead of the TUI;
# --json so every command and its result is captured; --ephemeral so no session file is written
# into the user's home; and -c projects.'<start folder>'.trust_level='trusted', which is what the
# user's config.toml already says for the folders the Director starts Codex in (a project layer
# loads only when trusted). The -c hooks override is the thing under test.
#
# THE MODEL IS SCRIPTED. On 14 October 2026 this machine's Codex sign-in was at its usage limit
# (until 10 November 2026) and its config.toml model is refused for a ChatGPT sign-in. Rules and
# hooks are enforced by the Codex CLI before a command runs - the model only asks - so the real CLI
# is pointed at scripted_model.py, a local endpoint passed as a custom model provider with -c, which
# asks for the same three commands every run. What Codex sends BACK for each command (its
# function_call_output) is the refusal, quoted, and the first request shows the SessionStart hook's
# context arriving. Nothing about the enforcement path differs from a real model's turn.
#
# Usage:  powershell -NoProfile -File b-codex-rules.ps1
# Output: b-output\<run>\events.jsonl, verdict.txt beside this script.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$outRoot = Join-Path $here 'b-output'
New-Item -ItemType Directory -Force $outRoot | Out-Null

$started = New-Object System.Collections.Generic.List[object]
$scratch = Join-Path $env:TEMP ("fd-p0-b-" + [guid]::NewGuid().ToString('N').Substring(0, 8))

$runs = @(
    @{ Name = 'kit-hook-and-repo-rule'; KitHook = $true },
    # Control: the same line with no kit hook. git tag must then run, which shows the refusal in
    # the first run came from the kit hook and nothing else.
    @{ Name = 'control-no-kit-hook'; KitHook = $false }
)

function Sanitize([string]$s) {
    $s.Replace($scratch.Replace('\', '\\'), '<scratch>').Replace($scratch.Replace('\', '/'), '<scratch>').Replace($scratch, '<scratch>').Replace($scratch.ToLowerInvariant(), '<scratch>')
}

try {
    foreach ($run in $runs) {
        $n = $run.Name
        Write-Host "=== run: $n ==="
        $root = Join-Path $scratch $n
        $repo = Join-Path $root 'repo'
        $kit = Join-Path $root 'kit'
        New-Item -ItemType Directory -Force $repo, $kit, (Join-Path $repo '.codex\rules') | Out-Null
        & git -C $repo init -q
        & git -C $repo -c user.name=t -c user.email=t@example.invalid commit -q --allow-empty -m init

        # The start folder's own rule.
        Set-Content -LiteralPath (Join-Path $repo '.codex\rules\repo.rules') -Encoding ascii -Value @'
prefix_rule(pattern = ["git", "push"], decision = "forbidden", justification = "REPO-RULE-3318: pushing is forbidden in this repository.")
'@

        # The kit: a rules file and the hook script that enforces it. Both outside the checkout.
        $rulesJson = @{ deny = @(@{ pattern = '\bgit\s+tag\b'; reason = 'KIT-RULE-7702: tagging is forbidden for this seat.' }) } | ConvertTo-Json -Depth 5
        Set-Content -LiteralPath (Join-Path $kit 'rules.json') -Value $rulesJson -Encoding ascii
        $hookLog = Join-Path $kit 'hook-calls.log'
        Set-Content -LiteralPath (Join-Path $kit 'enforce.ps1') -Encoding ascii -Value @'
$raw = [Console]::In.ReadToEnd()
$kitDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Content -LiteralPath (Join-Path $kitDir 'hook-calls.log') -Value $raw
$evt = $raw | ConvertFrom-Json
$cmd = if ($evt.tool_input.command -is [string]) { $evt.tool_input.command } else { ($evt.tool_input.command -join ' ') }
$rules = Get-Content -Raw -LiteralPath (Join-Path $kitDir 'rules.json') | ConvertFrom-Json
foreach ($r in $rules.deny) {
    if ($cmd -match $r.pattern) {
        $out = @{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $r.reason } } | ConvertTo-Json -Depth 5 -Compress
        [Console]::Out.Write($out)
        exit 0
    }
}
exit 0
'@

        $preamble = Join-Path $kit 'preamble.json'
        $envelope = @{ hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = 'PREAMBLE-MARKER-6083: this text was injected by the Director SessionStart hook.' } } | ConvertTo-Json -Depth 5
        Set-Content -LiteralPath $preamble -Value $envelope -Encoding ascii

        $prompt = 'This is an automated permission test. Run each of these three shell commands separately, in this order, and do not retry or work around any refused command: (1) git status (2) git tag fd-proof (3) git push origin main. Then answer with one line per command giving its outcome or the exact refusal text, and a fourth line saying whether a line starting PREAMBLE-MARKER is in your context and, if so, the number after it.'

        # The scripted model endpoint for this run (see scripted_model.py and the header above).
        $commandsFile = Join-Path $root 'commands.json'
        Set-Content -LiteralPath $commandsFile -Encoding ascii -Value '["git status", "git tag fd-proof", "git push origin main"]'
        $requestLog = Join-Path $root 'requests.jsonl'
        $portFile = Join-Path $root 'port.txt'
        $server = Start-Process -FilePath 'python' -NoNewWindow -PassThru -ArgumentList ('"' + (Join-Path $here 'scripted_model.py') + '" --commands-json "' + $commandsFile + '" --log "' + $requestLog + '" --port-file "' + $portFile + '"')
        $started.Add($server)
        $deadline = (Get-Date).AddSeconds(20)
        while (-not (Test-Path -LiteralPath $portFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
        if (-not (Test-Path -LiteralPath $portFile)) { throw 'The scripted model endpoint did not start within 20 seconds.' }
        $port = (Get-Content -LiteralPath $portFile).Trim()

        $trustKey = "projects.'" + $repo.ToLowerInvariant() + "'.trust_level='trusted'"
        $codexArgs = @('exec', '--dangerously-bypass-approvals-and-sandbox', '--dangerously-bypass-hook-trust',
            '--json', '--ephemeral', '-m', 'scripted', '-c', "model_provider='scripted'",
            '-c', "model_providers.scripted.name='scripted'", '-c', "model_providers.scripted.base_url='http://127.0.0.1:$port/v1'",
            '-c', "model_providers.scripted.wire_api='responses'", '-c', $trustKey)
        if ($run.KitHook) {
            $script = (Join-Path $kit 'enforce.ps1').Replace('\', '/')
            $codexArgs += @('-c', "hooks.PreToolUse=[{matcher='^Bash`$',hooks=[{type='command',command='powershell -NoProfile -ExecutionPolicy Bypass -File $script',timeout=10}]}]")
        }
        $codexArgs += @($prompt)

        $outDir = Join-Path $outRoot $n
        New-Item -ItemType Directory -Force $outDir | Out-Null
        $events = Join-Path $root 'events-raw.jsonl'

        $saved = @{}
        foreach ($k in @(Get-ChildItem env: | Where-Object { $_.Name -like 'CC_*' } | ForEach-Object Name)) {
            $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $null)
        }
        $env:CC_SESSION_PREAMBLE_FILE = $preamble
        Push-Location $repo
        try {
            $line = 'codex ' + (($codexArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
            Set-Content -LiteralPath (Join-Path $outDir 'launch-line.txt') -Value (Sanitize $line) -Encoding ascii
            # Started with an empty stdin: codex exec appends piped stdin to the prompt and waits on it.
            $emptyIn = Join-Path $root 'empty-stdin.txt'
            Set-Content -LiteralPath $emptyIn -Value $null -NoNewline
            $argString = ($codexArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
            $p = Start-Process -FilePath 'codex' -ArgumentList $argString -NoNewWindow -Wait -PassThru `
                -RedirectStandardInput $emptyIn -RedirectStandardOutput $events -RedirectStandardError (Join-Path $root 'stderr.txt')
            Write-Host "codex exit code: $($p.ExitCode)"
        }
        finally {
            Pop-Location
            Remove-Item env:CC_SESSION_PREAMBLE_FILE -ErrorAction SilentlyContinue
            foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
        }

        if (-not $server.HasExited) { Stop-Process -Id $server.Id }
        $verdict = New-Object System.Collections.Generic.List[string]
        foreach ($l in (& python (Join-Path $here 'scripted_model.py') summarize $requestLog --needle 'PREAMBLE-MARKER-6083')) { $verdict.Add($l) }
        $tags = (& git -C $repo tag) -join ','
        $verdict.Add("TAGS IN THE REPOSITORY AFTER THE RUN: '$tags'")
        if (Test-Path -LiteralPath $hookLog) {
            $calls = @(Get-Content -LiteralPath $hookLog | Where-Object { $_.Trim() })
            $verdict.Add("KIT HOOK: called $($calls.Count) time(s)")
            Copy-Item -LiteralPath $hookLog -Destination (Join-Path $outDir 'hook-calls.log')
        } else {
            $verdict.Add('KIT HOOK: never called')
        }
        $trimmed = foreach ($l in Get-Content -LiteralPath $events) {
            if (-not $l.Trim()) { continue }
            $o = $l | ConvertFrom-Json
            $t = "$($o.type)"
            if ($o.item) {
                $it = $o.item
                if ($it.type -eq 'command_execution' -and $t -eq 'item.completed') {
                    $verdict.Add("COMMAND: $($it.command) | exit=$($it.exit_code) status=$($it.status) | output: $((("$($it.aggregated_output)") -replace '\s+', ' ').Trim())")
                }
                if ($it.type -eq 'agent_message') { $verdict.Add("AGENT MESSAGE: $($it.text)") }
                if ($it.type -eq 'error') { $verdict.Add("ERROR ITEM: $($it.message)") }
            }
            if ($t -eq 'error' -or $t -eq 'turn.failed') { $verdict.Add("EVENT $t : $($l)") }
            Sanitize $l
        }
        $trimmed | Set-Content -LiteralPath (Join-Path $outDir 'events.jsonl') -Encoding ascii
        $err = Get-Content -Raw -LiteralPath (Join-Path $root 'stderr.txt') -ErrorAction SilentlyContinue
        if ($err) { Set-Content -LiteralPath (Join-Path $outDir 'stderr.txt') -Value (Sanitize $err) -Encoding ascii }
        $verdict = $verdict | ForEach-Object { Sanitize $_ }
        $verdict | Set-Content -LiteralPath (Join-Path $outDir 'verdict.txt') -Encoding ascii
        $verdict | ForEach-Object { Write-Host $_ }
    }
}
finally {
    # Stop only the endpoint processes this script started.
    foreach ($sp in $started) { if (-not $sp.HasExited) { Stop-Process -Id $sp.Id } }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -Recurse -Force -LiteralPath $scratch }
}
