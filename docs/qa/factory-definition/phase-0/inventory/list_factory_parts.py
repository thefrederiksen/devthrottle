"""Phase 0 (e): every factory's current parts, for phase 7 (recreating each factory from files).

Read only. Reads the Gateway through cc-devthrottle (factory list, schedule list, trigger list, and one
brief history read per factory to see whether the brief store answers this caller) and reads the disk
(each factory folder, the git clone it sits in, its briefs, rules files, tools, templates). It changes
nothing: every cc-devthrottle call is a list or a read, and every git call is rev-parse or remote get-url.

Schedule and trigger prompts are NOT copied out (they are the factory's instructions and may name people);
only the brief files and skills a prompt names are listed.

Usage:  python list_factory_parts.py [--json out.json]
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from measure_records import ROOTS  # noqa: E402  the records roots, one list for (d) and (e)
from list_seat_machines import mask, resolve  # noqa: E402  one masking and one resolve rule for (e) and (f)

CLI = shutil.which("cc-devthrottle")
if CLI is None:
    sys.exit("ERROR: cc-devthrottle is not on the PATH. Install DevThrottle (cc-devthrottle setup) and run again.")


def gateway(*args):
    """Run one read-only cc-devthrottle command and return (ok, parsed JSON or the error text)."""
    p = subprocess.run([CLI, *args], capture_output=True, text=True, encoding="utf-8", errors="replace")
    if p.returncode != 0:
        return False, (p.stderr.strip() or p.stdout.strip()).splitlines()[0] if (p.stderr.strip() or p.stdout.strip()) else f"exit {p.returncode}"
    return True, json.loads(p.stdout)


def git(path, *args):
    p = subprocess.run(["git", "-C", path, *args], capture_output=True, text=True)
    return p.stdout.strip() if p.returncode == 0 else None


def files_in(folder, sub, pattern=None):
    d = os.path.join(folder, sub) if sub else folder
    if not os.path.isdir(d):
        return []
    out = []
    for n in sorted(os.listdir(d)):
        full = os.path.join(d, n)
        if os.path.isfile(full) and (pattern is None or re.search(pattern, n, re.I)):
            out.append({"name": (sub + "/" if sub else "") + n, "bytes": os.path.getsize(full)})
    return out


def settings_rules(path):
    """Count the permission rules in one .claude/settings.json, if present."""
    f = os.path.join(path, ".claude", "settings.json")
    if not os.path.isfile(f):
        return None
    with open(f, encoding="utf-8") as fh:
        data = json.load(fh)
    perms = data.get("permissions", {}) or {}
    return {"file": f.replace("\\", "/"), "allow": len(perms.get("allow", []) or []),
            "deny": len(perms.get("deny", []) or []), "ask": len(perms.get("ask", []) or []),
            "hooks": sorted((data.get("hooks") or {}).keys())}


# A cron that names a day that never comes (31 February, or 1 January 03:00 used as a parking date is NOT
# one - it fires once a year). Only 31 February is reported as never firing.
NEVER_CRON = {"0 0 31 2 *"}

BRIEF_REF = re.compile(r"[\w./-]*(?:agents/[\w.-]+\.ya?ml|[\w-]*BRIEF[\w-]*\.md)", re.I)
SKILL_REF = re.compile(r"(?:skill|the `?/?)\s+`?([a-z0-9][a-z0-9-]{2,})`?\s+skill|\bskill\s+`?/?([a-z0-9][a-z0-9-]{2,})", re.I)


def refs(text):
    briefs = sorted(set(BRIEF_REF.findall(text or "")))
    skills = sorted({a or b for a, b in SKILL_REF.findall(text or "")} - {"from", "and", "the"})
    return briefs, skills


def factory_subdir(seats):
    """Where the factory's own files sit inside its registered folder, from where its brief files are."""
    dirs = []
    for s in seats:
        b = (s.get("briefFile") or "").replace("\\", "/")
        if "/" not in b:
            continue
        d = b.rsplit("/", 1)[0]
        dirs.append("" if d == "agents" else (d[: -len("/agents")] if d.endswith("/agents") else d))
    if not dirs:
        return ""
    common = os.path.commonprefix([d + "/" for d in dirs])
    return common.rsplit("/", 1)[0] if "/" in common else ""


