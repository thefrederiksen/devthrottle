# Proof test for issue #232: deploy-cockpit.ps1 must ship the Blazor scoped-CSS bundle
# (wwwroot\cc-director-cockpit.styles.css + .br/.gz) so a .razor.css change deploys fresh,
# not stale.
#
# Hermetic: builds a fake "publish" stage and a fake "live target" under the temp dir, then
# exercises the REAL Sync-CockpitWwwroot function from scripts\deploy-cockpit.ps1 (loaded via
# -DefineOnly, so the live fleet is never touched). Asserts:
#   1. The scoped bundle + .br/.gz land in the target and hash-match the stage (AC1).
#   2. A CHANGED scoped bundle (the .razor.css edit) overwrites a STALE one in the live target,
#      so the change is reflected without a manual full-folder swap (AC2).
#   3. The old miss is gone: a previous run only copied app.css; the bundle now arrives too.
#
# ASCII only. Run: powershell -ExecutionPolicy Bypass -File scripts\test\test-deploy-cockpit-css.ps1
# Exit 0 = PASS, non-zero = FAIL.

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$deployScript = Join-Path $repoRoot 'scripts\deploy-cockpit.ps1'
if (-not (Test-Path $deployScript)) { Write-Host "FAIL: deploy script not found at $deployScript"; exit 1 }

# Load Sync-CockpitWwwroot from the real deploy script WITHOUT running the deploy.
. $deployScript -DefineOnly
if (-not (Get-Command Sync-CockpitWwwroot -ErrorAction SilentlyContinue)) {
  Write-Host "FAIL: Sync-CockpitWwwroot was not defined by the deploy script"; exit 1
}

$failures = New-Object System.Collections.Generic.List[string]
function Assert-True([bool] $cond, [string] $msg) {
  if ($cond) { Write-Host "  PASS: $msg" } else { Write-Host "  FAIL: $msg"; $script:failures.Add($msg) }
}
function Get-Hash([string] $path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("cc-232-" + [Guid]::NewGuid().ToString('N'))
$stageWww  = Join-Path $work 'stage\wwwroot'
$targetWww = Join-Path $work 'target\wwwroot'

try {
  # --- Arrange: a published wwwroot that mirrors the real shape ---
  New-Item -ItemType Directory -Force (Join-Path $stageWww 'js')   | Out-Null
  New-Item -ItemType Directory -Force (Join-Path $stageWww 'lib')  | Out-Null
  New-Item -ItemType Directory -Force (Join-Path $stageWww '_framework') | Out-Null
  Set-Content -LiteralPath (Join-Path $stageWww 'app.css') -Value 'body{color:#111}' -Encoding ascii
  Set-Content -LiteralPath (Join-Path $stageWww 'js\app.js') -Value 'console.log("v2")' -Encoding ascii
  Set-Content -LiteralPath (Join-Path $stageWww 'lib\xterm.css') -Value '.xterm{}' -Encoding ascii
  Set-Content -LiteralPath (Join-Path $stageWww '_framework\blazor.web.js') -Value '// blazor v2' -Encoding ascii
  # The scoped bundle reflecting a NEW Fleet.razor.css (the AC2 scenario), plus siblings.
  $newBundle = '.b-fleet[b-xyz]{background:#0a84ff}'   # the "after" styling
  Set-Content -LiteralPath (Join-Path $stageWww 'cc-director-cockpit.styles.css') -Value $newBundle -Encoding ascii
  Set-Content -LiteralPath (Join-Path $stageWww 'cc-director-cockpit.styles.css.br') -Value 'BR-NEW' -Encoding ascii
  Set-Content -LiteralPath (Join-Path $stageWww 'cc-director-cockpit.styles.css.gz') -Value 'GZ-NEW' -Encoding ascii

  # --- Arrange: a live target with the OLD bundle (simulating the previous deploy where only
  #     app.css/js were copied, so the scoped bundle was stale) ---
  New-Item -ItemType Directory -Force $targetWww | Out-Null
  $oldBundle = '.b-fleet[b-old]{background:#333}'      # the "before" stale styling
  Set-Content -LiteralPath (Join-Path $targetWww 'cc-director-cockpit.styles.css') -Value $oldBundle -Encoding ascii
  $beforeHash = Get-Hash (Join-Path $targetWww 'cc-director-cockpit.styles.css')

  # --- Act: run the real mirror function (what the deploy now does) ---
  Sync-CockpitWwwroot -StageWwwroot $stageWww -TargetWwwroot $targetWww | Out-Null

  # --- Assert AC1: every static asset, including the scoped bundle, hash-matches publish ---
  $assets = @(
    'cc-director-cockpit.styles.css',
    'cc-director-cockpit.styles.css.br',
    'cc-director-cockpit.styles.css.gz',
    'app.css',
    'js\app.js',
    'lib\xterm.css',
    '_framework\blazor.web.js'
  )
  foreach ($rel in $assets) {
    $s = Join-Path $stageWww  $rel
    $t = Join-Path $targetWww $rel
    Assert-True (Test-Path $t) "deployed: $rel"
    if (Test-Path $t) {
      Assert-True ((Get-Hash $s) -eq (Get-Hash $t)) "hash matches publish: $rel"
    }
  }

  # --- Assert AC2: the stale bundle was overwritten by the new one (change reflected live) ---
  $afterHash = Get-Hash (Join-Path $targetWww 'cc-director-cockpit.styles.css')
  Assert-True ($afterHash -ne $beforeHash) "scoped bundle changed in live target (stale -> fresh)"
  $live = Get-Content -Raw -LiteralPath (Join-Path $targetWww 'cc-director-cockpit.styles.css')
  Assert-True ($live.Trim() -eq $newBundle) "live bundle now equals the new .razor.css output"

  # --- Regression guard: the exact bug. The bundle must NOT still equal the old stale content. ---
  Assert-True ($live.Trim() -ne $oldBundle) "regression: stale scoped bundle no longer served"
}
finally {
  if (Test-Path $work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

Write-Host ''
if ($failures.Count -eq 0) {
  Write-Host "RESULT: PASS - all assertions green; scoped-CSS bundle now deploys fresh (issue #232)."
  exit 0
} else {
  Write-Host ("RESULT: FAIL - {0} assertion(s) failed:" -f $failures.Count)
  foreach ($f in $failures) { Write-Host "  - $f" }
  exit 1
}
