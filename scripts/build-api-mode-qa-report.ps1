#Requires -Version 7.4
<#
.SYNOPSIS
    API mode mission (#4070), phase 6: build the QA report - one self-contained HTML file - from the proof runs.

.DESCRIPTION
    Reads the folders scripts/prove-api-mode-pi.ps1 wrote (each holds results.json, summary.txt, wire.jsonl,
    director-pi.log, spend.txt and screens/), and writes one HTML file in the dev report shape, so it can be published
    to the owner as it is with `cc-dev-reports open <file>`: a header with one of the three status words, a summary, a
    questions section, the detail sections, and an evidence section that says plainly what was NOT proven.

    Every screenshot is embedded as a data address and every accessibility dump as text, because a dev report is one
    file with no links to anything beside it. There is no script in the page.

    The findings (things the runs showed that are not a pass or a fail of a check) are read from -Findings: a text
    file of blocks separated by a blank line, each block's first line its title and the rest its detail.

.PARAMETER Runs
    The run folders, one per platform. A platform with no run is reported as not proven.

.PARAMETER Out
    The HTML file to write.

.PARAMETER Findings
    The findings file. Default: qa-findings.txt beside the report.

.PARAMETER Status
    The report's status word: done, waiting-on-you or agent-working. Default: done when every run passed and both
    Linux and Windows have a run, otherwise agent-working.

.EXAMPLE
    pwsh -File scripts/build-api-mode-qa-report.ps1 -Runs proofs/phase-6/linux-20261019-060000,proofs/phase-6/windows-20261019-080000 -Out proofs/phase-6/qa-report.html
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Runs,
    [Parameter(Mandatory)][string]$Out,
    [string]$Findings,
    [ValidateSet('done', 'waiting-on-you', 'agent-working')][string]$Status
)

$ErrorActionPreference = 'Stop'
if (-not $Findings) { $Findings = Join-Path (Split-Path $Out) 'qa-findings.txt' }

function Html([string]$Text) { [System.Net.WebUtility]::HtmlEncode($Text) }

function Read-Run([string]$Folder) {
    $file = Join-Path $Folder 'results.json'
    if (-not (Test-Path $file)) { throw "no results.json in ${Folder}: is it a folder scripts/prove-api-mode-pi.ps1 wrote?" }
    $r = Get-Content $file -Raw | ConvertFrom-Json -Depth 20
    $r | Add-Member -NotePropertyName folder -NotePropertyValue $Folder
    return $r
}

function Image([string]$Folder, [string]$Relative) {
    $path = Join-Path $Folder $Relative
    if (-not (Test-Path $path)) { return '<p class="missing">no picture was taken</p>' }
    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
    return "<img alt=""$(Html $Relative)"" src=""data:image/png;base64,$b64"">"
}

function Dump([string]$Folder, [string]$Relative) {
    $path = Join-Path $Folder $Relative
    if (-not (Test-Path $path)) { return '' }
    return "<details><summary>Accessibility dump ($(Html (Split-Path $Relative -Leaf)))</summary><pre>$(Html (Get-Content $path -Raw))</pre></details>"
}

function Badge([string]$Result) {
    $class = switch ($Result) { 'PASS' { 'pass' } 'FAIL' { 'fail' } default { 'other' } }
    "<span class=""badge $class"">$(Html $Result)</span>"
}

$runData = @($Runs | ForEach-Object { Read-Run $_ })
$platforms = @($runData | ForEach-Object { $_.platform })
$missing = @('linux', 'windows' | Where-Object { $_ -notin $platforms })
$failedSteps = @($runData | ForEach-Object { $run = $_; $_.steps | Where-Object { $_.result -eq 'FAIL' } | ForEach-Object { "$($run.platform): $($_.step)" } })
$allPassed = $failedSteps.Count -eq 0 -and @($runData | Where-Object { $_.exitCode -ne 0 }).Count -eq 0
if (-not $Status) { $Status = if ($allPassed -and $missing.Count -eq 0) { 'done' } else { 'agent-working' } }
$spend = ($runData | Measure-Object -Property spendDollars -Sum).Sum

$findingBlocks = @()
if (Test-Path $Findings) {
    $findingBlocks = @(((Get-Content $Findings -Raw) -split "(\r?\n){2,}") | Where-Object { $_.Trim() } | ForEach-Object {
        $lines = @($_.Trim() -split "\r?\n")
        [pscustomobject]@{ Title = $lines[0]; Detail = (($lines | Select-Object -Skip 1) -join ' ') }
    })
}

$h = [System.Text.StringBuilder]::new()
function Add([string]$Text) { [void]$h.AppendLine($Text) }

