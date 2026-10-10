# CC Director Release Process

## Overview

GitHub Actions builds and publishes the release. **A person pushes a tag; nothing else.** The
workflow creates the release page, attaches every asset, and only then publishes it.

## How to Release a New Version

The steps live in the `release-manager` skill (`.claude/skills/release-manager/SKILL.md`). In short:

1. **Freeze the candidate.** Write `docs/public/release-notes/v<version>.md` in a worktree cut from
   origin/main and run `scripts/new-release.ps1`. It merges the version bump (in
   `Directory.Build.props`, the single version source) and the notes in ONE pull request and
   prints its merge commit: the candidate. The notes are never edited after that; work merged later
   waits for the next release. Never merge the notes and the bump separately.
2. **Gate the candidate once**, in a worktree detached at it:
   `scripts\test-local.ps1 -Parked -Configuration Release`. A red candidate is not run again - fix
   the cause on main and cut a new candidate.
3. **Tag it:** `scripts/new-release.ps1 -Tag <candidate>`. It tags only a candidate that
   `scripts/assert-gated.ps1` accepts.

The workflow then:

- verifies the tag matches `Directory.Build.props`, and that the written notes exist and say
  something;
- builds every component for Windows and macOS and checks the asset list is complete;
- creates the release **as a draft**, attaches all assets, and publishes it once the manifest is
  provably attached.

Monitor progress at: https://github.com/example-org/devthrottle/actions

### Two things NOT to do, and why

**Do not create or publish the release yourself in the web interface.** Publishing makes a release
"latest" the instant you click, while the workflow's assets arrive minutes afterwards. Every updater
that checks in between sees a newest version with no `release-manifest.json` and fails outright -
measured on v1.8.8 at five minutes and twenty-three seconds, with a launcher failing six seconds
before the assets landed. A failed update check used to look exactly like being up to date, so this
went unnoticed for as long as the project has existed. Pushing the tag and leaving the release alone
is what closes that window (issue #1079).

**Do not use "Generate release notes".** It produces a list of internal pull-request titles, which
is what v1.8.7 shipped to strangers. Every release page that looked right before v1.9.0 was a person
pasting the written notes over that list afterwards - correct by accident. The workflow publishes
`docs/public/release-notes/<tag>.md` and FAILS when it is absent; it will not invent a substitute,
because a page that looks like release notes does not get read (issue #1106).

## Versioning

We use [Semantic Versioning](https://semver.org/):

- **MAJOR.MINOR.PATCH** (e.g., `1.2.0`)
- Tags are prefixed with `v` (e.g., `v1.2.0`)
- **MAJOR** - Breaking changes or major redesigns
- **MINOR** - New features, backward compatible
- **PATCH** - Bug fixes

Pre-release versions use a suffix: `v2.0.0-rc.1`, `v1.3.0-beta.1`

## Download Links

- **Latest release:** https://github.com/example-org/devthrottle/releases/latest
- **Direct EXE download:** https://github.com/example-org/devthrottle/releases/latest/download/cc-director.exe

The README download link always points to the latest release automatically.

## What the Workflow Does

The workflow (`.github/workflows/release.yml`) replicates the local `scripts/release.ps1` build process:

1. **Pre-build Core** - Workaround for .NET 10 WPF `_wpftmp` stack overflow when building project references from clean state
2. **Build WPF with RID** - Compiles XAML markup with `-r win-x64`
3. **MSBuild Publish** - Uses `dotnet msbuild -t:Publish` with `NoBuild=true` instead of `dotnet publish` to avoid the bundle size bug that incorrectly bundles the full runtime

The version number from the tag (e.g., `1.3.0` from `v1.3.0`) is injected into the build via `/p:Version=`, overriding the default in the .csproj.

## Local Builds

For local testing, `scripts/release.ps1` still works:

```powershell
.\scripts\release.ps1                   # Framework-dependent (~10 MB)
.\scripts\release.ps1 -SelfContained    # Standalone (~150+ MB)
```

The version comes from `Directory.Build.props`, which is the single version source for every
binary in the release (see `docs/architecture/VERSIONING.md`). The workflow fails the release if
the tag and that file disagree.

## Previous Tags

| Tag | Date | Notes |
|-----|------|-------|
| v1.1.0 | 2026-02 | Current |
| v1.0.0 | 2026-02 | First stable |
| v0.2.0 | 2026-02 | Pre-release |

## Implementation Status

**Live.** `.github/workflows/release.yml` has built and published every release since v1.2.0. The
"not yet implemented" note that stood here was left over from before the workflow existed and was
plainly contradicted by every release page in the repository.
