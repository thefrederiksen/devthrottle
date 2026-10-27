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
       Claims the release. Exactly one session can be releasing at all: the claim is the branch
       release-claim, created only if it does not exist, naming the version and the holder. A second session is refused and told
       who holds it. On 17 October two sessions fixed the same failure in parallel because nothing
       stopped a second release seat.

    2. Write docs/public/release-notes/vX.Y.Z.md in this checkout, covering the last tag up to the
       commit -Release names (run -Release without -Yes: it prints the last good commit and the
       commits to cover, then stops at the missing notes or at the question), and get the owner's
       sign-off on the wording.

    3. -Release -Version X.Y.Z [-Yes]
       Finds the last good commit G: the newest commit on main whose "CI result" check passed. Makes ONE commit on top of it that changes only the version in
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

# EVERY git and gh call in this script goes through Invoke-Native, and $LASTEXITCODE is the only
# signal that one failed. git writes ordinary lines to stderr when nothing is wrong - the CRLF
# warning from git add, the "remote:" hint after a push - and Windows PowerShell 5.1 turns each such
# line into a terminating NativeCommandError under $ErrorActionPreference = "Stop", although git
# exited 0. That stopped -Release -Yes halfway on v2.24.0, v2.28.0 and v2.32.0 (issues #4330, #4331).
# So the preference is lowered for exactly the call, stderr is merged in and taken out again, and
# each stderr line is shown (or dropped with -Quiet) instead of thrown. Standard output is returned.
# The names inside are unusual on purpose: the script block reads the caller's variables by name.
function Invoke-Native([scriptblock]$nativeCall, [switch]$Quiet) {
    $outerPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { $nativeLines = @(& $nativeCall 2>&1) }
    finally { $ErrorActionPreference = $outerPreference }
    foreach ($nativeLine in $nativeLines) {
        if ($nativeLine -is [System.Management.Automation.ErrorRecord]) {
            if (-not $Quiet) { Write-Host $nativeLine.ToString() -ForegroundColor DarkGray }
        }
        else { $nativeLine }
    }
}

# A native call whose exit code IS the answer (merge-base --is-ancestor, gh auth status).
function Get-NativeExitCode([scriptblock]$nativeCall) {
    Invoke-Native $nativeCall -Quiet | Out-Null
    return $LASTEXITCODE
}

$modes = @($Claim, $Unclaim, $Release) | Where-Object { $_ }
if ($modes.Count -ne 1) {
    Fail "Say exactly one of -Claim, -Unclaim or -Release." "Run: Get-Help .\scripts\new-release.ps1 -Detailed"
}
$newVersion = $Version.Trim()
if ($newVersion -notmatch '^\d+\.\d+\.\d+(-rc\d+)?$') {
    Fail "Invalid or missing -Version: '$newVersion'." "Expected X.Y.Z or X.Y.Z-rcN, for example -Version 2.24.0"
}
$tagName = "v$newVersion"
$branch  = "release/$tagName"
# ONE claim for releasing at all, not one per version: two seats releasing 2.24.0 and 2.24.1 at once
# would both hold a claim, and the second merge-back would conflict on Directory.Build.props.
$claimBranch = "release-claim"

# Who is releasing: the session when a session runs this, otherwise the person at the machine.
# USERNAME and COMPUTERNAME are Windows-only environment variables. On macOS and Linux both are
# empty, so the identity came out as " on ", git stripped the trailing space when it stored the
# claim subject, and the holder it read back could never equal a freshly computed one. That jammed
# the seat: the release was refused as held by somebody else, and -Unclaim refused it for the same
# reason, so the only way out was for the owner to delete the claim branch by hand. It happened
# while releasing v2.26.0 from the Mac. [Environment] answers on all three operating systems.
$identity = if ($env:CC_SESSION_ID) {
    "session $env:CC_SESSION_ID"
} else {
    "$([Environment]::UserName) on $([Environment]::MachineName)"
}
$identity = $identity.Trim()
if (-not $identity) {
    Fail "Could not work out who is releasing: both the user name and the machine name are empty." "The claim records who holds the release seat, and an empty holder can never be matched again. Set CC_SESSION_ID, or run where the operating system can name the user and the machine."
}

