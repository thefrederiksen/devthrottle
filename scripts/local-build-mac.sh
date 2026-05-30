#!/usr/bin/env bash
#
# local-build-mac.sh — Builds CC Director (Avalonia) locally on macOS.
#
# Publishes CC Director Avalonia as a single-file executable for macOS.
# Framework-dependent by default (~40 MB, requires the .NET 10 runtime).
# Pass --self-contained for a standalone build (~150+ MB, no runtime needed).
#
# This is the macOS counterpart of scripts/local-build-avalonia.ps1.
# The Windows-only NuGet packages (Microsoft.Web.WebView2, NAudio) restore
# cleanly and are simply not exercised at runtime on macOS.
#
# Usage:
#   scripts/local-build-mac.sh                 # framework-dependent, auto RID
#   scripts/local-build-mac.sh --self-contained
#   scripts/local-build-mac.sh --slot 1        # output -> cc-director-mac1
#   scripts/local-build-mac.sh --configuration Debug
#   scripts/local-build-mac.sh --rid osx-x64   # force a runtime identifier
#
set -euo pipefail

# ----------------------------------------------------------------------------
# Args
# ----------------------------------------------------------------------------
SELF_CONTAINED=false
CONFIGURATION="Release"
SLOT=""
RID=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --self-contained) SELF_CONTAINED=true; shift ;;
        --configuration|-c) CONFIGURATION="$2"; shift 2 ;;
        --slot) SLOT="$2"; shift 2 ;;
        --rid) RID="$2"; shift 2 ;;
        -h|--help)
            grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

# ----------------------------------------------------------------------------
# Locate the .NET SDK
# ----------------------------------------------------------------------------
# The dotnet SDK is commonly installed under ~/.dotnet (via dotnet-install.sh)
# and may not be on the global PATH. Add it if present.
if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if ! command -v dotnet >/dev/null 2>&1; then
    echo "ERROR: 'dotnet' not found." >&2
    echo "Install the .NET 10 SDK, e.g.:" >&2
    echo "  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir \"\$HOME/.dotnet\"" >&2
    echo "  (or: brew install --cask dotnet-sdk)" >&2
    exit 1
fi

# ----------------------------------------------------------------------------
# Resolve runtime identifier (RID)
# ----------------------------------------------------------------------------
if [[ -z "$RID" ]]; then
    case "$(uname -m)" in
        arm64|aarch64) RID="osx-arm64" ;;
        x86_64)        RID="osx-x64" ;;
        *) echo "ERROR: unsupported macOS architecture '$(uname -m)'" >&2; exit 1 ;;
    esac
fi

# ----------------------------------------------------------------------------
# Paths
# ----------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_PATH="$REPO_ROOT/src/CcDirector.Avalonia/CcDirector.Avalonia.csproj"
CORE_PATH="$REPO_ROOT/src/CcDirector.Core/CcDirector.Core.csproj"

# Read <Version> from the csproj (first non-empty match).
VERSION="$(grep -oE '<Version>[^<]+</Version>' "$PROJECT_PATH" | head -1 | sed -E 's/<\/?Version>//g' || true)"
if [[ -z "$VERSION" ]]; then
    echo "ERROR: could not read <Version> from $PROJECT_PATH" >&2
    exit 1
fi

echo "Building CC Director Avalonia v$VERSION ($CONFIGURATION) for $RID"
if [[ "$SELF_CONTAINED" == true ]]; then
    echo "  Mode: Self-contained (no .NET runtime required on target machine)"
else
    echo "  Mode: Framework-dependent (.NET 10 runtime required)"
fi

# ----------------------------------------------------------------------------
# Step 0: Clean
# ----------------------------------------------------------------------------
echo "  Cleaning previous build..."
dotnet clean "$PROJECT_PATH" -c "$CONFIGURATION" --nologo -v q

# ----------------------------------------------------------------------------
# Step 1: Pre-build Core dependency
# ----------------------------------------------------------------------------
echo "  Pre-building Core dependency..."
dotnet build "$CORE_PATH" -c "$CONFIGURATION" --nologo -v q

# ----------------------------------------------------------------------------
# Step 2 & 3: Build + publish single-file for the macOS RID
# ----------------------------------------------------------------------------
# Unlike the Windows script we do NOT pass -p:NoBuild=true: the macOS runtime
# pack must be restored as part of publish, otherwise single-file publish
# fails with NETSDK1112 (runtime pack not downloaded).
echo "  Publishing..."
PUBLISH_ARGS=(
    publish "$PROJECT_PATH"
    -c "$CONFIGURATION"
    -r "$RID"
    --self-contained "$SELF_CONTAINED"
    -p:PublishSingleFile=true
    -p:IncludeNativeLibrariesForSelfExtract=true
    --nologo
    -v q
)
if [[ "$SELF_CONTAINED" == true ]]; then
    PUBLISH_ARGS+=(-p:EnableCompressionInSingleFile=true)
fi
dotnet "${PUBLISH_ARGS[@]}"

# ----------------------------------------------------------------------------
# Locate published output
# ----------------------------------------------------------------------------
PUBLISH_DIR="$REPO_ROOT/src/CcDirector.Avalonia/bin/$CONFIGURATION/net10.0/$RID/publish"
EXE_PATH="$PUBLISH_DIR/cc-director"

if [[ ! -f "$EXE_PATH" ]]; then
    echo "ERROR: published executable not found at $EXE_PATH" >&2
    exit 1
fi

# ----------------------------------------------------------------------------
# Copy to local_builds/mac
# ----------------------------------------------------------------------------
DEST_DIR="$REPO_ROOT/local_builds/mac"
mkdir -p "$DEST_DIR"
EXE_NAME="cc-director-mac${SLOT}"
DEST_PATH="$DEST_DIR/$EXE_NAME"
cp -f "$EXE_PATH" "$DEST_PATH"
chmod +x "$DEST_PATH"

EXE_SIZE_MB="$(echo "scale=1; $(stat -f%z "$DEST_PATH") / 1048576" | bc)"
echo ""
echo "Build complete: ${EXE_SIZE_MB} MB"
echo "  $DEST_PATH"