def disk_parts(folder, seats):
    if not folder:
        return {"present": False, "why": "no folder registered"}
    local = folder.replace("/", os.sep)
    if not os.path.isdir(local):
        why = "a path on another machine" if folder.startswith("/") else "not present on this machine"
        return {"present": False, "folder": folder, "why": why}
    top = git(local, "rev-parse", "--show-toplevel")
    sub = factory_subdir(seats)
    fdir = os.path.join(local, *sub.split("/")) if sub else local
    briefs = []
    for s in seats:
        b = s.get("briefFile")
        if b:
            full = os.path.join(local, *b.replace("\\", "/").split("/"))
            briefs.append({"seat": s["id"], "name": b, "bytes": os.path.getsize(full) if os.path.isfile(full) else None})
    parts = {
        "present": True,
        "folder": folder,
        "clone": top,
        "cloneRemote": re.sub(r"//[^/@]+@", "//", git(local, "remote", "get-url", "origin") or "") or None if top else None,
        "cloneBranch": git(local, "rev-parse", "--abbrev-ref", "HEAD") if top else None,
        "factoryFiles": sub or "(the folder itself)",
        "briefsOnDisk": briefs,
        "templates": files_in(fdir, "", r"\.(md|ya?ml|html|json)$"),
        "tools": files_in(fdir, "tools") + files_in(fdir, "", r"\.py$") + files_in(fdir, "outreach", r"\.py$"),
        "rulesFiles": files_in(fdir, "permissions"),
        "settingsInFolder": settings_rules(local),
        "settingsInClone": settings_rules(top) if top else None,
        "skillsInFolder": sorted(os.listdir(os.path.join(local, ".claude", "skills"))) if os.path.isdir(os.path.join(local, ".claude", "skills")) else [],
        "otherFolders": sorted(n for n in os.listdir(fdir) if os.path.isdir(os.path.join(fdir, n))
                               and n not in {"agents", "tools", "permissions", ".claude", "__pycache__", ".git"}),
    }
    return parts


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--json", help="also write the inventory as JSON to this file")
    args = ap.parse_args()

    ok, reg = gateway("factory", "list", "--json")
    if not ok:
        print(f"ERROR: factory list failed: {reg}", file=sys.stderr)
        return 2
    ok, schedules = gateway("schedule", "list", "--json")
    if not ok:
        print(f"ERROR: schedule list failed: {schedules}", file=sys.stderr)
        return 2
    ok, triggers = gateway("trigger", "list", "--json")
    if not ok:
        print(f"ERROR: trigger list failed: {triggers}", file=sys.stderr)
        return 2

    ok, repos = gateway("repo", "list", "--json")
    if not ok:
        print(f"ERROR: repo list failed: {repos}", file=sys.stderr)
        return 2

    def shown(folder, machine):
        on = [r for r in repos if r.get("machineName") == machine]
        return folder if resolve(folder, on) else mask(folder)

    inv = {"readUtc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
           "machine": os.environ.get("COMPUTERNAME", ""), "factories": []}
    for f in sorted(reg["factories"], key=lambda x: x["factory"]):
        fid = f["factory"]
        seats = []
        for s in f["seats"]:
            seats.append({k: s.get(k) for k in ("id", "name", "role", "briefFile", "computer", "line", "schedules")})
        # One brief-store read per factory: does the store answer this caller, and how many versions does the
        # first seat have. Every seat's versions are read only when the first one answers.
        brief_store = {}
        first = seats[0]["id"] if seats else None
        if first:
            ok, page = gateway("factory", "brief", "history", "--factory", fid, "--seat", first, "--count", "1", "--json")
            if ok:
                for s in seats:
                    ok2, p2 = gateway("factory", "brief", "history", "--factory", fid, "--seat", s["id"], "--count", "100", "--json")
                    brief_store[s["id"]] = p2 if ok2 else {"refused": p2}
            else:
                brief_store = {"refused": page}
        fs = []
        for j in schedules:
            if j.get("factory") != fid:
                continue
            b, sk = refs(j["action"].get("seed"))
            fs.append({"id": j["id"], "seat": j.get("seat"), "enabled": j["enabled"],
                       "kind": j["scheduleKind"], "cron": j.get("cronExpression"), "runAt": j.get("runAt"),
                       "timeZone": j.get("timeZoneId"), "machine": (j.get("target") or {}).get("machine"),
                       "repoPath": shown(j["action"].get("repoPath"), (j.get("target") or {}).get("machine")), "preventOverlap": j.get("preventOverlap"),
                       "autoDismiss": j["action"].get("autoDismiss"), "lifecycle": j.get("lifecycle"),
                       "shifts": j.get("shifts"), "neverFires": j.get("cronExpression") in NEVER_CRON, "briefsNamed": b, "skillsNamed": sk,
                       "inRegistry": any(j["id"] in (s.get("schedules") or []) for s in seats)})
        ft = []
        for t in triggers:
            if t.get("factory") != fid:
                continue
            b, sk = refs(t.get("prompt"))
            ft.append({"id": t["id"], "name": t["name"], "seat": t.get("factoryAgent"), "machine": t.get("machine"),
                       "repoPath": shown(t.get("repoPath"), t.get("machine")), "checkCommand": t.get("checkCommand"),
                       "intervalSeconds": t.get("intervalSeconds"), "paused": t.get("paused"),
                       "status": t.get("statusText"), "briefsNamed": b, "skillsNamed": sk})
        registered = {sid for s in seats for sid in (s.get("schedules") or [])}
        inv["factories"].append({
            "id": fid, "title": f["title"], "bossSeat": f.get("bossSeat"), "computer": f.get("computer"),
            "purpose": f.get("purpose"), "goalText": bool(f.get("goalText")), "goalFile": f.get("goalFile"),
            "seats": seats, "briefStore": brief_store, "schedules": fs, "triggers": ft,
            "schedulesInRegistryButNotFound": sorted(registered - {j["id"] for j in schedules}),
            "recordsRoots": [r for r, owner in ROOTS if owner == fid],
            "disk": disk_parts(f.get("folder"), seats),
        })
    inv["schedulesWithNoFactory"] = [{"id": j["id"], "name": j["name"], "enabled": j["enabled"],
                                      "machine": (j.get("target") or {}).get("machine")}
                                     for j in schedules if not j.get("factory")]
    print_report(inv)
    if args.json:
        with open(args.json, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(inv, fh, indent=1, sort_keys=True)
            fh.write("\n")
    return 0


def print_report(inv):
    print(f"Factory parts read on {inv['machine']} at {inv['readUtc']}")
    for f in inv["factories"]:
        d = f["disk"]
        print()
        print(f"=== {f['id']}  ({f['title']})  boss seat: {f['bossSeat'] or '(none)'}  registered computer: {f['computer']}")
        print(f"    seats: {len(f['seats'])}, schedules: {len(f['schedules'])} "
              f"({sum(1 for s in f['schedules'] if s['enabled'])} enabled), triggers: {len(f['triggers'])}")
        if "refused" in f["briefStore"]:
            print(f"    brief store: REFUSED to this session - {f['briefStore']['refused']}")
        for s in f["seats"]:
            bs = f["briefStore"].get(s["id"]) if "refused" not in f["briefStore"] else None
            ver = f"  brief store: {bs.get('total', bs)}" if bs else ""
            print(f"    seat {s['id']:22} role {s['role'] or '-':22} on {s['computer'] or '-':20} brief file {s['briefFile'] or '-'}"
                  f"  schedules {','.join(s['schedules'] or []) or '-'}{ver}")
        for j in f["schedules"]:
            when = j["cron"] if j["kind"] == "recurring" else f"once {j['runAt']}"
            when += " (never fires)" if j["neverFires"] else (f" ({', '.join(j['shifts'])} shift)" if j["shifts"] else "")
            print(f"    schedule {j['id']} {'on ' if j['enabled'] else 'OFF'} {j['lifecycle']:6} seat {j['seat'] or '-':20} {when:30} on {j['machine']}"
                  f"  in {j['repoPath']}{'' if j['inRegistry'] else '  [not in registry]'}")
            if j["briefsNamed"] or j["skillsNamed"]:
                print(f"        names: {', '.join(j['briefsNamed'] + ['skill ' + s for s in j['skillsNamed']])}")
        for t in f["triggers"]:
            print(f"    trigger {t['name']} seat {t['seat']} every {t['intervalSeconds']}s on {t['machine']} in {t['repoPath']}"
                  f"  {'PAUSED' if t['paused'] else t['status']}")
            print(f"        check: {t['checkCommand']}")
        for missing in f["schedulesInRegistryButNotFound"]:
            print(f"    registry names schedule {missing}, which the Gateway does not have")
        print(f"    records: {', '.join('%LOCALAPPDATA%/' + r for r in f['recordsRoots']) or '(none found on this machine)'}")
        if not d["present"]:
            print(f"    folder: {d.get('folder', '-')}  NOT READ ({d['why']})")
            continue
        print(f"    folder: {d['folder']}")
        print(f"    clone:  {d['clone']}  branch {d['cloneBranch']}  remote {d['cloneRemote']}")
        print(f"    factory files in: {d['factoryFiles']}")
        missing = [b['name'] for b in d['briefsOnDisk'] if b['bytes'] is None]
        print(f"    brief files on disk: {sum(1 for b in d['briefsOnDisk'] if b['bytes'] is not None)} of {len(d['briefsOnDisk'])}"
              f"{'  MISSING: ' + ', '.join(missing) if missing else ''}")
        print(f"    templates/docs: {', '.join(b['name'] for b in d['templates']) or '-'}")
        print(f"    tools: {', '.join(b['name'] for b in d['tools']) or '-'}")
        print(f"    rules files: {', '.join(b['name'] for b in d['rulesFiles']) or '-'}")
        for label, st in (("folder", d["settingsInFolder"]), ("clone", d["settingsInClone"])):
            if st:
                print(f"    .claude/settings.json in {label}: allow {st['allow']}, deny {st['deny']}, ask {st['ask']}, hooks {st['hooks'] or '-'}")
        if d["skillsInFolder"]:
            print(f"    skills in folder: {', '.join(d['skillsInFolder'])}")
        if d["otherFolders"]:
            print(f"    other folders: {', '.join(d['otherFolders'])}")
    print()
    allj = [j for f in inv["factories"] for j in f["schedules"]]
    print(f"Factory schedules: {len(allj)}; whose cron is 31 February and never fires: {sum(1 for j in allj if j['neverFires'])}")
    print(f"Factory seats: {sum(len(f['seats']) for f in inv['factories'])}; factories whose brief store refused this session: "
          f"{sum(1 for f in inv['factories'] if 'refused' in f['briefStore'])} of {len(inv['factories'])}")
    print()
    print(f"Schedules with no factory: {len(inv['schedulesWithNoFactory'])}")
    for j in inv["schedulesWithNoFactory"]:
        print(f"    {j['id']} {'on ' if j['enabled'] else 'OFF'} on {j['machine']}  {j['name']}")


if __name__ == "__main__":
    sys.exit(main())