# --- Tools this needs ---
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "The GitHub CLI (gh) is not installed." "Install from https://cli.github.com/ then run: gh auth login"
}
if ((Get-NativeExitCode { gh auth status }) -ne 0) { Fail "The GitHub CLI is not signed in." "Run: gh auth login" }

Write-Host "Fetching origin..." -ForegroundColor Gray
Invoke-Native { git -C $repoRoot fetch --quiet --tags origin main }
if ($LASTEXITCODE -ne 0) { Fail "Could not fetch origin." }

$remoteTag = Invoke-Native { git -C $repoRoot ls-remote --tags origin $tagName }
if ($LASTEXITCODE -ne 0) { Fail "Could not read the tags on origin." }
if ($remoteTag) { Fail "Tag $tagName already exists on the remote." "That version has been released. Pick the next one." }

# The claim, read from the claim commit's subject ("release claim <tag> by <who>"), or null when nobody holds it.
function Get-Claim {
    $line = Invoke-Native { git -C $repoRoot ls-remote origin "refs/heads/$claimBranch" }
    if ($LASTEXITCODE -ne 0) { Fail "Could not read the claim branch $claimBranch on origin." }
    if (-not $line) { return $null }
    Invoke-Native { git -C $repoRoot fetch --quiet origin "refs/heads/${claimBranch}:refs/remotes/origin/$claimBranch" }
    if ($LASTEXITCODE -ne 0) { Fail "The claim branch $claimBranch exists but could not be fetched." }
    $subject = Invoke-Native { git -C $repoRoot log -1 --format=%s "origin/$claimBranch" }
    if ($LASTEXITCODE -ne 0) { Fail "Could not read the claim commit on $claimBranch." }
    if ($subject -notmatch '^release claim (\S+) by (.+)$') { Fail "The claim branch $claimBranch does not say who holds it: '$subject'." "The owner deletes branch $claimBranch." }
    return [pscustomobject]@{ Tag = $matches[1]; Holder = $matches[2] }
}

if ($Claim) {
    # ==================================== CLAIM THE RELEASE ====================================
    $held = Get-Claim
    if ($held) {
        if ($held.Holder -eq $identity -and $held.Tag -eq $tagName) { Write-Host "You already hold the claim on $tagName." -ForegroundColor Green; exit 0 }
        Fail "$($held.Holder) is releasing $($held.Tag)." "One release seat at a time. Ask the owner whether that seat is still releasing; if it is not, it runs -Unclaim, or the owner deletes branch $claimBranch."
    }
    $tree = Invoke-Native { git -C $repoRoot rev-parse "origin/main^{tree}" }
    if ($LASTEXITCODE -ne 0 -or -not $tree) { Fail "Could not read the tree of origin/main." }
    $claimCommit = Invoke-Native { git -C $repoRoot commit-tree $tree -p origin/main -m "release claim $tagName by $identity" }
    if ($LASTEXITCODE -ne 0 -or -not $claimCommit) { Fail "Could not make the claim commit." }
    # An empty lease means "only if the branch does not exist": two seats racing, exactly one wins.
    Invoke-Native { git -C $repoRoot push --quiet "--force-with-lease=refs/heads/${claimBranch}:" origin "${claimCommit}:refs/heads/$claimBranch" }
    if ($LASTEXITCODE -ne 0) {
        $held = Get-Claim
        Fail "Could not claim $tagName$(if ($held) { " - $($held.Holder) claimed $($held.Tag) first" })." "One release seat at a time."
    }
    Write-Host "Claimed $tagName for $identity." -ForegroundColor Green
    exit 0
}

function Remove-Claim {
    Invoke-Native { git -C $repoRoot push --quiet origin --delete $claimBranch }
    if ($LASTEXITCODE -ne 0) { Fail "Could not delete the claim branch $claimBranch." "Delete it by hand: git push origin --delete $claimBranch" }
}

