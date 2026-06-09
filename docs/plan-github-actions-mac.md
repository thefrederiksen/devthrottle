# Plan: GitHub Actions for Windows + Mac Releases

## Context

Current `release.yml` builds 3 Windows-only artifacts. We need to add parallel Mac build jobs and include Mac assets in the same GitHub Release.

## Current State (release.yml)

```
4 jobs, all Windows:
  build-director      -> cc-director.exe (win-x64)
  build-python-tools  -> cc-pdf.exe, cc-html.exe, cc-word.exe
  build-setup-wizard  -> cc-director-setup.exe (win-x64)
  create-release      -> bundles all into GitHub Release
```

## Target State

```
8 jobs (4 Windows + 4 Mac):
  build-director-win       -> cc-director-win-x64.exe
  build-director-mac       -> cc-director-mac-arm64 (Avalonia build)
  build-python-tools-win   -> cc-pdf.exe, cc-html.exe, cc-word.exe
  build-python-tools-mac   -> cc-pdf, cc-html, cc-word (no .exe)
  build-setup-wizard-win   -> cc-director-setup-win-x64.exe
  build-setup-wizard-mac   -> cc-director-setup-mac-arm64
  build-node-tools         -> cc-browser (platform-agnostic npm package)
  create-release           -> bundles ALL into single GitHub Release
```

## Step-by-Step

### Step 1: Rename existing jobs for clarity

- `build-director` -> `build-director-win`
- `build-python-tools` -> `build-python-tools-win`
- `build-setup-wizard` -> `build-setup-wizard-win`
- Add `-win-x64` suffix to artifact filenames for clarity

### Step 2: Add Mac Director build job

```yaml
build-director-mac:
  name: Build CC Director (macOS)
  runs-on: macos-latest  # macOS 14, Apple Silicon

  steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: "10.0.x"

    - name: Publish for macOS ARM64
      run: >
        dotnet publish src/CcDirector.Avalonia/CcDirector.Avalonia.csproj
        -c Release -r osx-arm64 --self-contained true
        -p:PublishSingleFile=true

    - uses: actions/upload-artifact@v4
      with:
        name: cc-director-mac
        path: src/CcDirector.Avalonia/bin/Release/net10.0/osx-arm64/publish/cc-director
```

### Step 3: Add Mac Python tools build job

```yaml
build-python-tools-mac:
  name: Build Python Tools (macOS)
  runs-on: macos-latest

  steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-python@v5
      with:
        python-version: "3.11"

    - name: Build Python tools
      run: |
        tools=("cc-pdf" "cc-html" "cc-word")
        mkdir -p artifacts
        for tool in "${tools[@]}"; do
          cd "tools/$tool"
          pip install -r requirements.txt pyinstaller
          pyinstaller --onefile --name "$tool" src/main.py
          cp "dist/$tool" "../../artifacts/${tool}-mac"
          cd ../..
        done

    - uses: actions/upload-artifact@v4
      with:
        name: python-tools-mac
        path: artifacts/
```

### Step 4: Add Mac Setup Wizard build job

```yaml
build-setup-wizard-mac:
  name: Build Setup Wizard (macOS)
  runs-on: macos-latest

  steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: "10.0.x"

    - name: Publish for macOS ARM64
      run: >
        dotnet publish tools/cc-director-setup-avalonia/CcDirectorSetup.csproj
        -c Release -r osx-arm64 --self-contained true
        -p:PublishSingleFile=true

    - uses: actions/upload-artifact@v4
      with:
        name: setup-wizard-mac
        path: tools/cc-director-setup-avalonia/bin/Release/net10.0/osx-arm64/publish/cc-director-setup
```

### Step 5: Update CI workflow

```yaml
# ci.yml -- add macOS matrix
strategy:
  matrix:
    os: [windows-latest, macos-latest]
    include:
      - os: windows-latest
        solution: cc-director.sln           # Full solution (WPF + Avalonia)
      - os: macos-latest
        solution: cc-director-crossplatform.sln  # Avalonia only (no WPF)
```

### Step 6: Update create-release job

- Add `needs:` for all Mac jobs
- Collect Mac artifacts with `-mac` suffix in filenames
- Update release-manifest.json to include Mac assets with platform field
- Release contains both Windows and Mac binaries side by side

### Step 7: Asset naming convention

```
Release assets:
  cc-director-win-x64.exe           # Windows main app
  cc-director-mac-arm64             # Mac main app
  cc-director-setup-win-x64.exe     # Windows setup
  cc-director-setup-mac-arm64       # Mac setup
  cc-pdf-win-x64.exe                # Windows Python tool
  cc-pdf-mac-arm64                  # Mac Python tool
  cc-html-win-x64.exe / cc-html-mac-arm64
  cc-word-win-x64.exe / cc-word-mac-arm64
  release-manifest.json             # Updated with platform info
```

## Files to Modify

- `.github/workflows/release.yml` -- add 4 Mac jobs, rename Windows jobs, update create-release
- `.github/workflows/ci.yml` -- add macOS matrix build
- `tools/cc-pdf/build.sh` (new) -- Mac build script for PyInstaller
- `tools/cc-html/build.sh` (new) -- Mac build script
- `tools/cc-word/build.sh` (new) -- Mac build script

## Dependencies

- This plan depends on the Avalonia port (Avalonia projects must exist before GitHub Actions can build them)
- Python tools can be built for Mac independently (no Avalonia dependency)
- Node tools are already platform-agnostic

## Verification

- Push a test tag (e.g., `v0.0.1-mac-test`) to trigger release workflow
- Verify all 8 jobs complete successfully
- Download Mac artifacts and test on macOS (or VM)
- Verify release-manifest.json contains all assets with correct checksums
- Verify setup wizard runs on Mac and installs tools correctly
- Verify cc-director Avalonia app launches on Mac with working terminal

## Implementation Status

> NOTE (2026-06-09): `cc-director-crossplatform.sln` was later removed during a root cleanup, and the
> macOS CI matrix described below was not retained. `ci.yml` and `release.yml` currently build only
> `cc-director.sln` on Windows. The status below is kept as a historical record of the original plan.

- [x] Step 1: Rename existing jobs for clarity (build-director-win, build-python-tools-win, build-setup-wizard-win)
- [x] Step 2: Add Mac Director build job (build-director-mac)
- [x] Step 3: Add Mac Python tools build job (build-python-tools-mac)
- [x] Step 4: Add Mac Setup Wizard build job (build-setup-wizard-mac)
- [x] Step 5: Update CI workflow (macOS matrix with cc-director-crossplatform.sln)
- [x] Step 6: Update create-release job (collects both Win + Mac artifacts with platform suffix)
- [x] Step 7: Asset naming convention applied

### Files created/modified:
- `.github/workflows/release.yml` -- 6 build jobs + create-release (was 3+1)
- `.github/workflows/ci.yml` -- matrix build: windows-latest + macos-latest
- `cc-director-crossplatform.sln` -- Avalonia-only solution (7 cross-platform projects)
- `tools/cc-director-setup-avalonia/` -- full Avalonia port of setup wizard (cross-platform)

### Remaining:
- `build-node-tools` job -- add when cc-browser packaging is ready

## Execution Order (across both plans)

1. Multi-target Core libs + create Avalonia project skeleton
2. Port setup wizard as proof of concept
3. Set up GitHub Actions for Mac builds (can test with just setup wizard)
4. Port main app
5. Finalize CI + release pipeline
