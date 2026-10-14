"""Phase 0 (d): measure the records every factory keeps on this machine today.

Read only. Walks each records root under %LOCALAPPDATA% that a factory writes to, sorts every file into
one kind (journal, working document, attachment, scratch), and prints per factory and per kind: file
count, total bytes, the largest file, and for attachments and working documents the size spread and how
many files would be over each candidate cap.

File paths below the first folder are NOT printed: they carry prospect, customer and person names, and
this record is committed to a public repository. The largest file is shown as "<first folder>/.../<ext>".

Usage:  python measure_records.py [--json out.json]
"""

import argparse
import hashlib
import json
import os
import sys
from datetime import datetime, timezone

# Each records root on disk, and the factory it belongs to. Found by listing %LOCALAPPDATA% and reading
# each factory's brief for the folder it names (FACTORY.md, agents/*.yaml) on 14 Oct 2026.
ROOTS = [
    ("cc-factory/devthrottle", "devthrottle"),
    ("cc-factory/cfo", "cc-factory"),
    ("cc-factory-factory", "cc-factory"),
    ("cc-factory/vercel-release", "(no factory: release tool lock log)"),
    ("business-research-factory", "business-research"),
    ("clickfunnels-factory", "clickfunnels"),
    ("mindzie-web-factory", "mindzie-web"),
    ("warmforward-factory", "warmforward"),
    ("website-factory", "website-business"),
    ("cc-director/factories", "money-saver"),
]

ATTACHMENT_EXT = {"png", "jpg", "jpeg", "gif", "webp", "bmp", "wav", "mp3", "m4a", "mp4", "webm", "pdf", "zip"}
SCRATCH_DIRS = {"scratch", "transcribe", "tmp", "temp", "cache", "__pycache__"}
JOURNAL_DIRS = {"journal", "memos"}
JOURNAL_EXT = {"log", "jsonl"}
DATABASE_EXT = {"db", "sqlite", "sqlite3", "db-wal", "db-shm", "sqlite-wal", "sqlite-shm"}

KB = 1024
MB = 1024 * 1024
CANDIDATE_CAPS = {
    "attachment": [1 * MB, 2 * MB, 3 * MB, 5 * MB],
    "working document": [256 * KB, 1 * MB, 2 * MB],
    "journal": [64 * KB, 256 * KB],
}


def kind_of(rel_parts):
    """One kind per file, by these rules in this order (stated in README.md)."""
    name = rel_parts[-1]
    ext = name.rsplit(".", 1)[-1].lower() if "." in name else ""
    folders = {p.lower() for p in rel_parts[:-1]}
    if folders & SCRATCH_DIRS:
        return "scratch"
    if ext in ATTACHMENT_EXT:
        return "attachment"
    if ext in DATABASE_EXT or name.lower().endswith((".db-wal", ".db-shm")):
        return "database"
    if folders & JOURNAL_DIRS or ext in JOURNAL_EXT:
        return "journal"
    return "working document"


def shown_path(rel_parts):
    """First folder and extension only - deeper folders and file names may carry names of people."""
    name = rel_parts[-1]
    ext = name.rsplit(".", 1)[-1].lower() if "." in name else "(no extension)"
    if len(rel_parts) == 1:
        return name if name.lower() in {"register.json", "state.json", "runs.log", "releases.log"} else f"*.{ext}"
    return f"{rel_parts[0]}/.../*.{ext}"


def percentile(sorted_sizes, p):
    if not sorted_sizes:
        return 0
    i = min(len(sorted_sizes) - 1, int(round(p / 100.0 * (len(sorted_sizes) - 1))))
    return sorted_sizes[i]