if ($Unclaim) {
    $held = Get-Claim
    if (-not $held) { Write-Host "Nobody holds the release claim." -ForegroundColor Green; exit 0 }
    if ($held.Holder -ne $identity) { Fail "$($held.Holder) holds the claim on $($held.Tag), not you ($identity)." "Only the holder gives up a claim. The owner can delete branch $claimBranch." }
    Remove-Claim
    Write-Host "Gave up the claim on $($held.Tag)." -ForegroundColor Green
    exit 0
}

# ======================================== RELEASE ========================================

# --- 1. This session holds the claim, for this version ---
$held = Get-Claim
if (-not $held) { Fail "Nobody has claimed the release." "Claim it first: .\scripts\new-release.ps1 -Claim -Version $newVersion" }
if ($held.Holder -ne $identity) { Fail "$($held.Holder) holds the release claim, not you ($identity)." "One release seat at a time." }
if ($held.Tag -ne $tagName) { Fail "Your claim is for $($held.Tag), not $tagName." "Run -Unclaim, then -Claim -Version $newVersion." }

# --- 2. The last good commit: the newest commit on main whose "CI result" check passed ---
# "CI result" is the verdict of a run that TESTED the commit. The workflow run's own conclusion is not:
# a scheduled run that skips an already-tested commit, red or green, still ends "success". Successful
# runs only nominate candidates, newest first; each is confirmed by its check. The deploy gate reads the
# same check.
function Test-Green([string]$sha) {
    # The query holds no double quotes on purpose: Windows PowerShell 5.1 strips them from a native
    # command's arguments, which turned "success" into a jq function call. The count is made here instead.
    $conclusions = @(Invoke-Native { gh api "repos/$repoSlug/commits/$sha/check-runs?check_name=CI%20result&per_page=100" --jq '.check_runs[].conclusion' })
    if ($LASTEXITCODE -ne 0) { Fail "Could not read the CI result check of $sha from GitHub." }
    return (@($conclusions | Where-Object { $_ -eq 'success' }).Count -gt 0)
}
$candidates = Invoke-Native { gh api "repos/$repoSlug/actions/workflows/ci.yml/runs?branch=main&status=success&per_page=50" --jq ".workflow_runs[].head_sha" }
if ($LASTEXITCODE -ne 0) { Fail "Could not read the test runs from GitHub." }
$lastGood = $null
foreach ($sha in ($candidates | Select-Object -Unique)) {
    # A run for a commit that is no longer on main (a force-push, a missing object) is passed over.
    if ((Get-NativeExitCode { git -C $repoRoot merge-base --is-ancestor $sha origin/main }) -ne 0) { continue }
    if (Test-Green $sha) { $lastGood = $sha; break }
}
if (-not $lastGood) {
    Fail "No commit on main has a passing CI result among the last 50 successful runs." "Main has been red for a long time. Fix it first; a release is cut only from a tested commit."
}

