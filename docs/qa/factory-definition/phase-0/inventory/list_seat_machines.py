"""Phase 0 (f): every machine a factory seat runs on today, with the repositories registered on it.

Read only. A seat "runs on" a machine when the registry names that machine for the seat, or a schedule or
trigger of the factory targets it. For each such machine this lists whether its Director is online and at
what version, every repository registered on it (cc-devthrottle repo list), and every folder a seat on that
machine starts in - resolved, as the mission's rule in 5.5 step 6 will, to a registered repository by name.
A folder that resolves to no registered repository on its machine is printed as UNRESOLVED: under the new
rule that seat would be refused at publish and at start until the repository is registered there.

Usage:  python list_seat_machines.py [--json out.json]
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from datetime import datetime, timezone

CLI = shutil.which("cc-devthrottle")
if CLI is None:
    sys.exit("ERROR: cc-devthrottle is not on the PATH. Install DevThrottle (cc-devthrottle setup) and run again.")


def gateway(*args):
    p = subprocess.run([CLI, *args], capture_output=True, text=True, encoding="utf-8", errors="replace")
    if p.returncode != 0:
        sys.exit(f"ERROR: cc-devthrottle {' '.join(args)} failed: {(p.stderr or p.stdout).strip()}")
    return json.loads(p.stdout)


def norm(path):
    return (path or "").replace("\\", "/").rstrip("/").lower()


def resolve(folder, repos):
    """The registered repository whose path is the folder or contains it - the longest such path wins."""
    f = norm(folder)
    best = None
    for r in repos:
        rp = norm(r["path"])
        if rp and (f == rp or f.startswith(rp + "/")) and (best is None or len(rp) > len(norm(best["path"]))):
            best = r
    return best


def mask(folder):
    """A start folder that is neither a registered repository nor its factory's registered folder is printed with
    its last part hidden:
    such folders are personal projects and can carry a third party's name, and this record is public."""
    f = (folder or "").replace("\\", "/").rstrip("/")
    return f.rsplit("/", 1)[0] + "/" + HIDDEN_FOLDER if "/" in f else "<hidden>"


HIDDEN_FOLDER = "<a folder not registered as a repository>"
HIDDEN_SEAT = "<seat id withheld>"


def withheld_seats(rows):
    """(factory, seat) pairs whose start folder was hidden by mask(). Their seat id is hidden too: a seat is
    named after the personal project its folder is, so the id can carry the same third party's name."""
    return {(r["factory"], r["seat"]) for r in rows for fo in r["folders"] if HIDDEN_FOLDER in (fo["folder"] or "")}


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--json", help="also write the result as JSON to this file")
    args = ap.parse_args()

    factories = gateway("factory", "list", "--json")["factories"]
    schedules = gateway("schedule", "list", "--json")
    triggers = gateway("trigger", "list", "--json")
    online = {m["machineName"]: m for m in gateway("machine", "list", "--json")}
    repos = gateway("repo", "list", "--json")

    registered = {f["factory"]: f.get("folder") for f in factories}
    # (machine, factory, seat) -> the folders that seat starts in on that machine, and where each came from.
    uses = {}

    def add(machine, factory, seat, folder, source):
        if not machine:
            return
        key = (machine, factory, seat or "-")
        uses.setdefault(key, [])
        if folder is not None:
            uses[key].append({"folder": folder, "source": source})

    for f in factories:
        for s in f["seats"]:
            add(s.get("computer") or f.get("computer"), f["factory"], s["id"], f.get("folder"), "registry folder (the factory's one folder)")
    for j in schedules:
        if j.get("factory"):
            add((j.get("target") or {}).get("machine"), j["factory"], j.get("seat"), j["action"].get("repoPath"),
                f"schedule {j['id']}")
    for t in triggers:
        if t.get("factory"):
            add(t.get("machine"), t["factory"], t.get("factoryAgent"), t.get("repoPath"), f"trigger {t['name']}")

    out = {"readUtc": datetime.now(timezone.utc).isoformat(timespec="seconds"), "machines": []}
    for machine in sorted({k[0] for k in uses}):
        mrepos = sorted((r for r in repos if r.get("machineName") == machine), key=lambda r: r["name"].lower())
        seats = []
        for (m, fac, seat), folders in sorted(uses.items()):
            if m != machine:
                continue
            seen = {}
            for u in folders:
                seen.setdefault(norm(u["folder"]), {"folder": u["folder"], "sources": []})["sources"].append(u["source"])
            res = []
            for v in seen.values():
                r = resolve(v["folder"], mrepos)
                shown = v["folder"]
                if r is None and norm(v["folder"]) != norm(registered.get(fac)):
                    shown = mask(v["folder"])
                res.append({"folder": shown, "sources": v["sources"],
                            "repository": r["name"] if r else None, "repositoryPath": r["path"] if r else None})
            seats.append({"factory": fac, "seat": seat, "folders": res})
        info = online.get(machine)
        # Only the repositories a seat resolves to are listed by name; the rest are counted. A machine's full
        # list names client repositories, and this record is committed to a public repository.
        used = {fo["repository"] for s in seats for fo in s["folders"] if fo["repository"]}
        out["machines"].append({
            "machine": machine, "online": info is not None,
            "directorVersion": info.get("version") if info else None,
            "repositoryCount": len(mrepos),
            "repositories": [{"name": r["name"], "path": r["path"], "remote": re.sub(r"//[^/@]+@", "//", r.get("remoteUrl") or "") or None} for r in mrepos
                             if r["name"] in used],
            "seats": seats,
        })

    hidden = withheld_seats([s for m in out["machines"] for s in m["seats"]])
    for m in out["machines"]:
        for s in m["seats"]:
            if (s["factory"], s["seat"]) in hidden:
                s["seat"] = HIDDEN_SEAT

    print(f"Machines factory seats run on, read at {out['readUtc']}")
    for m in out["machines"]:
        print()
        state = f"online, Director {m['directorVersion']}" if m["online"] else "NOT online now (no Director answering)"
        print(f"=== {m['machine']}  ({state})")
        print(f"    registered repositories: {m['repositoryCount']}; the {len(m['repositories'])} a seat here uses:")
        for r in m["repositories"]:
            print(f"        {r['name']:34} {r['path']}")
        print(f"    seats on this machine: {len(m['seats'])}")
        for s in m["seats"]:
            for fo in s["folders"] or [{"folder": None, "sources": [], "repository": None}]:
                target = f"-> {fo['repository']}" if fo["repository"] else "UNRESOLVED (no registered repository holds it)"
                print(f"        {s['factory']:18} {s['seat']:22} {fo['folder']}  {target}  [{', '.join(fo['sources'])}]")
    unresolved = sum(1 for m in out["machines"] for s in m["seats"] for fo in s["folders"] if not fo["repository"])
    total = sum(len(s["folders"]) for m in out["machines"] for s in m["seats"])
    print()
    print(f"Seat folders: {total}; resolved to a registered repository on their machine: {total - unresolved}; unresolved: {unresolved}")
    if args.json:
        with open(args.json, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(out, fh, indent=1, sort_keys=True)
            fh.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
