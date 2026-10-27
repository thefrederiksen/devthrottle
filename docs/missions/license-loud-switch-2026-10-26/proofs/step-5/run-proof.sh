#!/usr/bin/env bash
# Step 5 proof: a sign-in by hand into an isolated config folder holding account A leaves A in the spares.
#
# Runs the real `cc-devthrottle license sign-in --make-active` from this checkout, with fake_claude.py standing in for
# `claude auth login`. Every folder is a new temporary one; the machine's real Claude login is never named.
#
#   PYTHON=<a python with the tool's dependency floors> bash run-proof.sh
set -euo pipefail

PYTHON="${PYTHON:-python}"
HERE="$(cd "$(dirname "$0")" && pwd)"
TOOL="$(cd "$HERE/../../../../../tools/cc-devthrottle" && pwd)"
ROOT="$(mktemp -d)"
# The tool takes a folder only in its one canonical form, which on Windows is the native spelling.
native() { if [[ "${OS:-}" == "Windows_NT" ]]; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
DEFAULT="$(native "$ROOT/claude-default")"
STORE="$(native "$ROOT/store")"
BIN="$ROOT/bin"
CODE="PROOFCODE-aaaaaaaaaaaaaaaaaaaa#PROOFSTATE-bbbbbbbbbbbbbbbbbbbb"
A="account-a@example.com"
B="account-b@example.com"
C="account-c@example.com"

# `claude` on PATH is the stand-in, nothing else.
mkdir -p "$BIN"
if [[ "${OS:-}" == "Windows_NT" ]]; then
  printf '@"%s" "%s" %%*\r\n' "$(cygpath -w "$(command -v "$PYTHON")")" "$(cygpath -w "$HERE/fake_claude.py")" > "$BIN/claude.cmd"
else
  printf '#!/bin/sh\nexec "%s" "%s" "$@"\n' "$(command -v "$PYTHON")" "$HERE/fake_claude.py" > "$BIN/claude"
  chmod +x "$BIN/claude"
fi
export PATH="$BIN:$PATH"
unset CC_LICENSE_ALLOW_REAL_DEFAULT
export CC_LICENSE_DEFAULT_CONFIG_DIR="$DEFAULT" CC_LICENSE_STORE_DIR="$STORE"
# The command app straight from this checkout's source, as the tests import it. Not the console entry point: that
# one reports a failure to the Gateway, and step 5's refusal is expected.
license() { "$PYTHON" -c "import sys; sys.path[:0] = sys.argv[1:3]; sys.argv = ['cc-devthrottle'] + sys.argv[3:]; from src.cli import app; app(prog_name='cc-devthrottle')" "$TOOL" "$(dirname "$TOOL")" license "$@"; }
fingerprint() { "$PYTHON" -c "import hashlib,sys;print(hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest()[:16])" "$1"; }

echo "== The isolated machine: default folder $DEFAULT, store $STORE"
echo "== 1. Account A is the active sign-in (made by the stand-in login, straight into the default folder)"
echo "$CODE" | CLAUDE_CONFIG_DIR="$DEFAULT" "$PYTHON" "$HERE/fake_claude.py" auth login --claudeai --email "$A" >> "$ROOT/seed.log"
A_BEFORE="$(fingerprint "$ROOT/claude-default/.credentials.json")"
echo "A's sign-in fingerprint (sha256 of its credentials file, first 16): $A_BEFORE"
license list

echo
echo "== 2. A sign-in by hand to account B that replaces the active account"
echo "\$ cc-devthrottle license sign-in $B --make-active --remote-approval   (code typed on standard input)"
echo "$CODE" | license sign-in "$B" --make-active --remote-approval

echo
echo "== 3. What the machine holds now"
license list
license list --json
A_SPARE="$ROOT/store/spares/account-a-example.com/.credentials.json"
A_AFTER="$(fingerprint "$A_SPARE")"
echo "A's spare holds fingerprint $A_AFTER"
if [[ "$A_AFTER" != "$A_BEFORE" ]]; then echo "FAIL: the spare is not A's own sign-in"; exit 1; fi
echo "OK: A is kept as a spare, and it is the very sign-in A had before"

echo
echo "== 4. The same account again: B is renewed in place, not put into the spares a second time"
echo "$CODE" | license sign-in "$B" --make-active --remote-approval
license list

echo
echo "== 5. The failure case: the active account (B) cannot be kept, so nothing is signed in"
mkdir -p "$ROOT/store/spares/account-b-example.com"
echo "$CODE" | CLAUDE_CONFIG_DIR="$(native "$ROOT/store/spares/account-b-example.com")" "$PYTHON" "$HERE/fake_claude.py" auth login --claudeai --email "$C" >> "$ROOT/seed.log"
echo "(B's spare folder now holds another account's key, which putting B away would overwrite)"
BEFORE="$(find "$ROOT/claude-default" "$ROOT/store/spares" -type f -exec sha256sum {} + | sort)"
set +e
echo "$CODE" | license sign-in "$C" --make-active --remote-approval
EXIT=$?
set -e
echo "exit code: $EXIT"
AFTER="$(find "$ROOT/claude-default" "$ROOT/store/spares" -type f -exec sha256sum {} + | sort)"
if [[ "$EXIT" == "0" ]]; then echo "FAIL: the sign-in went ahead"; exit 1; fi
if [[ "$BEFORE" != "$AFTER" ]]; then echo "FAIL: a file changed"; exit 1; fi
echo "OK: refused with the reason, and no file in the default folder or the spares changed"

rm -rf "$ROOT"
echo
echo "PROOF PASSED"
