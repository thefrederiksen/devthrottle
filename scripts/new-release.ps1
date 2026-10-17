#Requires -Version 5.1
<#
.SYNOPSIS
    Releases DevThrottle from the LAST GOOD COMMIT: the newest commit on main with a green full test
    run. No candidate is cut, nothing is tested at release time, and a release takes minutes.

.DESCRIPTION
    THE RULE (the owner, 17 October 2026: "Why the hell can we not just release our software when we
    say, and then an hour later we have a release?"). The full test suite runs on main on its own,
    every two hours when main has moved (.github/workflows/ci.yml). Every commit it passes is a commit
    we can ship. A release is a version tag on the newest of them, plus the notes. Nothing else.

    The old flow merged a version bump and the notes into main, which made a NEW commit, which then
    had to be tested from scratch; any red meant a fix, another commit and another full run. v2.23.0
    took fifteen hours that way while its code had already passed every test.

    THREE STEPS, each one command:

    1. -Claim -Version X.Y.Z
       Claims the release. Exactly one session can hold a version's claim: the claim is a branch,
       release-claim/vX.Y.Z, created only if it does not exist. A second session is refused and told
       who holds it. On 17 October two sessions fixed the same failure in parallel because nothing
       stopped a second release seat.

    2. Write docs/public/release-notes/vX.Y.Z.md in this checkout, covering the last tag up to the
       commit -Release will name (run -Release without -Yes to see it first), and get the owner's
       sign-off on the wording.

    3. -Release -Version X.Y.Z [-Yes]
       Finds the last good commit G. Makes ONE commit on top of it that changes only the version in
       Directory.Build.props and adds the notes file - nothing a test could see. Pushes it as branch
       release/vX.Y.Z, tags it, and pushes the tag; the release workflow builds and publishes. Then
       it merges the branch back into main with a merge commit, so main gets the notes and the
       version, and the tagged commit stays reachable from main. Finally it gives up the claim.

    The release workflow checks the same thing again before it builds: the tagged commit's parent
    must have a green full run and the tagged commit may change only those two files.

    Work merged to main after G is not in this release. It goes in the next one.

.PARAMETER Claim
    Claim the release of -Version for this session.

.PARAMETER Unclaim
    Give up the claim on -Version without releasing.

.PARAMETER Release
    Release -Version from the last good commit.

.PARAMETER Version
    The version, X.Y.Z or X.Y.Z-rcN.

.PARAMETER Yes
    Do not ask before pushing the tag. For a release seat, after the owner has signed off the notes
    and said to release.

.EXAMPLE
    .\scripts\new-release.ps1 -Claim -Version 2.24.0
    .\scripts\new-release.ps1 -Release -Version 2.24.0          <- shows the last good commit, asks
    .\scripts\new-release.ps1 -Release -Version 2.24.0 -Yes
#>
param(
    [switch]$Claim,
    [switch]$Unclaim,
    [switch]$Release,
    [string]$Version = "",
    [switch]$Yes
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$repoSlug = "thefrederiksen/devthrottle"

function Fail($message, $howToFix) {
    Write-Host ""
    Write-Host "ERROR: $message" -ForegroundColor Red
    if ($howToFix) { Write-Host $howToFix -ForegroundColor Yellow }
    Write-Host ""
    exit 1
}

$modes = @($Claim, $Unclaim, $Release) | Where-Object { $_ }
if ($modes.Count -ne 1) {
    Fail "Say exactly one of -Claim, -Unclaim or -Release." "Run: Get-Help .\scripts\new-release.ps1 -Detailed"
}
$newVersion = $Version.Trim()
if ($newVersion -notmatch '^\d+\.\d+\.\d+(-rc\d+)?$') {
    Fail "Invalid or missing -Version: '$newVersion'." "Expected X.Y.Z or X.Y.Z-rcN, for example -Version 2.24.0"
}
$tagName     = "v$newVersion"
$claimBranch = "release-claim/$tagName"
$branch      = "release/$tagName"

# Who is releasing: the session when a session runs this, otherwise the person at the machine.
$identity = if ($env:CC_SESSION_ID) { "session $env:CC_SESSION_ID" } else { "$env:USERNAME on $env:COMPUTERNAME" }

# --- Tools this needs ---
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "The GitHub CLI (gh) is not installed." "Install from https://cli.github.com/ then run: gh auth login"
}
gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "The GitHub CLI is not signed in." "Run: gh auth login" }

