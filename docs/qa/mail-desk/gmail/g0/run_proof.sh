#!/usr/bin/env bash
# Mail Desk Gmail support, proof G0: run the probe against the real mailbox in a stripped environment and write
# transcript.txt. Read-only against Gmail (the probe's guard allows GET only). The address is read from the cc-gmail
# account's config.json by nickname and is never written to the transcript: every token containing "@" is replaced,
# and the home folder is written as "~".
#
# Run from the repository root:  bash docs/qa/mail-desk/gmail/g0/run_proof.sh personal
set -euo pipefail

NICKNAME="${1:?usage: run_proof.sh <cc-gmail account nickname>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REAL_HOME="$(getent passwd "$(id -u)" | cut -d: -f6)"
ACCOUNT_DIR="$REAL_HOME/.cc-director/config/gmail/accounts/$NICKNAME"
ADDRESS="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["email"])' "$ACCOUNT_DIR/config.json")"
TOKEN="$ACCOUNT_DIR/token.json"
STRIPPED_PATH="/usr/bin:/bin:$REAL_HOME/.local/bin"
SCRATCH="$(mktemp -d)"
OUT="$HERE/transcript.txt"
trap 'rm -rf "$SCRATCH"' EXIT

redact() { sed -E -e "s#$REAL_HOME#~#g" -e 's#[^[:space:]"│|]*@[^[:space:]"│|]*#<address>#g' -e "s#$SCRATCH#<scratch>#g"; }

token_facts() {
    python3 - "$TOKEN" <<'EOF'
import hashlib, json, os, stat, sys
path = sys.argv[1]
data = json.load(open(path))
st = os.stat(path)
print(f"  mode {oct(stat.S_IMODE(st.st_mode))}, modified {int(st.st_mtime)}, keys {list(data)}")
print("  value hash prefixes:", {k: hashlib.sha256(json.dumps(v, sort_keys=True).encode()).hexdigest()[:8] for k, v in data.items()})
EOF
}

section() { printf '\n=== %s ===\n' "$1"; }

{
    echo "G0 transcript, $(date -u +%Y-%m-%dT%H:%M:%SZ), machine $(hostname), commit $(git -C "$HERE" rev-parse --short HEAD)"
    echo "The address is passed as --address \"\$ADDRESS\", read from accounts/$NICKNAME/config.json; it is never printed."
    PROBE_REL="docs/qa/mail-desk/gmail/g0/gmail_probe.py"
    echo "gmail_probe.py SHA-256: $(sha256sum "$HERE/gmail_probe.py" | cut -d' ' -f1)"
    if [ "$(git -C "$HERE" hash-object "$HERE/gmail_probe.py")" = "$(git -C "$HERE" rev-parse "HEAD:$PROBE_REL" 2>/dev/null)" ]; then
        echo "gmail_probe.py is byte-for-byte the version committed at $(git -C "$HERE" rev-parse --short HEAD)"
    else
        echo "gmail_probe.py DIFFERS from the version committed at $(git -C "$HERE" rev-parse --short HEAD): this transcript is not from a committed probe"
    fi

    section "token.json before"
    token_facts

    section "Run 1: stripped environment, forced refresh"
    echo "\$ env -i PATH=$STRIPPED_PATH uv run --script gmail_probe.py --address \"\$ADDRESS\" --force-refresh"
    env -i PATH="$STRIPPED_PATH" uv run --script "$HERE/gmail_probe.py" --address "$ADDRESS" --force-refresh 2>&1 || echo "exit $?"

    section "token.json after run 1"
    token_facts

    section "cc-gmail reads the account after run 1"
    echo "\$ cc-gmail accounts status $NICKNAME"
    COLUMNS=250 "$REAL_HOME/.local/bin/cc-gmail" accounts status "$NICKNAME" 2>&1 || echo "exit $?"
    echo "\$ cc-gmail accounts list"
    COLUMNS=250 "$REAL_HOME/.local/bin/cc-gmail" accounts list 2>&1 || echo "exit $?"

    section "token.json after cc-gmail read it (cc-gmail rewrites it only when it had to refresh)"
    token_facts

    section "Run 2: stripped environment, the read-only measurements (no forced refresh)"
    echo "\$ env -i PATH=$STRIPPED_PATH uv run --script gmail_probe.py --address \"\$ADDRESS\" --measure"
    env -i PATH="$STRIPPED_PATH" uv run --script "$HERE/gmail_probe.py" --address "$ADDRESS" --measure 2>&1 || echo "exit $?"

    section "Run 3: bogus HOME and bogus LOCALAPPDATA, each holding a decoy store with the same address; forced refresh"
    for base in "$SCRATCH/home/.cc-director/config/gmail" "$SCRATCH/localappdata/cc-director/config/gmail"; do
        mkdir -p "$base/accounts/decoy"
        python3 -c 'import json,sys; json.dump({"email": sys.argv[2], "auth_method": "app_password"}, open(sys.argv[1], "w"))' \
            "$base/accounts/decoy/config.json" "$ADDRESS"
    done
    echo "Decoys: <scratch>/home/.cc-director/config/gmail/accounts/decoy and <scratch>/localappdata/cc-director/config/gmail/accounts/decoy"
    echo "(app_password accounts: if the probe used either, it would fail naming 'decoy' before any network call)"
    echo "\$ env -i PATH=$STRIPPED_PATH HOME=<scratch>/home LOCALAPPDATA=<scratch>/localappdata uv run --script gmail_probe.py --address \"\$ADDRESS\" --force-refresh"
    env -i PATH="$STRIPPED_PATH" HOME="$SCRATCH/home" LOCALAPPDATA="$SCRATCH/localappdata" \
        UV_CACHE_DIR="$REAL_HOME/.cache/uv" \
        uv run --script "$HERE/gmail_probe.py" --address "$ADDRESS" --force-refresh 2>&1 || echo "exit $?"
    echo "(UV_CACHE_DIR is set in run 3 only so uv does not build a fresh cache under the bogus HOME; the probe does not read it)"

    section "token.json after run 3"
    token_facts

    section "cc-gmail reads the account after run 3"
    echo "\$ cc-gmail accounts status $NICKNAME"
    COLUMNS=250 "$REAL_HOME/.local/bin/cc-gmail" accounts status "$NICKNAME" 2>&1 || echo "exit $?"
} 2>&1 | redact > "$OUT"

echo "wrote $OUT"
