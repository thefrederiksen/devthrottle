#!/usr/bin/env bash
#
# local-build-linux.sh - Builds CC Director (Avalonia) locally on Linux.
#
# Publishes CC Director Avalonia as a single-file executable for Linux.
# SELF-CONTAINED by default (~130 MB, no .NET runtime needed on the target machine).
# Pass --framework-dependent for a smaller build (~35 MB) that requires the ASP.NET Core
# runtime to already be installed.
#
# Why self-contained is the default here and not on macOS: a clean Ubuntu desktop has no
# .NET at all, and the Director needs the ASP.NET CORE runtime rather than the base one
# because ControlApi hosts Kestrel. Making the download carry its own runtime is the only
# shape that cannot dead-end on a fresh machine. The larger file is the price.
#
# This is the Linux counterpart of scripts/local-build-mac.sh. The macOS-only steps -
# codesign, xattr, security find-identity - have no Linux equivalent and are simply absent;
# Linux has no Gatekeeper and no signature-keyed privacy grants, so nothing replaces them.
# The Windows-only NuGet packages (Microsoft.Web.WebView2, NAudio) restore cleanly and are
# not exercised at runtime on Linux.
#
# Usage:
#   scripts/local-build-linux.sh                      # self-contained, auto RID
#   scripts/local-build-linux.sh --framework-dependent
#   scripts/local-build-linux.sh --slot 1             # output -> cc-director-linux1
#   scripts/local-build-linux.sh --configuration Debug
#   scripts/local-build-linux.sh --rid linux-arm64    # force a runtime identifier
#
set -euo pipefail

# ----------------------------------------------------------------------------
# Args
# ----------------------------------------------------------------------------
SELF_CONTAINED=true
CONFIGURATION="Release"
SLOT=""
RID=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --self-contained) SELF_CONTAINED=true; shift ;;
        --framework-dependent) SELF_CONTAINED=false; shift ;;
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
# The dotnet SDK is commonly installed under ~/.dotnet (via dotnet-install.sh) and may not
# be on the global PATH. Add it if present.
if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if ! command -v dotnet >/dev/null 2>&1; then
    echo "ERROR: 'dotnet' not found." >&2
    echo "Install the .NET 10 SDK, e.g.:" >&2
    echo "  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir \"\$HOME/.dotnet\"" >&2
    exit 1
fi

# ----------------------------------------------------------------------------
# Resolve runtime identifier (RID)
# ----------------------------------------------------------------------------
if [[ -z "$RID" ]]; then
    case "$(uname -m)" in
        x86_64|amd64)   RID="linux-x64" ;;
        aarch64|arm64)  RID="linux-arm64" ;;
        *) echo "ERROR: unsupported Linux architecture '$(uname -m)'" >&2; exit 1 ;;
    esac
fi

# ----------------------------------------------------------------------------
# Paths
# ----------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_PATH="$REPO_ROOT/src/CcDirector.Avalonia/CcDirector.Avalonia.csproj"
CORE_PATH="$REPO_ROOT/src/CcDirector.Core/CcDirector.Core.csproj"
VERSION_PROPS="$REPO_ROOT/Directory.Build.props"

# Read <Version> from Directory.Build.props (the single source of truth; no .csproj may declare its
# own <Version> or it would silently override the centralized one). The project files therefore have
# no <Version>, so we must read the props file here.
VERSION="$(grep -oE '<Version>[^<]+</Version>' "$VERSION_PROPS" | head -1 | sed -E 's/<\/?Version>//g' || true)"
if [[ -z "$VERSION" ]]; then
    echo "ERROR: could not read <Version> from $VERSION_PROPS" >&2
    exit 1
fi

echo "Building CC Director Avalonia v$VERSION ($CONFIGURATION) for $RID"
if [[ "$SELF_CONTAINED" == true ]]; then
    echo "  Mode: Self-contained (no .NET runtime required on target machine)"
else
    echo "  Mode: Framework-dependent (ASP.NET Core 10 runtime required on target machine)"
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
# Step 2 & 3: Build + publish single-file for the Linux RID
# ----------------------------------------------------------------------------
# Unlike the Windows script we do NOT pass -p:NoBuild=true: the Linux runtime pack must be
# restored as part of publish, otherwise single-file publish fails with NETSDK1112 (runtime
# pack not downloaded).
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
# Copy to local_builds/linux
# ----------------------------------------------------------------------------
DEST_DIR="$REPO_ROOT/local_builds/linux"
mkdir -p "$DEST_DIR"
EXE_NAME="cc-director-linux${SLOT}"
DEST_PATH="$DEST_DIR/$EXE_NAME"
cp -f "$EXE_PATH" "$DEST_PATH"
chmod +x "$DEST_PATH"

# Nothing signs this binary and nothing needs to. There is no Gatekeeper on Linux, no
# quarantine attribute, and no privacy grants keyed to a code signature - so the whole
# re-signing block the macOS script needs after `cp` has no counterpart here.

EXE_SIZE_MB="$(awk "BEGIN { printf \"%.1f\", $(stat -c%s "$DEST_PATH") / 1048576 }")"
echo ""
echo "Build complete: ${EXE_SIZE_MB} MB"
echo "  $DEST_PATH"
echo "  sha256: $(sha256sum "$DEST_PATH" | cut -d' ' -f1)"