Write-Host "Fetching origin..." -ForegroundColor Gray
git -C $repoRoot fetch --quiet --tags origin main
if ($LASTEXITCODE -ne 0) { Fail "Could not fetch origin." }

if (git -C $repoRoot ls-remote --tags origin $tagName) { Fail "Tag $tagName already exists on the remote." "That version has been released. Pick the next one." }

# The claim's holder, read from the claim commit's subject, or null when nobody holds it.
function Get-ClaimHolder {
    $line = git -C $repoRoot ls-remote origin "refs/heads/$claimBranch"
    if (-not $line) { return $null }
    git -C $repoRoot fetch --quiet origin "refs/heads/${claimBranch}:refs/remotes/origin/$claimBranch"
    if ($LASTEXITCODE -ne 0) { Fail "The claim $claimBranch exists but could not be fetched." }
    $subject = git -C $repoRoot log -1 --format=%s "origin/$claimBranch"
    return ($subject -replace "^release claim $([regex]::Escape($tagName)) by ", "")
}

if ($Claim) {
    # ==================================== CLAIM THE RELEASE ====================================
    $holder = Get-ClaimHolder
    if ($holder) {
        if ($holder -eq $identity) { Write-Host "You already hold the claim on $tagName." -ForegroundColor Green; exit 0 }
        Fail "$tagName is already claimed by $holder." "One release seat at a time. Ask the owner whether that seat is still releasing; if it is not, it runs -Unclaim, or the owner deletes branch $claimBranch."
    }
    $tree = git -C $repoRoot rev-parse "origin/main^{tree}"
    $claimCommit = git -C $repoRoot commit-tree $tree -p origin/main -m "release claim $tagName by $identity"
    if ($LASTEXITCODE -ne 0 -or -not $claimCommit) { Fail "Could not make the claim commit." }
    # An empty lease means "only if the branch does not exist": two seats racing, exactly one wins.
    git -C $repoRoot push --quiet "--force-with-lease=refs/heads/${claimBranch}:" origin "${claimCommit}:refs/heads/$claimBranch"
    if ($LASTEXITCODE -ne 0) {
        $holder = Get-ClaimHolder
        Fail "Could not claim $tagName$(if ($holder) { " - $holder claimed it first" })." "One release seat at a time."
    }
    Write-Host "Claimed $tagName for $identity." -ForegroundColor Green
    exit 0
}

function Remove-Claim {
    git -C $repoRoot push --quiet origin --delete $claimBranch
    if ($LASTEXITCODE -ne 0) { Fail "Could not delete the claim branch $claimBranch." "Delete it by hand: git push origin --delete $claimBranch" }
}

if ($Unclaim) {
    $holder = Get-ClaimHolder
    if (-not $holder) { Write-Host "Nobody holds the claim on $tagName." -ForegroundColor Green; exit 0 }
    if ($holder -ne $identity) { Fail "$tagName is claimed by $holder, not by you ($identity)." "Only the holder gives up a claim. The owner can delete branch $claimBranch." }
    Remove-Claim
    Write-Host "Gave up the claim on $tagName." -ForegroundColor Green
    exit 0
}

# ======================================== RELEASE ========================================

# --- 1. This session holds the claim ---
$holder = Get-ClaimHolder
if (-not $holder) { Fail "Nobody has claimed $tagName." "Claim it first: .\scripts\new-release.ps1 -Claim -Version $newVersion" }
if ($holder -ne $identity) { Fail "$tagName is claimed by $holder, not by you ($identity)." "One release seat at a time." }

