#!/usr/bin/env bash
#
# Build the macOS CC Director Python tools bundle - the mac analog of build-python-bundle.ps1.
# Produces two release assets under OUT_DIR (default dist/python-bundle):
#
#   cc-python-macos-arm64.tar.gz       relocatable python-build-standalone CPython, laid out flat so
#                                      extracting into PythonDir yields PythonDir/bin/python3
#   cc-tools-pyenv-macos-arm64.tar.gz  wheelhouse/ (de-duped dep wheels + every tool wheel) +
#                                      requirements.lock + tools-manifest.json
#
# The installer (PythonToolsInstaller) extracts the python, runs `python3 -m venv`, then
# `pip install --no-index --find-links wheelhouse <tools>` fully offline, and symlinks each tool's
# console script into ~/.local/bin.
#
# Requires: uv (https://astral.sh) + python3 + tar on PATH. Apple Silicon (arm64).
# Usage: bash scripts/build-python-bundle.sh [OUT_DIR]   (env: PY_VERSION, default 3.12)
set -euo pipefail

PY_VERSION="${PY_VERSION:-3.12}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="${1:-$REPO_ROOT/dist/python-bundle}"
cd "$REPO_ROOT"

step() { echo "[build-python-bundle] $*"; }
fail() { echo "[build-python-bundle] ERROR: $*" >&2; exit 1; }

command -v uv >/dev/null 2>&1 || fail "uv not found on PATH"
command -v python3 >/dev/null 2>&1 || fail "python3 not found on PATH"

WORK="$REPO_ROOT/build/python-bundle"
WHEELHOUSE="$WORK/wheelhouse"
PYSTAGE="$WORK/python"
rm -rf "$WORK"
mkdir -p "$WHEELHOUSE" "$OUT_DIR"

# ---- 1. Select the built Python tools from the registry --------------------------------------
step "reading tools/registry.json"
TOOL_DIRS=()
while IFS= read -r line; do TOOL_DIRS+=("$line"); done < <(python3 - <<'PY'
import json, os
reg = json.load(open("tools/registry.json"))
for t in reg["tools"]:
    if t.get("type") == "python" and t.get("built") and t["name"] != "cc-director-setup":
        print(os.path.join("tools", t.get("source_dir") or t["name"]))
PY
)
[ "${#TOOL_DIRS[@]}" -gt 0 ] || fail "no built python tools in registry"
step "selected ${#TOOL_DIRS[@]} built python tools"

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
built = {t["name"] for t in registry["tools"] if t.get("type")=="python" and t.get("built") and t["name"]!="cc-director-setup"}
ours = {"cc-shared", "cc-storage"} | built
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
    if name not in built: continue
    add(pp, extras=("full",) if name == "cc-vault" else ())   # cc-vault ships converters under [full]
add("tools/cc_shared/pyproject.toml"); add("tools/cc_storage/pyproject.toml")
open(sys.argv[1], "w").write("\n".join(sorted(reqs)) + "\n")
print(f"{len(reqs)} third-party requirement lines")
PY

step "compiling pinned lock (python $PY_VERSION / macos-arm64)"
uv pip compile "$WORK/thirdparty.in" \
    --python-version "$PY_VERSION" --python-platform aarch64-apple-darwin \
    --no-annotate --no-header -o "$WORK/requirements.lock" \
    || fail "combined lock did not resolve for macos-arm64 (dependency conflict / missing arm64 wheel - see plan contingency)"
grep '==' "$WORK/requirements.lock" > "$WORK/download.txt"
step "locked third-party deps: $(grep -c '==' "$WORK/requirements.lock" | tr -d ' ')"

# ---- 4. Fill the wheelhouse with the third-party closure (arm64 wheels) ----------------------
# Some pure-python deps are sdist-only on PyPI (e.g. GPUtil); --only-binary rejects them, so download
# what we can, then build universal wheels for the stragglers. Pass several macos arm64 / universal2
# platform tags so pip can pick the best compatible wheel for each package.
step "downloading macos-arm64 wheels for the locked deps"
PYV="$(echo "$PY_VERSION" | tr -d '.')"   # 3.12 -> 312
PLATFORMS=(--platform macosx_11_0_arm64 --platform macosx_12_0_arm64 --platform macosx_13_0_arm64 \
           --platform macosx_14_0_arm64 --platform macosx_11_0_universal2 --platform macosx_10_9_universal2)
if ! dl_out="$(python3 -m pip download --only-binary=:all: --python-version "$PYV" "${PLATFORMS[@]}" \
                 -r "$WORK/download.txt" -d "$WHEELHOUSE" 2>&1)"; then
    echo "$dl_out"
    missing="$(echo "$dl_out" | sed -n 's/.*No matching distribution found for \([^ ]*\).*/\1/p')"
    [ -n "$missing" ] || fail "pip download failed for a non-sdist reason (see output above)"
    step "sdist-only packages need a built wheel: $(echo "$missing" | tr '\n' ' ')"
    names="$(echo "$missing" | sed 's/==.*//' | paste -sd'|' -)"
    grep -viE "^(${names})==" "$WORK/download.txt" > "$WORK/download2.txt" || true
    python3 -m pip download --only-binary=:all: --python-version "$PYV" "${PLATFORMS[@]}" \
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
BUNDLE_VERSION="$(grep -oE '<Version>[^<]+</Version>' src/CcDirector.Avalonia/CcDirector.Avalonia.csproj | head -1 | sed -E 's#</?Version>##g')"
python3 - "$WORK/tools-manifest.json" "$BUNDLE_VERSION" "$EXACTVER" <<'PY'
import json, os, sys, tomllib
out, bundle, pyver = sys.argv[1], sys.argv[2], sys.argv[3]
reg = json.load(open("tools/registry.json"))
tools = []
for t in reg["tools"]:
    if not (t.get("type") == "python" and t.get("built") and t["name"] != "cc-director-setup"):
        continue
    d = os.path.join("tools", t.get("source_dir") or t["name"])
    pp = tomllib.load(open(os.path.join(d, "pyproject.toml"), "rb"))
    scripts = list((pp.get("project", {}).get("scripts") or {}).keys())
    dist = "cc-vault[full]" if t["name"] == "cc-vault" else t["name"]   # cc-vault converters are under [full]
    tools.append({"id": t["name"], "dist": dist, "scripts": scripts})
json.dump({"bundleVersion": bundle, "pythonVersion": pyver, "tools": tools}, open(out, "w"), indent=2)
print(f"{len(tools)} tools")
PY

# ---- 7. Pack the two assets (.tar.gz preserves +x bits and symlinks) -------------------------
step "packaging assets into $OUT_DIR"
PYTGZ="$OUT_DIR/cc-python-macos-arm64.tar.gz"
TOOLSTGZ="$OUT_DIR/cc-tools-pyenv-macos-arm64.tar.gz"
rm -f "$PYTGZ" "$TOOLSTGZ"
tar -czf "$PYTGZ" -C "$PYSTAGE" .
tar -czf "$TOOLSTGZ" -C "$WORK" wheelhouse requirements.lock tools-manifest.json

bytes() { stat -f%z "$1" 2>/dev/null || stat -c%s "$1"; }
pymb=$(( $(bytes "$PYTGZ") / 1048576 ))
toolsmb=$(( $(bytes "$TOOLSTGZ") / 1048576 ))
wheels="$(ls "$WHEELHOUSE"/*.whl | wc -l | tr -d ' ')"
step "DONE. python=${pymb}MB, tools-pyenv=${toolsmb}MB ($wheels wheels), total=$((pymb + toolsmb))MB"
step "assets: $PYTGZ ; $TOOLSTGZ"
