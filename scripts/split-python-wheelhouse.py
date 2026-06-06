# Split the python-bundle wheelhouse into a CORE set and an EXTRAS set (issue #174).
#
# Called by scripts/build-python-bundle.ps1 and .sh AFTER the combined wheelhouse is
# fully downloaded and BOTH tools-manifest.json files are written. Both tiers resolve
# from ONE combined lock, so versions stay mutually consistent; this script only
# decides which wheel ships in which zip.
#
# Partition rule (the only correct one - see the importlib-metadata lesson below):
#
#   moved to extras = closure(extras tools) - closure(core tools)
#   core keeps everything else
#
# where both closures are walked over the wheels' own Requires-Dist metadata with
# REAL environment-marker evaluation for the target platform. The extras install runs
# `pip install --no-index --find-links extras-wheelhouse <extras tools>` against a venv
# that contains exactly closure(core) - what pip ACTUALLY installed - so the extras
# wheelhouse must hold precisely the closure difference. A conservative walk (ignoring
# platform/python-version markers) is NOT safe here: it once attributed
# importlib-metadata+zipp to core (a core package wants them only on python<3.x), pip
# then never installed them, and the extras install died unable to find them.
# Wheels reachable from NEITHER closure (platform-false stragglers pip download grabbed,
# e.g. backports-tarfile on py3.12) stay in core; they are tiny and harmless.
#
# Requested extras (e.g. cc-vault[full], httpx[http2]) propagate through the walk
# exactly like pip does. Fails loudly (exit 1) if the partition is inconsistent.
#
# Usage:
#   python split-python-wheelhouse.py --wheelhouse DIR --extras-wheelhouse DIR \
#       --core-manifest FILE --extras-manifest FILE --platform windows|macos \
#       [--python-version 3.12.11]
import argparse, email, json, os, re, shutil, sys, zipfile

norm = lambda n: re.sub(r"[-_.]+", "-", n).lower()

MARKER_ENVS = {
    "windows": {
        "os_name": "nt", "sys_platform": "win32", "platform_system": "Windows",
        "platform_machine": "AMD64", "implementation_name": "cpython",
        "platform_python_implementation": "CPython",
    },
    "macos": {
        "os_name": "posix", "sys_platform": "darwin", "platform_system": "Darwin",
        "platform_machine": "arm64", "implementation_name": "cpython",
        "platform_python_implementation": "CPython",
    },
}


def load_wheels(wheelhouse):
    """name -> {file, deps:[(name, extras, marker)]} from each wheel's METADATA."""
    wheels = {}
    for f in sorted(os.listdir(wheelhouse)):
        if not f.endswith(".whl"):
            continue
        with zipfile.ZipFile(os.path.join(wheelhouse, f)) as z:
            meta_name = next(n for n in z.namelist() if n.endswith(".dist-info/METADATA"))
            msg = email.message_from_bytes(z.read(meta_name))
        deps = []
        for rd in msg.get_all("Requires-Dist") or []:
            marker = None
            if ";" in rd:
                rd, marker = rd.split(";", 1)
                marker = marker.strip()
            m = re.match(r"^\s*([A-Za-z0-9_.\-]+)\s*(?:\[([^\]]*)\])?", rd)
            if m:
                extras = set((m.group(2) or "").replace(" ", "").split(",")) - {""}
                deps.append((norm(m.group(1)), extras, marker))
        wheels[norm(f.split("-")[0])] = {"file": f, "deps": deps}
    return wheels


def make_evaluator(env):
    """A real (not conservative) PEP 508 marker evaluator for the simple marker shapes
    this wheel set uses. Unknown keys/shapes evaluate True (include) - that keeps a wheel
    in core, which can only over-supply core, never starve extras of a CORE-installed dep."""

    def ev(marker, extras):
        if not marker:
            return True
        marker = marker.strip()
        for op, fn in ((" or ", any), (" and ", all)):
            depth, parts, start = 0, [], 0
            for i in range(len(marker)):
                c = marker[i]
                if c == "(":
                    depth += 1
                elif c == ")":
                    depth -= 1
                elif depth == 0 and marker[i:i + len(op)] == op:
                    parts.append(marker[start:i])
                    start = i + len(op)
            if parts:
                parts.append(marker[start:])
                return fn(ev(p, extras) for p in parts)
        if marker.startswith("(") and marker.endswith(")"):
            return ev(marker[1:-1], extras)
        m = re.match(r"""^\s*([a-z_]+)\s*(==|!=|>=|<=|>|<|~=)\s*['"]([^'"]*)['"]\s*$""", marker)
        if not m:
            # also accept the flipped form: '"win32" == sys_platform'
            m2 = re.match(r"""^\s*['"]([^'"]*)['"]\s*(==|!=)\s*([a-z_]+)\s*$""", marker)
            if not m2:
                return True
            val, op2, key = m2.groups()
        else:
            key, op2, val = m.groups()
        if key == "extra":
            return (val in extras) if op2 == "==" else (val not in extras)
        actual = env.get(key)
        if actual is None:
            return True
        if op2 == "==":
            return actual == val
        if op2 == "!=":
            return actual != val
        vt = lambda s: tuple(int(x) for x in re.findall(r"\d+", s)[:3])
        a, b = vt(actual), vt(val)
        return {">=": a >= b, "<=": a <= b, ">": a > b, "<": a < b, "~=": a >= b}[op2]

    return ev