# --- 2. The written notes. The workflow publishes this file verbatim and stops without it. ---
$notesRel  = "docs/public/release-notes/$tagName.md"
$notesPath = Join-Path $repoRoot "docs\public\release-notes\$tagName.md"
if (-not (Test-Path $notesPath)) {
    Fail "No written release notes for $tagName." "Expected: $notesPath`n`nWrite them in this checkout first. The workflow publishes that file as the release page and refuses to invent a substitute."
}
$notesChars = ((Get-Content $notesPath -Raw) -replace '\s', '').Length
if ($notesChars -lt 200) {
    Fail "$notesRel has only $notesChars non-whitespace characters." "That is a placeholder, not release notes. The workflow applies the same floor."
}

# --- 3. The last good commit: the newest commit on main that a full test run passed ---
# Runs come back newest first. A run for a commit that is no longer on main (a force-push) is skipped.
$greenShas = gh api "repos/$repoSlug/actions/workflows/ci.yml/runs?branch=main&status=success&per_page=50" --jq ".workflow_runs[].head_sha"
if ($LASTEXITCODE -ne 0) { Fail "Could not read the test runs from GitHub." }
$lastGood = $null
foreach ($sha in $greenShas) {
    git -C $repoRoot merge-base --is-ancestor $sha origin/main 2>$null
    if ($LASTEXITCODE -eq 0) { $lastGood = $sha; break }
}
if (-not $lastGood) {
    Fail "No commit on main has a green full test run among the last 50 runs." "Main has been red for a long time. Fix it first; a release is cut only from a tested commit."
}
$lastTag = git -C $repoRoot describe --tags --abbrev=0 --match "v*" $lastGood
$inRelease = @(git -C $repoRoot log --oneline --first-parent "$lastTag..$lastGood")
if ($inRelease.Count -eq 0) {
    Fail "Nothing new has passed a full test run since $lastTag. The last good commit $lastGood is already released." "Wait for the next green run on main (every two hours when main has moved), or start one by hand: gh workflow run CI --repo $repoSlug --ref main"
}
$numeric = { param($v) [version](($v -replace '^v', '') -replace '-rc\d+$', '') }
if ((& $numeric $newVersion) -le (& $numeric $lastTag)) {
    Fail "$tagName is not higher than the last release $lastTag." "Pick the next version."
}
$waiting   = @(git -C $repoRoot log --oneline --first-parent "$lastGood..origin/main")

Write-Host ""
Write-Host "=== Release summary ===" -ForegroundColor Yellow
Write-Host "  Version    : $tagName"
Write-Host "  Last good  : $lastGood"
Write-Host "  Covers     : $lastTag .. $lastGood ($($inRelease.Count) commits on main)"
Write-Host "  Waiting    : $($waiting.Count) newer commits on main, not yet tested green - they go in the next release"
Write-Host "  Notes      : $notesRel ($notesChars characters)"
Write-Host "  Claimed by : $holder"
Write-Host ""
Write-Host "The notes must cover exactly $lastTag .. ${lastGood}:" -ForegroundColor Gray
$inRelease | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
Write-Host ""

if (-not $Yes) {
    $confirm = Read-Host "Tag and release $tagName from $lastGood? This cannot be undone. (Y/N)"
    if ($confirm -ne 'Y' -and $confirm -ne 'y') { Write-Host "Aborted. Nothing was changed." -ForegroundColor Yellow; exit 0 }
}

