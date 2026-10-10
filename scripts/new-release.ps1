#Requires -Version 5.1
<#
.SYNOPSIS
    Cuts a release in two steps: freeze the candidate, then - once the release gate
    has passed on it - tag it.

.DESCRIPTION
    STEP 1, no arguments: FREEZE THE CANDIDATE.
        Bumps Directory.Build.props and commits it together with the written notes
        (docs/public/release-notes/vX.Y.Z.md) in ONE pull request, merges it, and
        prints the merge commit. That commit is the release candidate. It does NOT
        tag. The notes are never edited after this; work merged later waits for
        the next release.

    Then run the release gate ONCE on the candidate, in a worktree detached at it
    (the release-manager skill, Step 9):
        .\scripts\test-local.ps1 -Parked -Configuration Release

    STEP 2, -Tag <candidate>: TAG THE GATED CANDIDATE.
        Refuses unless scripts\assert-gated.ps1 accepts the candidate, then tags it
        and pushes the tag. The tag goes on the candidate even when main has moved
        on. The person releasing runs this step.

    WHY TWO STEPS
    -------------
    The release workflow runs no tests and a pushed tag cannot be un-pushed, so the
    release gate must run on the exact commit that is tagged. This script used to
    merge and tag in one go, with no gate between them. And because the notes and
    the bump were not frozen together, every notes edit made a new commit that
    voided any gate run already under way: v2.18.0's notes were rewritten four times.

    WHY A PULL REQUEST INSTEAD OF PUSHING TO MAIN
    ---------------------------------------------
    main is protected by a ruleset that requires a pull request. An older version
    of this script pushed main and then the tag; the branch push was rejected and
    the tag push succeeded, which left v1.9.2 pointing at a commit that was not on
    main. The tag is created only for a commit proven to be on origin/main.

    The product version lives in EXACTLY ONE file: Directory.Build.props at the
    repo root (see docs/architecture/VERSIONING.md).

.PARAMETER Version
    Step 1: the new version (X.Y.Z or X.Y.Z-rcN). Without it, step 1 asks, offering
    the next patch version.

.PARAMETER Yes
    Step 1: do not ask "Go?". For a release seat, which runs non-interactively; the
    human has already signed off the notes. The -Tag step has no such switch: the
    person releasing confirms the tag push themselves.

.PARAMETER Tag
    The candidate commit to tag (the merge commit step 1 printed).

.EXAMPLE
    .\scripts\new-release.ps1
    Current version: 1.9.2
    New version [1.9.3]:            <- press Enter to take it

.EXAMPLE
    .\scripts\new-release.ps1 -Version 2.18.0 -Yes

.EXAMPLE
    .\scripts\new-release.ps1 -Tag 1a2b3c4d
#>
param(
    [string]$Version = "",
    [switch]$Yes,
    [string]$Tag = ""
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

# --- Tools this needs ---
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "The GitHub CLI (gh) is not installed." "It is required because main only accepts changes through a pull request.`nInstall from https://cli.github.com/ then run: gh auth login"
}
gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "The GitHub CLI is not signed in." "Run: gh auth login" }

Write-Host "Fetching origin..." -ForegroundColor Gray
git -C $repoRoot fetch --quiet --tags origin main
if ($LASTEXITCODE -ne 0) { Fail "Could not fetch origin." }