def measure(base):
    out = {"measuredUtc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
           "machine": os.environ.get("COMPUTERNAME", ""), "base": "%LOCALAPPDATA%", "roots": []}
    for rel_root, factory in ROOTS:
        root = os.path.join(base, *rel_root.split("/"))
        entry = {"root": rel_root, "factory": factory, "exists": os.path.isdir(root), "kinds": {}}
        files = []
        digests = {}  # attachment content hash -> size: what storing each distinct file once would cost
        if entry["exists"]:
            for dirpath, _dirs, names in os.walk(root):
                for n in names:
                    full = os.path.join(dirpath, n)
                    rel = os.path.relpath(full, root).replace("\\", "/").split("/")
                    files.append((os.path.getsize(full), rel))
                    if kind_of(rel) == "attachment":
                        with open(full, "rb") as fh:
                            digests[hashlib.sha256(fh.read()).hexdigest()] = os.path.getsize(full)
        for size, rel in files:
            k = entry["kinds"].setdefault(kind_of(rel), {"files": 0, "bytes": 0, "largest": 0, "largestAt": "", "sizes": []})
            k["files"] += 1
            k["bytes"] += size
            k["sizes"].append(size)
            if size > k["largest"]:
                k["largest"], k["largestAt"] = size, shown_path(rel)
        entry["bigDocuments"] = sorted(
            ({"size": size, "at": shown_path(rel)} for size, rel in files
             if kind_of(rel) == "working document" and size > 256 * KB), key=lambda d: -d["size"])
        entry["attachmentsDistinct"] = {"files": len(digests), "bytes": sum(digests.values())}
        entry["files"] = len(files)
        entry["bytes"] = sum(s for s, _ in files)
        out["roots"].append(entry)

    # Across every factory, per kind: the spread and the count over each candidate cap.
    totals = {}
    for entry in out["roots"]:
        for kind, k in entry["kinds"].items():
            t = totals.setdefault(kind, {"files": 0, "bytes": 0, "sizes": []})
            t["files"] += k["files"]
            t["bytes"] += k["bytes"]
            t["sizes"].extend(k["sizes"])
    for kind, t in totals.items():
        s = sorted(t.pop("sizes"))
        t["p50"], t["p90"], t["p99"], t["max"] = (percentile(s, 50), percentile(s, 90), percentile(s, 99), s[-1] if s else 0)
        t["overCap"] = {str(c): sum(1 for x in s if x > c) for c in CANDIDATE_CAPS.get(kind, [])}
    out["totals"] = totals
    per_factory = {}
    for entry in out["roots"]:
        f = per_factory.setdefault(entry["factory"], {"files": 0, "bytes": 0, "attachmentBytes": 0, "recordBytes": 0, "databaseBytes": 0})
        f["files"] += entry["files"]
        f["bytes"] += entry["bytes"]
        for kind, k in entry["kinds"].items():
            if kind == "attachment":
                f["attachmentBytes"] += k["bytes"]
            elif kind == "database":
                f["databaseBytes"] += k["bytes"]
            elif kind != "scratch":
                f["recordBytes"] += k["bytes"]
    out["perFactory"] = per_factory
    for entry in out["roots"]:
        for k in entry["kinds"].values():
            k.pop("sizes")
    return out


def human(n):
    if n >= MB:
        return f"{n / MB:.2f} MB"
    if n >= KB:
        return f"{n / KB:.1f} KB"
    return f"{n} B"


def print_report(m):
    print(f"Factory records measured on {m['machine']} at {m['measuredUtc']}, under {m['base']}")
    print()
    for e in m["roots"]:
        if not e["exists"]:
            print(f"{e['root']}  [{e['factory']}]  NOT PRESENT")
            continue
        print(f"{e['root']}  [{e['factory']}]  {e['files']} files, {human(e['bytes'])}")
        for kind in ("journal", "working document", "database", "attachment", "scratch"):
            k = e["kinds"].get(kind)
            if k:
                print(f"    {kind:17} {k['files']:5} files {human(k['bytes']):>11}   largest {human(k['largest']):>10}  {k['largestAt']}")
                if kind == "attachment":
                    dd = e["attachmentsDistinct"]
                    print(f"    {'':17} {dd['files']:5} distinct by content {human(dd['bytes']):>11}")
    print()
    print("Working documents over 256 KB:")
    for e in m["roots"]:
        for d in e.get("bigDocuments", []):
            print(f"    {human(d['size']):>10}  {e['root']}/{d['at']}  [{e['factory']}]")
    print()
    print("Per factory (records = journals + working documents; databases, attachments and scratch apart):")
    for f, t in sorted(m["perFactory"].items(), key=lambda kv: -kv[1]["bytes"]):
        print(f"    {f[:40]:40} {t['files']:5} files {human(t['bytes']):>11}  records {human(t['recordBytes']):>10}  databases {human(t['databaseBytes']):>10}  attachments {human(t['attachmentBytes']):>10}")
    print()
    print("Every factory together, per kind:")
    for kind, t in m["totals"].items():
        caps = ", ".join(f"over {human(int(c))}: {n}" for c, n in t["overCap"].items())
        print(f"    {kind:17} {t['files']:5} files {human(t['bytes']):>11}  median {human(t['p50'])}, 90% {human(t['p90'])}, 99% {human(t['p99'])}, max {human(t['max'])}")
        if caps:
            print(f"    {'':17} {caps}")


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--json", help="also write the measurement as JSON to this file")
    args = ap.parse_args()
    base = os.environ.get("LOCALAPPDATA")
    if not base:
        print("ERROR: LOCALAPPDATA is not set; this measures a Windows machine's factory records.", file=sys.stderr)
        return 2
    m = measure(base)
    print_report(m)
    if args.json:
        with open(args.json, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(m, fh, indent=1, sort_keys=True)
            fh.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