# --- 3. The last release, and what is new since it ---
# The newest version tag reachable from main. A release tag sits on the release commit, a CHILD of its
# last good commit, merged back into main - so it is not an ancestor of that last good commit, and
# `git describe <last good>` would name the release before it.
$lastTag = @(Invoke-Native { git -C $repoRoot tag --merged origin/main --list "v*" --sort=-v:refname })[0]
if ($LASTEXITCODE -ne 0) { Fail "Could not list the release tags on origin/main." }
if (-not $lastTag) { Fail "No release tag is reachable from origin/main." }
$lastTagCommit = Invoke-Native { git -C $repoRoot rev-parse "$lastTag^{commit}" }
if ($LASTEXITCODE -ne 0) { Fail "Could not resolve $lastTag to a commit." }
if ((Get-NativeExitCode { git -C $repoRoot merge-base --is-ancestor $lastGood $lastTagCommit }) -eq 0) {
    Fail "Nothing new has passed a full test run since ${lastTag}: the last good commit $lastGood is already in it." "Wait for the next green run on main (every two hours when main has moved), or start one by hand: gh workflow run CI --repo $repoSlug --ref main"
}
$inRelease = @(Invoke-Native { git -C $repoRoot log --oneline --no-merges "$lastTagCommit..$lastGood" })
if ($LASTEXITCODE -ne 0) { Fail "Could not list the commits from $lastTag to $lastGood." }
# The newest green commit can be the merge that brought the last release back to main: not inside that
# tag's history, yet holding no new work.
if ($inRelease.Count -eq 0) {
    Fail "Nothing new has passed a full test run since ${lastTag}: $lastGood adds no commits to it." "Wait for the next green run on main, or start one by hand: gh workflow run CI --repo $repoSlug --ref main"
}
# Compared with the highest FINAL release only, so 2.24.0 may follow its own 2.24.0-rc1. Version sort puts
# a release candidate above its final, and an existing tag is refused at the start in any case.
$numeric = { param($v) [version](($v -replace '^v', '') -replace '-rc\d+$', '') }
$allTags = @(Invoke-Native { git -C $repoRoot tag --list "v*" --sort=-v:refname })
if ($LASTEXITCODE -ne 0) { Fail "Could not list the release tags." }
$highestFinal = @($allTags | Where-Object { $_ -notmatch '-rc\d+$' })[0]
if ($highestFinal -and (& $numeric $newVersion) -le (& $numeric $highestFinal)) {
    Fail "$tagName is not higher than the highest release $highestFinal." "Pick the next version."
}
$waiting = @(Invoke-Native { git -C $repoRoot log --oneline --no-merges "$lastGood..origin/main" })
if ($LASTEXITCODE -ne 0) { Fail "Could not list the commits on main after $lastGood." }

$notesRel  = "docs/public/release-notes/$tagName.md"
$notesPath = Join-Path $repoRoot "docs\public\release-notes\$tagName.md"

Write-Host ""
Write-Host "=== Release summary ===" -ForegroundColor Yellow
Write-Host "  Version    : $tagName"
Write-Host "  Last good  : $lastGood"
Write-Host "  Covers     : $lastTag .. $lastGood ($($inRelease.Count) commits)"
Write-Host "  Waiting    : $($waiting.Count) newer commits on main, not yet tested green - they go in the next release"
Write-Host "  Claimed by : $($held.Holder)"
Write-Host ""
Write-Host "The notes must cover exactly these commits, $lastTag .. ${lastGood}:" -ForegroundColor Gray
$inRelease | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
Write-Host ""

# --- 4. The written notes. The workflow publishes this file verbatim and stops without it. ---
if (-not (Test-Path $notesPath)) {
    Fail "No written release notes for $tagName yet." "Write them at $notesPath, covering the commits listed above, and get the owner's sign-off. The workflow publishes that file as the release page and refuses to invent a substitute."
}
$notesChars = ((Get-Content $notesPath -Raw) -replace '\s', '').Length
if ($notesChars -lt 200) {
    Fail "$notesRel has only $notesChars non-whitespace characters." "That is a placeholder, not release notes. The workflow applies the same floor."
}
Write-Host "  Notes      : $notesRel ($notesChars characters)"
Write-Host ""

if (-not $Yes) {
    $confirm = Read-Host "Tag and release $tagName from $lastGood? This cannot be undone. (Y/N)"
    if ($confirm -ne 'Y' -and $confirm -ne 'y') { Write-Host "Aborted. Nothing was changed." -ForegroundColor Yellow; exit 0 }
}