Add '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">'
Add '<title>API mode QA report</title><style>'
Add ':root{--bg:#ffffff;--fg:#1a1d24;--muted:#5b6170;--line:#d9dde5;--pass:#127a3a;--fail:#b3261e;--other:#8a6d00;--card:#f5f7fa}'
Add '@media (prefers-color-scheme: dark){:root{--bg:#0f1218;--fg:#e6e9ef;--muted:#9aa3b2;--line:#2a3140;--pass:#4cc27a;--fail:#ff6b61;--other:#e2c050;--card:#171c25}}'
Add 'body{background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,sans-serif;margin:0;padding:16px;max-width:1100px;margin-inline:auto}'
Add 'h1{font-size:1.6em;margin:.2em 0}h2{margin-top:1.6em;border-bottom:1px solid var(--line);padding-bottom:.2em}h3{margin-bottom:.3em}'
Add 'table{border-collapse:collapse;width:100%;margin:.5em 0}td,th{border:1px solid var(--line);padding:6px 8px;text-align:left;vertical-align:top}'
Add '.badge{font-weight:700;padding:1px 8px;border-radius:10px;white-space:nowrap}.pass{color:var(--pass)}.fail{color:var(--fail)}.other{color:var(--other)}'
Add '.muted{color:var(--muted)}.shots{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:12px}'
Add '.shot{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:8px}.shot img{max-width:100%;border:1px solid var(--line)}'
Add 'pre{white-space:pre-wrap;font-size:12px;max-height:400px;overflow:auto;background:var(--card);padding:8px}code{font-size:.92em}.missing{color:var(--fail)}'
Add '</style></head><body>'

# ---- header, summary, questions ------------------------------------------------------------------------------
Add "<section data-dev-report=""header"" data-dev-report-status=""$Status"">"
Add '<h1>API mode on Pi: QA report</h1>'
Add "<p class=""muted"">Mission #4070, phase 6 (section 7 of the mission document). Built $(Html ((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm'))) UTC from $($runData.Count) proof run(s): $(Html ($platforms -join ', ')).</p>"
Add '</section>'

Add '<section data-dev-report="summary"><h2>Summary</h2>'
foreach ($run in $runData) {
    $pass = @($run.steps | Where-Object { $_.result -eq 'PASS' }).Count
    $fail = @($run.steps | Where-Object { $_.result -eq 'FAIL' }).Count
    $word = if ($fail -eq 0 -and $run.exitCode -eq 0) { 'every check passed' } else { "$fail check(s) failed" }
    Add "<p><strong>$(Html $run.platform)</strong>: $word ($pass passed, $fail failed). Pi $(Html $run.pi), commit <code>$(Html (([string]$run.commit).Substring(0, 9)))</code>, tree $(Html $run.tree).</p>"
}
foreach ($m in $missing) { Add "<p><strong>$m</strong>: <span class=""missing"">no run yet, so nothing on $m is proven by this report.</span></p>" }
if ($allPassed) {
    Add "<p>On the platforms above, a user can start Pi in API mode, every prompt shows Delivered (Pi's own acknowledgement) or a reason, the Chat shows the conversation live with tool cards, Stop ends the turn as cancelled, take the wheel opens Pi's real terminal on the same conversation, and hand back returns to API mode with what was typed kept and the waiting prompts delivered.</p>"
} else {
    Add "<p class=""missing"">Not every check passed, so the flow is not proven as a whole: $(Html ($failedSteps -join '; ')).</p>"
}
Add "<p>Estimated Pi spend for these runs: $([math]::Round($spend, 2)) dollars.</p>"
Add '</section>'

Add '<section data-dev-report="questions"><h2>Questions</h2><p data-dev-report-no-questions>No questions - nothing needed from you.</p></section>'

# ---- what was built ------------------------------------------------------------------------------------------
Add '<section data-dev-report="detail"><h2>What was built</h2><table><tr><th>Phase</th><th>What</th></tr>'
foreach ($row in @(
    @('0. Event model', 'One session event model every agent dialect reports upward, and recorded Pi wire logs as test fixtures.'),
    @('1. Transport and Pi dialect', 'The API-mode transport (a long-lived child process speaking line-delimited JSON) and Pi''s own protocol (pi --mode rpc): delivery from Pi''s acknowledgement, Stop as abort, resume by session id.'),
    @('2. Gateway ruling', 'The Gateway folds the events into the display verdict, stores receipts, relays the live events, and owns the take the wheel and hand back verb.'),
    @('3. Live Chat', 'The Chat tab renders the live events, receipts and tool cards, once in client-core for the Cockpit and the phone.'),
    @('4. Take the wheel', 'Mode switching with one holder per conversation, the queue held while the user has the wheel, and the Terminal tab''s button.'),
    @('5. Choosing it', 'The New Session Connection row (Cockpit, phone, desktop), the roster tag, and --connection on spawn and list.'),
    @('6. Proof', 'The switch turned on (with a guard refusing API mode to a Director older than 2.23.0), the end-to-end proof script, and this report.'))) {
    Add "<tr><td>$(Html $row[0])</td><td>$(Html $row[1])</td></tr>"
}
Add '</table></section>'

