#!/usr/bin/env bash
#
# Build the CC Director Python tools bundle for the CURRENT Unix platform - the macOS and Linux
# analog of build-python-bundle.ps1. Produces two release assets under OUT_DIR (default
# dist/python-bundle), named for the platform this runs on:
#
#   cc-python-<platform>.tar.gz        relocatable python-build-standalone CPython, laid out flat
#                                      so extracting into PythonDir yields PythonDir/bin/python3
#   cc-tools-pyenv-<platform>.tar.gz   wheelhouse/ (de-duped dep wheels + the core tool wheels)
#                                      + requirements.lock + tools-manifest.json
#
# <platform> is "macos-arm64" on Apple Silicon and "linux-x64" on 64-bit Intel/AMD Linux. This is
# ONE script with a platform switch rather than a per-platform copy: the tool selection, the
# dependency closure and the manifest are identical everywhere, and two copies of that logic would
# drift the first time the registry changed. Only three things differ - the asset names, the uv
# resolver target, and the pip wheel platform tags - and they are set once, below.
#
# Core is an explicit allowlist: a tool ships ONLY when it has "ship": true in tools/registry.json.
# Non-core tools stay in the repo (buildable for dev) but never enter the bundle. There is no
# longer a core/extras split.
#
# The installer (PythonToolsInstaller) extracts the python, runs `python3 -m venv`, then
# `pip install --no-index --find-links wheelhouse <tools>` fully offline, and symlinks each tool's
# console script into ~/.local/bin.
#
# Requires: uv (https://astral.sh) + python3 + tar on PATH.
# Supported hosts: macOS on Apple Silicon (arm64), Linux on x86_64.
# Usage: bash scripts/build-python-bundle.sh [OUT_DIR]   (env: PY_VERSION, default 3.12)
set -euo pipefail

PY_VERSION="${PY_VERSION:-3.12}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="${1:-$REPO_ROOT/dist/python-bundle}"
cd "$REPO_ROOT"

step() { echo "[build-python-bundle] $*"; }
fail() { echo "[build-python-bundle] ERROR: $*" >&2; exit 1; }

# ---- 0. Resolve the target platform -----------------------------------------------------------
# PLATFORM_TAG feeds the two asset names, and those names are the keys the installer looks up in
# the release manifest (PythonToolsInstaller.PythonAsset / .ToolsAsset). Changing a name here
# without changing it there ships a bundle that nothing ever fetches.
#
# UV_PLATFORM is the resolver target for the pinned lock and PIP_PLATFORMS are the wheel tags pip
# may choose from. Both describe the TARGET, never the build host - otherwise a wheel built for the
# runner sneaks into a bundle that has to run somewhere else.
case "$(uname -s)-$(uname -m)" in
    Darwin-arm64)
        PLATFORM_TAG="macos-arm64"
        UV_PLATFORM="aarch64-apple-darwin"
        PIP_PLATFORMS=(--platform macosx_11_0_arm64 --platform macosx_12_0_arm64 \
                       --platform macosx_13_0_arm64 --platform macosx_14_0_arm64 \
                       --platform macosx_11_0_universal2 --platform macosx_10_9_universal2)
        ;;
    Linux-x86_64)
        PLATFORM_TAG="linux-x64"
        UV_PLATFORM="x86_64-unknown-linux-gnu"
        # manylinux tags newest first, so pip prefers the most modern wheel each package offers;
        # the bare linux_x86_64 tag at the end catches the few published without a manylinux tag.
        PIP_PLATFORMS=(--platform manylinux_2_28_x86_64 --platform manylinux_2_17_x86_64 \
                       --platform manylinux2014_x86_64 --platform manylinux2010_x86_64 \
                       --platform manylinux1_x86_64 --platform linux_x86_64)
        ;;
    *)
        fail "unsupported host '$(uname -s)-$(uname -m)'. This script builds the bundle for macOS arm64 or Linux x86_64; Windows uses scripts/build-python-bundle.ps1."
        ;;
esac

command -v uv >/dev/null 2>&1 || fail "uv not found on PATH"
command -v python3 >/dev/null 2>&1 || fail "python3 not found on PATH"
# Step 4 fills the wheelhouse with "python3 -m pip download", and a python3 WITHOUT pip is a real
# and common state - Debian and Ubuntu ship pip as a separate python3-pip package, so a stock
# Ubuntu 24.04 has python3 and no pip at all. Left unchecked it surfaces two hundred lines later as
# "No module named pip" swallowed inside a pip-download error branch that then reports a wrong
# cause ("failed for a non-sdist reason"). Check it here, where the message can name the fix.
python3 -m pip --version >/dev/null 2>&1 || fail "python3 has no pip module. Install it first, e.g. 'sudo apt-get install -y python3-pip' on Ubuntu or Debian; on a GitHub runner use actions/setup-python, which provides one."

