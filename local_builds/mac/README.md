# macOS local builds

macOS counterpart of the Windows `local_builds/_local_build_avalonia*.bat` flow.
Builds the cross-platform **Avalonia** UI (the WPF project is Windows-only and is
not built here).

## One-time toolchain setup

CC Director targets `net10.0`, so you need the **.NET 10 SDK**.

```bash
# User-local install, no sudo (recommended):
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"

# …or via Homebrew (requires an interactive sudo password):
#   brew install --cask dotnet-sdk
```

The build script auto-detects an SDK under `~/.dotnet`, so no PATH changes are
required. To use `dotnet` directly in your own shell, add:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
```

## Build

```bash
# Framework-dependent single-file build (~40 MB, needs .NET 10 runtime):
./local_builds/mac/_local_build_mac1.sh

# Standalone self-contained build (~150+ MB, no runtime needed):
./local_builds/mac/_local_build_mac1.sh --self-contained
```

Or call the underlying script directly for more control:

```bash
scripts/local-build-mac.sh --slot 1               # -> local_builds/mac/cc-director-mac1
scripts/local-build-mac.sh --self-contained
scripts/local-build-mac.sh --rid osx-x64          # force Intel RID
scripts/local-build-mac.sh --configuration Debug
```

The runtime identifier (RID) is auto-detected: `osx-arm64` on Apple Silicon,
`osx-x64` on Intel.

## Output

The built executable is a native Mach-O binary at:

```
local_builds/mac/cc-director-mac<slot>
```

Run it directly: `./local_builds/mac/cc-director-mac1`

## Notes

- The Windows-only NuGet packages (`Microsoft.Web.WebView2`, `NAudio`) restore
  cleanly on macOS and are simply not exercised at runtime. WebView2-backed and
  WinMM-audio features are no-ops on macOS.
- Whisper GPU acceleration ships its Metal kernel (`ggml-metal.metal`) alongside
  the binary in the publish output.
- macOS Gatekeeper may quarantine a freshly built unsigned binary. If macOS
  refuses to launch it, clear the quarantine attribute:
  `xattr -d com.apple.quarantine local_builds/mac/cc-director-mac1`