# ---- the checks, per platform --------------------------------------------------------------------------------
foreach ($run in $runData) {
    Add "<section data-dev-report=""detail""><h2>The checks on $(Html $run.platform)</h2>"
    Add "<p class=""muted"">Run $(Html $run.stamp), started $(Html $(if ($run.startedUtc -is [datetime]) { $run.startedUtc.ToUniversalTime().ToString('yyyy-MM-dd HH:mm') + ' UTC' } else { [string]$run.startedUtc })). Evidence beside this report: <code>$(Html (Split-Path $run.folder -Leaf))/</code> wire.jsonl (every request and answer), director-pi.log, summary.txt, spend.txt.</p>"
    Add '<table><tr><th>Check</th><th>Result</th><th>Evidence</th></tr>'
    foreach ($s in $run.steps | Where-Object { $_.step -notlike 'Screen:*' }) {
        Add "<tr><td>$(Html $s.step)</td><td>$(Badge $s.result)</td><td>$(Html $s.detail)</td></tr>"
    }
    Add '</table>'
    if (@($run.notTaken).Count -gt 0) {
        Add '<p><strong>Not taken in this run:</strong></p><ul>'
        foreach ($n in $run.notTaken) { Add "<li>$(Html $n)</li>" }
        Add '</ul>'
    }
    Add '</section>'

    Add "<section data-dev-report=""detail""><h2>Screens on $(Html $run.platform)</h2><div class=""shots"">"
    foreach ($shot in $run.screens) {
        $word = if ($shot.ok) { 'PASS' } else { 'FAIL' }
        Add "<div class=""shot""><h3>$(Html $shot.name) - $(Html $shot.surface) $(Badge $word)</h3><p class=""muted"">$(Html $shot.detail)</p>$(Image $run.folder $shot.png)$(Dump $run.folder $shot.aria)</div>"
    }
    if (@($run.screens).Count -eq 0) { Add '<p class="missing">No screens were taken in this run.</p>' }
    Add '</div></section>'
}

# ---- failure cases proven by unit tests ----------------------------------------------------------------------
$unit = @($runData | Select-Object -First 1).unitTestProven
Add '<section data-dev-report="detail"><h2>Failure cases proven by unit tests</h2>'
Add '<p>A real run cannot provoke these without killing Pi from outside or racing its queue. Pi exiting mid-turn is held to the Delivery Lead''s ruling of 16 October 2026 in event-model.md: Delivered stays true (Pi acknowledged it) and the failed turn shows beside it; only a prompt Pi never acknowledged is Refused, "Pi exited before acknowledging".</p><table><tr><th>Case</th><th>Tests</th></tr>'
foreach ($u in $unit) { Add "<tr><td>$(Html $u.case)</td><td>$((@($u.tests) | ForEach-Object { "<code>$(Html $_)</code>" }) -join '<br>')</td></tr>" }
Add '</table></section>'

# ---- findings ------------------------------------------------------------------------------------------------
Add '<section data-dev-report="detail"><h2>Found along the way</h2>'
if ($findingBlocks.Count -eq 0) { Add '<p>None recorded.</p>' }
else { Add '<ul>'; foreach ($f in $findingBlocks) { Add "<li><strong>$(Html $f.Title)</strong> $(Html $f.Detail)</li>" }; Add '</ul>' }
Add '</section>'

# ---- evidence, and what was NOT proven -----------------------------------------------------------------------
Add '<section data-dev-report="evidence"><h2>What this rests on, and what it does not prove</h2>'
Add '<p>Each run built a Gateway and a slot-5 test Director from the tree named above, on a throwaway storage root, and drove a real Pi through that Gateway with cc-devthrottle and the Gateway''s own routes. The screens are the real Cockpit and phone app that Gateway served, photographed while the run held each state, and every screen check is a presence check of the words the state shows.</p>'
Add '<p><strong>Not proven by this report:</strong></p><ul>'
foreach ($m in $missing) { Add "<li>Anything on ${m}: there is no run.</li>" }
foreach ($f in $failedSteps) { Add "<li>$(Html $f): it failed (see its row).</li>" }
foreach ($line in @(
    'The hosted Gateway: these runs used a Gateway built from the tree. The hosted one gets API mode at the mission''s single deploy, after this report.',
    'Pi exiting mid-turn and Pi refusing a prompt in a real run: proven by the unit tests above, not by these runs.',
    'A real Director older than 2.23.0 refusing an API-mode create: proven by the Gateway''s tests with a fake Director, not against a real old build.',
    'That the released v2.23.0 Director gives the full experience: the version floor admits it, but it predates the event push, the wheel and the crash journal''s connection mode, which are on main under the same version number.',
    'Steering mid-turn, permission cards and other agents: out of the mission''s scope.')) {
    Add "<li>$(Html $line)</li>"
}
Add '</ul></section></body></html>'

$h.ToString() | Set-Content -Path $Out -Encoding utf8
$size = [math]::Round((Get-Item $Out).Length / 1MB, 1)
Write-Host "[build-api-mode-qa-report] wrote $Out ($size MB), status $Status, platforms: $($platforms -join ', ')$(if ($missing) { "; missing: $($missing -join ', ')" })"
