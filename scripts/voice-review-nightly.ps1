# Nightly voice-review runner.
#
# Launched by the Windows Scheduled Task "cc-voice-review-nightly". Runs Claude Code
# headless to invoke the voice-review skill, which reviews any new voice turns, archives
# flagged ones locally, updates the day's digest, and files sanitized GitHub issues.
#
# This is intended to run via Task Scheduler (parent = svchost), NOT from inside another
# Claude Code session - a nested pseudo-console makes the child claude.exe exit early.
#
# It is autonomous by design (the user approved unattended issue creation), so it runs
# with --dangerously-skip-permissions. The skill enforces the privacy rule that no
# recording content ever reaches GitHub.

$ErrorActionPreference = "Stop"

$repo = "D:\ReposFred\devthrottle"
$logDir = Join-Path $env:LOCALAPPDATA "cc-director\voice-review\logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ("run-{0}.log" -f (Get-Date -Format "yyyy-MM-dd-HHmmss"))

$prompt = @'
Run the voice-review skill at .claude/skills/voice-review/SKILL.md now. Review every
voice turn that has no reviewed.json, judge inbound and outbound fidelity, archive any
flagged turns to the local voice-review area, append to today's digest, mark each turn
reviewed, and file sanitized GitHub issues per the skill's privacy rules (NEVER put
recording content in an issue; reference turn ids and archived paths only; dedup against
existing open voice-review issues). If there are no unreviewed turns, write a one-line
"nothing new" digest entry and exit without creating any issue.
'@

Set-Location $repo

"[{0}] voice-review nightly start" -f (Get-Date -Format "o") | Out-File -FilePath $log -Encoding utf8 -Append

# Resolve claude from PATH; fail loudly if absent (no silent fallback).
$claude = (Get-Command claude -ErrorAction SilentlyContinue).Source
if (-not $claude) {
    "ERROR: claude CLI not found on PATH for the task account." | Out-File -FilePath $log -Encoding utf8 -Append
    exit 1
}

& $claude -p $prompt --dangerously-skip-permissions *>> $log
$code = $LASTEXITCODE

"[{0}] voice-review nightly end (exit {1})" -f (Get-Date -Format "o"), $code | Out-File -FilePath $log -Encoding utf8 -Append
exit $code
