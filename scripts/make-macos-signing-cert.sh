#!/usr/bin/env bash
#
# make-macos-signing-cert.sh — Create the ONE stable self-signed certificate that every macOS
# build is signed with, and print what to paste into the repository's GitHub secrets.
#
# RUN THIS ONCE, EVER. The whole point is that the certificate never changes.
#
# ---------------------------------------------------------------------------------------------
# WHY THIS EXISTS (issue #4518)
#
# macOS records a privacy permission ("DevThrottle would like to access data from other apps",
# Screen Recording, the microphone) against the application's DESIGNATED REQUIREMENT - the rule
# that decides which code counts as this app. Ad-hoc signing has no certificate to name us by, so
# that rule degenerates to a hash of the build:
#
#     designated => cdhash H"499b1ed8..."          <- ad-hoc: a DIFFERENT app every build
#
# The Director auto-updates on start-up and ships every day or two, so every update made every
# stored grant stop matching and the user was asked again, forever. Signing every build with one
# stable certificate gives a rule that survives rebuilds:
#
#     designated => identifier "com.devthrottle.ccdirector" and certificate leaf = H"65f4a7a8..."
#
# The certificate does NOT have to come from Apple, and does NOT have to be trusted on the machine:
# codesign signs happily with an untrusted self-signed certificate and the requirement matches on
# the leaf hash. Measured on devthrottle-mac-mini, 5 November 2026: two deliberately different
# builds signed with one such certificate produced byte-identical designated requirements.
#
#
# --timestamp=none IS A THIRD THING THE APPLE MIGRATION MUST CHANGE. Every comment here says the
# Developer ID switch is the two secrets plus notarize and staple. It is not: notarization refuses a
# signature with no secure timestamp, so this flag has to go in all four places that carry it -
# scripts/package-mac-app.sh (two) and .github/workflows/release.yml (two). It is off today because
# an untrusted self-signed certificate has no timestamp authority to ask, and asking adds a network
# call to every build for nothing.
#
# WHAT THIS DOES NOT BUY. A self-signed certificate is still an unknown developer to Gatekeeper.
# It does nothing for the download-and-double-click install, for notarization, or for the name
# shown in Login Items. Those need a paid Apple Developer ID certificate, and when one exists it
# replaces this certificate by changing the same two secrets - nothing else in the pipeline moves.
#
# ---------------------------------------------------------------------------------------------
# IF THE KEY IS EVER LOST OR ROTATED, every user grants every permission once more. That is the
# only cost, but it is the reason this is generated once and kept, not generated per build.
#
# Usage:
#   scripts/make-macos-signing-cert.sh [--out <dir>] [--name "<certificate common name>"]
#
# --out defaults to ~/.devthrottle-signing and must be OUTSIDE every git repository: this writes a
# private signing key, and the script refuses to put one in a working tree.
#
set -euo pipefail

# NOT the working directory. This script writes a CODE-SIGNING PRIVATE KEY, and the documented way
# to run it is from the repository root, so a default of $(pwd) put the key in the working tree one
# `git add -A` away from being committed - and nothing in .gitignore covered a .p12. The key is also
# the thing whose loss costs every user a fresh round of permission prompts, so it does not belong
# anywhere a `git clean` can reach either.
OUT_DIR="$HOME/.devthrottle-signing"
CN="DevThrottle Code Signing"
DAYS=7300   # 20 years. An expired certificate would orphan every grant on the same day.

while [[ $# -gt 0 ]]; do
    case "$1" in
        --out)  OUT_DIR="$2"; shift 2 ;;
        --name) CN="$2"; shift 2 ;;
        -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

# Refuse to write the key inside a git working tree, wherever --out points. This is cheap, and the
# alternative is a private key in a commit, which cannot be undone by deleting it in a later one.
#
# The test is made on the nearest EXISTING ancestor, so a path that does not exist yet is judged by
# where it would be created rather than by creating it: asking git about a missing directory answers
# nothing, and creating it first would leave an empty directory behind inside the repository it was
# just refused for.
PROBE="$OUT_DIR"
while [[ ! -d "$PROBE" && "$PROBE" != "/" && -n "$PROBE" ]]; do
    PARENT="$(dirname "$PROBE")"
    [[ "$PARENT" == "$PROBE" ]] && break
    PROBE="$PARENT"
done

if TOPLEVEL="$(git -C "$PROBE" rev-parse --show-toplevel 2>/dev/null)" && [[ -n "$TOPLEVEL" ]]; then
    echo "ERROR: $OUT_DIR is inside the git repository $TOPLEVEL" >&2
    echo "       This script writes a PRIVATE SIGNING KEY. Choose --out outside every repository." >&2
    echo "       The default, $HOME/.devthrottle-signing, is such a place." >&2
    exit 1
fi

mkdir -p "$OUT_DIR"
chmod 700 "$OUT_DIR"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# A password protects the .p12 at rest and in the GitHub secret.
P12_PASSWORD="$(openssl rand -base64 24)"

cat > "$WORK/ext.cnf" <<EOF
[req]
distinguished_name = dn
x509_extensions    = v3
prompt             = no
[dn]
CN = $CN
[v3]
basicConstraints   = critical,CA:false
keyUsage           = critical,digitalSignature
extendedKeyUsage   = critical,codeSigning
EOF

openssl req -x509 -newkey rsa:2048 -nodes -days "$DAYS" \
    -keyout "$WORK/key.pem" -out "$WORK/cert.pem" -config "$WORK/ext.cnf" 2>/dev/null

# macOS `security import` cannot read OpenSSL 3's PKCS#12 defaults (AES-256 + SHA-256 MAC); it
# fails with "MAC verification failed during PKCS12 import (wrong password?)". The legacy
# algorithms below are what it can actually open.
openssl pkcs12 -export \
    -out "$OUT_DIR/devthrottle-codesign.p12" \
    -inkey "$WORK/key.pem" -in "$WORK/cert.pem" \
    -name "$CN" -passout "pass:$P12_PASSWORD" \
    -legacy -macalg sha1 -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES 2>/dev/null

# Readable by this user alone. The default umask leaves it world-readable on most machines.
chmod 600 "$OUT_DIR/devthrottle-codesign.p12"

SHA1="$(openssl x509 -in "$WORK/cert.pem" -noout -fingerprint -sha1 \
        | sed 's/.*=//; s/://g')"

cat <<EOF

Created: $OUT_DIR/devthrottle-codesign.p12
Common name:       $CN
Leaf SHA-1:        $SHA1
Valid for:         $DAYS days

Every build signed with this certificate will carry:

    designated => identifier "<bundle id>" and certificate leaf = H"$(echo "$SHA1" | tr 'A-Z' 'a-z')"

---------------------------------------------------------------------------------------------
Store these two as GitHub repository SECRETS (Settings -> Secrets and variables -> Actions):

  MACOS_CERT_P12_BASE64
$(base64 < "$OUT_DIR/devthrottle-codesign.p12" | tr -d '\n' | sed 's/^/    /')

  MACOS_CERT_PASSWORD
    $P12_PASSWORD

Then KEEP A COPY of the .p12 and the password somewhere durable and private - a password manager,
not this repository. Losing them costs every user one more round of permission prompts.

Until both secrets exist the release jobs sign ad-hoc exactly as before, so nothing breaks by
creating this file and not wiring it up yet.
---------------------------------------------------------------------------------------------
EOF
