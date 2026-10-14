# Factory Definition, phase 0, proof (b) part 2: what Codex does when the rule hook FAILS.
#
# Same route as b-codex-rules.ps1 (a PreToolUse hook passed with -c, the Director's two bypass
# flags, the scripted model endpoint asking for one command: git tag fd-proof). Five hooks:
#   missing      - the hook's script file does not exist
#   crash        - exits 1 after writing garbage to stdout
#   timeout      - sleeps 20 seconds against timeout=3
#   exit-2       - exits 2 with a reason on stderr (the block Codex documents)
#   deny-control - answers the well-formed permissionDecision deny
# For each: was the tag created, and did anything in stderr or the --json events say so.
# This measurement was first made by the review session (reviews/phase0-launch-review.md, finding 1);
# this script reproduces it so it is committed beside the verdict.
#
# Usage:  powershell -NoProfile -File b2-codex-hook-failure-modes.ps1
# Output: b2-output\<mode>\verdict.txt, b2-output\summary.txt

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$outRoot = Join-Path $here 'b2-output'
New-Item -ItemType Directory -Force $outRoot | Out-Null
$scratch = Join-Path $env:TEMP ("fd-p0-b2-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$started = New-Object System.Collections.Generic.List[object]
$summary = New-Object System.Collections.Generic.List[string]

function Sanitize([string]$s) {
    $s.Replace($scratch.Replace('\', '\\'), '<scratch>').Replace($scratch.Replace('\', '/'), '<scratch>').Replace($scratch, '<scratch>').Replace($scratch.ToLowerInvariant(), '<scratch>')
}

$modes = [ordered]@{
    'missing'      = @{ Script = $null; Timeout = 10 }
    'crash'        = @{ Script = "[Console]::Out.Write('not json {{{'); exit 1"; Timeout = 10 }
    'timeout'      = @{ Script = "Start-Sleep -Seconds 20; exit 0"; Timeout = 3 }
    'exit-2'       = @{ Script = "[Console]::Error.Write('EXIT2-RULE: tagging is forbidden.'); exit 2"; Timeout = 10 }
    'deny-control' = @{ Script = "[Console]::Out.Write('{""hookSpecificOutput"":{""hookEventName"":""PreToolUse"",""permissionDecision"":""deny"",""permissionDecisionReason"":""DENY-CONTROL: tagging is forbidden.""}}'); exit 0"; Timeout = 10 }
}

try {
    foreach ($m in $modes.Keys) {
        Write-Host "=== $m ==="
        $root = Join-Path $scratch $m
        $repo = Join-Path $root 'repo'
        $kit = Join-Path $root 'kit'
        New-Item -ItemType Directory -Force $repo, $kit | Out-Null
        & git -C $repo init -q
        & git -C $repo -c user.name=t -c user.email=t@example.invalid commit -q --allow-empty -m init

        $hookScript = Join-Path $kit 'enforce.ps1'
        if ($modes[$m].Script) { Set-Content -LiteralPath $hookScript -Value $modes[$m].Script -Encoding ascii }

        $commandsFile = Join-Path $root 'commands.json'
        Set-Content -LiteralPath $commandsFile -Encoding ascii -Value '["git tag fd-proof"]'
        $requestLog = Join-Path $root 'requests.jsonl'
        $portFile = Join-Path $root 'port.txt'
        $server = Start-Process -FilePath 'python' -NoNewWindow -PassThru -ArgumentList ('"' + (Join-Path $here 'scripted_model.py') + '" --commands-json "' + $commandsFile + '" --log "' + $requestLog + '" --port-file "' + $portFile + '"')
        $started.Add($server)
        $deadline = (Get-Date).AddSeconds(20)
        while (-not (Test-Path -LiteralPath $portFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
        if (-not (Test-Path -LiteralPath $portFile)) { throw 'The scripted model endpoint did not start within 20 seconds.' }
        $port = (Get-Content -LiteralPath $portFile).Trim()

        $hookValue = "hooks.PreToolUse=[{matcher='^Bash`$',hooks=[{type='command',command='powershell -NoProfile -ExecutionPolicy Bypass -File " + $hookScript.Replace('\', '/') + "',timeout=" + $modes[$m].Timeout + "}]}]"
        $codexArgs = @('exec', '--dangerously-bypass-approvals-and-sandbox', '--dangerously-bypass-hook-trust', '--json', '--ephemeral',
            '-m', 'scripted', '-c', "model_provider='scripted'", '-c', "model_providers.scripted.name='scripted'",
            '-c', "model_providers.scripted.base_url='http://127.0.0.1:$port/v1'", '-c', "model_providers.scripted.wire_api='responses'",
            '-c', $hookValue, 'Run the script.')
        $argString = ($codexArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
        $emptyIn = Join-Path $root 'empty-stdin.txt'
        Set-Content -LiteralPath $emptyIn -Value $null -NoNewline
        $saved = @{}
        foreach ($k in @(Get-ChildItem env: | Where-Object { $_.Name -like 'CC_*' } | ForEach-Object Name)) {
            $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $null)
        }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            $p = Start-Process -FilePath 'codex' -ArgumentList $argString -NoNewWindow -Wait -PassThru -WorkingDirectory $repo `
                -RedirectStandardInput $emptyIn -RedirectStandardOutput (Join-Path $root 'events.jsonl') -RedirectStandardError (Join-Path $root 'stderr.txt')
        }
        finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
        $sw.Stop()
        if (-not $server.HasExited) { Stop-Process -Id $server.Id }

        $tag = ((& git -C $repo tag) -join ',')
        $err = (Get-Content -LiteralPath (Join-Path $root 'stderr.txt') -ErrorAction SilentlyContinue | Where-Object { $_ -notmatch 'Reading additional input from stdin' }) -join ' '
        $hookEvents = @(Get-Content -LiteralPath (Join-Path $root 'events.jsonl') | Where-Object { $_ -match '(?i)hook|blocked' -and $_ -notmatch 'dangerously-bypass-hook-trust' })
        $v = New-Object System.Collections.Generic.List[string]
        $v.Add("CODEX EXIT CODE: $($p.ExitCode), run took $([int]$sw.Elapsed.TotalSeconds) s")
        $v.Add("TAG CREATED: $(if ($tag) { 'yes' } else { 'no' })")
        $v.Add("STDERR (stdin notice removed): '$err'")
        $v.Add("JSON EVENTS NAMING A HOOK OR A BLOCK: $($hookEvents.Count)")
        foreach ($e in $hookEvents) { $v.Add("  $e") }
        foreach ($l in (& python (Join-Path $here 'scripted_model.py') summarize $requestLog)) { $v.Add($l) }
        $v = $v | ForEach-Object { Sanitize $_ }
        $outDir = Join-Path $outRoot $m
        New-Item -ItemType Directory -Force $outDir | Out-Null
        $v | Set-Content -LiteralPath (Join-Path $outDir 'verdict.txt') -Encoding ascii
        $v | ForEach-Object { Write-Host $_ }
        $summary.Add("| $m | tag created: $(if ($tag) { 'yes' } else { 'no' }) | stderr: $(if ($err) { (Sanitize $err) } else { 'nothing' }) | hook or block events: $($hookEvents.Count) |")
    }
    $summary | Set-Content -LiteralPath (Join-Path $outRoot 'summary.txt') -Encoding ascii
    Write-Host '=== SUMMARY ==='
    $summary | ForEach-Object { Write-Host $_ }
}
finally {
    foreach ($sp in $started) { if (-not $sp.HasExited) { Stop-Process -Id $sp.Id } }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -Recurse -Force -LiteralPath $scratch }
}
