# Audit script for issue #174: attribute wheelhouse weight to top-level deps.
# Fully OFFLINE: builds the dependency graph from the wheelhouse wheels' own
# METADATA (exact pinned versions), evaluates environment markers for the
# bundle target (win_amd64 / cp312), and computes per-top-level closures.
#
# Usage: python scripts/audit-python-bundle.py [bundle-work-dir]
#   bundle-work-dir defaults to <repo>/build/python-bundle (the output of
#   scripts/build-python-bundle.ps1) and must contain wheelhouse/ + thirdparty.in.
import os, re, json, sys, zipfile, email

WORK = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "build", "python-bundle")
WHEELHOUSE = os.path.join(WORK, "wheelhouse")
if not os.path.isdir(WHEELHOUSE):
    sys.exit(f"ERROR: no wheelhouse at {WHEELHOUSE} - run scripts/build-python-bundle.ps1 first "
             "or pass the bundle work dir as the first argument")

ENV = {  # marker environment for the bundle target
    "os_name": "nt", "sys_platform": "win32", "platform_system": "Windows",
    "platform_machine": "AMD64", "python_version": "3.12",
    "python_full_version": "3.12.12", "implementation_name": "cpython",
    "platform_python_implementation": "CPython",
}

norm = lambda n: n.lower().replace("_", "-").replace(".", "-")

def eval_marker(marker, extra=None):
    """Crude but sufficient marker evaluation for this wheel set."""
    if not marker:
        return True
    # split on 'and' / 'or' respecting no parens nesting beyond simple cases
    marker = marker.strip()
    # handle parenthesised groups by recursion
    if marker.startswith("(") and marker.endswith(")"):
        return eval_marker(marker[1:-1], extra)
    for op, fn in ((" or ", any), (" and ", all)):
        depth, parts, start = 0, [], 0
        for i in range(len(marker)):
            if marker[i] == "(":
                depth += 1
            elif marker[i] == ")":
                depth -= 1
            elif depth == 0 and marker[i:i+len(op)] == op:
                parts.append(marker[start:i]); start = i + len(op)
        if parts:
            parts.append(marker[start:])
            return fn(eval_marker(p, extra) for p in parts)
    m = re.match(r"""^\s*([a-z_]+)\s*(==|!=|>=|<=|>|<|~=)\s*['"]([^'"]*)['"]\s*$""", marker)
    if not m:
        m2 = re.match(r"""^\s*['"]([^'"]*)['"]\s*(==|!=)\s*([a-z_]+)\s*$""", marker)
        if m2:
            val, op2, key = m2.groups()
            m = type("M", (), {"groups": lambda self: (key, op2, val)})()
        else:
            return True  # unknown -> include (conservative)
    key, op2, val = m.groups()
    if key == "extra":
        have = extra or set()
        return (val in have) if op2 == "==" else (val not in have)
    actual = ENV.get(key)
    if actual is None:
        return True
    if op2 == "==": return actual == val
    if op2 == "!=": return actual != val
    # version comparisons on dotted strings
    def vt(s): return tuple(int(x) for x in re.findall(r"\d+", s)[:3])
    a, b = vt(actual), vt(val)
    return {">=": a >= b, "<=": a <= b, ">": a > b, "<": a < b, "~=": a >= b}[op2]

# load all wheels: name -> {size, deps: [(name, marker)]}
wheels = {}
for f in os.listdir(WHEELHOUSE):
    if not f.endswith(".whl"):
        continue
    dist = norm(f.split("-")[0])
    path = os.path.join(WHEELHOUSE, f)
    deps = []
    with zipfile.ZipFile(path) as z:
        meta_name = next(n for n in z.namelist() if n.endswith(".dist-info/METADATA"))
        msg = email.message_from_bytes(z.read(meta_name))
        for rd in msg.get_all("Requires-Dist") or []:
            # "name[extras] (specifier) ; marker"  or  "name[extras]>=x ; marker"
            marker = None
            if ";" in rd:
                rd, marker = rd.split(";", 1)
                marker = marker.strip()
            m = re.match(r"^\s*([A-Za-z0-9_.\-]+)\s*(?:\[([^\]]*)\])?", rd)
            if m:
                deps.append((norm(m.group(1)), set((m.group(2) or "").replace(" ", "").split(",")) - {""}, marker))
    wheels[dist] = {"size": os.path.getsize(path), "deps": deps, "file": f}

def closure(root, root_extras=frozenset()):
    seen = {}
    stack = [(root, frozenset(root_extras))]
    while stack:
        name, extras = stack.pop()
        if name not in wheels:
            continue
        prev = seen.get(name)
        if prev is not None and extras <= prev:
            continue
        seen[name] = (prev or frozenset()) | extras
        for dep, dep_extras, marker in wheels[name]["deps"]:
            if eval_marker(marker, extras):
                stack.append((dep, frozenset(dep_extras)))
    return set(seen)

tops = []
for l in open(os.path.join(WORK, "thirdparty.in")):
    l = l.strip()
    if not l:
        continue
    m = re.match(r"^([A-Za-z0-9_.\-]+)\s*(?:\[([^\]]*)\])?", l)
    tops.append((l, norm(m.group(1)), frozenset((m.group(2) or "").replace(" ", "").split(",")) - {""}))

results = {}
for raw, name, extras in tops:
    pkgs = closure(name, extras)
    mb = sum(wheels[p]["size"] for p in pkgs if p in wheels) / 1e6
    results[raw] = {"root": name, "count": len(pkgs), "mb": round(mb, 1), "pkgs": sorted(pkgs)}

# unique attribution
for raw in results:
    others = set()
    for o in results:
        if o != raw:
            others |= set(results[o]["pkgs"])
    uniq = [p for p in results[raw]["pkgs"] if p not in others]
    results[raw]["unique_pkgs"] = uniq
    results[raw]["unique_mb"] = round(sum(wheels[p]["size"] for p in uniq if p in wheels) / 1e6, 1)

json.dump(results, open(os.path.join(WORK, "audit_closures.json"), "w"), indent=1)

print(f"{'requirement':<42} {'clos#':>5} {'closMB':>7} {'uniq#':>5} {'uniqMB':>7}")
for raw, d in sorted(results.items(), key=lambda kv: -kv[1]["unique_mb"]):
    print(f"{raw:<42} {d['count']:>5} {d['mb']:>7} {len(d['unique_pkgs']):>5} {d['unique_mb']:>7}")

reached = set()
for d in results.values():
    reached |= set(d["pkgs"])
total = sum(w["size"] for w in wheels.values()) / 1e6
print(f"\nwheelhouse: {len(wheels)} wheels, {total:.1f} MB")
print("packages NOT reached by any top-level closure (excl. our cc-*):")
for p in sorted(wheels):
    if p not in reached and not p.startswith("cc-"):
        print(f"  {p}  ({wheels[p]['size']/1e6:.1f} MB)  [{wheels[p]['file']}]")

# detail: unique closure members for the big four
for raw in results:
    if results[raw]["unique_mb"] >= 5:
        d = results[raw]
        print(f"\n== {raw}  (unique {d['unique_mb']} MB) ==")
        for p in sorted(d["unique_pkgs"], key=lambda p: -wheels.get(p, {"size": 0})["size"]):
            print(f"  {wheels[p]['size']/1e6:7.1f} MB  {p}")