if ($Tag) {
    # ============================== STEP 2: TAG THE GATED CANDIDATE ==============================
    $candidate = git -C $repoRoot rev-parse --verify --quiet "$Tag^{commit}"
    if (-not $candidate) { Fail "'$Tag' is not a commit in this repository." "Pass the candidate commit that step 1 printed." }

    git -C $repoRoot merge-base --is-ancestor $candidate origin/main
    if ($LASTEXITCODE -ne 0) {
        Fail "$candidate is not on origin/main." "A release is tagged on the merge commit of its candidate pull request, which is on main."
    }

    [xml]$candidateProps = (git -C $repoRoot show "${candidate}:Directory.Build.props") -join "`n"
    $newVersion = $candidateProps.SelectSingleNode("//Version").InnerText
    if (-not $newVersion) { Fail "Could not read <Version> from Directory.Build.props at $candidate." }
    $tagName = "v$newVersion"

    if (git -C $repoRoot tag -l $tagName)                  { Fail "Tag $tagName already exists locally." }
    if (git -C $repoRoot ls-remote --tags origin $tagName) { Fail "Tag $tagName already exists on the remote." "That version has been released." }

    # The notes must be IN the candidate: the workflow publishes the file at the tag and stops without it.
    $notesRel = "docs/public/release-notes/$tagName.md"
    if (-not (git -C $repoRoot ls-tree --name-only $candidate -- $notesRel)) {
        Fail "$notesRel is not in the candidate $candidate." "The notes and the bump merge in one pull request. Run step 1 again."
    }
    # A candidate is the commit that froze the notes: its own change touches them. A later commit that
    # merely carries an older notes file is not a candidate - work merged between the two is not in the notes.
    if (-not (git -C $repoRoot diff-tree --no-commit-id --name-only -r $candidate -- $notesRel)) {
        Fail "$candidate does not change $notesRel, so it is not a candidate." "Tag the merge commit of the pull request that froze the notes - the one step 1 printed, or the notes-correction pull request that replaced it."
    }
    $notesAtCandidate = git -C $repoRoot show "${candidate}:$notesRel"
    $candidateNotesChars = (($notesAtCandidate -join "") -replace '\s', '').Length
    if ($candidateNotesChars -lt 200) {
        Fail "$notesRel at $candidate has only $candidateNotesChars non-whitespace characters." "That is a placeholder, not release notes. The workflow applies the same floor."
    }

    # The release gate must have passed on exactly this commit.
    $assertGated = Join-Path $PSScriptRoot "assert-gated.ps1"
    if (-not (Test-Path $assertGated)) {
        Fail "scripts\assert-gated.ps1 is not in this checkout, so nothing can prove $candidate was gated." "Do not tag. Update this checkout to a main that has assert-gated.ps1, then run this again."
    }
    & $assertGated $candidate
    if ($LASTEXITCODE -ne 0) {
        Fail "assert-gated.ps1 refused $candidate." "Run the release gate on the candidate first:`n  git worktree add ../devthrottle-gate-$tagName --detach $candidate`n  cd ../devthrottle-gate-$tagName`n  .\scripts\test-local.ps1 -Parked -Configuration Release"
    }

    Write-Host ""
    Write-Host "=== Tag summary ===" -ForegroundColor Yellow
    Write-Host "  Tag       : $tagName"
    Write-Host "  Candidate : $candidate"
    Write-Host "  Notes     : $notesRel ($candidateNotesChars characters)"
    Write-Host ""
    $confirm = Read-Host "Push the tag? This cannot be undone. (Y/N)"
    if ($confirm -ne 'Y' -and $confirm -ne 'y') { Write-Host "Aborted. Nothing was tagged." -ForegroundColor Yellow; exit 0 }

    Write-Host "Tagging $tagName on $candidate..." -ForegroundColor Cyan
    git -C $repoRoot tag $tagName $candidate
    if ($LASTEXITCODE -ne 0) { Fail "Could not create the tag." }
    git -C $repoRoot push origin $tagName
    if ($LASTEXITCODE -ne 0) { Fail "Could not push the tag." "The tag exists locally only. Run: git push origin $tagName" }

    Write-Host ""
    Write-Host "Released $tagName." -ForegroundColor Green
    Write-Host "  Build    : https://github.com/$repoSlug/actions/workflows/release.yml" -ForegroundColor Cyan
    Write-Host "  Release  : https://github.com/$repoSlug/releases/tag/$tagName" -ForegroundColor Cyan
    Write-Host "  Download : https://stdevthrottledl.blob.core.windows.net/download/latest/devthrottle-setup-win-x64.exe" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "The workflow builds, signs, publishes, mirrors the public downloads, and then" -ForegroundColor Gray
    Write-Host "fetches the installer back from that address to check its hash. If it goes red," -ForegroundColor Gray
    Write-Host "the release is not usable - read the failing step before announcing anything." -ForegroundColor Gray
    Write-Host ""
    exit 0
}

# ================================ STEP 1: FREEZE THE CANDIDATE ================================

# --- The single version source ---
$propsPath = Join-Path $repoRoot "Directory.Build.props"
if (-not (Test-Path $propsPath)) { Fail "Directory.Build.props not found at $propsPath" }

[xml]$props = Get-Content $propsPath
$currentVersion = $props.SelectSingleNode("//Version").InnerText
if (-not $currentVersion) { Fail "Could not read <Version> from $propsPath" }

# --- Suggest the next patch version, which is what we ship day to day ---
$suggested = $null
if ($currentVersion -match '^(\d+)\.(\d+)\.(\d+)$') {
    $suggested = "{0}.{1}.{2}" -f $matches[1], $matches[2], ([int]$matches[3] + 1)
}