# --- 4. One commit on the last good commit: the version and the notes, nothing else ---
$work = Join-Path ([System.IO.Path]::GetTempPath()) "devthrottle-release-$tagName-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
git -C $repoRoot worktree add --quiet --detach $work $lastGood
if ($LASTEXITCODE -ne 0) { Fail "Could not make a worktree at $lastGood." }
try {
    $propsAt = Join-Path $work "Directory.Build.props"
    $propsText = [System.IO.File]::ReadAllText($propsAt)
    $versionTag = [regex]'<Version>[^<]*</Version>'
    if ($versionTag.Matches($propsText).Count -ne 1) { Fail "Directory.Build.props at $lastGood does not hold exactly one <Version>." }
    [System.IO.File]::WriteAllText($propsAt, $versionTag.Replace($propsText, "<Version>$newVersion</Version>", 1))
    $notesDir = Join-Path $work "docs\public\release-notes"
    New-Item -ItemType Directory -Force -Path $notesDir | Out-Null
    Copy-Item -Path $notesPath -Destination (Join-Path $notesDir "$tagName.md") -Force

    git -C $work add -- Directory.Build.props $notesRel
    git -C $work commit --quiet -m "release: $tagName"
    if ($LASTEXITCODE -ne 0) { Fail "Could not commit the version and the notes." }
    $releaseCommit = git -C $work rev-parse HEAD
}
finally {
    git -C $repoRoot worktree remove --force $work
}

# --- 5. Prove the release commit is the last good commit plus those two files, before anything is pushed ---
$parent = git -C $repoRoot rev-parse "$releaseCommit^"
if ($parent -ne $lastGood) { Fail "The release commit's parent is $parent, not the last good commit $lastGood." }
$changed = @(git -C $repoRoot diff --name-only $lastGood $releaseCommit)
$unexpected = @($changed | Where-Object { $_ -ne "Directory.Build.props" -and $_ -ne $notesRel })
if ($unexpected.Count -gt 0) { Fail "The release commit changes more than the version and the notes:`n$($unexpected -join "`n")" }

# --- 6. Push the branch, then the tag ---
git -C $repoRoot push --quiet origin "${releaseCommit}:refs/heads/$branch"
if ($LASTEXITCODE -ne 0) { Fail "Could not push $branch." "Nothing is tagged, so nothing is released." }
git -C $repoRoot tag $tagName $releaseCommit
if ($LASTEXITCODE -ne 0) { Fail "Could not create the tag." }
git -C $repoRoot push --quiet origin $tagName
if ($LASTEXITCODE -ne 0) { Fail "Could not push the tag." "The tag exists locally only. Run: git push origin $tagName" }
Write-Host ""
Write-Host "Tagged $tagName on $releaseCommit. The release workflow is building it:" -ForegroundColor Green
Write-Host "  https://github.com/$repoSlug/actions/workflows/release.yml" -ForegroundColor Cyan

# --- 7. Bring the notes and the version back to main with a merge commit, so the tag stays on main's history ---
$prBody = "Brings the $tagName release commit back to main: the version in Directory.Build.props and $notesRel. The release was tagged on this commit, which is the last good commit $lastGood plus these two files.`n`nMerged with a merge commit, never squashed, so the tagged commit stays reachable from main."
gh pr create --repo $repoSlug --base main --head $branch --title "release: $tagName - notes and version back to main" --body $prBody | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "The release is tagged, but the pull request back to main could not be opened." "Open it by hand from $branch into main and merge it with a merge commit (never squash)." }
gh pr merge $branch --repo $repoSlug --merge --delete-branch
if ($LASTEXITCODE -ne 0) { Fail "The release is tagged, but the pull request back to main did not merge." "Read why on the pull request, resolve it, and merge it with a merge commit (never squash)." }

# --- 8. Give up the claim ---
Remove-Claim

Write-Host ""
Write-Host "Released $tagName." -ForegroundColor Green
Write-Host "  Release  : https://github.com/$repoSlug/releases/tag/$tagName" -ForegroundColor Cyan
Write-Host "  Download : https://stdevthrottledl.blob.core.windows.net/download/latest/devthrottle-setup-win-x64.exe" -ForegroundColor Cyan
Write-Host ""
Write-Host "Watch the build to the end: gh run list --repo $repoSlug --workflow release.yml --limit 1, then gh run watch <that id>" -ForegroundColor Gray
Write-Host ""
exit 0
