#!/usr/bin/env bash
#
# Installs the cc-director-launcher as a per-user launchd agent so it runs in the
# clean GUI session (outside any Claude Code process tree). Idempotent.
#
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LABEL="com.centerconsulting.ccd-launcher"
PLIST_SRC="$DIR/$LABEL.plist"
PLIST_DST="$HOME/Library/LaunchAgents/$LABEL.plist"
SCRIPT="$DIR/launcher.py"
CCD_BIN="${CCD_BIN:-$HOME/ReposFred/cc-director/local_builds/mac/cc-director-mac1}"
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

mkdir -p "$HOME/Library/LaunchAgents"

# Render the plist with absolute paths.
sed -e "s|__SCRIPT__|$SCRIPT|g" \
    -e "s|__CCD_BIN__|$CCD_BIN|g" \
    -e "s|__DOTNET_ROOT__|$DOTNET_ROOT|g" \
    -e "s|__DIR__|$DIR|g" \
    "$PLIST_SRC" > "$PLIST_DST"

UID_NUM="$(id -u)"
DOMAIN="gui/$UID_NUM"

# Reload cleanly.
launchctl bootout "$DOMAIN/$LABEL" 2>/dev/null || true
launchctl bootstrap "$DOMAIN" "$PLIST_DST" 2>/dev/null || launchctl load -w "$PLIST_DST"
launchctl enable "$DOMAIN/$LABEL" 2>/dev/null || true
launchctl kickstart -k "$DOMAIN/$LABEL" 2>/dev/null || true

sleep 1
echo "Installed $LABEL"
echo "  plist: $PLIST_DST"
echo "  bin:   $CCD_BIN"
echo "Try:  curl -s http://127.0.0.1:8765/status"