Write-Host ""
Write-Host "Current version: $currentVersion" -ForegroundColor Cyan
if ($Version) {
    $newVersion = $Version.Trim()
} elseif ($suggested) {
    $answer = Read-Host "New version [$suggested]"
    if ([string]::IsNullOrWhiteSpace($answer)) { $newVersion = $suggested } else { $newVersion = $answer.Trim() }
} else {
    $newVersion = (Read-Host "New version (X.Y.Z or X.Y.Z-rcN)").Trim()
}

if ($newVersion -notmatch '^\d+\.\d+\.\d+(-rc\d+)?$') {
    Fail "Invalid version format: '$newVersion'." "Expected X.Y.Z or X.Y.Z-rcN"
}
if ($newVersion -eq $currentVersion) { Fail "New version is the same as the current one ($currentVersion)." }

$tagName = "v$newVersion"
$branch  = "release/$tagName"

if (git -C $repoRoot tag -l $tagName)                  { Fail "Tag $tagName already exists locally." }
if (git -C $repoRoot ls-remote --tags origin $tagName) { Fail "Tag $tagName already exists on the remote." "That version has been released. Pick the next one." }

# --- Release notes must exist. The workflow publishes this file verbatim as the
#     release page and fails without it. Catching that here costs nothing; a
#     pushed tag cannot be un-pushed. The file may be uncommitted - this script
#     commits it along with the version bump. ---
$notesRel  = "docs/public/release-notes/$tagName.md"   # forward slashes: matched against git status output
$notesPath = Join-Path $repoRoot "docs\public\release-notes\$tagName.md"
if (-not (Test-Path $notesPath)) {
    Fail "No written release notes for $tagName." "Expected: $notesPath`n`nWrite them first. The workflow publishes that file as the release page and refuses`nto invent a substitute - a list of internal pull request titles looks like release`nnotes and therefore ships unread."
}
$notesChars = ((Get-Content $notesPath -Raw) -replace '\s', '').Length
if ($notesChars -lt 200) {
    Fail "$notesRel has only $notesChars non-whitespace characters." "That is a placeholder, not release notes. The workflow applies the same floor."
}

# --- Guard: no .csproj may carry its own <Version> (it silently overrides the props file) ---
$stray = Get-ChildItem $repoRoot -Recurse -Filter *.csproj |
    Where-Object { $_.FullName -notmatch '\\archived\\' } |
    Where-Object { (Get-Content $_.FullName -Raw) -match '<Version>' }
if ($stray) {
    $list = ($stray | ForEach-Object { "  - $($_.FullName)" }) -join "`n"
    Fail "These .csproj files declare their own <Version>, overriding Directory.Build.props:`n$list" "Remove the <Version> elements; the props file is the single source of truth."
}

# --- Working tree must be clean apart from the two files this script owns ---
$notesPattern = [regex]::Escape($notesRel)
$dirty = git -C $repoRoot status --porcelain | Where-Object {
    $_ -notmatch 'Directory\.Build\.props$' -and $_ -notmatch "$notesPattern$"
}
if ($dirty) {
    Fail "The working tree has changes that are not part of this release:`n$($dirty -join "`n")" "Commit or discard them first. A release must be cut from a known state."
}

# --- Cut from exactly origin/main, in a checkout of your own (a worktree cut from origin/main) ---
$base = git -C $repoRoot rev-parse origin/main
$head = git -C $repoRoot rev-parse HEAD
if ($head -ne $base) {
    git -C $repoRoot merge-base --is-ancestor $head $base
    if ($LASTEXITCODE -eq 0) {
        $landed = git -C $repoRoot log --oneline "$head..$base"
        Fail "main moved since this checkout was cut. The notes do not cover:`n$($landed -join "`n")" "Bring this checkout up to origin/main (git merge --ff-only origin/main), extend the notes to cover these, and run this again."
    }
    Fail "This checkout is at $head, which is not on origin/main ($base)." "Cut a worktree from origin/main, write the notes there, and run this from it:`n  git worktree add ../devthrottle-release-$tagName -b $branch origin/main"
}
$lastTag = git -C $repoRoot describe --tags --abbrev=0 --match "v*" $base

$isPreRelease = $newVersion -match '-rc\d+$'

Write-Host ""
Write-Host "=== Candidate summary ===" -ForegroundColor Yellow
Write-Host "  Version : $currentVersion -> $newVersion"
Write-Host "  Covers  : $lastTag .. $base"
Write-Host "  Branch  : $branch"
Write-Host "  Notes   : $notesRel ($notesChars characters)"
if ($isPreRelease) { Write-Host "  Type    : Pre-release" -ForegroundColor Yellow }
else               { Write-Host "  Type    : Stable release" -ForegroundColor Green }
Write-Host ""
Write-Host "This bumps the version and merges it WITH the notes in one pull request. It does not tag." -ForegroundColor Gray
Write-Host ""

