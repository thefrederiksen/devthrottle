"""Phase 0 (d): measure each factory's folder as a definition would carry it, for the two definition caps.

Read only. For every registered factory it sizes the folder its own files sit in (the folder the brief files
are under), twice: the WHOLE folder, and what is left after the EXCLUSION RULE below - the part that is
definition (briefs, templates, rules, tools, checks, skills). It prints both totals and the largest file left.

The exclusion rule - a folder with one of these names, at any depth, is NOT definition:
  .git  .temp  __pycache__  .pytest_cache  tests  qa  state  research  sample  workshop
Each was found holding work, test fixtures, saved state or raw material, never what decides how a seat behaves
(workshop: raw client photo batches; sample and research: reference material; state: a factory's saved data,
which is a record, section 5.6). Phase 7 should leave the same folders out when it writes a factory as files.

When the registered folder is not on this machine (website-business names a devlinux path), the folder its
schedules and triggers start in on this machine is measured instead, and the output says so.

Usage:  python measure_definition_folders.py [--json out.json]
"""

import argparse
import json
import os
import sys
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from list_factory_parts import factory_subdir, gateway  # noqa: E402  one source for both parts

EXCLUDED = {".git", ".temp", "__pycache__", ".pytest_cache", "tests", "qa", "state", "research", "sample", "workshop"}
KB = 1024


def size_folder(folder):
    whole = kept = 0
    largest = (0, "")
    for dirpath, dirs, names in os.walk(folder):
        parts = set(os.path.relpath(dirpath, folder).replace("\\", "/").split("/"))
        excluded = bool(parts & EXCLUDED)
        for n in names:
            size = os.path.getsize(os.path.join(dirpath, n))
            if ".git" in parts:
                continue  # never counted, not even in the whole folder: it is git's, not the factory's
            whole += size
            if not excluded:
                kept += size
                if size > largest[0]:
                    largest = (size, os.path.relpath(os.path.join(dirpath, n), folder).replace("\\", "/"))
    return whole, kept, largest


def seat_dirs(seats):
    """Each seat's own brief folder, less a trailing agents/. Used when seats keep briefs in separate folders
    (money-saver: tools/daily-cost-report and tools/weekly-cost-report), whose shared parent holds other
    tools that are not this factory's."""
    dirs = set()
    for s in seats:
        b = (s.get("briefFile") or "").replace("\\", "/")
        if "/" in b:
            d = b.rsplit("/", 1)[0]
            dirs.add("" if d == "agents" else (d[: -len("/agents")] if d.endswith("/agents") else d))
    return sorted(dirs)


def local_folders(f, triggers):
    """The folders holding this factory's own files on this machine, and how they were chosen."""
    folder = (f.get("folder") or "").replace("/", os.sep)
    if folder and os.path.isdir(folder):
        dirs = seat_dirs(f["seats"])
        if len(dirs) > 1 and "" not in dirs and factory_subdir(f["seats"]) not in dirs:
            return [os.path.join(folder, *d.split("/")) for d in dirs], "each seat's own brief folder"
        sub = factory_subdir(f["seats"])
        return [os.path.join(folder, *sub.split("/")) if sub else folder], "registered folder"
    # The registered folder is elsewhere: use where this factory's starts run on this machine.
    for t in triggers:
        if t.get("factory") == f["factory"] and os.path.isdir(t.get("repoPath") or ""):
            return [t["repoPath"].replace("/", os.sep)], "the folder its triggers start in (registered folder not on this machine)"
    return [], f"not on this machine ({f.get('folder')})"


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--json", help="also write the measurement as JSON to this file")
    args = ap.parse_args()
    ok, reg = gateway("factory", "list", "--json")
    ok2, triggers = gateway("trigger", "list", "--json")
    ok3, schedules = gateway("schedule", "list", "--json")
    if not (ok and ok2 and ok3):
        print("ERROR: reading the Gateway failed; run cc-devthrottle factory list to see why.", file=sys.stderr)
        return 2
    out = {"measuredUtc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
           "excluded": sorted(EXCLUDED), "factories": []}
    for f in sorted(reg["factories"], key=lambda x: x["factory"]):
        folders, how = local_folders(f, triggers)
        row = {"factory": f["factory"], "measured": how}
        if folders:
            sizes = [size_folder(d) for d in folders]
            whole, kept = sum(x[0] for x in sizes), sum(x[1] for x in sizes)
            largest = max((x[2] for x in sizes), key=lambda l: l[0])
            row.update({"wholeBytes": whole, "definitionBytes": kept, "largestBytes": largest[0], "largestFile": largest[1]})
        out["factories"].append(row)

    print(f"Factory folders measured at {out['measuredUtc']}; not definition: {', '.join(out['excluded'])}")
    print()
    print(f"    {'factory':20} {'whole folder':>14} {'definition':>12} {'largest file':>13}  file")
    for r in out["factories"]:
        if "wholeBytes" not in r:
            print(f"    {r['factory']:20} {'-':>14} {'-':>12} {'-':>13}  {r['measured']}")
            continue
        note = "" if r["measured"] == "registered folder" else f"   [{r['measured']}]"
        print(f"    {r['factory']:20} {r['wholeBytes'] / KB:11.1f} KB {r['definitionBytes'] / KB:9.1f} KB "
              f"{r['largestBytes'] / KB:10.1f} KB  {r['largestFile']}{note}")
    measured = [r for r in out["factories"] if "wholeBytes" in r]
    print()
    print(f"Largest definition: {max(r['definitionBytes'] for r in measured) / KB:.1f} KB; "
          f"largest single definition file: {max(r['largestBytes'] for r in measured) / KB:.1f} KB")
    if args.json:
        with open(args.json, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(out, fh, indent=1, sort_keys=True)
            fh.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
