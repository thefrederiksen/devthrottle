# CC Director Release Process

## Overview

CC Director uses GitHub Actions to automate building, testing, and publishing releases. Releases are created entirely from the GitHub web UI -- no local commands required.

## How to Release a New Version

1. Go to https://github.com/thefrederiksen/devthrottle/releases
2. Click **"Draft a new release"**
3. In the **"Choose a tag"** dropdown, type a new tag (e.g., `v1.3.0`) and select **"Create new tag on publish"**
4. Set the **Target** to `main`
5. Enter a title (e.g., `CC Director v1.3.0`)
6. Click **"Generate release notes"** to auto-populate from commits since the last tag
7. Click **"Publish release"**

GitHub Actions will automatically:
- Install .NET 10 SDK
- Run all unit tests
- Build the single-file EXE using the 3-step workaround for .NET 10 bugs
- Attach `cc-director.exe` to the release as a downloadable asset

Monitor progress at: https://github.com/thefrederiksen/devthrottle/actions

## Versioning

We use [Semantic Versioning](https://semver.org/):

- **MAJOR.MINOR.PATCH** (e.g., `1.2.0`)
- Tags are prefixed with `v` (e.g., `v1.2.0`)
- **MAJOR** - Breaking changes or major redesigns
- **MINOR** - New features, backward compatible
- **PATCH** - Bug fixes

Pre-release versions use a suffix: `v2.0.0-rc.1`, `v1.3.0-beta.1`

## Download Links

- **Latest release:** https://github.com/thefrederiksen/devthrottle/releases/latest
- **Direct EXE download:** https://github.com/thefrederiksen/devthrottle/releases/latest/download/cc-director.exe

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

Local builds use the version from `CcDirector.Wpf.csproj`. Only CI builds get the tag version.

## Previous Tags

| Tag | Date | Notes |
|-----|------|-------|
| v1.1.0 | 2026-02 | Current |
| v1.0.0 | 2026-02 | First stable |
| v0.2.0 | 2026-02 | Pre-release |

## Implementation Status

**Not yet implemented.** The GitHub Actions workflow (`.github/workflows/release.yml`) still needs to be created. See the implementation tasks for details.