WORK="$REPO_ROOT/build/python-bundle"
WHEELHOUSE="$WORK/wheelhouse"
PYSTAGE="$WORK/python"
rm -rf "$WORK"
mkdir -p "$WHEELHOUSE" "$OUT_DIR"

# ---- 1. Select the SHIPPED Python tools from the registry ------------------------------------
# Core is an explicit allowlist: a tool ships only when it has "ship": true in tools/registry.json.
step "reading tools/registry.json"
TOOL_DIRS=()
while IFS= read -r line; do TOOL_DIRS+=("$line"); done < <(python3 - <<'PY'
import json, os
reg = json.load(open("tools/registry.json"))
for t in reg["tools"]:
    if t.get("type") == "python" and t.get("ship"):
        print(os.path.join("tools", t.get("source_dir") or t["name"]))
PY
)
[ "${#TOOL_DIRS[@]}" -gt 0 ] || fail "no shipped python tools in registry (set \"ship\": true on core tools)"
step "selected ${#TOOL_DIRS[@]} shipped python tools"

# ---- 2. Build every tool + shared-lib wheel into the wheelhouse ------------------------------
step "building tool + shared-lib wheels"
for d in tools/cc_shared tools/cc_storage "${TOOL_DIRS[@]}"; do
    [ -f "$d/pyproject.toml" ] || fail "missing pyproject.toml in $d"
    # Clean stale build/egg-info so setuptools never re-includes a leftover 'src' package.
    rm -rf "$d/build" "$d"/*.egg-info
    uv build --wheel "$d" -o "$WHEELHOUSE" >/dev/null || fail "wheel build failed for $d"
done

# ---- 3. Build the THIRD-PARTY requirement set, then resolve a pinned lock --------------------
# Never feed our cc-* distribution names to the resolver (a real "cc-vault" exists on PyPI); resolve
# only third-party deps (parsed from pyprojects, excluding every cc-* name). Inter-tool deps are
# satisfied at install time from the wheelhouse with --no-index.
step "collecting third-party dependencies from tool pyprojects"
python3 - "$WORK/thirdparty.in" <<'PY'
import tomllib, glob, os, re, sys, json
registry = json.load(open("tools/registry.json"))
ship = {t["name"] for t in registry["tools"] if t.get("type")=="python" and t.get("ship")}
allcc = {t["name"] for t in registry["tools"]}     # every cc-* name (any type)
ours = {"cc-shared", "cc-storage"} | allcc         # our own dists never come from PyPI
norm = lambda r: re.split(r"[<>=!~ \[]", r.strip(), 1)[0].lower().replace("_", "-")
reqs = set()
def add(pp, extras=()):
    d = tomllib.load(open(pp, "rb")); proj = d.get("project", {})
    for r in (proj.get("dependencies") or []):
        if norm(r) not in ours: reqs.add(r.strip())
    opt = proj.get("optional-dependencies") or {}
    for e in extras:
        for r in (opt.get(e) or []):
            if norm(r) not in ours: reqs.add(r.strip())
for pp in glob.glob("tools/cc-*/pyproject.toml"):
    name = os.path.basename(os.path.dirname(pp))
    if name not in ship: continue                  # only shipped tools' deps enter the wheelhouse
    add(pp, extras=("full",) if name == "cc-vault" else ())   # cc-vault ships converters under [full]
add("tools/cc_shared/pyproject.toml"); add("tools/cc_storage/pyproject.toml")
open(sys.argv[1], "w").write("\n".join(sorted(reqs)) + "\n")
print(f"{len(reqs)} third-party requirement lines")
PY

step "compiling pinned lock (python $PY_VERSION / $PLATFORM_TAG)"
uv pip compile "$WORK/thirdparty.in" \
    --python-version "$PY_VERSION" --python-platform "$UV_PLATFORM" \
    --no-annotate --no-header -o "$WORK/requirements.lock" \
    || fail "combined lock did not resolve for $PLATFORM_TAG (dependency conflict / missing wheel - see plan contingency)"
grep '==' "$WORK/requirements.lock" > "$WORK/download.txt"
step "locked third-party deps: $(grep -c '==' "$WORK/requirements.lock" | tr -d ' ')"