def closure(wheels, roots, ev):
    """All wheelhouse packages reachable from roots ([(name, extras)]), pip-style extras
    propagation, real marker evaluation."""
    seen = {}  # name -> set of extras already walked
    stack = list(roots)
    while stack:
        name, extras = stack.pop()
        if name not in wheels:
            continue  # satisfied outside the wheelhouse (stdlib) or platform-absent
        prev = seen.get(name)
        if prev is not None and extras <= prev:
            continue
        seen[name] = (prev or set()) | extras
        for dep, dep_extras, marker in wheels[name]["deps"]:
            if ev(marker, seen[name]):
                stack.append((dep, set(dep_extras)))
    return set(seen)


def manifest_roots(path):
    """[(dist-name, extras)] from a tools-manifest.json (dist may be 'cc-vault[full]')."""
    roots = []
    for t in json.load(open(path, encoding="utf-8-sig"))["tools"]:
        m = re.match(r"^([A-Za-z0-9_.\-]+)(?:\[([^\]]*)\])?$", t["dist"])
        roots.append((norm(m.group(1)), set((m.group(2) or "").split(",")) - {""}))
    return roots


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--wheelhouse", required=True, help="combined wheelhouse; becomes the CORE wheelhouse")
    ap.add_argument("--extras-wheelhouse", required=True, help="destination dir for the extras-only wheels")
    ap.add_argument("--core-manifest", required=True, help="tools-manifest.json for the core tier")
    ap.add_argument("--extras-manifest", required=True, help="tools-manifest.json for the extras tier")
    ap.add_argument("--platform", required=True, choices=sorted(MARKER_ENVS), help="marker-eval target")
    ap.add_argument("--python-version", default="3.12", help="target python version for marker eval")
    a = ap.parse_args()

    env = dict(MARKER_ENVS[a.platform])
    parts = re.findall(r"\d+", a.python_version)
    env["python_version"] = ".".join(parts[:2])
    env["python_full_version"] = ".".join(parts[:3]) if len(parts) >= 3 else env["python_version"] + ".0"
    ev = make_evaluator(env)

    wheels = load_wheels(a.wheelhouse)
    core_roots = manifest_roots(a.core_manifest)
    extras_roots = manifest_roots(a.extras_manifest)
    if not core_roots:
        sys.exit("ERROR: core manifest lists no tools")
    if not extras_roots:
        sys.exit("ERROR: extras manifest lists no tools - if the extras tier is gone, "
                 "remove the split from the build scripts instead of shipping an empty zip")

    core = closure(wheels, core_roots, ev)         # what pip will install for the core tier
    extras_needs = closure(wheels, extras_roots, ev)
    move = extras_needs - core                     # exactly what the extras install must carry

    # No silent gaps, no leaks:
    orphan_roots = [n for n, _ in extras_roots if n not in move]
    if orphan_roots:
        sys.exit(f"ERROR: extras tool wheel(s) did not land in the move set: {orphan_roots} "
                 "- a core tool depends on an extras tool, fix the tiers")
    missing_core = [n for n, _ in core_roots if n not in core]
    if missing_core:
        sys.exit(f"ERROR: core tool wheel(s) missing from the core closure: {missing_core}")
    unreachable = set(wheels) - core - extras_needs
    if unreachable:
        kept = ", ".join(sorted(unreachable))
        print(f"note: {len(unreachable)} wheel(s) reachable from neither tier stay in core: {kept}")

    os.makedirs(a.extras_wheelhouse, exist_ok=True)
    moved_bytes = 0
    for name in sorted(move):
        f = wheels[name]["file"]
        moved_bytes += os.path.getsize(os.path.join(a.wheelhouse, f))
        shutil.move(os.path.join(a.wheelhouse, f), os.path.join(a.extras_wheelhouse, f))

    print(f"core wheelhouse:   {len(wheels) - len(move)} wheels")
    print(f"extras wheelhouse: {len(move)} wheels ({moved_bytes / 1e6:.1f} MB moved)")


if __name__ == "__main__":
    main()