# --- 5. One commit on the last good commit: the version and the notes, nothing else ---
$work = Join-Path ([System.IO.Path]::GetTempPath()) "devthrottle-release-$tagName-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
Invoke-Native { git -C $repoRoot worktree add --quiet --detach $work $lastGood }
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

    Invoke-Native { git -C $work add -- Directory.Build.props $notesRel }
    if ($LASTEXITCODE -ne 0) { Fail "Could not stage the version and the notes." }
    Invoke-Native { git -C $work commit --quiet -m "release: $tagName" }
    if ($LASTEXITCODE -ne 0) { Fail "Could not commit the version and the notes." }
    $releaseCommit = Invoke-Native { git -C $work rev-parse HEAD }
    if ($LASTEXITCODE -ne 0 -or -not $releaseCommit) { Fail "Could not read the release commit." }
}
finally {
    Invoke-Native { git -C $repoRoot worktree remove --force $work }
    if ($LASTEXITCODE -ne 0) { Fail "Could not remove the release worktree at $work." "Nothing is pushed yet. Remove it by hand: git worktree remove --force $work" }
}

# --- 6. Prove the release commit is the last good commit plus those two files, before anything is pushed ---
$parent = Invoke-Native { git -C $repoRoot rev-parse "$releaseCommit^" }
if ($LASTEXITCODE -ne 0) { Fail "Could not read the parent of the release commit $releaseCommit." }
if ($parent -ne $lastGood) { Fail "The release commit's parent is $parent, not the last good commit $lastGood." }
$changed = @(Invoke-Native { git -C $repoRoot diff --name-only $lastGood $releaseCommit })
if ($LASTEXITCODE -ne 0) { Fail "Could not list what the release commit changes." }
$unexpected = @($changed | Where-Object { $_ -ne "Directory.Build.props" -and $_ -ne $notesRel })
if ($unexpected.Count -gt 0) { Fail "The release commit changes more than the version and the notes:`n$($unexpected -join "`n")" }

# --- 7. Push the branch, then the tag ---
Invoke-Native { git -C $repoRoot push --quiet origin "${releaseCommit}:refs/heads/$branch" }
if ($LASTEXITCODE -ne 0) { Fail "Could not push $branch." "Nothing is tagged, so nothing is released." }
Invoke-Native { git -C $repoRoot tag $tagName $releaseCommit }
if ($LASTEXITCODE -ne 0) { Fail "Could not create the tag." }
Invoke-Native { git -C $repoRoot push --quiet origin $tagName }
if ($LASTEXITCODE -ne 0) { Fail "Could not push the tag." "The tag exists locally only. Run: git push origin $tagName" }
Write-Host ""
Write-Host "Tagged $tagName on $releaseCommit. The release workflow is building it:" -ForegroundColor Green
Write-Host "  https://github.com/$repoSlug/actions/workflows/release.yml" -ForegroundColor Cyan

# --- 8. Bring the notes and the version back to main with a merge commit, so the tag stays on main's history ---
$prBody = "Brings the $tagName release commit back to main: the version in Directory.Build.props and $notesRel. The release was tagged on this commit, which is the last good commit $lastGood plus these two files.`n`nMerged with a merge commit, never squashed, so the tagged commit stays reachable from main."
Invoke-Native { gh pr create --repo $repoSlug --base main --head $branch --title "release: $tagName - notes and version back to main" --body $prBody } | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "The release is tagged, but the pull request back to main could not be opened." "Open it by hand from $branch into main and merge it with a merge commit (never squash)." }
Invoke-Native { gh pr merge $branch --repo $repoSlug --merge --delete-branch }
if ($LASTEXITCODE -ne 0) { Fail "The release is tagged, but the pull request back to main did not merge." "Read why on the pull request, resolve it, and merge it with a merge commit (never squash)." }

# --- 9. Give up the claim ---
Remove-Claim

Write-Host ""
Write-Host "Released $tagName." -ForegroundColor Green
Write-Host "  Release  : https://github.com/$repoSlug/releases/tag/$tagName" -ForegroundColor Cyan
Write-Host "  Download : https://devthrottle.blob.core.windows.net/download/devthrottle-setup-win-x64.exe" -ForegroundColor Cyan
Write-Host ""
Write-Host "Watch the build to the end: gh run list --repo $repoSlug --workflow release.yml --limit 1, then gh run watch <that id>" -ForegroundColor Gray
Write-Host ""
exit 0