# ---- 4. Fill the wheelhouse with the third-party closure (target-platform wheels) ------------
# Some pure-python deps are sdist-only on PyPI (e.g. GPUtil); --only-binary rejects them, so download
# what we can, then build wheels for the stragglers. PIP_PLATFORMS (set at the top) carries several
# tags for the target platform so pip can pick the best compatible wheel for each package.
step "downloading $PLATFORM_TAG wheels for the locked deps"
PYV="$(echo "$PY_VERSION" | tr -d '.')"   # 3.12 -> 312
if ! dl_out="$(python3 -m pip download --only-binary=:all: --python-version "$PYV" "${PIP_PLATFORMS[@]}" \
                 -r "$WORK/download.txt" -d "$WHEELHOUSE" 2>&1)"; then
    echo "$dl_out"
    missing="$(echo "$dl_out" | sed -n 's/.*No matching distribution found for \([^ ]*\).*/\1/p')"
    [ -n "$missing" ] || fail "pip download failed for a non-sdist reason (see output above)"
    step "sdist-only packages need a built wheel: $(echo "$missing" | tr '\n' ' ')"
    names="$(echo "$missing" | sed 's/==.*//' | paste -sd'|' -)"
    grep -viE "^(${names})==" "$WORK/download.txt" > "$WORK/download2.txt" || true
    python3 -m pip download --only-binary=:all: --python-version "$PYV" "${PIP_PLATFORMS[@]}" \
        -r "$WORK/download2.txt" -d "$WHEELHOUSE" || fail "pip download failed after filtering sdist-only packages"
    for m in $missing; do
        python3 -m pip wheel "$m" --no-deps -w "$WHEELHOUSE" || fail "could not build a wheel for sdist-only package $m"
    done
fi

# ---- 5. Stage the python-build-standalone CPython (flat: PYSTAGE/bin/python3) -----------------
step "provisioning python-build-standalone $PY_VERSION via uv"
uv python install "$PY_VERSION" >/dev/null || fail "uv python install $PY_VERSION failed"
PYEXE="$(uv python find "$PY_VERSION")"
[ -x "$PYEXE" ] || fail "could not locate the provisioned python $PY_VERSION (got: $PYEXE)"
PYROOT="$(cd "$(dirname "$PYEXE")/.." && pwd)"   # .../bin/python3 -> the standalone root
EXACTVER="$("$PYEXE" -c 'import platform; print(platform.python_version())')"
step "bundling CPython $EXACTVER from $PYROOT"
mkdir -p "$PYSTAGE"
cp -R "$PYROOT/." "$PYSTAGE/"   # flat copy so the archive extracts to PythonDir/bin/python3

# ---- 6. Write the tools manifest -------------------------------------------------------------
step "writing tools-manifest.json"
# Product version lives in Directory.Build.props (single source, see docs/architecture/VERSIONING.md)
BUNDLE_VERSION="$(grep -oE '<Version>[^<]+</Version>' Directory.Build.props | head -1 | sed -E 's#</?Version>##g')"
python3 - "$WORK/tools-manifest.json" "$BUNDLE_VERSION" "$EXACTVER" <<'PY'
import json, os, sys, tomllib
out, bundle, pyver = sys.argv[1], sys.argv[2], sys.argv[3]
reg = json.load(open("tools/registry.json"))
tools = []
for t in reg["tools"]:
    if not (t.get("type") == "python" and t.get("ship")):
        continue
    d = os.path.join("tools", t.get("source_dir") or t["name"])
    pp = tomllib.load(open(os.path.join(d, "pyproject.toml"), "rb"))
    scripts = list((pp.get("project", {}).get("scripts") or {}).keys())
    dist = "cc-vault[full]" if t["name"] == "cc-vault" else t["name"]   # cc-vault converters are under [full]
    tools.append({"id": t["name"], "dist": dist, "scripts": scripts})
if not tools: sys.exit("ERROR: no shipped python tools in registry (set \"ship\": true on core tools)")
json.dump({"bundleVersion": bundle, "pythonVersion": pyver, "tools": tools}, open(out, "w"), indent=2)
print(f"{len(tools)} shipped tools")
PY

# ---- 7. Pack the two assets (.tar.gz preserves +x bits and symlinks) -------------------------
step "packaging assets into $OUT_DIR"
PYTGZ="$OUT_DIR/cc-python-$PLATFORM_TAG.tar.gz"
TOOLSTGZ="$OUT_DIR/cc-tools-pyenv-$PLATFORM_TAG.tar.gz"
rm -f "$PYTGZ" "$TOOLSTGZ"
tar -czf "$PYTGZ" -C "$PYSTAGE" .
tar -czf "$TOOLSTGZ" -C "$WORK" wheelhouse requirements.lock tools-manifest.json

bytes() { stat -f%z "$1" 2>/dev/null || stat -c%s "$1"; }
pymb=$(( $(bytes "$PYTGZ") / 1048576 ))
toolsmb=$(( $(bytes "$TOOLSTGZ") / 1048576 ))
wheels="$(ls "$WHEELHOUSE"/*.whl | wc -l | tr -d ' ')"
step "DONE. python=${pymb}MB, tools-pyenv=${toolsmb}MB ($wheels wheels)"
step "assets: $PYTGZ ; $TOOLSTGZ"