if (-not $Yes) {
    $confirm = Read-Host "Go? (Y/N)"
    if ($confirm -ne 'Y' -and $confirm -ne 'y') { Write-Host "Aborted. Nothing was changed." -ForegroundColor Yellow; exit 0 }
}

# --- 1. Branch, bump, commit, push ---
Write-Host ""
$currentBranch = git -C $repoRoot rev-parse --abbrev-ref HEAD
if ($currentBranch -ne $branch) {
    Write-Host "Creating $branch..." -ForegroundColor Cyan
    git -C $repoRoot checkout -q -b $branch
    if ($LASTEXITCODE -ne 0) { Fail "Could not create $branch." }
}

$props.SelectSingleNode("//Version").InnerText = $newVersion
$props.Save($propsPath)

git -C $repoRoot add -- $propsPath $notesPath
git -C $repoRoot commit -q -m "release: $tagName"
git -C $repoRoot push -q -u origin $branch
if ($LASTEXITCODE -ne 0) { Fail "Could not push $branch." }

# --- 2. Pull request, then merge. main requires this; see the header. ---
Write-Host "Opening the pull request..." -ForegroundColor Cyan
$prBody = "Version bump and release notes for $tagName, frozen together. The merge commit of this pull request is the release candidate: the release gate runs once on it, and it is tagged only after scripts/assert-gated.ps1 accepts it.`n`nThe notes cover $lastTag up to $base. Work merged after this waits for the next release.`n`nSee $notesRel for what is in this release."
gh pr create --repo $repoSlug --base main --head $branch --title "release: $tagName" --body $prBody | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Could not open the pull request." "The branch is pushed. Open the pull request by hand and merge it once nothing else has landed on main." }

# The notes describe $lastTag..$base. If main has moved, the squash commit would carry work the
# notes do not cover, and the notes may not be edited after the merge - so stop BEFORE merging.
git -C $repoRoot fetch --quiet origin main
$baseNow = git -C $repoRoot rev-parse origin/main
if ($baseNow -ne $base) {
    $landed = git -C $repoRoot log --oneline "$base..$baseNow"
    Fail "main moved while the candidate was being cut. These commits are not covered by the notes:`n$($landed -join "`n")" "The pull request is open and NOT merged. Close it (gh pr close $branch --delete-branch), then recut as the release-manager skill says in Step 8 under 'If main moved'."
}

Write-Host "Merging..." -ForegroundColor Cyan
gh pr merge $branch --repo $repoSlug --squash --delete-branch
if ($LASTEXITCODE -ne 0) {
    Fail "The pull request did not merge." "It is open and the branch is pushed. Read why on the pull request. Once it merges, its merge commit is the candidate - check that its parent is $base before gating it."
}

# --- 3. Record the candidate, and prove it is exactly base + this release ---
$candidate = gh pr view $branch --repo $repoSlug --json mergeCommit --jq ".mergeCommit.oid"
if (-not $candidate) { Fail "Could not read the merge commit of the release pull request." "Read it on the pull request page; that commit is the candidate." }
git -C $repoRoot fetch --quiet origin main
$parent = git -C $repoRoot rev-parse "$candidate^"
if ($parent -ne $base) {
    $landed = git -C $repoRoot log --oneline "$base..$parent"
    Fail "The candidate $candidate carries commits the notes do not cover:`n$($landed -join "`n")" "Do not gate or tag it. Correct the notes in a pull request; its merge commit is the new candidate."
}

[xml]$check = (git -C $repoRoot show "${candidate}:Directory.Build.props") -join "`n"
$onCandidate = $check.SelectSingleNode("//Version").InnerText
if ($onCandidate -ne $newVersion) {
    Fail "The candidate $candidate reports version '$onCandidate', not '$newVersion'." "The merge did not land as expected. Nothing has been tagged, so nothing has been released."
}

Write-Host ""
Write-Host "Candidate for $tagName frozen: $candidate" -ForegroundColor Green
Write-Host ""
Write-Host "Next - the release seat runs the gate ONCE on it:" -ForegroundColor Cyan
Write-Host "  git worktree add ../devthrottle-gate-$tagName --detach $candidate"
Write-Host "  cd ../devthrottle-gate-$tagName"
Write-Host "  .\scripts\test-local.ps1 -Parked -Configuration Release"
Write-Host ""
Write-Host "Then the person releasing tags it:" -ForegroundColor Cyan
Write-Host "  .\scripts\new-release.ps1 -Tag $candidate"
Write-Host ""
